using System.Globalization;
using System.Security.Claims;
using AmpmHrmsPro.Data;
using AmpmHrmsPro.Models;
using AmpmHrmsPro.Services;
using AmpmHrmsPro.Services.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;

namespace AmpmHrmsPro.Controllers
{
    // ═══════════════════════════════════════════
    // PAYROLL — Payroll > Run Payroll: pick a month, Generate (draft
    // payslips from salary structures + attendance), adjust any slip
    // (paid days, advance, income tax, arrears …), then Finalise so
    // employees can see and print it under My Payslips.
    // See Services/PayrollEngine.cs for the calculation rules.
    // ═══════════════════════════════════════════
    [Authorize]
    [RequirePermission(Perm.PayrollView, Perm.PayrollManage)]
    public class PayrollController : Controller
    {
        readonly AppDbContext _db;
        public PayrollController(AppDbContext db) => _db = db;

        public async Task<IActionResult> Index(int? year, int? month)
        {
            var today = IndiaTime.Today;
            var def = today.AddMonths(-1);
            int y = year ?? def.Year, m = month ?? def.Month;
            ViewBag.Year = y; ViewBag.Month = m;
            var run = await _db.PayrollRuns.FirstOrDefaultAsync(r => r.Year == y && r.Month == m);
            ViewBag.Run = run;
            var slips = run == null ? new List<Payslip>() : await _db.Payslips.Where(p => p.PayrollRunId == run.Id)
                .OrderBy(p => p.EmployeeName).ToListAsync();
            ViewBag.Runs = await _db.PayrollRuns.OrderByDescending(r => r.Year).ThenByDescending(r => r.Month).Take(24).ToListAsync();
            ViewBag.WithStructure = await _db.EmployeeSalaryStructures.Where(s => s.EffectiveTo == null).Select(s => s.EmployeeId).Distinct().CountAsync();
            ViewBag.ActiveEmployees = await _db.Employees.CountAsync(e => e.IsActive);
            return View(slips);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Generate(int year, int month)
        {
            if (month < 1 || month > 12 || year < 2000) return BadRequest();
            var run = await PayrollEngine.GetOrCreateRunAsync(_db, year, month);
            if (run.Status == "Finalised")
            {
                TempData["Error"] = "This month is already finalised. Reopen it first to make changes.";
                return RedirectToAction("Index", new { year, month });
            }
            var (done, none) = await PayrollEngine.GenerateAsync(_db, run);
            TempData["Success"] = $"{done} payslip(s) generated for {MonthName(year, month)}."
                + (none.Count > 0 ? $" {none.Count} active employee(s) have no salary structure yet and were skipped." : "");
            if (none.Count > 0) TempData["NoStructure"] = string.Join(", ", none.Take(60)) + (none.Count > 60 ? $" … and {none.Count - 60} more" : "");
            return RedirectToAction("Index", new { year, month });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Finalise(int year, int month)
        {
            var run = await _db.PayrollRuns.FirstOrDefaultAsync(r => r.Year == year && r.Month == month);
            if (run == null) return NotFound();
            run.Status = "Finalised"; run.FinalisedAt = DateTime.UtcNow; run.FinalisedBy = User.Identity?.Name;
            await _db.SaveChangesAsync();
            TempData["Success"] = $"{MonthName(year, month)} payroll finalised — employees can now see their payslips.";
            return RedirectToAction("Index", new { year, month });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Reopen(int year, int month)
        {
            var run = await _db.PayrollRuns.FirstOrDefaultAsync(r => r.Year == year && r.Month == month);
            if (run == null) return NotFound();
            run.Status = "Draft"; run.FinalisedAt = null;
            await _db.SaveChangesAsync();
            TempData["Success"] = $"{MonthName(year, month)} reopened as draft — employees can't see these payslips until you finalise again.";
            return RedirectToAction("Index", new { year, month });
        }

        public async Task<IActionResult> Edit(int id)
        {
            var slip = await _db.Payslips.Include(p => p.Lines).Include(p => p.PayrollRun).FirstOrDefaultAsync(p => p.Id == id);
            if (slip == null) return NotFound();
            return View(slip);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, decimal? paidDaysOverride, string? remarks, List<int> lineId, List<decimal> lineAmount, List<int>? manualIds, string? reset)
        {
            var slip = await _db.Payslips.Include(p => p.Lines).Include(p => p.PayrollRun).FirstOrDefaultAsync(p => p.Id == id);
            if (slip == null) return NotFound();
            if (slip.PayrollRun?.Status == "Finalised") { TempData["Error"] = "This month is finalised — reopen it to edit."; return RedirectToAction("Edit", new { id }); }

            if (reset == "1")
            {
                foreach (var l in slip.Lines) l.IsManual = false;
                slip.PaidDaysOverride = null;
            }
            else
            {
                slip.PaidDaysOverride = paidDaysOverride.HasValue && paidDaysOverride.Value >= 0 && paidDaysOverride.Value <= slip.DaysInMonth ? paidDaysOverride : null;
                var manual = (manualIds ?? new()).ToHashSet();
                for (int i = 0; i < lineId.Count && i < lineAmount.Count; i++)
                {
                    var l = slip.Lines.FirstOrDefault(x => x.Id == lineId[i]);
                    if (l == null) continue;
                    if (manual.Contains(l.Id)) { l.IsManual = true; l.Amount = Math.Max(0, Math.Round(lineAmount[i], 2)); }
                    else l.IsManual = false;
                }
            }
            slip.Remarks = string.IsNullOrWhiteSpace(remarks) ? null : remarks.Trim()[..Math.Min(300, remarks.Trim().Length)];
            PayrollEngine.Recalculate(slip);
            await _db.SaveChangesAsync();
            TempData["Success"] = $"Payslip of {slip.EmployeeName} updated — net pay ₹{slip.NetPay:N0}.";
            return RedirectToAction("Edit", new { id });
        }

        public async Task<IActionResult> Slip(int id)
        {
            var slip = await _db.Payslips.Include(p => p.Lines).FirstOrDefaultAsync(p => p.Id == id);
            if (slip == null) return NotFound();
            return await SlipView(_db, slip, this);
        }

        public async Task<IActionResult> PrintAll(int year, int month)
        {
            var run = await _db.PayrollRuns.FirstOrDefaultAsync(r => r.Year == year && r.Month == month);
            if (run == null) return NotFound();
            var slips = await _db.Payslips.Include(p => p.Lines).Where(p => p.PayrollRunId == run.Id).OrderBy(p => p.EmployeeName).ToListAsync();
            await FillCompany(_db, ViewData, HttpContext);
            return View("Slip", slips);
        }

        internal static async Task<IActionResult> SlipView(AppDbContext db, Payslip slip, Controller c)
        {
            await FillCompany(db, c.ViewData, c.HttpContext);
            return c.View("~/Views/Payroll/Slip.cshtml", new List<Payslip> { slip });
        }

        internal static async Task FillCompany(AppDbContext db, ViewDataDictionary vd, HttpContext http)
        {
            var co = await db.Companies.AsNoTracking().FirstOrDefaultAsync(x => x.Id == db.CurrentCompanyId);
            vd["CoName"] = (co?.LegalName ?? co?.Name ?? "").ToUpperInvariant();
            vd["CoAddress"] = co?.Address ?? "";
            var env = http.RequestServices.GetRequiredService<IWebHostEnvironment>();
            var custom = $"img/payslip-logo-{co?.Code}.png";
            vd["CoLogo"] = env.WebRootFileProvider.GetFileInfo(custom).Exists ? "/" + custom : co?.LogoDataUrl;
        }

        internal static string MonthName(int y, int m) => new DateTime(y, m, 1).ToString("MMMM yyyy", CultureInfo.InvariantCulture);
    }

    // ── Employee self-service: My Payslips (finalised months only) ──
    [Authorize]
    [RequireModule(Modules.Payroll)]
    public class MyPayslipsController : Controller
    {
        readonly AppDbContext _db;
        public MyPayslipsController(AppDbContext db) => _db = db;
        int CurrentEmpId => int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);

        public async Task<IActionResult> Index()
        {
            var slips = await _db.Payslips.Include(p => p.PayrollRun)
                .Where(p => p.EmployeeId == CurrentEmpId && p.PayrollRun!.Status == "Finalised")
                .OrderByDescending(p => p.Year).ThenByDescending(p => p.Month).ToListAsync();
            return View("~/Views/Payroll/My.cshtml", slips);
        }

        public async Task<IActionResult> Slip(int id)
        {
            var slip = await _db.Payslips.Include(p => p.Lines).Include(p => p.PayrollRun)
                .FirstOrDefaultAsync(p => p.Id == id && p.EmployeeId == CurrentEmpId && p.PayrollRun!.Status == "Finalised");
            if (slip == null) return NotFound();
            return await PayrollController.SlipView(_db, slip, this);
        }
    }
}
