using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using AmpmHrmsPro.Data;
using AmpmHrmsPro.Models;
using AmpmHrmsPro.Services;

namespace AmpmHrmsPro.Controllers
{
    // ═══════════════════════════════════════════
    // COMP-OFF RULE — customizable compensatory-off engine. Three screens:
    //
    //   Rules   — define named rules (earn thresholds, which off-days
    //             count, auto vs manual, expiry days, optional balance cap).
    //   Assign  — the three assignment methods explicitly asked for
    //             (Category-wise / Grade-wise / single-Employee-wise), all
    //             of which just set Employee.CompOffRuleId — see
    //             Models/CompOffModels.cs's header comment for why there's
    //             no separate precedence layer.
    //   Ledger  — per-employee earned/used/expired history, plus a Manual
    //             Credit action for off-attendance-record instances and a
    //             Cancel action to correct a mistaken entry.
    //
    // Consumption (spending an earned credit) happens automatically when a
    // Comp-Off leave Application is approved — see ApplicationsController /
    // MobileManagerController's Approve actions, both of which call
    // CompOffEngine.TryConsumeAsync. Nothing here writes UsedDays directly.
    // ═══════════════════════════════════════════
    [Authorize(Roles = "admin,hr")]
    public class CompOffController : Controller
    {
        readonly AppDbContext _db;
        public CompOffController(AppDbContext db) => _db = db;

        int CurrentEmpId => int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);

        // ═══════════════════════════════════════════
        // RULES
        // ═══════════════════════════════════════════
        public IActionResult Rules()
        {
            return View(_db.CompOffRules.OrderBy(r => r.Name).ToList());
        }

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult SaveRule(CompOffRule model)
        {
            model.Name = (model.Name ?? "").Trim();
            if (string.IsNullOrWhiteSpace(model.Name))
            { TempData["Error"] = "Rule name is required."; return RedirectToAction("Rules"); }
            if (model.ExpiryDays <= 0)
            { TempData["Error"] = "Expiry (days) must be greater than zero."; return RedirectToAction("Rules"); }
            if (model.MinHoursForFullDay <= 0)
            { TempData["Error"] = "Full-day hours threshold must be greater than zero."; return RedirectToAction("Rules"); }
            if (model.MinHoursForHalfDay > 0 && model.MinHoursForHalfDay >= model.MinHoursForFullDay)
            { TempData["Error"] = "Half-day hours threshold must be less than the full-day threshold."; return RedirectToAction("Rules"); }
            if (!model.CountHolidays && !model.CountWeekOffs)
            { TempData["Error"] = "At least one of Holidays / Week-Offs must count for this rule to ever earn anything."; return RedirectToAction("Rules"); }

            if (model.Id > 0)
            {
                var existing = _db.CompOffRules.Find(model.Id);
                if (existing == null) return NotFound();
                if (_db.CompOffRules.Any(r => r.Id != model.Id && r.Name == model.Name))
                { TempData["Error"] = $"A rule named '{model.Name}' already exists."; return RedirectToAction("Rules"); }
                existing.Name = model.Name;
                existing.Description = model.Description;
                existing.MinHoursForFullDay = model.MinHoursForFullDay;
                existing.MinHoursForHalfDay = model.MinHoursForHalfDay;
                existing.CountHolidays = model.CountHolidays;
                existing.CountWeekOffs = model.CountWeekOffs;
                existing.AutoCredit = model.AutoCredit;
                existing.ExpiryDays = model.ExpiryDays;
                existing.MaxOpenBalance = model.MaxOpenBalance;
                existing.IsActive = model.IsActive;
            }
            else
            {
                if (_db.CompOffRules.Any(r => r.Name == model.Name))
                { TempData["Error"] = $"A rule named '{model.Name}' already exists."; return RedirectToAction("Rules"); }
                model.Id = 0; model.IsActive = true; model.CreatedAt = DateTime.Now;
                _db.CompOffRules.Add(model);
            }
            _db.SaveChanges();
            TempData["Success"] = "Comp-Off rule saved.";
            return RedirectToAction("Rules");
        }

        [HttpPost]
        public IActionResult ToggleRule(int id)
        {
            var r = _db.CompOffRules.Find(id);
            if (r == null) return Json(new { success = false });
            r.IsActive = !r.IsActive;
            _db.SaveChanges();
            return Json(new { success = true, isActive = r.IsActive });
        }

        [HttpPost]
        public IActionResult DeleteRule(int id)
        {
            var r = _db.CompOffRules.Find(id);
            if (r == null) return Json(new { success = false });
            bool inUse = _db.Employees.Any(e => e.CompOffRuleId == id) || _db.CompOffLedgers.Any(l => l.CompOffRuleId == id);
            if (inUse) { r.IsActive = false; _db.SaveChanges(); return Json(new { success = true, message = "In use — deactivated instead of deleted." }); }
            _db.CompOffRules.Remove(r);
            _db.SaveChanges();
            return Json(new { success = true, message = "Rule deleted." });
        }

        // ═══════════════════════════════════════════
        // ASSIGN — Category-wise / Grade-wise / Employee-wise, all setting
        // the same Employee.CompOffRuleId. Whichever runs last for a given
        // employee wins — that's the "sirf ek level" behaviour asked for,
        // no automatic precedence between the three methods.
        // ═══════════════════════════════════════════
        public IActionResult Assign()
        {
            ViewBag.RuleList = _db.CompOffRules.Where(r => r.IsActive).OrderBy(r => r.Name).ToList();
            ViewBag.GradeList = _db.Grades.Where(g => g.IsActive).OrderBy(g => g.Name).ToList();
            ViewBag.CategoryList = _db.Employees.Where(e => e.IsActive && e.Category != null && e.Category != "")
                .Select(e => e.Category!).Distinct().OrderBy(c => c).ToList();
            ViewBag.EmployeeList = _db.Employees.Include(e => e.CompOffRule).Include(e => e.Grade)
                .Where(e => e.IsActive).OrderBy(e => e.Name).ToList();
            return View();
        }

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult AssignByCategory(string category, int? ruleId)
        {
            if (string.IsNullOrWhiteSpace(category)) { TempData["Error"] = "Select a Category."; return RedirectToAction("Assign"); }
            // Validate ruleId — a forged/stale POST could send a non-existent or inactive rule ID.
            // null is valid (means "clear"), but any non-null value must refer to an active rule.
            if (ruleId.HasValue && !_db.CompOffRules.Any(r => r.Id == ruleId && r.IsActive))
            { TempData["Error"] = "Selected Comp-Off rule not found or inactive."; return RedirectToAction("Assign"); }
            var employees = _db.Employees.Where(e => e.IsActive && e.Category == category).ToList();
            foreach (var e in employees) e.CompOffRuleId = ruleId;
            _db.SaveChanges();
            TempData["Success"] = $"Comp-Off rule {(ruleId.HasValue ? "assigned to" : "cleared for")} {employees.Count} employee(s) in Category '{category}'.";
            return RedirectToAction("Assign");
        }

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult AssignByGrade(int gradeId, int? ruleId)
        {
            var grade = _db.Grades.Find(gradeId);
            if (grade == null) { TempData["Error"] = "Select a Grade."; return RedirectToAction("Assign"); }
            if (ruleId.HasValue && !_db.CompOffRules.Any(r => r.Id == ruleId && r.IsActive))
            { TempData["Error"] = "Selected Comp-Off rule not found or inactive."; return RedirectToAction("Assign"); }
            var employees = _db.Employees.Where(e => e.IsActive && e.GradeId == gradeId).ToList();
            foreach (var e in employees) e.CompOffRuleId = ruleId;
            _db.SaveChanges();
            TempData["Success"] = $"Comp-Off rule {(ruleId.HasValue ? "assigned to" : "cleared for")} {employees.Count} employee(s) in Grade '{grade.Name}'.";
            return RedirectToAction("Assign");
        }

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult AssignByEmployee(int employeeId, int? ruleId)
        {
            var emp = _db.Employees.Find(employeeId);
            if (emp == null) { TempData["Error"] = "Select an Employee."; return RedirectToAction("Assign"); }
            if (ruleId.HasValue && !_db.CompOffRules.Any(r => r.Id == ruleId && r.IsActive))
            { TempData["Error"] = "Selected Comp-Off rule not found or inactive."; return RedirectToAction("Assign"); }
            emp.CompOffRuleId = ruleId;
            _db.SaveChanges();
            TempData["Success"] = $"Comp-Off rule {(ruleId.HasValue ? "assigned to" : "cleared for")} '{emp.Name}'.";
            return RedirectToAction("Assign");
        }

        // ═══════════════════════════════════════════
        // LEDGER
        // ═══════════════════════════════════════════
        public async Task<IActionResult> Ledger(int? employeeId)
        {
            ViewBag.EmployeeList = _db.Employees.Where(e => e.IsActive).OrderBy(e => e.Name).ToList();
            ViewBag.RuleList = _db.CompOffRules.OrderBy(r => r.Name).ToList();
            ViewBag.SelectedEmployeeId = employeeId;

            if (employeeId.HasValue)
            {
                ViewBag.Employee = _db.Employees.Include(e => e.CompOffRule).FirstOrDefault(e => e.Id == employeeId);
                ViewBag.Balance = await CompOffEngine.GetAvailableBalanceAsync(_db, employeeId.Value);
                var ledger = await CompOffEngine.GetLedgerAsync(_db, employeeId.Value);
                return View(ledger);
            }

            // No employee selected — show a company-wide balance summary
            // instead of an empty screen, so this doubles as an overview.
            // A plain class (not a ValueTuple) travels through ViewBag here
            // deliberately — a ValueTuple round-tripped through the dynamic
            // ViewBag has bitten this codebase before (element names don't
            // survive it reliably); a named class has no such gotcha.
            //
            // N+1 fix: run expiry sweep + balance aggregation in ONE query across
            // all employees rather than calling GetAvailableBalanceAsync (which
            // issues its own SweepAsync + SELECT per employee). We do a
            // single-pass sweep write then a single aggregate read.
            var employees = _db.Employees.Where(e => e.IsActive && e.CompOffRuleId != null)
                .Include(e => e.CompOffRule).Include(e => e.Department).OrderBy(e => e.Name).ToList();
            if (employees.Any())
            {
                string today = DateTime.Today.ToString("yyyy-MM-dd");
                var ids = employees.Select(e => e.Id).ToHashSet();
                // Batch sweep: recompute Status for all relevant rows in one round-trip
                var allRows = await _db.CompOffLedgers
                    .Where(l => ids.Contains(l.EmployeeId) && l.Status != "Cancelled")
                    .ToListAsync();
                bool swept = false;
                foreach (var l in allRows)
                {
                    string next = l.EarnedDays - l.UsedDays <= 0 ? "Used"
                        : string.Compare(l.ExpiryDate, today) < 0 ? "Expired"
                        : "Available";
                    if (l.Status != next) { l.Status = next; swept = true; }
                }
                if (swept) await _db.SaveChangesAsync();
                // Batch aggregate: sum Available balance per employee in one query
                var balances = allRows
                    .Where(l => l.Status == "Available")
                    .GroupBy(l => l.EmployeeId)
                    .ToDictionary(g => g.Key, g => g.Sum(l => l.EarnedDays - l.UsedDays));
                var summary = employees.Select(e =>
                    new CompOffBalanceRow { Employee = e, Balance = balances.GetValueOrDefault(e.Id, 0m) }).ToList();
                ViewBag.Summary = summary;
            }
            else
            {
                ViewBag.Summary = new List<CompOffBalanceRow>();
            }
            return View(new List<CompOffLedger>());
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> ManualCredit(int employeeId, int? ruleId, string earnedDate, decimal earnedDays, string? remarks)
        {
            var emp = _db.Employees.Find(employeeId);
            if (emp == null) { TempData["Error"] = "Select an Employee."; return RedirectToAction("Ledger"); }
            if (!DateTime.TryParse(earnedDate, out _)) { TempData["Error"] = "Enter a valid date."; return RedirectToAction("Ledger", new { employeeId }); }
            if (earnedDays <= 0) { TempData["Error"] = "Days must be greater than zero."; return RedirectToAction("Ledger", new { employeeId }); }

            await CompOffEngine.ManualCreditAsync(_db, employeeId, ruleId ?? emp.CompOffRuleId, earnedDate, earnedDays, remarks, CurrentEmpId);
            TempData["Success"] = $"Manually credited {earnedDays:0.#} Comp-Off day(s) to '{emp.Name}'.";
            return RedirectToAction("Ledger", new { employeeId });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> CancelLedgerEntry(int id, int employeeId)
        {
            var l = _db.CompOffLedgers.Find(id);
            if (l == null) return NotFound();
            // Ownership guard — a crafted POST with mismatched id/employeeId
            // must not silently cancel another employee's credit.
            if (l.EmployeeId != employeeId) return NotFound();
            if (l.UsedDays > 0)
            { TempData["Error"] = "Can't cancel — this credit has already been (partly) used. Revoke the leave application that used it instead."; return RedirectToAction("Ledger", new { employeeId }); }
            l.Status = "Cancelled";
            l.Remarks = (l.Remarks ?? "") + " [cancelled by admin]";
            await _db.SaveChangesAsync();
            TempData["Success"] = "Ledger entry cancelled.";
            return RedirectToAction("Ledger", new { employeeId });
        }
    }
}
