using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using AmpmHrmsPro.Data;
using AmpmHrmsPro.Models;
using AmpmHrmsPro.Services;
using AmpmHrmsPro.Services.Tenancy;

namespace AmpmHrmsPro.Controllers.Api
{
    // ═══════════════════════════════════════════
    // MOBILE AUTH — the React Native app's counterpart to AccountController
    // (same EmpCode + BCrypt password check against the same Employee
    // table), just returning a JWT instead of setting a cookie. Every other
    // Mobile* controller requires this token via
    // [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)].
    // ═══════════════════════════════════════════
    [ApiController]
    [Route("api/mobile/auth")]
    public class MobileAuthController : ControllerBase
    {
        readonly AppDbContext _db;
        readonly IJwtService _jwt;
        readonly ITenantContext _tenant;
        public MobileAuthController(AppDbContext db, IJwtService jwt, ITenantContext tenant) { _db = db; _jwt = jwt; _tenant = tenant; }

        // CompanyCode is optional so the existing app keeps working: when it's
        // missing, the employee code must be unique across all companies.
        public record LoginRequest(string EmpCode, string Password, string? CompanyCode = null);

        [HttpPost("login")]
        public async Task<IActionResult> Login(LoginRequest req)
        {
            const string invalid = "Invalid company code, employee code or password.";
            var empCode = (req.EmpCode ?? "").Trim();
            var code = CompanyProvisioning.NormalizeCode(req.CompanyCode);
            Company? company;
            if (code.Length > 0)
                company = await _db.Companies.FirstOrDefaultAsync(c => c.Code == code);
            else
            {
                var ids = await _db.Employees.IgnoreQueryFilters()
                    .Where(e => e.EmpCode == empCode && e.IsActive)
                    .Select(e => EF.Property<int>(e, AppDbContext.TenantColumn)).Distinct().ToListAsync();
                if (ids.Count > 1) return Unauthorized(new { message = "Please enter your company code." });
                company = ids.Count == 1 ? await _db.Companies.FirstOrDefaultAsync(c => c.Id == ids[0]) : null;
            }
            if (company == null) return Unauthorized(new { message = invalid });
            var info = CompanyInfoService.From(company);
            if (!info.CanUse) return Unauthorized(new { message = info.IsSuspended ? "Company account suspended." : "Subscription expired." });
            if (!Modules.Parse(company.Modules).Contains(Modules.MobileApp))
                return Unauthorized(new { message = "The mobile app is not part of your company's plan." });

            _tenant.Use(company.Id);
            var emp = await _db.Employees.FirstOrDefaultAsync(e => e.EmpCode == empCode && e.IsActive);
            if (emp == null) return Unauthorized(new { message = invalid });
            if (emp.LockoutUntil.HasValue && emp.LockoutUntil > DateTime.UtcNow)
                return Unauthorized(new { message = "Account locked after too many wrong attempts. Try again later." });
            if (!BCrypt.Net.BCrypt.Verify(req.Password ?? "", emp.PasswordHash))
            {
                emp.FailedLoginCount++;
                if (company.MaxFailedLogins > 0 && emp.FailedLoginCount >= company.MaxFailedLogins)
                {
                    emp.LockoutUntil = DateTime.UtcNow.AddMinutes(Math.Max(1, company.LockoutMinutes));
                    emp.FailedLoginCount = 0;
                }
                _db.Audit("LoginFailed", $"Mobile: wrong password for {emp.EmpCode}", "Employee", emp.Id.ToString(), userId: emp.Id, userName: emp.Name);
                await _db.SaveChangesAsync();
                return Unauthorized(new { message = invalid });
            }
            emp.FailedLoginCount = 0;
            emp.LockoutUntil = null;
            emp.LastLoginAt = DateTime.UtcNow;
            _db.Audit("Login", $"Mobile: {emp.Name} ({emp.EmpCode}) signed in", "Employee", emp.Id.ToString(), userId: emp.Id, userName: emp.Name);
            await _db.SaveChangesAsync();

            // A manager's mobile dashboard also needs to know if they're a
            // Department Head (App.cs's escalation rule routes an
            // unassigned approval to whoever heads the applicant's
            // department) — the app uses isTeamLead to decide whether to
            // show the Manager tabs at all (role alone doesn't capture a
            // Department Head who logged in with role "employee").
            bool isTeamLead = emp.Role is "manager" or "admin" or "hr"
                || _db.Employees.Any(e => e.ReportingManagerId == emp.Id)
                || _db.Departments.Any(d => d.HeadEmployeeId == emp.Id);

            return Ok(new
            {
                token = _jwt.GenerateToken(emp, company),
                companyCode = company.Code,
                companyName = company.Name,
                employeeId = emp.Id,
                empCode = emp.EmpCode,
                name = emp.Name,
                role = emp.Role,
                isTeamLead,
                photoUrl = emp.PhotoUrl,
            });
        }

        [HttpGet("me")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        public IActionResult Me()
        {
            int empId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var emp = _db.Employees.FirstOrDefault(e => e.Id == empId);
            if (emp == null) return NotFound();

            bool isTeamLead = emp.Role is "manager" or "admin" or "hr"
                || _db.Employees.Any(e => e.ReportingManagerId == emp.Id)
                || _db.Departments.Any(d => d.HeadEmployeeId == emp.Id);

            return Ok(new
            {
                employeeId = emp.Id,
                empCode = emp.EmpCode,
                name = emp.Name,
                role = emp.Role,
                isTeamLead,
                photoUrl = emp.PhotoUrl,
            });
        }
    }
}
