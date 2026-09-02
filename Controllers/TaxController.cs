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
    // INCOME TAX / TDS — Admin side. Slab Settings and Tax Sections are the
    // admin-editable master data PayrollTaxEngine reads (see that file's
    // header comment — nothing about tax law is hardcoded there). This
    // controller also lets Admin view or fill any employee's investment
    // declaration on their behalf and approve/reject individual declared
    // items, per the user's explicit requirement that Admin have the same
    // full rights an employee has over their own declaration. The employee
    // self-service side of the same data lives in MyTaxController — the
    // two share their actual save/upload/review logic via
    // Services/TaxDeclarationHelper.cs so behavior never drifts apart.
    // ═══════════════════════════════════════════
    [Authorize(Roles = "admin,hr")]
    public class TaxController : Controller
    {
        readonly AppDbContext _db;
        readonly IPayrollTaxEngine _engine;
        public TaxController(AppDbContext db, IPayrollTaxEngine engine) { _db = db; _engine = engine; }

        int CurrentEmpId => int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);

        // ── Tax Slab Settings ──
        public IActionResult SlabSettings()
        {
            return View(_db.TaxSlabSettingsList.Include(s => s.Slabs).Include(s => s.SurchargeSlabs)
                .OrderByDescending(s => s.FinancialYear).ThenBy(s => s.Regime).ToList());
        }

        public IActionResult SlabSettingsEdit(int id)
        {
            var settings = id > 0
                ? _db.TaxSlabSettingsList.Include(s => s.Slabs).Include(s => s.SurchargeSlabs).FirstOrDefault(s => s.Id == id)
                : new TaxSlabSettings { FinancialYear = _engine.CurrentFinancialYear() };
            if (id > 0 && settings == null) return NotFound();
            return View(settings);
        }

        public record SlabDto(decimal From, decimal? To, decimal Rate);

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult SaveSlabSettings(int id, string financialYear, string regime, decimal standardDeduction,
            decimal rebate87AIncomeLimit, decimal rebate87AMaxAmount, decimal cessPercent, string slabsJson, string surchargeJson)
        {
            financialYear = (financialYear ?? "").Trim();
            if (financialYear.Length > 10) financialYear = financialYear[..10];
            if (string.IsNullOrWhiteSpace(financialYear)) { TempData["Error"] = "Financial Year is required (e.g. 2026-27)."; return RedirectToAction("SlabSettingsEdit", new { id }); }
            if (regime != "Old" && regime != "New") { TempData["Error"] = "Regime must be Old or New."; return RedirectToAction("SlabSettingsEdit", new { id }); }

            List<SlabDto> slabs, surcharge;
            try
            {
                slabs = System.Text.Json.JsonSerializer.Deserialize<List<SlabDto>>(slabsJson ?? "[]") ?? new();
                surcharge = System.Text.Json.JsonSerializer.Deserialize<List<SlabDto>>(surchargeJson ?? "[]") ?? new();
            }
            catch { TempData["Error"] = "Couldn't read the slab rows — please try again."; return RedirectToAction("SlabSettingsEdit", new { id }); }
            if (!slabs.Any()) { TempData["Error"] = "Add at least one tax slab."; return RedirectToAction("SlabSettingsEdit", new { id }); }

            TaxSlabSettings settings;
            if (id > 0)
            {
                var existing = _db.TaxSlabSettingsList.Include(s => s.Slabs).Include(s => s.SurchargeSlabs).FirstOrDefault(s => s.Id == id);
                if (existing == null) return NotFound();
                if (_db.TaxSlabSettingsList.Any(s => s.Id != id && s.FinancialYear == financialYear && s.Regime == regime))
                { TempData["Error"] = $"FY {financialYear} / {regime} Regime already has settings."; return RedirectToAction("SlabSettingsEdit", new { id }); }
                existing.FinancialYear = financialYear; existing.Regime = regime;
                _db.TaxSlabs.RemoveRange(existing.Slabs); _db.TaxSurchargeSlabs.RemoveRange(existing.SurchargeSlabs);
                existing.Slabs = new List<TaxSlab>(); existing.SurchargeSlabs = new List<TaxSurchargeSlab>();
                settings = existing;
            }
            else
            {
                if (_db.TaxSlabSettingsList.Any(s => s.FinancialYear == financialYear && s.Regime == regime))
                { TempData["Error"] = $"FY {financialYear} / {regime} Regime already has settings — edit that row instead."; return RedirectToAction("SlabSettingsEdit", new { id }); }
                settings = new TaxSlabSettings { FinancialYear = financialYear, Regime = regime };
                _db.TaxSlabSettingsList.Add(settings);
            }
            settings.StandardDeduction = standardDeduction;
            settings.Rebate87AIncomeLimit = rebate87AIncomeLimit;
            settings.Rebate87AMaxAmount = rebate87AMaxAmount;
            settings.CessPercent = cessPercent;

            int o = 0; foreach (var s in slabs.OrderBy(s => s.From)) settings.Slabs.Add(new TaxSlab { FromAmount = s.From, ToAmount = s.To, RatePercent = s.Rate, DisplayOrder = o++ });
            o = 0; foreach (var s in surcharge.OrderBy(s => s.From)) settings.SurchargeSlabs.Add(new TaxSurchargeSlab { FromAmount = s.From, ToAmount = s.To, RatePercent = s.Rate, DisplayOrder = o++ });

            _db.SaveChanges();
            TempData["Success"] = "Tax slab settings saved.";
            return RedirectToAction("SlabSettings");
        }

        [HttpPost]
        public IActionResult ToggleSlabSettings(int id)
        {
            var s = _db.TaxSlabSettingsList.Find(id);
            if (s == null) return Json(new { success = false });
            s.IsActive = !s.IsActive;
            _db.SaveChanges();
            return Json(new { success = true, isActive = s.IsActive });
        }

        // ── Tax Section Master ──
        public IActionResult Sections()
        {
            return View(_db.TaxSectionMasters.OrderBy(s => s.DisplayOrder).ThenBy(s => s.Name).ToList());
        }

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult SaveSection(TaxSectionMaster model)
        {
            model.Code = (model.Code ?? "").Trim().ToUpper();
            model.Name = (model.Name ?? "").Trim();
            if (model.Code.Length > 20) model.Code = model.Code[..20];
            if (model.Name.Length > 120) model.Name = model.Name[..120];
            if ((model.Description ?? "").Length > 300) model.Description = model.Description![..300];
            if (string.IsNullOrWhiteSpace(model.Code) || string.IsNullOrWhiteSpace(model.Name))
            { TempData["Error"] = "Code and Name are required."; return RedirectToAction("Sections"); }

            if (model.Id > 0)
            {
                var existing = _db.TaxSectionMasters.Find(model.Id);
                if (existing == null) return NotFound();
                if (_db.TaxSectionMasters.Any(s => s.Id != model.Id && s.Code == model.Code)) { TempData["Error"] = $"Code '{model.Code}' already used."; return RedirectToAction("Sections"); }
                existing.Code = model.Code; existing.Name = model.Name; existing.Description = model.Description;
                existing.MaxLimit = model.MaxLimit; existing.ApplicableRegime = model.ApplicableRegime;
                existing.RequiresDocument = model.RequiresDocument; existing.DisplayOrder = model.DisplayOrder; existing.IsActive = model.IsActive;
            }
            else
            {
                if (_db.TaxSectionMasters.Any(s => s.Code == model.Code)) { TempData["Error"] = $"Code '{model.Code}' already exists."; return RedirectToAction("Sections"); }
                model.Id = 0; model.IsActive = true;
                _db.TaxSectionMasters.Add(model);
            }
            _db.SaveChanges();
            TempData["Success"] = "Tax section saved.";
            return RedirectToAction("Sections");
        }

        [HttpPost]
        public IActionResult ToggleSection(int id)
        {
            var s = _db.TaxSectionMasters.Find(id);
            if (s == null) return Json(new { success = false });
            s.IsActive = !s.IsActive;
            _db.SaveChanges();
            return Json(new { success = true, isActive = s.IsActive });
        }

        // ── Declarations — Admin can review any employee's, or fill one in
        // on their behalf, both through the same shared partial view used
        // by MyTaxController for the employee's own self-service page. ──
        public IActionResult Declarations(string? financialYear, string? status, int? departmentId)
        {
            var fy = string.IsNullOrWhiteSpace(financialYear) ? _engine.CurrentFinancialYear() : financialYear;
            var q = _db.TaxDeclarationHeaders.Include(h => h.Employee).ThenInclude(e => e!.Department)
                .Include(h => h.Items).Where(h => h.FinancialYear == fy);
            if (departmentId.HasValue) q = q.Where(h => h.Employee!.DepartmentId == departmentId);

            var headers = q.ToList();
            if (!string.IsNullOrWhiteSpace(status))
            {
                if (status == "PendingItems") headers = headers.Where(h => h.Items.Any(i => i.Status == "Pending")).ToList();
                else headers = headers.Where(h => h.Status == status).ToList();
            }

            ViewBag.FinancialYear = fy;
            ViewBag.Status = status;
            ViewBag.DepartmentId = departmentId;
            ViewBag.Departments = _db.Departments.Where(d => d.IsActive).OrderBy(d => d.Name).ToList();
            ViewBag.Employees = _db.Employees.Where(e => e.IsActive).OrderBy(e => e.Name).ToList();
            return View(headers.OrderBy(h => h.Employee!.Name).ToList());
        }

        public IActionResult Declaration(int employeeId, string? financialYear)
        {
            var emp = _db.Employees.Find(employeeId);
            if (emp == null) return NotFound();
            var fy = string.IsNullOrWhiteSpace(financialYear) ? _engine.CurrentFinancialYear() : financialYear;
            var header = TaxDeclarationHelper.GetOrCreateHeader(_db, employeeId, fy);

            ViewBag.IsAdminView = true;
            ViewBag.Employee = emp;
            ViewBag.FinancialYear = fy;
            ViewBag.Sections = _db.TaxSectionMasters.Where(s => s.IsActive).OrderBy(s => s.DisplayOrder).ToList();
            ViewBag.TaxResult = _engine.ComputeTax(employeeId, fy);
            return View("~/Views/Tax/Declaration.cshtml", header);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult SaveHeader(int employeeId, string financialYear, string regimeChoice, decimal annualRentPaid, bool isMetroCity)
        {
            var (ok, msg) = TaxDeclarationHelper.SaveHeaderFields(_db, employeeId, financialYear, regimeChoice, annualRentPaid, isMetroCity);
            TempData[ok ? "Success" : "Error"] = msg;
            return RedirectToAction("Declaration", new { employeeId, financialYear });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult SaveItem(int employeeId, string financialYear, int sectionId, string? description, decimal declaredAmount, int? itemId)
        {
            var (ok, msg) = TaxDeclarationHelper.UpsertItem(_db, employeeId, financialYear, sectionId, description, declaredAmount, itemId);
            TempData[ok ? "Success" : "Error"] = msg;
            return RedirectToAction("Declaration", new { employeeId, financialYear });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> UploadDocument(int itemId, int headerId, int employeeId, string financialYear, IFormFile? file)
        {
            var (ok, msg) = await TaxDeclarationHelper.UploadDocumentAsync(_db, itemId, headerId, file);
            TempData[ok ? "Success" : "Error"] = msg;
            return RedirectToAction("Declaration", new { employeeId, financialYear });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult DeleteItem(int itemId, int headerId, int employeeId, string financialYear)
        {
            var (ok, msg) = TaxDeclarationHelper.DeleteItem(_db, itemId, headerId);
            TempData[ok ? "Success" : "Error"] = msg;
            return RedirectToAction("Declaration", new { employeeId, financialYear });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult ReviewItem(int itemId, int employeeId, string financialYear, string decision, decimal? approvedAmount, string? remarks)
        {
            var (ok, msg) = TaxDeclarationHelper.ReviewItem(_db, itemId, decision, approvedAmount, remarks, CurrentEmpId);
            TempData[ok ? "Success" : "Error"] = msg;
            return RedirectToAction("Declaration", new { employeeId, financialYear });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult Submit(int headerId, int employeeId, string financialYear)
        {
            var (ok, msg) = TaxDeclarationHelper.Submit(_db, headerId);
            TempData[ok ? "Success" : "Error"] = msg;
            return RedirectToAction("Declaration", new { employeeId, financialYear });
        }

        // ── TDS Report — department-wise, current (or chosen) FY ──
        public IActionResult TdsReport(string? financialYear, int? departmentId)
        {
            var fy = string.IsNullOrWhiteSpace(financialYear) ? _engine.CurrentFinancialYear() : financialYear;
            var employees = _db.Employees.Include(e => e.Department).Where(e => e.IsActive);
            if (departmentId.HasValue) employees = employees.Where(e => e.DepartmentId == departmentId);

            var rows = employees.ToList()
                .Where(e => _db.EmployeeSalaryStructures.Any(s => s.EmployeeId == e.Id))
                .Select(e => new { Employee = e, Result = _engine.ComputeTax(e.Id, fy) })
                .OrderBy(r => r.Employee.Department != null ? r.Employee.Department.Name : "")
                .ThenBy(r => r.Employee.Name)
                .ToList();

            ViewBag.FinancialYear = fy;
            ViewBag.DepartmentId = departmentId;
            ViewBag.Departments = _db.Departments.Where(d => d.IsActive).OrderBy(d => d.Name).ToList();
            ViewBag.Rows = rows;
            return View();
        }
    }
}
