using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AmpmHrmsPro.Data;
using AmpmHrmsPro.Models;
using ClosedXML.Excel;

namespace AmpmHrmsPro.Controllers
{
    [Authorize(Roles = "admin,hr")]
    public class LeaveBalanceController : Controller
    {
        private readonly AppDbContext _db;
        public LeaveBalanceController(AppDbContext db) => _db = db;

        // ── Index: view all balances ──────────────────────────────────────
        public async Task<IActionResult> Index(string? leaveType, string? search, int year = 0)
        {
            if (year == 0) year = DateTime.Now.Year;

            var q = _db.LeaveBalances
                .Include(b => b.Employee)
                .Where(b => b.Year == year)
                .AsQueryable();

            if (!string.IsNullOrEmpty(leaveType))
                q = q.Where(b => b.LeaveTypeCode == leaveType);

            if (!string.IsNullOrEmpty(search))
                q = q.Where(b =>
                    b.Employee!.Name.Contains(search) ||
                    b.Employee.EmpCode.Contains(search));

            var rows = await q.OrderBy(b => b.Employee!.EmpCode)
                              .ThenBy(b => b.LeaveTypeCode)
                              .ToListAsync();

            ViewBag.SelectedLeaveType = leaveType ?? "";
            ViewBag.Search = search ?? "";
            ViewBag.Year = year;
            ViewBag.Years = Enumerable.Range(DateTime.Now.Year - 2, 5).Reverse().ToList();

            return View(rows);
        }

        // ── BulkUpload GET ────────────────────────────────────────────────
        public IActionResult BulkUpload()
        {
            ViewBag.Year = DateTime.Now.Year;
            return View();
        }

        // ── BulkUpload POST ───────────────────────────────────────────────
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> BulkUpload(IFormFile file, string leaveTypeCode, int year)
        {
            if (file == null || file.Length == 0)
            {
                TempData["Error"] = "Please select an Excel file.";
                return RedirectToAction(nameof(BulkUpload));
            }

            leaveTypeCode = leaveTypeCode?.Trim().ToUpper() ?? "";
            var validTypes = new HashSet<string> { "EL", "CL", "SL" };
            if (!validTypes.Contains(leaveTypeCode))
            {
                TempData["Error"] = "Leave Type must be EL, CL, or SL.";
                return RedirectToAction(nameof(BulkUpload));
            }

            // Build a dictionary empCode → Employee.Id for fast lookup.
            // We normalise every key in TWO ways so that codes stored with leading zeros
            // (e.g. "00006") also match a file that contains "6", and vice-versa.
            var empMapRaw = await _db.Employees.Select(e => new { e.EmpCode, e.Id }).ToListAsync();
            var empMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in empMapRaw)
            {
                var key = e.EmpCode.Trim().ToUpper();
                empMap.TryAdd(key, e.Id);                  // original  ("00006")
                if (int.TryParse(key, out int n))
                    empMap.TryAdd(n.ToString(), e.Id);     // stripped  ("6")
            }

            int inserted = 0, updated = 0, skipped = 0;
            var errors = new List<string>();

            using var stream = new MemoryStream();
            await file.CopyToAsync(stream);
            stream.Position = 0;

            using var wb = new XLWorkbook(stream);

            // Use the first sheet (report = "EL Report"/"CL Report", template = "EL Bulk Upload"/"CL Bulk Upload")
            IXLWorksheet ws = wb.Worksheets.First();

            // ── Column layout (both EL/CL Report and Bulk Upload Template share this) ──
            //
            // BulkUpload Template (EL_BulkUpload_Template.xlsx):
            //   A(1)=EmpCode  B(2)=Name  C(3)=DOJ  D(4)=Category  E(5)=SvcYrs
            //   F(6)=CF  G-M(7-13)=Earned Jan-Jul  N(14)=TotalEarned(formula,skip)
            //   O-U(15-21)=Consumed Jan-Jul  V(22)=TotalConsumed(formula,skip)  W(23)=Balance(skip)
            //   Data rows start at row 5 (rows 1-4 = headers/instructions)
            //
            // EL/CL Report (EL_Report_Jul2026.xlsx):
            //   A(1)=S.No  B(2)=EmpCode  C(3)=Name  D(4)=DOJ  E(5)=SvcYrs
            //   F(6)=CF  G-M(7-13)=Earned Jan-Jul  N(14)=TotalEarned(formula,skip)
            //   O-U(15-21)=Consumed Jan-Jul  V(22)=TotalConsumed(formula,skip)  W-Y=Balance
            //   Data rows start at row 4 (rows 1-3 = headers)
            //
            // Auto-detect emp code column: if col 1 is numeric (S.No in the report) → empCodeCol=2
            // If col 1 is a non-numeric string (emp code in the template) → empCodeCol=1

            int lastRow = ws.LastRowUsed()?.RowNumber() ?? 3;

            // Auto-detect format: try to match col2 against empMap on the first live data row.
            // Report format  → col1=S.No(numeric), col2=EmpCode → empCodeCol=2
            // Template format→ col1=EmpCode,        col2=Name    → empCodeCol=1
            int empCodeCol = 1; // default: template (EmpCode in col 1)
            for (int s = 4; s <= Math.Min(lastRow, 15); s++)
            {
                var v1 = ws.Cell(s, 1).GetString().Trim();
                var v2 = ws.Cell(s, 2).GetString().Trim();
                if (string.IsNullOrEmpty(v1) && string.IsNullOrEmpty(v2)) continue;
                // If col2 is a known EmpCode → report format
                if (!string.IsNullOrEmpty(v2) && empMap.ContainsKey(v2.ToUpper()))
                    empCodeCol = 2;
                // else keep empCodeCol=1 (template format)
                break;
            }

            // Track already-processed employees within this upload to prevent duplicate-key
            // when the same emp code appears more than once in the file (header/total rows etc.)
            var processedEmpIds = new HashSet<int>();

            for (int row = 4; row <= lastRow; row++)
            {
                var empCodeRaw = ws.Cell(row, empCodeCol).GetString().Trim();
                if (string.IsNullOrEmpty(empCodeRaw)) continue;

                var empCodeKey = empCodeRaw.ToUpper();
                if (!empMap.TryGetValue(empCodeKey, out int empId))
                {
                    // skip header rows, example rows, totals rows, unknown codes silently
                    skipped++;
                    continue;
                }

                // Skip duplicate emp codes within the same upload batch
                if (!processedEmpIds.Add(empId))
                {
                    skipped++;
                    continue;
                }

                decimal D(int col) => GetDecimal(ws.Cell(row, col));

                // CF=col6, EarnedJan-Jul=cols7-13, TotalEarned=col14(skip),
                // ConsumedJan-Jul=cols15-21, TotalConsumed=col22(skip)
                // Aug-Dec months are left as 0 (not in current template; add when year progresses)
                var lb = new LeaveBalance
                {
                    EmployeeId    = empId,
                    LeaveTypeCode = leaveTypeCode,
                    Year          = year,
                    CarryForward  = D(6),
                    EarnedJan = D(7),  EarnedFeb = D(8),  EarnedMar = D(9),
                    EarnedApr = D(10), EarnedMay = D(11), EarnedJun = D(12),
                    EarnedJul = D(13),
                    // col 14 = Total Earned formula — skip
                    EarnedAug = 0, EarnedSep = 0, EarnedOct = 0, EarnedNov = 0, EarnedDec = 0,
                    ConsumedJan = D(15), ConsumedFeb = D(16), ConsumedMar = D(17),
                    ConsumedApr = D(18), ConsumedMay = D(19), ConsumedJun = D(20),
                    ConsumedJul = D(21),
                    // col 22 = Total Consumed formula — skip
                    ConsumedAug = 0, ConsumedSep = 0, ConsumedOct = 0, ConsumedNov = 0, ConsumedDec = 0,
                    UpdatedAt = DateTime.Now
                };

                // Upsert: find existing row for this employee+leaveType+year
                var existing = await _db.LeaveBalances.FirstOrDefaultAsync(b =>
                    b.EmployeeId == empId &&
                    b.LeaveTypeCode == leaveTypeCode &&
                    b.Year == year);

                if (existing == null)
                {
                    _db.LeaveBalances.Add(lb);
                    inserted++;
                }
                else
                {
                    existing.CarryForward  = lb.CarryForward;
                    existing.EarnedJan = lb.EarnedJan; existing.EarnedFeb = lb.EarnedFeb;
                    existing.EarnedMar = lb.EarnedMar; existing.EarnedApr = lb.EarnedApr;
                    existing.EarnedMay = lb.EarnedMay; existing.EarnedJun = lb.EarnedJun;
                    existing.EarnedJul = lb.EarnedJul; existing.EarnedAug = lb.EarnedAug;
                    existing.EarnedSep = lb.EarnedSep; existing.EarnedOct = lb.EarnedOct;
                    existing.EarnedNov = lb.EarnedNov; existing.EarnedDec = lb.EarnedDec;
                    existing.ConsumedJan = lb.ConsumedJan; existing.ConsumedFeb = lb.ConsumedFeb;
                    existing.ConsumedMar = lb.ConsumedMar; existing.ConsumedApr = lb.ConsumedApr;
                    existing.ConsumedMay = lb.ConsumedMay; existing.ConsumedJun = lb.ConsumedJun;
                    existing.ConsumedJul = lb.ConsumedJul; existing.ConsumedAug = lb.ConsumedAug;
                    existing.ConsumedSep = lb.ConsumedSep; existing.ConsumedOct = lb.ConsumedOct;
                    existing.ConsumedNov = lb.ConsumedNov; existing.ConsumedDec = lb.ConsumedDec;
                    existing.UpdatedAt   = lb.UpdatedAt;
                    updated++;
                }
            }

            await _db.SaveChangesAsync();

            TempData["Success"] = $"{leaveTypeCode} {year}: {inserted} inserted, {updated} updated, {skipped} skipped.";
            if (errors.Any())
                TempData["UploadErrors"] = string.Join("\n", errors.Take(20));

            return RedirectToAction(nameof(Index), new { leaveType = leaveTypeCode, year });
        }

        // ── Employee detail: EL + CL for one employee ─────────────────────
        public async Task<IActionResult> Employee(int id, int year = 0)
        {
            if (year == 0) year = DateTime.Now.Year;
            var emp = await _db.Employees.FindAsync(id);
            if (emp == null) return NotFound();

            var balances = await _db.LeaveBalances
                .Where(b => b.EmployeeId == id && b.Year == year)
                .ToListAsync();

            ViewBag.Employee = emp;
            ViewBag.Year = year;
            ViewBag.Years = Enumerable.Range(DateTime.Now.Year - 2, 5).Reverse().ToList();
            return View(balances);
        }

        // ── Delete a single balance row ────────────────────────────────────
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            var row = await _db.LeaveBalances.FindAsync(id);
            if (row != null)
            {
                _db.LeaveBalances.Remove(row);
                await _db.SaveChangesAsync();
                TempData["Success"] = "Leave balance record deleted.";
            }
            return RedirectToAction(nameof(Index));
        }

        // ── Helper: safely parse a cell as decimal ─────────────────────────
        private static decimal GetDecimal(IXLCell cell)
        {
            try
            {
                if (cell.IsEmpty()) return 0;
                return cell.GetValue<decimal>();
            }
            catch { return 0; }
        }
    }
}
