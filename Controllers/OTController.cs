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
    // OT RULE — overtime management for Worker-category employees.
    // Mirrors the Comp-Off module structure:
    //   Rules   — define named OT rules (thresholds, rates, OT type).
    //   Assign  — Category / Grade / Employee-wise assignment (all write
    //             Employee.OTRuleId directly).
    //   Ledger  — per-employee OT history; approve, mark-paid, manual entry.
    // ═══════════════════════════════════════════
    [Authorize(Roles = "admin,hr")]
    public class OTController : Controller
    {
        readonly AppDbContext _db;
        public OTController(AppDbContext db) => _db = db;

        int CurrentEmpId => int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);

        // ═══════════════════════════════════════════
        // RULES
        // ═══════════════════════════════════════════
        public IActionResult Rules()
            => View(_db.OTRules.OrderBy(r => r.Name).ToList());

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult SaveRule(OTRule model)
        {
            model.Name = (model.Name ?? "").Trim();
            if (string.IsNullOrWhiteSpace(model.Name))
            { TempData["Error"] = "Rule name is required."; return RedirectToAction("Rules"); }
            if (model.MinOTMinutesPerDay < 0)
            { TempData["Error"] = "Minimum OT minutes must be zero or more."; return RedirectToAction("Rules"); }
            if (!model.CountAfterShiftHours && !model.CountHolidays && !model.CountWeekOffs)
            { TempData["Error"] = "At least one OT trigger must be enabled."; return RedirectToAction("Rules"); }
            if (model.NormalOTMultiplier <= 0 || model.HolidayOTMultiplier <= 0)
            { TempData["Error"] = "OT rate multipliers must be greater than zero."; return RedirectToAction("Rules"); }

            if (model.Id > 0)
            {
                var existing = _db.OTRules.Find(model.Id);
                if (existing == null) return NotFound();
                if (_db.OTRules.Any(r => r.Id != model.Id && r.Name == model.Name))
                { TempData["Error"] = $"A rule named '{model.Name}' already exists."; return RedirectToAction("Rules"); }
                existing.Name                 = model.Name;
                existing.Description          = model.Description;
                existing.OTType               = model.OTType;
                existing.CountAfterShiftHours = model.CountAfterShiftHours;
                existing.CountHolidays        = model.CountHolidays;
                existing.CountWeekOffs        = model.CountWeekOffs;
                existing.MinOTMinutesPerDay   = model.MinOTMinutesPerDay;
                existing.MaxOTMinutesPerDay   = model.MaxOTMinutesPerDay;
                existing.NormalOTMultiplier   = model.NormalOTMultiplier;
                existing.HolidayOTMultiplier  = model.HolidayOTMultiplier;
                existing.MinutesPerOTLeaveDay = model.MinutesPerOTLeaveDay;
                existing.UseSlabRounding      = model.UseSlabRounding;
                existing.IsRetailRule         = model.IsRetailRule;
                existing.IsActive             = model.IsActive;
            }
            else
            {
                if (_db.OTRules.Any(r => r.Name == model.Name))
                { TempData["Error"] = $"A rule named '{model.Name}' already exists."; return RedirectToAction("Rules"); }
                model.Id = 0; model.IsActive = true; model.CreatedAt = DateTime.Now;
                _db.OTRules.Add(model);
            }
            _db.SaveChanges();
            TempData["Success"] = "OT rule saved.";
            return RedirectToAction("Rules");
        }

        [HttpPost]
        public IActionResult ToggleRule(int id)
        {
            var r = _db.OTRules.Find(id);
            if (r == null) return Json(new { success = false });
            r.IsActive = !r.IsActive;
            _db.SaveChanges();
            return Json(new { success = true, isActive = r.IsActive });
        }

        [HttpPost]
        public IActionResult DeleteRule(int id)
        {
            var r = _db.OTRules.Find(id);
            if (r == null) return Json(new { success = false });
            bool inUse = _db.Employees.Any(e => e.OTRuleId == id) || _db.OTLedgers.Any(l => l.OTRuleId == id);
            if (inUse) { r.IsActive = false; _db.SaveChanges(); return Json(new { success = true, message = "In use — deactivated instead of deleted." }); }
            _db.OTRules.Remove(r);
            _db.SaveChanges();
            return Json(new { success = true, message = "Rule deleted." });
        }

        // ═══════════════════════════════════════════
        // ASSIGN
        // ═══════════════════════════════════════════
        public IActionResult Assign()
        {
            ViewBag.RuleList     = _db.OTRules.Where(r => r.IsActive).OrderBy(r => r.Name).ToList();
            ViewBag.GradeList    = _db.Grades.Where(g => g.IsActive).OrderBy(g => g.Name).ToList();
            ViewBag.CategoryList = _db.Employees.Where(e => e.IsActive && e.Category != null && e.Category != "")
                .Select(e => e.Category!).Distinct().OrderBy(c => c).ToList();
            ViewBag.EmployeeList = _db.Employees.Include(e => e.OTRule).Include(e => e.Grade)
                .Where(e => e.IsActive).OrderBy(e => e.Name).ToList();
            return View();
        }

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult AssignByCategory(string category, int? ruleId)
        {
            if (string.IsNullOrWhiteSpace(category)) { TempData["Error"] = "Select a Category."; return RedirectToAction("Assign"); }
            if (ruleId.HasValue && !_db.OTRules.Any(r => r.Id == ruleId && r.IsActive))
            { TempData["Error"] = "Selected OT rule not found or inactive."; return RedirectToAction("Assign"); }
            var employees = _db.Employees.Where(e => e.IsActive && e.Category == category).ToList();
            foreach (var e in employees) e.OTRuleId = ruleId;
            _db.SaveChanges();
            TempData["Success"] = $"OT rule {(ruleId.HasValue ? "assigned to" : "cleared for")} {employees.Count} employee(s) in Category '{category}'.";
            return RedirectToAction("Assign");
        }

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult AssignByGrade(int gradeId, int? ruleId)
        {
            var grade = _db.Grades.Find(gradeId);
            if (grade == null) { TempData["Error"] = "Select a Grade."; return RedirectToAction("Assign"); }
            if (ruleId.HasValue && !_db.OTRules.Any(r => r.Id == ruleId && r.IsActive))
            { TempData["Error"] = "Selected OT rule not found or inactive."; return RedirectToAction("Assign"); }
            var employees = _db.Employees.Where(e => e.IsActive && e.GradeId == gradeId).ToList();
            foreach (var e in employees) e.OTRuleId = ruleId;
            _db.SaveChanges();
            TempData["Success"] = $"OT rule {(ruleId.HasValue ? "assigned to" : "cleared for")} {employees.Count} employee(s) in Grade '{grade.Name}'.";
            return RedirectToAction("Assign");
        }

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult AssignByEmployee(int employeeId, int? ruleId)
        {
            var emp = _db.Employees.Find(employeeId);
            if (emp == null) { TempData["Error"] = "Select an Employee."; return RedirectToAction("Assign"); }
            if (ruleId.HasValue && !_db.OTRules.Any(r => r.Id == ruleId && r.IsActive))
            { TempData["Error"] = "Selected OT rule not found or inactive."; return RedirectToAction("Assign"); }
            emp.OTRuleId = ruleId;
            _db.SaveChanges();
            TempData["Success"] = $"OT rule {(ruleId.HasValue ? "assigned to" : "cleared for")} '{emp.Name}'.";
            return RedirectToAction("Assign");
        }

        // ═══════════════════════════════════════════
        // LEDGER
        // ═══════════════════════════════════════════
        public async Task<IActionResult> Ledger(int? employeeId, string? status)
        {
            ViewBag.EmployeeList = _db.Employees.Where(e => e.IsActive).OrderBy(e => e.Name).ToList();
            ViewBag.RuleList     = _db.OTRules.OrderBy(r => r.Name).ToList();
            ViewBag.SelectedEmployeeId = employeeId;
            ViewBag.SelectedStatus     = status;

            if (employeeId.HasValue)
            {
                ViewBag.Employee = _db.Employees.Include(e => e.OTRule).FirstOrDefault(e => e.Id == employeeId);
                var q = db.OTLedgers.Where(l => l.EmployeeId == employeeId);
                if (!string.IsNullOrWhiteSpace(status)) q = q.Where(l => l.Status == status);
                return View(await q.Include(l => l.OTRule).OrderByDescending(l => l.Date).ToListAsync());
            }

            // Company-wide summary (batch — avoids N+1)
            var employees = _db.Employees.Where(e => e.IsActive && e.OTRuleId != null)
                .Include(e => e.OTRule).Include(e => e.Department).OrderBy(e => e.Name).ToList();
            var ids = employees.Select(e => e.Id).ToHashSet();
            var grouped = await _db.OTLedgers
                .Where(l => ids.Contains(l.EmployeeId) && (l.Status == "Pending" || l.Status == "Approved"))
                .GroupBy(l => l.EmployeeId)
                .Select(g => new { EmployeeId = g.Key, TotalMinutes = g.Sum(l => l.OTMinutes) })
                .ToDictionaryAsync(g => g.EmployeeId, g => g.TotalMinutes);
            ViewBag.Summary = employees.Select(e =>
                new OTSummaryRow { Employee = e, OTMinutes = grouped.GetValueOrDefault(e.Id, 0) }).ToList();
            return View(new List<OTLedger>());
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> ManualOT(int employeeId, int? ruleId, string date, int otMinutes, string otKind, string? remarks)
        {
            var emp = _db.Employees.Find(employeeId);
            if (emp == null) { TempData["Error"] = "Select an Employee."; return RedirectToAction("Ledger"); }
            if (!DateTime.TryParse(date, out _)) { TempData["Error"] = "Enter a valid date."; return RedirectToAction("Ledger", new { employeeId }); }
            if (otMinutes <= 0) { TempData["Error"] = "OT minutes must be greater than zero."; return RedirectToAction("Ledger", new { employeeId }); }
            await OTLedgerEngine.ManualOTAsync(_db, employeeId, ruleId ?? emp.OTRuleId, date, otMinutes, otKind, remarks, CurrentEmpId);
            TempData["Success"] = $"Manually logged {OTLedgerEngine.FormatOTMinutes(otMinutes)} OT for '{emp.Name}'.";
            return RedirectToAction("Ledger", new { employeeId });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> ApproveOT(int id, int employeeId, string? remarks)
        {
            bool ok = await OTLedgerEngine.ApproveOTAsync(_db, id, remarks);
            TempData[ok ? "Success" : "Error"] = ok ? "OT approved." : "OT row not found or already processed.";
            return RedirectToAction("Ledger", new { employeeId });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> MarkPaid(int id, int employeeId)
        {
            bool ok = await OTLedgerEngine.MarkPaidAsync(_db, id);
            TempData[ok ? "Success" : "Error"] = ok ? "OT marked as Paid." : "OT row not found or not yet approved.";
            return RedirectToAction("Ledger", new { employeeId });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> CancelOT(int id, int employeeId)
        {
            var l = await _db.OTLedgers.FindAsync(id);
            if (l == null || l.EmployeeId != employeeId) return NotFound();
            if (l.Status == "Paid")
            { TempData["Error"] = "Can't cancel — this OT has already been paid."; return RedirectToAction("Ledger", new { employeeId }); }
            l.Status = "Cancelled";
            l.Remarks = (l.Remarks ?? "") + " [cancelled by admin]";
            await _db.SaveChangesAsync();
            TempData["Success"] = "OT entry cancelled.";
            return RedirectToAction("Ledger", new { employeeId });
        }

        // Convenience shorthand used inside the Ledger view
        private AppDbContext db => _db;
    }
}
