using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using AmpmHrmsPro.Data;
using AmpmHrmsPro.Services;

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
        public MobileAuthController(AppDbContext db, IJwtService jwt) { _db = db; _jwt = jwt; }

        public record LoginRequest(string EmpCode, string Password);

        [HttpPost("login")]
        public IActionResult Login(LoginRequest req)
        {
            var emp = _db.Employees.FirstOrDefault(e => e.EmpCode == req.EmpCode && e.IsActive);
            if (emp == null || !BCrypt.Net.BCrypt.Verify(req.Password, emp.PasswordHash))
                return Unauthorized(new { message = "Invalid employee code or password." });

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
                token = _jwt.GenerateToken(emp),
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
