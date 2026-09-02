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
    // SALARY STRUCTURE — Admin builds a customizable Salary Component
    // master (Salary > Components), bundles components into reusable
    // Templates (Salary > Templates, e.g. one per Grade), then assigns a
    // versioned actual structure to each employee (Salary > Employee
    // Structure). See Services/PayrollTaxEngine.cs for how a structure
    // resolves into a monthly/annual breakdown and feeds the tax engine.
    // ═══════════════════════════════════════════
    [Authorize(Roles = "admin,hr")]
    public class SalaryController : Controller
    {
        readonly AppDbContext _db;
        readonly IPayrollTaxEngine _engine;
        public SalaryController(AppDbContext db, IPayrollTaxEngine engine) { _db = db; _engine = engine; }

        int CurrentEmpId => int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);

        // ── Components ──
        public IActionResult Components()
        {
            return View(_db.SalaryComponents.OrderBy(c => c.DisplayOrder).ThenBy(c => c.Name).ToList());
        }

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult SaveSalaryComponent(SalaryComponent model)
        {
            // Truncate to the DB column caps — HTML maxlength is a UI nicety
            // only, not a server-side guarantee, and an over-length value
            // would otherwise throw an unhandled DbUpdateException.
            model.Name = (model.Name ?? "").Trim();
            if (model.Name.Length > 80) model.Name = model.Name[..80];
            if ((model.Code ?? "").Length > 20) model.Code = model.Code![..20];
            if (string.IsNullOrWhiteSpace(model.Name)) { TempData["Error"] = "Component name is required."; return RedirectToAction("Components"); }
            if (model.IsBasic && model.CalculationType == "PercentOfBasic")
            { TempData["Error"] = "The Basic component itself can't be calculated as a % of Basic — that's circular. Use Fixed or % of CTC."; return RedirectToAction("Components"); }

            SalaryComponent comp;
            if (model.Id > 0)
            {
                var existing = _db.SalaryComponents.Find(model.Id);
                if (existing == null) return NotFound();
                existing.Name = model.Name; existing.Code = model.Code; existing.ComponentType = model.ComponentType;
                existing.CalculationType = model.CalculationType; existing.DefaultValue = model.DefaultValue;
                existing.IsBasic = model.IsBasic; existing.IsTaxable = model.IsTaxable; existing.IsHRA = model.IsHRA;
                existing.DisplayOrder = model.DisplayOrder; existing.IsActive = model.IsActive;
                comp = existing;
            }
            else
            {
                model.Id = 0; model.IsActive = true;
                _db.SalaryComponents.Add(model);
                comp = model;
            }

            _db.SaveChanges(); // need comp.Id assigned before the "unmark others" pass below

            // Only one active component should ever be marked IsBasic /
            // IsHRA — auto-unmark any other one rather than blocking the
            // save, since re-marking is how Admin naturally "moves" the flag.
            if (comp.IsBasic)
                foreach (var other in _db.SalaryComponents.Where(c => c.Id != comp.Id && c.IsBasic)) other.IsBasic = false;
            if (comp.IsHRA)
                foreach (var other in _db.SalaryComponents.Where(c => c.Id != comp.Id && c.IsHRA)) other.IsHRA = false;
            _db.SaveChanges();

            TempData["Success"] = "Salary component saved.";
            return RedirectToAction("Components");
        }

        [HttpPost]
        public IActionResult ToggleSalaryComponent(int id)
        {
            var c = _db.SalaryComponents.Find(id);
            if (c == null) return Json(new { success = false });
            c.IsActive = !c.IsActive;
            _db.SaveChanges();
            return Json(new { success = true, isActive = c.IsActive });
        }

        [HttpPost]
        public IActionResult DeleteSalaryComponent(int id)
        {
            var c = _db.SalaryComponents.Find(id);
            if (c == null) return Json(new { success = false });
            bool inUse = _db.SalaryStructureTemplateItems.Any(i => i.SalaryComponentId == id) || _db.EmployeeSalaryComponents.Any(i => i.SalaryComponentId == id);
            if (inUse) { c.IsActive = false; _db.SaveChanges(); return Json(new { success = true, message = "In use by a template or an employee's structure — deactivated instead of deleted." }); }
            _db.SalaryComponents.Remove(c);
            _db.SaveChanges();
            return Json(new { success = true, message = "Component deleted." });
        }

        // ── Templates ──
        public IActionResult Templates()
        {
            return View(_db.SalaryStructureTemplates.Include(t => t.Grade).Include(t => t.Items).OrderBy(t => t.Name).ToList());
        }

        public IActionResult TemplateEdit(int id)
        {
            var template = id > 0
                ? _db.SalaryStructureTemplates.Include(t => t.Items).ThenInclude(i => i.SalaryComponent).FirstOrDefault(t => t.Id == id)
                : new SalaryStructureTemplate();
            if (id > 0 && template == null) return NotFound();
            ViewBag.Grades = _db.Grades.Where(g => g.IsActive).OrderBy(g => g.Name).ToList();
            ViewBag.Components = _db.SalaryComponents.Where(c => c.IsActive).OrderBy(c => c.DisplayOrder).ThenBy(c => c.Name).ToList();
            return View(template);
        }

        public record SalaryItemDto(int ComponentId, string CalcType, decimal Value);

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult SaveTemplate(int id, string name, string? description, int? gradeId, string itemsJson)
        {
            name = (name ?? "").Trim();
            if (name.Length > 80) name = name[..80];
            if (string.IsNullOrWhiteSpace(name)) { TempData["Error"] = "Template name is required."; return RedirectToAction("TemplateEdit", new { id }); }

            List<SalaryItemDto> items;
            try { items = System.Text.Json.JsonSerializer.Deserialize<List<SalaryItemDto>>(itemsJson ?? "[]") ?? new(); }
            catch { TempData["Error"] = "Couldn't read the component rows — please try again."; return RedirectToAction("TemplateEdit", new { id }); }
            if (!items.Any()) { TempData["Error"] = "Add at least one salary component."; return RedirectToAction("TemplateEdit", new { id }); }

            // The component marked IsBasic must resolve independently of
            // Basic itself (Fixed or % of CTC) — % of Basic here would
            // silently resolve Basic to ₹0 (see PayrollTaxEngine.BuildBreakdown
            // pass 1/2), which then zeroes PF, HRA exemption and the whole
            // tax computation with no error shown anywhere.
            var basicComponentIds = _db.SalaryComponents.Where(c => c.IsBasic).Select(c => c.Id).ToHashSet();
            if (items.Any(it => basicComponentIds.Contains(it.ComponentId) && it.CalcType == "PercentOfBasic"))
            { TempData["Error"] = "The Basic component can't be calculated as a % of Basic — that's circular. Use Fixed or % of CTC for it."; return RedirectToAction("TemplateEdit", new { id }); }

            SalaryStructureTemplate template;
            if (id > 0)
            {
                var existing = _db.SalaryStructureTemplates.Include(t => t.Items).FirstOrDefault(t => t.Id == id);
                if (existing == null) return NotFound();
                if (_db.SalaryStructureTemplates.Any(t => t.Id != id && t.Name == name))
                { TempData["Error"] = $"A template named '{name}' already exists."; return RedirectToAction("TemplateEdit", new { id }); }
                existing.Name = name; existing.Description = description; existing.GradeId = gradeId;
                _db.SalaryStructureTemplateItems.RemoveRange(existing.Items); // rebuild from scratch, same pattern as Week-Off / Leave Policy rules
                existing.Items = new List<SalaryStructureTemplateItem>();
                template = existing;
            }
            else
            {
                if (_db.SalaryStructureTemplates.Any(t => t.Name == name)) { TempData["Error"] = $"A template named '{name}' already exists."; return RedirectToAction("TemplateEdit", new { id }); }
                template = new SalaryStructureTemplate { Name = name, Description = description, GradeId = gradeId };
                _db.SalaryStructureTemplates.Add(template);
            }

            int order = 0;
            foreach (var it in items)
            {
                if (!_db.SalaryComponents.Any(c => c.Id == it.ComponentId)) continue;
                template.Items.Add(new SalaryStructureTemplateItem { SalaryComponentId = it.ComponentId, CalculationType = it.CalcType, Value = it.Value, DisplayOrder = order++ });
            }

            _db.SaveChanges();
            TempData["Success"] = "Salary structure template saved.";
            return RedirectToAction("Templates");
        }

        [HttpPost]
        public IActionResult ToggleTemplate(int id)
        {
            var t = _db.SalaryStructureTemplates.Find(id);
            if (t == null) return Json(new { success = false });
            t.IsActive = !t.IsActive;
            _db.SaveChanges();
            return Json(new { success = true, isActive = t.IsActive });
        }

        [HttpPost]
        public IActionResult DeleteTemplate(int id)
        {
            var t = _db.SalaryStructureTemplates.Include(x => x.Items).FirstOrDefault(x => x.Id == id);
            if (t == null) return Json(new { success = false });
            if (_db.EmployeeSalaryStructures.Any(s => s.SourceTemplateId == id)) { t.IsActive = false; _db.SaveChanges(); return Json(new { success = true, message = "In use by an employee's salary structure — deactivated instead of deleted." }); }
            _db.SalaryStructureTemplateItems.RemoveRange(t.Items);
            _db.SalaryStructureTemplates.Remove(t);
            _db.SaveChanges();
            return Json(new { success = true, message = "Template deleted." });
        }

        // ── Employee Structure (versioned assignment) ──
        public IActionResult EmployeeStructure(int? employeeId)
        {
            ViewBag.Employees = _db.Employees.Where(e => e.IsActive).OrderBy(e => e.Name).ToList();
            if (employeeId == null || employeeId == 0) return View();

            var emp = _db.Employees.Find(employeeId);
            if (emp == null) return NotFound();
            ViewBag.SelectedEmployee = emp;

            var history = _db.EmployeeSalaryStructures.Include(s => s.Components).ThenInclude(c => c.SalaryComponent)
                .Include(s => s.SourceTemplate)
                .Where(s => s.EmployeeId == employeeId).OrderByDescending(s => s.EffectiveFrom).ToList();
            ViewBag.History = history;

            var current = history.FirstOrDefault(s => s.EffectiveTo == null);
            if (current != null) ViewBag.CurrentBreakdown = PayrollTaxEngine.BuildBreakdown(current);

            return View();
        }

        public IActionResult AssignStructure(int employeeId, int? copyFromTemplateId, int? copyFromStructureId)
        {
            var emp = _db.Employees.Find(employeeId);
            if (emp == null) return NotFound();
            ViewBag.Employee = emp;
            ViewBag.Templates = _db.SalaryStructureTemplates.Where(t => t.IsActive).OrderBy(t => t.Name).ToList();
            ViewBag.Components = _db.SalaryComponents.Where(c => c.IsActive).OrderBy(c => c.DisplayOrder).ThenBy(c => c.Name).ToList();

            var current = _db.EmployeeSalaryStructures.Include(s => s.Components)
                .Where(s => s.EmployeeId == employeeId && s.EffectiveTo == null).FirstOrDefault();
            ViewBag.CurrentAnnualCTC = current?.AnnualCTC ?? 0;

            if (copyFromTemplateId.HasValue)
            {
                var t = _db.SalaryStructureTemplates.Include(x => x.Items).FirstOrDefault(x => x.Id == copyFromTemplateId);
                ViewBag.PrefillItems = t?.Items.Select(i => new { componentId = i.SalaryComponentId, calcType = i.CalculationType, value = i.Value }).ToList();
                ViewBag.PrefillSourceTemplateId = copyFromTemplateId;
            }
            else if (copyFromStructureId.HasValue || current != null)
            {
                var src = copyFromStructureId.HasValue
                    ? _db.EmployeeSalaryStructures.Include(s => s.Components).FirstOrDefault(s => s.Id == copyFromStructureId)
                    : current;
                if (src != null)
                {
                    ViewBag.PrefillItems = src.Components.Select(i => new { componentId = i.SalaryComponentId, calcType = i.CalculationType, value = i.Value }).ToList();
                    if (copyFromStructureId.HasValue) ViewBag.CurrentAnnualCTC = src.AnnualCTC;
                }
            }

            return View();
        }

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult SaveEmployeeStructure(int employeeId, string effectiveFrom, decimal annualCTC, int? sourceTemplateId, string itemsJson)
        {
            var emp = _db.Employees.Find(employeeId);
            if (emp == null) return NotFound();
            if (string.IsNullOrWhiteSpace(effectiveFrom) || !DateTime.TryParse(effectiveFrom, out _))
            { TempData["Error"] = "A valid Effective From date is required."; return RedirectToAction("AssignStructure", new { employeeId }); }
            if (annualCTC <= 0) { TempData["Error"] = "Annual CTC must be greater than zero."; return RedirectToAction("AssignStructure", new { employeeId }); }

            List<SalaryItemDto> items;
            try { items = System.Text.Json.JsonSerializer.Deserialize<List<SalaryItemDto>>(itemsJson ?? "[]") ?? new(); }
            catch { TempData["Error"] = "Couldn't read the component rows — please try again."; return RedirectToAction("AssignStructure", new { employeeId }); }
            if (!items.Any()) { TempData["Error"] = "Add at least one salary component."; return RedirectToAction("AssignStructure", new { employeeId }); }
            if (!items.Any(i => _db.SalaryComponents.Any(c => c.Id == i.ComponentId && c.IsBasic)))
            { TempData["Error"] = "The structure must include the Basic component."; return RedirectToAction("AssignStructure", new { employeeId }); }

            // Same circularity guard as SaveTemplate — see the comment there.
            var basicComponentIds = _db.SalaryComponents.Where(c => c.IsBasic).Select(c => c.Id).ToHashSet();
            if (items.Any(it => basicComponentIds.Contains(it.ComponentId) && it.CalcType == "PercentOfBasic"))
            { TempData["Error"] = "The Basic component can't be calculated as a % of Basic — that's circular. Use Fixed or % of CTC for it."; return RedirectToAction("AssignStructure", new { employeeId }); }

            var current = _db.EmployeeSalaryStructures.FirstOrDefault(s => s.EmployeeId == employeeId && s.EffectiveTo == null);
            if (current != null && string.Compare(effectiveFrom, current.EffectiveFrom) <= 0)
            { TempData["Error"] = $"Effective From must be after the current structure's effective date ({current.EffectiveFrom})."; return RedirectToAction("AssignStructure", new { employeeId }); }

            var structure = new EmployeeSalaryStructure
            {
                EmployeeId = employeeId,
                EffectiveFrom = DateTime.Parse(effectiveFrom).ToString("yyyy-MM-dd"),
                AnnualCTC = annualCTC,
                SourceTemplateId = sourceTemplateId,
                CreatedByEmployeeId = CurrentEmpId
            };

            int order = 0;
            foreach (var it in items)
            {
                if (!_db.SalaryComponents.Any(c => c.Id == it.ComponentId)) continue;
                structure.Components.Add(new EmployeeSalaryComponent { SalaryComponentId = it.ComponentId, CalculationType = it.CalcType, Value = it.Value, DisplayOrder = order++ });
            }
            _db.EmployeeSalaryStructures.Add(structure);
            _db.SaveChanges(); // structure.Id + Components now exist so BuildBreakdown can run below

            // Resolve and cache each component's MonthlyAmount at assignment
            // time (fast reads later; PayrollTaxEngine still recomputes live
            // from Value/CalculationType wherever it needs the true figure).
            var resolvedStructure = _db.EmployeeSalaryStructures.Include(s => s.Components).ThenInclude(c => c.SalaryComponent).First(s => s.Id == structure.Id);
            var breakdown = PayrollTaxEngine.BuildBreakdown(resolvedStructure);
            foreach (var comp in resolvedStructure.Components)
            {
                var line = breakdown.Lines.FirstOrDefault(l => l.ComponentId == comp.SalaryComponentId);
                comp.MonthlyAmount = line?.Monthly ?? 0;
            }

            if (current != null)
                current.EffectiveTo = DateTime.Parse(effectiveFrom).AddDays(-1).ToString("yyyy-MM-dd");

            _db.SaveChanges();
            TempData["Success"] = $"Salary structure assigned to {emp.Name}, effective {structure.EffectiveFrom}.";
            return RedirectToAction("EmployeeStructure", new { employeeId });
        }
    }
}
