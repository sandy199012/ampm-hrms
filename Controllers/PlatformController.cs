using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using AmpmHrmsPro.Data;
using AmpmHrmsPro.Models;
using AmpmHrmsPro.Services.Tenancy;

namespace AmpmHrmsPro.Controllers
{
    // ═══════════════════════════════════════════════════════════════════
    // OWNER CONSOLE (/Platform) — for the software owner, not for client
    // companies. Create client companies, set plan / employee limit /
    // modules / expiry, suspend, and open a company as its admin for support.
    // ═══════════════════════════════════════════════════════════════════
    [Authorize(Roles = TenantClaims.PlatformRole)]
    public class PlatformController : Controller
    {
        private const string ImpersonatorIdClaim = "ImpersonatorPlatformUserId";

        private readonly AppDbContext _db;
        private readonly ICompanyProvisioning _provisioning;
        private readonly ICompanyInfoService _companies;
        private readonly IAccessService _access;

        public PlatformController(AppDbContext db, ICompanyProvisioning provisioning, ICompanyInfoService companies, IAccessService access)
        { _db = db; _provisioning = provisioning; _companies = companies; _access = access; }

        public override void OnActionExecuting(ActionExecutingContext context)
        {
            var action = context.RouteData.Values["action"]?.ToString();
            if (User.IsPlatformAdmin() && User.HasClaim("MustChangePassword", "1") && action is not ("ChangePassword" or "Logout" or "Login"))
                context.Result = RedirectToAction(nameof(ChangePassword));
            ViewData["OwnerName"] = User.Identity?.Name;
            base.OnActionExecuting(context);
        }

        private int OwnerId => int.TryParse(User.FindFirst("PlatformUserId")?.Value, out var id) ? id : 0;

        // ── Login ────────────────────────────────────────────────────────
        [AllowAnonymous, HttpGet]
        public IActionResult Login()
        {
            if (User.IsPlatformAdmin()) return RedirectToAction(nameof(Index));
            return View();
        }

        [AllowAnonymous, HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Login(string email, string password)
        {
            var u = await _db.PlatformUsers.FirstOrDefaultAsync(x => x.Email == (email ?? "").Trim() && x.IsActive);
            var now = DateTime.UtcNow;
            if (u != null && u.LockoutUntil > now)
            { ViewBag.Error = "Too many wrong attempts — try again in a few minutes."; return View(); }
            if (u == null || !BCrypt.Net.BCrypt.Verify(password ?? "", u.PasswordHash))
            {
                if (u != null)
                {
                    u.FailedLoginCount++;
                    if (u.FailedLoginCount >= 5) { u.LockoutUntil = now.AddMinutes(15); u.FailedLoginCount = 0; }
                    await _db.SaveChangesAsync();
                }
                ViewBag.Error = "Invalid login.";
                return View();
            }
            u.FailedLoginCount = 0; u.LockoutUntil = null; u.LastLoginAt = now;
            await _db.SaveChangesAsync();
            await SignInOwnerAsync(u);
            return RedirectToAction(nameof(Index));
        }

        private Task SignInOwnerAsync(PlatformUser u)
        {
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, "platform:" + u.Id),
                new("PlatformUserId", u.Id.ToString()),
                new(ClaimTypes.Name, u.Name),
                new(ClaimTypes.Role, TenantClaims.PlatformRole),
            };
            if (u.MustChangePassword) claims.Add(new("MustChangePassword", "1"));
            return HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)),
                new AuthenticationProperties { IsPersistent = false, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(4) });
        }

        public async Task<IActionResult> Logout()
        {
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return RedirectToAction(nameof(Login));
        }

        [HttpGet]
        public IActionResult ChangePassword() => View();

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> ChangePassword(string currentPassword, string newPassword, string confirmPassword)
        {
            var u = await _db.PlatformUsers.FirstAsync(x => x.Id == OwnerId);
            string? error = !BCrypt.Net.BCrypt.Verify(currentPassword ?? "", u.PasswordHash) ? "Current password is not correct."
                : newPassword != confirmPassword ? "Passwords don't match."
                : PasswordPolicy.Validate(newPassword, new Company { PasswordMinLength = 10, PasswordRequireComplex = true });
            if (error != null) { ViewBag.Error = error; return View(); }
            u.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword);
            u.MustChangePassword = false;
            await _db.SaveChangesAsync();
            await SignInOwnerAsync(u);
            TempData["Success"] = "Password changed.";
            return RedirectToAction(nameof(Index));
        }

        // ── Dashboard / company list ─────────────────────────────────────
        public record CompanyRow(Company Company, int Employees, int ActiveEmployees, DateTime? LastLogin);

        public async Task<IActionResult> Index(string? q)
        {
            var companies = await _db.Companies.AsNoTracking().OrderBy(c => c.Name).ToListAsync();
            if (!string.IsNullOrWhiteSpace(q))
                companies = companies.Where(c => c.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || c.Code.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();

            var counts = await _db.Employees.IgnoreQueryFilters()
                .GroupBy(e => EF.Property<int>(e, AppDbContext.TenantColumn))
                .Select(g => new { Id = g.Key, All = g.Count(), Active = g.Count(e => e.IsActive), Last = g.Max(e => e.LastLoginAt) })
                .ToListAsync();
            var byId = counts.ToDictionary(c => c.Id);
            var rows = companies.Select(c => new CompanyRow(c,
                byId.TryGetValue(c.Id, out var x) ? x.All : 0, x?.Active ?? 0, x?.Last)).ToList();
            ViewBag.Q = q;
            return View(rows);
        }

        // ── Create ───────────────────────────────────────────────────────
        [HttpGet]
        public IActionResult Create() => View();

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(string code, string name, string? contactName, string? contactEmail, string? contactPhone,
            string plan, int? maxEmployees, int? trialDays, DateTime? subscriptionEndsAt,
            string adminEmpCode, string adminName, string adminEmail, string? adminPassword)
        {
            try
            {
                var result = await _provisioning.CreateAsync(new NewCompanyRequest(code, name, contactName, contactEmail, contactPhone,
                    plan, maxEmployees, null, trialDays, subscriptionEndsAt?.ToUniversalTime(), adminEmpCode, adminName, adminEmail, adminPassword));
                return View("Created", result);
            }
            catch (InvalidOperationException ex)
            {
                ViewBag.Error = ex.Message;
                return View();
            }
        }

        // ── Edit plan / status ───────────────────────────────────────────
        [HttpGet]
        public async Task<IActionResult> Edit(int id)
        {
            var c = await _db.Companies.FirstOrDefaultAsync(x => x.Id == id);
            if (c == null) return NotFound();
            ViewBag.ActiveEmployees = await _provisioning.ActiveEmployeeCountAsync(id);
            ViewBag.Audit = await _db.AuditLogs.IgnoreQueryFilters()
                .Where(a => EF.Property<int>(a, AppDbContext.TenantColumn) == id)
                .OrderByDescending(a => a.Id).Take(15).AsNoTracking().ToListAsync();
            return View(c);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, string name, string? contactName, string? contactEmail, string? contactPhone,
            string plan, int maxEmployees, string[]? modules, DateTime? trialEndsAt, DateTime? subscriptionEndsAt, string status, string? notes)
        {
            var c = await _db.Companies.FirstOrDefaultAsync(x => x.Id == id);
            if (c == null) return NotFound();
            c.Name = string.IsNullOrWhiteSpace(name) ? c.Name : name.Trim();
            c.ContactName = contactName; c.ContactEmail = contactEmail; c.ContactPhone = contactPhone;
            c.Plan = Plans.Find(plan)?.Name ?? c.Plan;
            c.MaxEmployees = Math.Max(0, maxEmployees);
            c.Modules = string.Join(",", (modules ?? Array.Empty<string>()).Where(m => Modules.All.Any(a => a.Key == m)));
            c.TrialEndsAt = trialEndsAt.HasValue ? DateTime.SpecifyKind(trialEndsAt.Value.Date.AddDays(1).AddSeconds(-1), DateTimeKind.Utc) : null;
            c.SubscriptionEndsAt = subscriptionEndsAt.HasValue ? DateTime.SpecifyKind(subscriptionEndsAt.Value.Date.AddDays(1).AddSeconds(-1), DateTimeKind.Utc) : null;
            c.Status = status == "Suspended" ? "Suspended" : "Active";
            c.Notes = notes;
            await _db.SaveChangesAsync();
            _companies.Invalidate(id);
            _access.Invalidate(id);
            TempData["Success"] = $"{c.Name} updated.";
            return RedirectToAction(nameof(Edit), new { id });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> SetStatus(int id, string status)
        {
            var c = await _db.Companies.FirstOrDefaultAsync(x => x.Id == id);
            if (c == null) return NotFound();
            c.Status = status == "Suspended" ? "Suspended" : "Active";
            await _db.SaveChangesAsync();
            _companies.Invalidate(id);
            TempData["Success"] = $"{c.Name} is now {c.Status}.";
            return RedirectToAction(nameof(Index));
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Extend(int id, int days = 30)
        {
            var c = await _db.Companies.FirstOrDefaultAsync(x => x.Id == id);
            if (c == null) return NotFound();
            var from = (c.SubscriptionEndsAt ?? c.TrialEndsAt) is DateTime d && d > DateTime.UtcNow ? d : DateTime.UtcNow;
            if (c.Plan == "Trial") c.TrialEndsAt = from.AddDays(days); else c.SubscriptionEndsAt = from.AddDays(days);
            await _db.SaveChangesAsync();
            _companies.Invalidate(id);
            TempData["Success"] = $"{c.Name} extended by {days} days.";
            return RedirectToAction(nameof(Edit), new { id });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> ResetAdmin(int id)
        {
            var c = await _db.Companies.FirstOrDefaultAsync(x => x.Id == id);
            if (c == null) return NotFound();
            var admin = await _db.Employees.IgnoreQueryFilters()
                .Where(e => EF.Property<int>(e, AppDbContext.TenantColumn) == id && e.Role == "admin" && e.IsActive)
                .OrderBy(e => e.Id).FirstOrDefaultAsync();
            if (admin == null) { TempData["Error"] = "This company has no active admin."; return RedirectToAction(nameof(Edit), new { id }); }
            var pwd = PasswordPolicy.Generate();
            admin.PasswordHash = BCrypt.Net.BCrypt.HashPassword(pwd);
            admin.MustChangePassword = true;
            admin.LockoutUntil = null; admin.FailedLoginCount = 0;
            _db.Audit("PasswordReset", $"Provider reset the password of admin {admin.EmpCode}", "Employee", admin.Id.ToString(), id, null, "HRMS provider");
            await _db.SaveChangesAsync();
            TempData["Success"] = $"New password for {admin.EmpCode} ({admin.Name}): {pwd} — they'll be asked to change it at login.";
            return RedirectToAction(nameof(Edit), new { id });
        }

        // ── Support login ("open as admin") ──────────────────────────────
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Impersonate(int id)
        {
            var c = await _db.Companies.FirstOrDefaultAsync(x => x.Id == id);
            if (c == null) return NotFound();
            var admin = await _db.Employees.IgnoreQueryFilters()
                .Where(e => EF.Property<int>(e, AppDbContext.TenantColumn) == id && e.Role == "admin" && e.IsActive)
                .OrderBy(e => e.Id).FirstOrDefaultAsync();
            if (admin == null) { TempData["Error"] = "This company has no active admin user to open."; return RedirectToAction(nameof(Index)); }

            var owner = User.Identity?.Name ?? "Provider";
            _db.Audit("SupportLogin", $"HRMS provider ({owner}) opened the company as {admin.Name} for support", "Company", id.ToString(), id, null, owner);
            await _db.SaveChangesAsync();

            var principal = AccountController.BuildPrincipal(admin, c, impersonator: owner);
            ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim(ImpersonatorIdClaim, OwnerId.ToString()));
            await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal,
                new AuthenticationProperties { IsPersistent = false, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(2) });
            return Redirect("/");
        }

        [AllowAnonymous]
        public async Task<IActionResult> ExitImpersonation()
        {
            var ownerId = int.TryParse(User.FindFirst(ImpersonatorIdClaim)?.Value, out var oid) ? oid : 0;
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            var owner = ownerId > 0 ? await _db.PlatformUsers.FirstOrDefaultAsync(u => u.Id == ownerId && u.IsActive) : null;
            if (owner == null) return RedirectToAction(nameof(Login));
            await SignInOwnerAsync(owner);
            return RedirectToAction(nameof(Index));
        }
    }
}
