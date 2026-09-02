using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using AmpmHrmsPro.Data;
using AmpmHrmsPro.Services;

namespace AmpmHrmsPro.Controllers
{
    // Employee self-service — read-only OT ledger for Worker-category.
    // Hard-scoped to CurrentEmpId so a logged-in worker can only see
    // their own OT, never another employee's.
    [Authorize]
    public class MyOTController : Controller
    {
        readonly AppDbContext _db;
        public MyOTController(AppDbContext db) => _db = db;

        int CurrentEmpId => int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);

        public async Task<IActionResult> Index()
        {
            var ledger = await OTLedgerEngine.GetLedgerAsync(_db, CurrentEmpId);
            int totalPending  = ledger.Where(l => l.Status == "Pending").Sum(l => l.OTMinutes);
            int totalApproved = ledger.Where(l => l.Status == "Approved").Sum(l => l.OTMinutes);
            int totalPaid     = ledger.Where(l => l.Status == "Paid").Sum(l => l.OTMinutes);
            ViewBag.TotalPending  = totalPending;
            ViewBag.TotalApproved = totalApproved;
            ViewBag.TotalPaid     = totalPaid;
            return View(ledger);
        }
    }
}
