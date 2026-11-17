using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AmpmHrmsPro.Services.Tenancy;
using Microsoft.EntityFrameworkCore;
using AmpmHrmsPro.Data;
using AmpmHrmsPro.Models;
using ClosedXML.Excel;

namespace AmpmHrmsPro.Controllers
{
    [Authorize]
    [RequirePermission(Perm.LeaveView, Perm.LeaveManage)]
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

            // ── AMPM payroll workbook ("SL/EL/CL DATA AS ON …"): a "Salary" sheet
            //    with Paycode, opening balance, month-wise EARNED and CONSUMED
            //    (Jan–Dec) and BAL. Read all 12 months straight from it.
            foreach (var sheet in wb.Worksheets)
            {
                var layout = PayrollLayout.Detect(sheet);
                if (layout == null) continue;
                var r = await ImportPayrollSheetAsync(sheet, layout, layout.TypeCode ?? leaveTypeCode, year);
                TempData["Success"] = r.Message;
                if (r.Warnings.Count > 0) TempData["UploadErrors"] = string.Join("\n", r.Warnings.Take(40));
                return RedirectToAction(nameof(Index), new { leaveType = layout.TypeCode ?? leaveTypeCode, year });
            }

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
        // ── Payroll workbook import ───────────────────────────────────────
        // Payroll workbooks are full of formulas (some pointing at other files):
        // always read the value Excel saved, never re-calculate.
        static XLCellValue Val(IXLCell c) { try { return c.HasFormula ? c.CachedValue : c.Value; } catch { return Blank.Value; } }
        static string Txt(IXLCell c)
        {
            var v = Val(c);
            var s = v.IsText ? v.GetText() : v.IsNumber ? v.GetNumber().ToString(System.Globalization.CultureInfo.InvariantCulture) : v.IsBlank ? "" : v.ToString();
            return s.Trim();
        }
        static decimal Num(IXLCell c)
        {
            var v = Val(c);
            if (v.IsNumber) return (decimal)v.GetNumber();
            return decimal.TryParse(Txt(c), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0;
        }

        sealed class PayrollLayout
        {
            public int HeaderRow, CodeCol, NameCol, OpeningCol;
            public string? TypeCode;
            public List<(int Col, int Month)> Earned = new(), Consumed = new();

            public static PayrollLayout? Detect(IXLWorksheet ws)
            {
                for (int r = 1; r <= 12; r++)
                {
                    var codeCell = ws.Row(r).CellsUsed().FirstOrDefault(c => Txt(c).Equals("Paycode", StringComparison.OrdinalIgnoreCase));
                    if (codeCell == null) continue;
                    var months = ws.Row(r).CellsUsed().Where(c => Val(c).IsDateTime).Select(c => (Col: c.Address.ColumnNumber, Month: Val(c).GetDateTime().Month)).OrderBy(x => x.Col).ToList();
                    if (months.Count == 0 || r == 1) return null;
                    int earnedStart = 0, consumedStart = 0; string? type = null;
                    foreach (var g in ws.Row(r - 1).CellsUsed())
                    {
                        var t = Txt(g).ToUpperInvariant();
                        if (t.Contains("EARNED") && earnedStart == 0) { earnedStart = g.Address.ColumnNumber; type ??= t[..2]; }
                        if (t.Contains("CONSUMED") && consumedStart == 0) { consumedStart = g.Address.ColumnNumber; type ??= t[..2]; }
                    }
                    if (consumedStart == 0) return null;
                    var name = ws.Row(r).CellsUsed().FirstOrDefault(c => Txt(c).Equals("Name", StringComparison.OrdinalIgnoreCase));
                    return new PayrollLayout
                    {
                        HeaderRow = r, CodeCol = codeCell.Address.ColumnNumber, NameCol = name?.Address.ColumnNumber ?? 0,
                        OpeningCol = months[0].Col - 1,
                        TypeCode = type is "EL" or "CL" or "SL" ? type : null,
                        Earned = earnedStart > 0 ? months.Where(m => m.Col >= earnedStart && m.Col < consumedStart).ToList() : new(),
                        Consumed = months.Where(m => m.Col >= consumedStart).ToList(),
                    };
                }
                return null;
            }
        }

        async Task<(string Message, List<string> Warnings)> ImportPayrollSheetAsync(IXLWorksheet ws, PayrollLayout L, string type, int year)
        {
            var warnings = new List<string>();
            var emps = await _db.Employees.AsNoTracking().Select(e => new { e.Id, e.EmpCode, e.Name, e.IsActive }).ToListAsync();
            var byCode = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in emps.OrderByDescending(e => e.IsActive))
                byCode.TryAdd(e.EmpCode.Trim().TrimStart('0'), e.Id);
            var existing = await _db.LeaveBalances.Where(b => b.LeaveTypeCode == type && b.Year == year).ToListAsync();
            var done = new HashSet<int>();
            int inserted = 0, updated = 0, skipped = 0;
            int last = ws.LastRowUsed()?.RowNumber() ?? L.HeaderRow;
            for (int r = L.HeaderRow + 1; r <= last; r++)
            {
                var raw = Txt(ws.Cell(r, L.CodeCol));
                if (raw.EndsWith(".0")) raw = raw[..^2];
                if (raw.Length == 0) continue;
                var name = L.NameCol > 0 ? Txt(ws.Cell(r, L.NameCol)) : "";
                // "10749-" etc. is not a clean code — never guess which employee it is.
                if (!System.Text.RegularExpressions.Regex.IsMatch(raw, "^[0-9A-Za-z]+$") || !byCode.TryGetValue(raw.TrimStart('0'), out var empId))
                { warnings.Add($"{raw} {name}: not in Employee Master — skipped."); skipped++; continue; }
                if (!done.Add(empId)) { warnings.Add($"{raw} {name}: code appears twice in the file — second row skipped."); skipped++; continue; }

                var lb = existing.FirstOrDefault(b => b.EmployeeId == empId);
                if (lb == null) { lb = new LeaveBalance { EmployeeId = empId, LeaveTypeCode = type, Year = year }; _db.LeaveBalances.Add(lb); inserted++; }
                else updated++;
                var earned = new decimal[12]; var consumed = new decimal[12];
                foreach (var (col, m) in L.Earned) earned[m - 1] += Num(ws.Cell(r, col));
                foreach (var (col, m) in L.Consumed) consumed[m - 1] += Num(ws.Cell(r, col));
                lb.CarryForward = Num(ws.Cell(r, L.OpeningCol));
                (lb.EarnedJan, lb.EarnedFeb, lb.EarnedMar, lb.EarnedApr, lb.EarnedMay, lb.EarnedJun) = (earned[0], earned[1], earned[2], earned[3], earned[4], earned[5]);
                (lb.EarnedJul, lb.EarnedAug, lb.EarnedSep, lb.EarnedOct, lb.EarnedNov, lb.EarnedDec) = (earned[6], earned[7], earned[8], earned[9], earned[10], earned[11]);
                (lb.ConsumedJan, lb.ConsumedFeb, lb.ConsumedMar, lb.ConsumedApr, lb.ConsumedMay, lb.ConsumedJun) = (consumed[0], consumed[1], consumed[2], consumed[3], consumed[4], consumed[5]);
                (lb.ConsumedJul, lb.ConsumedAug, lb.ConsumedSep, lb.ConsumedOct, lb.ConsumedNov, lb.ConsumedDec) = (consumed[6], consumed[7], consumed[8], consumed[9], consumed[10], consumed[11]);
                lb.UpdatedAt = DateTime.Now;
            }
            await _db.SaveChangesAsync();
            return ($"{type} {year} (payroll sheet \"{ws.Name.Trim()}\", Jan–Dec): {inserted} added, {updated} updated, {skipped} skipped.", warnings);
        }

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
