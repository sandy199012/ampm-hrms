using Microsoft.AspNetCore.Mvc;
using AmpmHrmsPro.Services.Tenancy;

namespace AmpmHrmsPro.Controllers
{
    public class HomeController : Controller
    {
        private readonly IAccessService _access;
        public HomeController(IAccessService access) => _access = access;

        public async Task<IActionResult> Index()
        {
            if (User.Identity?.IsAuthenticated != true)
                return RedirectToAction("Login", "Account");
            if (User.IsPlatformAdmin()) return Redirect("/Platform");

            // HR dashboard for anyone allowed to see it (admin, hr, or a
            // custom role that includes it); self-service dashboard otherwise.
            var access = await _access.GetAsync(User);
            return access.Can(Perm.Dashboard)
                ? RedirectToAction("Index", "Admin")
                : RedirectToAction("Index", "EmployeeDashboard");
        }

        public IActionResult Error() => View();
    }
}
