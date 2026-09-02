using Microsoft.AspNetCore.Mvc;

namespace AmpmHrmsPro.Controllers
{
    public class HomeController : Controller
    {
        public IActionResult Index()
        {
            if (User.Identity?.IsAuthenticated != true)
                return RedirectToAction("Login", "Account");

            var role = User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value ?? "employee";
            return (role == "admin" || role == "hr")
                ? RedirectToAction("Index", "Admin")
                : RedirectToAction("Index", "EmployeeDashboard");
        }

        public IActionResult Error() => View();
    }
}
