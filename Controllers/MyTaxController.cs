using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using AmpmHrmsPro.Data;
using AmpmHrmsPro.Services;

namespace AmpmHrmsPro.Controllers
{
    // ═══════════════════════════════════════════
    // MY SALARY / MY TAX — the employee self-service side of the Salary
    // Structure + Income Tax module. This project didn't have an employee
    // self-service web area before (see ApplicationsController's header
    // comment) — everything else here is Admin/HR-only, and real
    // self-service elsewhere in this app happens through the two separate
    // mobile apps. Rather than extend either mobile codebase for this
    // (a much bigger, riskier lift — see the Kiosk app's build history),
    // this is a small new web area: [Authorize] alone (any logged-in
    // Employee.Role), every action hard-scoped to the caller's own
    // CurrentEmpId from the claim, never a posted employeeId, so there is
    // no way for one employee to view or edit another's declaration from
    // here. Mutation logic itself is shared with TaxController's "fill on
    // an employee's behalf" admin actions via Services/TaxDeclarationHelper
    // so both surfaces always behave identically.
    // ═══════════════════════════════════════════
    [Authorize]
    public class MyTaxController : Controller
    {
        readonly AppDbContext _db;
        readonly IPayrollTaxEngine _engine;
        public MyTaxController(AppDbContext db, IPayrollTaxEngine engine) { _db = db; _engine = engine; }

        int CurrentEmpId => int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);

        public IActionResult Salary()
        {
            var emp = _db.Employees.Find(CurrentEmpId);
            if (emp == null) return NotFound();
            ViewBag.Employee = emp;
            ViewBag.Breakdown = _engine.ComputeSalaryBreakdown(CurrentEmpId);
            return View();
        }

        public IActionResult Declaration(string? financialYear)
        {
            var fy = string.IsNullOrWhiteSpace(financialYear) ? _engine.CurrentFinancialYear() : financialYear;
            var header = TaxDeclarationHelper.GetOrCreateHeader(_db, CurrentEmpId, fy);

            ViewBag.IsAdminView = false;
            ViewBag.Employee = _db.Employees.Find(CurrentEmpId);
            ViewBag.FinancialYear = fy;
            ViewBag.Sections = _db.TaxSectionMasters.Where(s => s.IsActive).OrderBy(s => s.DisplayOrder).ToList();
            ViewBag.TaxResult = _engine.ComputeTax(CurrentEmpId, fy);
            return View("~/Views/Tax/Declaration.cshtml", header);
        }

        // SaveHeader/SaveItem never trust a posted headerId — they resolve
        // (and lazily create, on this first real write) the header purely
        // from CurrentEmpId + financialYear via TaxDeclarationHelper's
        // EnsureHeaderId, so there's nothing for a crafted headerId to
        // redirect a write onto. This also means visiting the Declaration
        // page itself never creates a row — only an actual save does.
        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult SaveHeader(string financialYear, string regimeChoice, decimal annualRentPaid, bool isMetroCity)
        {
            var (ok, msg) = TaxDeclarationHelper.SaveHeaderFields(_db, CurrentEmpId, financialYear, regimeChoice, annualRentPaid, isMetroCity);
            TempData[ok ? "Success" : "Error"] = msg;
            return RedirectToAction("Declaration", new { financialYear });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult SaveItem(string financialYear, int sectionId, string? description, decimal declaredAmount, int? itemId)
        {
            var (ok, msg) = TaxDeclarationHelper.UpsertItem(_db, CurrentEmpId, financialYear, sectionId, description, declaredAmount, itemId);
            TempData[ok ? "Success" : "Error"] = msg;
            return RedirectToAction("Declaration", new { financialYear });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> UploadDocument(int itemId, int headerId, string financialYear, IFormFile? file)
        {
            var (ok, msg) = await TaxDeclarationHelper.UploadDocumentAsync(_db, itemId, GuardedHeaderId(headerId), file);
            TempData[ok ? "Success" : "Error"] = msg;
            return RedirectToAction("Declaration", new { financialYear });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult DeleteItem(int itemId, int headerId, string financialYear)
        {
            var (ok, msg) = TaxDeclarationHelper.DeleteItem(_db, itemId, GuardedHeaderId(headerId));
            TempData[ok ? "Success" : "Error"] = msg;
            return RedirectToAction("Declaration", new { financialYear });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult Submit(int headerId, string financialYear)
        {
            var (ok, msg) = TaxDeclarationHelper.Submit(_db, GuardedHeaderId(headerId));
            TempData[ok ? "Success" : "Error"] = msg;
            return RedirectToAction("Declaration", new { financialYear });
        }

        // Confirms the posted headerId actually belongs to the caller
        // before any TaxDeclarationHelper call touches it — without this,
        // a crafted request with someone else's headerId would let one
        // employee edit another's declaration despite the [Authorize]
        // check above (that only proves *someone* is logged in).
        int GuardedHeaderId(int headerId)
        {
            var belongsToCaller = _db.TaxDeclarationHeaders.Any(h => h.Id == headerId && h.EmployeeId == CurrentEmpId);
            return belongsToCaller ? headerId : 0; // 0 makes every TaxDeclarationHelper call below fail its own "not found" check
        }
    }
}
