using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using AmpmHrmsPro.Data;
using AmpmHrmsPro.Services;

namespace AmpmHrmsPro.Controllers
{
    // ═══════════════════════════════════════════
    // MY COMP-OFF — employee self-service view of their own Comp-Off
    // balance and earn/use/expire history. [Authorize] only, hard-scoped to
    // CurrentEmpId, same self-service convention as MyTaxController (see its
    // header comment) — no posted employeeId is ever trusted here.
    // ═══════════════════════════════════════════
    [Authorize]
    public class MyCompOffController : Controller
    {
        readonly AppDbContext _db;
        public MyCompOffController(AppDbContext db) => _db = db;

        int CurrentEmpId => int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);

        public async Task<IActionResult> Index()
        {
            var emp = _db.Employees.Include(e => e.CompOffRule).FirstOrDefault(e => e.Id == CurrentEmpId);
            if (emp == null) return NotFound();
            ViewBag.Employee = emp;
            ViewBag.RuleName = emp.CompOffRule?.Name;
            ViewBag.Balance = await CompOffEngine.GetAvailableBalanceAsync(_db, CurrentEmpId);
            var ledger = await CompOffEngine.GetLedgerAsync(_db, CurrentEmpId);
            return View(ledger);
        }
    }
}
