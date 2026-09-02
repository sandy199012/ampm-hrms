using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AmpmHrmsPro.Data;
using AmpmHrmsPro.Models;

namespace AmpmHrmsPro.Controllers
{
    [Authorize]
    public class MyLeaveController : Controller
    {
        private readonly AppDbContext _db;
        public MyLeaveController(AppDbContext db) => _db = db;

        public async Task<IActionResult> Index(int year = 0)
        {
            if (year == 0) year = DateTime.Now.Year;

            var empCode = User.Identity?.Name ?? "";
            var employee = await _db.Employees
                .FirstOrDefaultAsync(e => e.EmpCode == empCode || e.Email == empCode);

            if (employee == null)
                return RedirectToAction("Index", "Home");

            var balances = await _db.LeaveBalances
                .Where(b => b.EmployeeId == employee.Id && b.Year == year)
                .ToListAsync();

            ViewBag.Employee = employee;
            ViewBag.Year = year;
            ViewBag.Years = Enumerable.Range(DateTime.Now.Year - 2, 4).Reverse().ToList();
            return View(balances);
        }
    }
}
