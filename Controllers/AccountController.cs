using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using AmpmHrmsPro.Data;
using AmpmHrmsPro.Models;
using AmpmHrmsPro.Services.Tenancy;

namespace AmpmHrmsPro.Controllers
{
    // ═══════════════════════════════════════════════════════════════════
    // ACCOUNT — company login (Company Code + Employee Code + Password),
    // lockout after repeated failures, optional 2-step verification
    // (authenticator app), forced / expired password change.
    // ═══════════════════════════════════════════════════════════════════
    public class AccountController : Controller
    {
        public const string CompanyCookie = "hrms_company";
        private const string PendingCookie = "hrms_2fa";

        private readonly AppDbContext _db;
        private readonly ITenantContext _tenant;
        private readonly ICompanyInfoService _companies;
        private readonly IAccessService _access;
        private readonly ITimeLimitedDataProtector _protector;

        public AccountController(AppDbContext db, ITenantContext tenant, ICompanyInfoService companies,
                                 IAccessService access, IDataProtectionProvider dp)
        {
            _db = db; _tenant = tenant; _companies = companies; _access = access;
            _protector = dp.CreateProtector("hrms-2fa-pending").ToTimeLimitedDataProtector();
        }

        // ── Login ────────────────────────────────────────────────────────
        [HttpGet]
        public async Task<IActionResult> Login(string? returnUrl = null, string? company = null)
        {
            if (User.Identity?.IsAuthenticated == true)
            {
                if (User.IsPlatformAdmin()) return Redirect("/Platform");
                if (User.GetCompanyId() > 0) return await LandingAsync(returnUrl);
            }
            var code = CompanyProvisioning.NormalizeCode(company ?? Request.Cookies[CompanyCookie]);
            ViewBag.CompanyCode = code;
            ViewBag.ReturnUrl = returnUrl;
            if (code.Length > 0)
            {
                var c = await _db.Companies.AsNoTracking().FirstOrDefaultAsync(x => x.Code == code);
                if (c != null) ViewBag.Company = CompanyInfoService.From(c);
            }
            return View();
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Login(string? companyCode, string empCode, string password, string? returnUrl = null)
        {
            const string invalid = "Invalid company code, employee code or password.";
            var code = CompanyProvisioning.NormalizeCode(companyCode);
            empCode = (empCode ?? "").Trim();
            ViewBag.CompanyCode = code;
            ViewBag.ReturnUrl = returnUrl;

            Company? company = null;
            if (code.Length > 0)
            {
                company = await _db.Companies.FirstOrDefaultAsync(c => c.Code == code);
                if (company == null) return LoginError("Company code not found. Check the code given by your HR.");
            }
            else
            {
                // No code typed: allowed only when the employee code is unique across companies.
                var ids = await _db.Employees.IgnoreQueryFilters()
                    .Where(e => e.EmpCode == empCode && e.IsActive)
                    .Select(e => EF.Property<int>(e, AppDbContext.TenantColumn)).Distinct().ToListAsync();
                if (ids.Count > 1) return LoginError("Please enter your Company Code.");
                if (ids.Count == 1) company = await _db.Companies.FirstOrDefaultAsync(c => c.Id == ids[0]);
                if (company == null) return LoginError(invalid);
            }
            ViewBag.Company = CompanyInfoService.From(company);

            var info = CompanyInfoService.From(company);
            if (info.IsSuspended) return LoginError("This company's HRMS account is suspended. Please contact your administrator.");
            if (info.IsExpired)   return LoginError("This company's subscription has expired. Please contact your administrator.");

            _tenant.Use(company.Id);
            var emp = await _db.Employees.FirstOrDefaultAsync(e => e.EmpCode == empCode && e.IsActive);
            if (emp == null)
            {
                _db.Audit("LoginFailed", $"Unknown employee code '{Trunc(empCode, 40)}'", userName: empCode);
                await _db.SaveChangesAsync();
                return LoginError(invalid);
            }

            var now = DateTime.UtcNow;
            if (emp.LockoutUntil.HasValue && emp.LockoutUntil.Value > now)
            {
                var mins = (int)Math.Ceiling((emp.LockoutUntil.Value - now).TotalMinutes);
                return LoginError($"Too many wrong attempts — account locked for {mins} more minute(s). Ask HR to unlock it if urgent.");
            }

            if (!BCrypt.Net.BCrypt.Verify(password ?? "", emp.PasswordHash))
            {
                emp.FailedLoginCount++;
                string msg = invalid;
                if (company.MaxFailedLogins > 0 && emp.FailedLoginCount >= company.MaxFailedLogins)
                {
                    emp.LockoutUntil = now.AddMinutes(Math.Max(1, company.LockoutMinutes));
                    emp.FailedLoginCount = 0;
                    msg = $"Too many wrong attempts — account locked for {Math.Max(1, company.LockoutMinutes)} minutes.";
                    _db.Audit("Lockout", $"{emp.Name} ({emp.EmpCode}) locked after repeated wrong passwords", "Employee", emp.Id.ToString(), userId: emp.Id, userName: emp.Name);
                }
                else
                {
                    _db.Audit("LoginFailed", $"Wrong password for {emp.EmpCode}", "Employee", emp.Id.ToString(), userId: emp.Id, userName: emp.Name);
                }
                await _db.SaveChangesAsync();
                return LoginError(msg);
            }

            emp.FailedLoginCount = 0;
            emp.LockoutUntil = null;
            if (company.PasswordExpiryDays > 0)
            {
                var changed = emp.PasswordChangedAt ?? emp.CreatedAt.ToUniversalTime();
                if (changed.AddDays(company.PasswordExpiryDays) < now) emp.MustChangePassword = true;
            }
            await _db.SaveChangesAsync();

            Response.Cookies.Append(CompanyCookie, company.Code, new CookieOptions
            { Expires = DateTimeOffset.UtcNow.AddYears(1), HttpOnly = true, IsEssential = true, SameSite = SameSiteMode.Lax });

            if (emp.TwoFactorEnabled && !string.IsNullOrEmpty(emp.TwoFactorSecret))
            {
                var token = _protector.Protect($"{company.Id}|{emp.Id}|{returnUrl}", TimeSpan.FromMinutes(5));
                Response.Cookies.Append(PendingCookie, token, new CookieOptions { HttpOnly = true, IsEssential = true, SameSite = SameSiteMode.Lax, MaxAge = TimeSpan.FromMinutes(5) });
                return RedirectToAction(nameof(TwoFactor));
            }

            await SignInAsync(emp, company);
            return await LandingAsync(returnUrl);
        }

        private IActionResult LoginError(string message)
        {
            ViewBag.Error = message;
            return View("Login");
        }

        // ── 2-step verification at login ─────────────────────────────────
        [HttpGet]
        public IActionResult TwoFactor()
        {
            if (ReadPending() == null) return RedirectToAction(nameof(Login));
            return View();
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> TwoFactor(string code)
        {
            var pending = ReadPending();
            if (pending == null) return RedirectToAction(nameof(Login));
            var (companyId, empId, returnUrl) = pending.Value;

            _tenant.Use(companyId);
            var company = await _db.Companies.FirstAsync(c => c.Id == companyId);
            var emp = await _db.Employees.FirstOrDefaultAsync(e => e.Id == empId && e.IsActive);
            if (emp == null) return RedirectToAction(nameof(Login));

            if (!Totp.Verify(emp.TwoFactorSecret, code))
            {
                emp.FailedLoginCount++;
                if (company.MaxFailedLogins > 0 && emp.FailedLoginCount >= company.MaxFailedLogins)
                {
                    emp.LockoutUntil = DateTime.UtcNow.AddMinutes(Math.Max(1, company.LockoutMinutes));
                    emp.FailedLoginCount = 0;
                    Response.Cookies.Delete(PendingCookie);
                }
                _db.Audit("LoginFailed", $"Wrong 2-step code for {emp.EmpCode}", "Employee", emp.Id.ToString(), userId: emp.Id, userName: emp.Name);
                await _db.SaveChangesAsync();
                ViewBag.Error = "That code is not correct. Use the latest 6-digit code from your authenticator app.";
                return View();
            }

            emp.FailedLoginCount = 0;
            await _db.SaveChangesAsync();
            Response.Cookies.Delete(PendingCookie);
            await SignInAsync(emp, company);
            return await LandingAsync(returnUrl);
        }

        private (int CompanyId, int EmpId, string? ReturnUrl)? ReadPending()
        {
            try
            {
                var raw = Request.Cookies[PendingCookie];
                if (string.IsNullOrEmpty(raw)) return null;
                var parts = _protector.Unprotect(raw).Split('|', 3);
                return (int.Parse(parts[0]), int.Parse(parts[1]), parts.Length > 2 && parts[2].Length > 0 ? parts[2] : null);
            }
            catch { return null; }
        }

        // ── Sign-in helpers ──────────────────────────────────────────────
        private async Task SignInAsync(Employee emp, Company company)
        {
            emp.LastLoginAt = DateTime.UtcNow;
            company.LastActivityAt = DateTime.UtcNow;
            _db.Audit("Login", $"{emp.Name} ({emp.EmpCode}) signed in", "Employee", emp.Id.ToString(), company.Id, emp.Id, emp.Name);
            await _db.SaveChangesAsync();
            await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, BuildPrincipal(emp, company),
                new AuthenticationProperties { IsPersistent = true, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8) });
        }

        public static ClaimsPrincipal BuildPrincipal(Employee emp, Company company, string? impersonator = null)
        {
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, emp.Id.ToString()),
                new(ClaimTypes.Name, emp.Name),
                new(ClaimTypes.Role, emp.Role),
                new("EmpCode", emp.EmpCode),
                new(TenantClaims.CompanyId, company.Id.ToString()),
                new(TenantClaims.CompanyCode, company.Code),
            };
            if (impersonator != null) claims.Add(new(TenantClaims.Impersonator, impersonator));
            else
            {
                if (emp.MustChangePassword) claims.Add(new("MustChangePassword", "1"));
                bool privileged = emp.Role is "admin" or "hr";
                if (company.Require2FAForAdmins && privileged && !emp.TwoFactorEnabled) claims.Add(new("MustSetup2FA", "1"));
            }
            return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
        }

        private Task<IActionResult> LandingAsync(string? returnUrl)
        {
            IActionResult r = !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) && !returnUrl.StartsWith("/Account", StringComparison.OrdinalIgnoreCase)
                ? LocalRedirect(returnUrl)
                : RedirectToAction("Index", "Home");
            return Task.FromResult(r);
        }

        private async Task ReSignInAsync(Employee emp)
        {
            var company = await _db.Companies.FirstAsync(c => c.Id == User.GetCompanyId());
            await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, BuildPrincipal(emp, company),
                new AuthenticationProperties { IsPersistent = true, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8) });
        }

        private int? CurrentEmpId => User.GetEmployeeId();

        // ── Logout ───────────────────────────────────────────────────────
        public async Task<IActionResult> Logout()
        {
            if (User.Identity?.IsAuthenticated == true && User.GetCompanyId() > 0 && User.FindFirst(TenantClaims.Impersonator) == null)
            {
                _db.Audit("Logout", $"{User.Identity.Name} signed out");
                try { await _db.SaveChangesAsync(); } catch { /* never block logout */ }
            }
            var wasOwner = User.FindFirst(TenantClaims.Impersonator) != null;
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return wasOwner ? Redirect("/Platform/Login") : RedirectToAction(nameof(Login));
        }

        // ── My security: change password, 2-step verification ───────────
        [Authorize, HttpGet]
        public async Task<IActionResult> Security()
        {
            var emp = await _db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.Id == CurrentEmpId);
            if (emp == null) return RedirectToAction(nameof(Login));
            ViewBag.Company = await _db.Companies.AsNoTracking().FirstAsync(c => c.Id == User.GetCompanyId());
            return View(emp);
        }

        [Authorize, HttpGet]
        public async Task<IActionResult> ChangePassword()
        {
            ViewBag.Policy = PasswordPolicy.Describe(await _db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.Id == User.GetCompanyId()));
            ViewBag.Forced = User.HasClaim("MustChangePassword", "1");
            return View();
        }

        [Authorize, HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> ChangePassword(string currentPassword, string newPassword, string confirmPassword)
        {
            var company = await _db.Companies.FirstAsync(c => c.Id == User.GetCompanyId());
            ViewBag.Policy = PasswordPolicy.Describe(company);
            ViewBag.Forced = User.HasClaim("MustChangePassword", "1");
            var emp = await _db.Employees.FirstOrDefaultAsync(e => e.Id == CurrentEmpId);
            if (emp == null) return RedirectToAction(nameof(Login));

            string? error = null;
            if (!BCrypt.Net.BCrypt.Verify(currentPassword ?? "", emp.PasswordHash)) error = "Current password is not correct.";
            else if (newPassword != confirmPassword) error = "New password and confirmation don't match.";
            else if (newPassword == currentPassword) error = "New password must be different from the current one.";
            else error = PasswordPolicy.Validate(newPassword, company);
            if (error != null) { ViewBag.Error = error; return View(); }

            emp.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword);
            emp.PasswordChangedAt = DateTime.UtcNow;
            emp.MustChangePassword = false;
            _db.Audit("PasswordChanged", $"{emp.Name} changed their password", "Employee", emp.Id.ToString());
            await _db.SaveChangesAsync();
            await ReSignInAsync(emp);
            TempData["Success"] = "Password changed.";
            return User.HasClaim("MustSetup2FA", "1") ? RedirectToAction(nameof(TwoFactorSetup)) : RedirectToAction("Index", "Home");
        }

        [Authorize, HttpGet]
        public async Task<IActionResult> TwoFactorSetup()
        {
            var emp = await _db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.Id == CurrentEmpId);
            if (emp == null) return RedirectToAction(nameof(Login));
            var secret = Totp.NewSecret();
            var company = await _companies.GetAsync(User.GetCompanyId());
            ViewBag.Secret = secret;
            ViewBag.Token = _protector.Protect(secret, TimeSpan.FromMinutes(15));
            ViewBag.OtpUri = Totp.Uri($"{company?.Name ?? PlatformBrand.ProductName}", emp.EmpCode, secret);
            ViewBag.Forced = User.HasClaim("MustSetup2FA", "1");
            return View(emp);
        }

        [Authorize, HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> TwoFactorSetup(string token, string code)
        {
            string secret;
            try { secret = _protector.Unprotect(token); }
            catch { TempData["Error"] = "Setup timed out — please scan the new code."; return RedirectToAction(nameof(TwoFactorSetup)); }

            if (!Totp.Verify(secret, code))
            {
                TempData["Error"] = "That code didn't match. Make sure the phone's time is correct and try the newest code.";
                return RedirectToAction(nameof(TwoFactorSetup));
            }
            var emp = await _db.Employees.FirstAsync(e => e.Id == CurrentEmpId);
            emp.TwoFactorSecret = secret;
            emp.TwoFactorEnabled = true;
            _db.Audit("2FAEnabled", $"{emp.Name} turned on 2-step verification", "Employee", emp.Id.ToString());
            await _db.SaveChangesAsync();
            await ReSignInAsync(emp);
            TempData["Success"] = "2-step verification is ON. You'll be asked for a code at every login.";
            return RedirectToAction(nameof(Security));
        }

        [Authorize, HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> TwoFactorDisable(string currentPassword)
        {
            var emp = await _db.Employees.FirstAsync(e => e.Id == CurrentEmpId);
            var company = await _db.Companies.FirstAsync(c => c.Id == User.GetCompanyId());
            if (company.Require2FAForAdmins && emp.Role is "admin" or "hr")
            { TempData["Error"] = "Your company requires 2-step verification for admin/HR users."; return RedirectToAction(nameof(Security)); }
            if (!BCrypt.Net.BCrypt.Verify(currentPassword ?? "", emp.PasswordHash))
            { TempData["Error"] = "Password is not correct."; return RedirectToAction(nameof(Security)); }
            emp.TwoFactorEnabled = false;
            emp.TwoFactorSecret = null;
            _db.Audit("2FADisabled", $"{emp.Name} turned off 2-step verification", "Employee", emp.Id.ToString());
            await _db.SaveChangesAsync();
            TempData["Success"] = "2-step verification turned off.";
            return RedirectToAction(nameof(Security));
        }

        // ── Info pages ───────────────────────────────────────────────────
        public IActionResult AccessDenied() => View();

        [Authorize]
        public IActionResult ModuleLocked(string? module)
        {
            ViewBag.Module = Modules.All.FirstOrDefault(m => m.Key == module).Label ?? module;
            return View();
        }

        [Authorize]
        public async Task<IActionResult> Subscription()
        {
            var info = await _companies.GetAsync(User.GetCompanyId());
            if (info != null && info.CanUse) return RedirectToAction("Index", "Home");
            return View(info);
        }

        private static string Trunc(string s, int n) => s.Length > n ? s[..n] : s;
    }
}
