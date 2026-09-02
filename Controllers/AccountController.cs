using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using AmpmHrmsPro.Data;

namespace AmpmHrmsPro.Controllers
{
    public class AccountController : Controller
    {
        private readonly AppDbContext _db;
        public AccountController(AppDbContext db) => _db = db;

        [HttpGet]
        public IActionResult Login()
        {
            if (User.Identity?.IsAuthenticated == true)
            {
                var role = User.FindFirst(ClaimTypes.Role)?.Value;
                bool isAdminOrHr = role == "admin" || role == "hr";
                return RedirectToAction(isAdminOrHr ? "Index" : "Salary", isAdminOrHr ? "Admin" : "MyTax");
            }
            return View();
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Login(string empCode, string password)
        {
            var emp = _db.Employees.FirstOrDefault(e => e.EmpCode == empCode && e.IsActive);
            if (emp == null || !BCrypt.Net.BCrypt.Verify(password, emp.PasswordHash))
            {
                ViewBag.Error = "Invalid employee code or password.";
                return View();
            }

            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, emp.Id.ToString()),
                new(ClaimTypes.Name, emp.Name),
                new(ClaimTypes.Role, emp.Role),
                new("EmpCode", emp.EmpCode)
            };
            var identity  = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            var principal = new ClaimsPrincipal(identity);
            await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal,
                new AuthenticationProperties { IsPersistent = true, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8) });

            // Admin/HR land on the main dashboard (AdminController is
            // [Authorize(Roles="admin,hr")] — anyone else would just hit
            // AccessDenied there). Every other role (employee, manager)
            // lands on the new self-service "My Salary" page instead — the
            // only web-side landing spot they actually have access to;
            // manager/employee self-service beyond that still lives in the
            // mobile apps, same as before this feature existed.
            bool isAdminOrHr = emp.Role == "admin" || emp.Role == "hr";
            return RedirectToAction(isAdminOrHr ? "Index" : "Salary", isAdminOrHr ? "Admin" : "MyTax");
        }

        public async Task<IActionResult> Logout()
        {
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return RedirectToAction("Login");
        }

        public IActionResult AccessDenied() => View();
    }
}
