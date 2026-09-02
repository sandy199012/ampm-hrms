using Microsoft.AspNetCore.Mvc;

namespace AmpmHrmsPro.Controllers
{
    public class HomeController : Controller
    {
        public IActionResult Index()
        {
            return User.Identity?.IsAuthenticated == true
                ? RedirectToAction("Index", "Admin")
                : RedirectToAction("Login", "Account");
        }

        public IActionResult Error() => View();
    }
}
