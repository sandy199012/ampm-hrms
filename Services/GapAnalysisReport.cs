// ─────────────────────────────────────────────────────────────────────────────
// Services/GapAnalysisReport.cs
//
// Month-to-Date Gap Analysis workbook — the exact format of
// "Attendance_MTD_<HoD>_<date>.xlsx" / "ALL_Employees_Attendance_MTD_<date>.xlsx":
//
//   Summary            one row per employee: Late / Hrs Not Completed / Extra /
//                      Absent counts + totals, and "Yesterday's Gaps"
//   Daily Detail (MTD) every employee × every date from the 1st to the report date
//   Gaps Only          the Daily Detail rows that have a gap
//
// Inputs (all uploaded on Attendance Review):
//   Roster       (Attendance Management.xlsx)  → plan, mode, planned in/out, buffer
//   Master Sheet (Employee Master)             → Reports To + who is in each HoD's team
//   Presence 360 daily CSV                     → actual in/out + status
//   (+ HRMS Biometric API attendance for any day with no Presence 360 row)
//
// Rules (from the roster's "How To Update" sheet; verified row-by-row against
// the sample files):
//   Late        in − planned in  >  buffer (default 10 min)
//   Short       hours worked − planned hours  <  0         (a missed out-punch counts too)
//   Extra       hours worked − planned hours  ≥  30 min;   work on Week Off / Holiday ≥ 30 min
//   Absent      roster says Working, no punch (WFH with no punch is not absent)
//   Leave / Comp Off = planned absence (no gap) · Official Travel = no gaps ·
//   Half Day Leave = expected hours halved, no late check
// ─────────────────────────────────────────────────────────────────────────────
using System.Globalization;
using System.Text.RegularExpressions;
using AmpmHrmsPro.Data;
using AmpmHrmsPro.Models;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;

namespace AmpmHrmsPro.Services
{
    public class MtdRow
    {
        public string Name = "";
        public string Code = "";
        public string? ReportsTo;
        public DateOnly Date;
        public string Plan = "Working";
        public string? Mode;
        public int? PlannedIn, PlannedOut, PlannedMins;
        public int? ActualIn, ActualOut, Worked, Diff;
        public string? Status;
        public int? LateMin;
        public bool Late, Short, Extra, Absent;
        public string? Gaps;
        public string? Note;
        public int ShortMin, ExtraMin;
        public bool Grey;
    }

    public record ReviewDataStatus(
        int RosterRows, DateOnly? RosterFrom, DateOnly? RosterTo, List<string> RosterMonths,
        int MasterEmployees, DateOnly? LatestP360Date);

    public interface IGapAnalysisReport
    {
        /// <summary>Employee codes in a HoD's team (every level below them); null HoD = everyone.</summary>
        Task<List<string>> ResolveScopeAsync(string? hodName, DateOnly upto, CancellationToken ct = default);
        Task<List<MtdRow>> BuildRowsAsync(IReadOnlyCollection<string>? codes, DateOnly upto, CancellationToken ct = default);
        byte[] BuildWorkbook(List<MtdRow> rows, DateOnly upto);
        Task<bool> HasDataForAsync(DateOnly date, CancellationToken ct = default);
        Task<string?> MasterNameForAsync(string hodName, CancellationToken ct = default);

        Task<(int Rows, List<string> Months)> ImportRosterAsync(Stream xlsx, CancellationToken ct = default);
        Task<int> ImportMasterAsync(Stream xlsx, CancellationToken ct = default);
        Task<ReviewDataStatus> GetStatusAsync(CancellationToken ct = default);

        string NormCode(string? code);
    }

    public class GapAnalysisReport : IGapAnalysisReport
    {
        private readonly AppDbContext _db;
        private readonly ILogger<GapAnalysisReport> _log;

        public GapAnalysisReport(AppDbContext db, ILogger<GapAnalysisReport> log)
        {
            _db  = db;
            _log = log;
        }

        // ── helpers ──────────────────────────────────────────────────────────
        public string NormCode(string? code)
        {
            var c = (code ?? "").Trim();
            if (c.EndsWith(".0")) c = c[..^2];
            c = c.TrimStart('0');
            return c.Length == 0 ? ((code ?? "").Trim().Length > 0 ? "0" : "") : c;
        }

        private static string NormName(string? s) =>
            Regex.Replace(s ?? "", @"\s+", " ").Trim().TrimEnd('.').Trim().ToUpperInvariant();

        private static int? Mins(TimeOnly? t) => t.HasValue ? t.Value.Hour * 60 + t.Value.Minute : null;
        private static int? Mins(TimeSpan? t) => t.HasValue ? (int)t.Value.TotalMinutes % 1440 : null;
        private static string Tm(int m) => $"{m / 60:00}:{m % 60:00}";
        private static string Hm(int m) => (m < 0 ? "-" : "") + $"{Math.Abs(m) / 60}:{Math.Abs(m) % 60:00}";

        private static DateOnly MonthStart(DateOnly d) => new(d.Year, d.Month, 1);

        // ── scope ────────────────────────────────────────────────────────────
        public async Task<string?> MasterNameForAsync(string hodName, CancellationToken ct = default)
        {
            var key = NormName(hodName);
            var m = (await _db.ReviewEmployees.AsNoTracking().ToListAsync(ct))
                .FirstOrDefault(e => NormName(e.Name) == key);
            return m?.Name;
        }

        public async Task<List<string>> ResolveScopeAsync(string? hodName, DateOnly upto, CancellationToken ct = default)
        {
            var from = MonthStart(upto);
            var monthCodes = (await _db.RosterEntries.AsNoTracking()
                    .Where(r => r.Date >= from && r.Date <= upto).Select(r => r.EmployeeCode).Distinct().ToListAsync(ct))
                .Select(NormCode).Where(c => c.Length > 0).ToHashSet();
            if (monthCodes.Count == 0)
                monthCodes = (await _db.P360DailyRecords.AsNoTracking()
                        .Where(r => r.Date >= from && r.Date <= upto).Select(r => r.EmpCode).Distinct().ToListAsync(ct))
                    .ToHashSet();

            if (string.IsNullOrWhiteSpace(hodName))
                return monthCodes.ToList();

            var hodKey = NormName(hodName);

            // 1) Master Sheet hierarchy (uploaded) — the source the sample reports use.
            var master = await _db.ReviewEmployees.AsNoTracking().ToListAsync(ct);
            if (master.Count > 0)
            {
                var hod = master.FirstOrDefault(m => NormName(m.Name) == hodKey);
                if (hod != null)
                {
                    var children = master.Where(m => !string.IsNullOrEmpty(m.ManagerCode))
                        .GroupBy(m => m.ManagerCode!).ToDictionary(g => g.Key, g => g.Select(x => x.EmpCode).ToList());
                    return Descendants(hod.EmpCode, children).Where(monthCodes.Contains).ToList();
                }
            }

            // 2) HRMS Employee Master (Reporting Manager).
            var emps = await _db.Employees.AsNoTracking()
                .Select(e => new { e.Id, e.EmpCode, e.Name, e.ReportingManagerId }).ToListAsync(ct);
            var hodEmp = emps.FirstOrDefault(e => NormName(e.Name) == hodKey);
            if (hodEmp != null)
            {
                var codeById = emps.ToDictionary(e => e.Id, e => NormCode(e.EmpCode));
                var children = emps.Where(e => e.ReportingManagerId.HasValue && codeById.ContainsKey(e.ReportingManagerId.Value))
                    .GroupBy(e => codeById[e.ReportingManagerId!.Value])
                    .ToDictionary(g => g.Key, g => g.Select(x => NormCode(x.EmpCode)).ToList());
                var res = Descendants(NormCode(hodEmp.EmpCode), children).Where(monthCodes.Contains).ToList();
                if (res.Count > 0) return res;
            }

            // 3) Presence 360 "Manager Name" chain.
            var p360 = await _db.P360DailyRecords.AsNoTracking()
                .Where(r => r.Date >= from && r.Date <= upto)
                .Select(r => new { r.EmpCode, r.EmployeeName, r.ManagerName }).ToListAsync(ct);
            var codeByName = p360.GroupBy(r => NormName(r.EmployeeName)).ToDictionary(g => g.Key, g => g.First().EmpCode);
            var kids = new Dictionary<string, List<string>>();
            foreach (var r in p360.Where(r => !string.IsNullOrWhiteSpace(r.ManagerName)))
            {
                var mk = NormName(r.ManagerName);
                var parent = codeByName.TryGetValue(mk, out var mc) ? mc : "name:" + mk;
                if (!kids.TryGetValue(parent, out var list)) kids[parent] = list = new List<string>();
                if (!list.Contains(r.EmpCode)) list.Add(r.EmpCode);
            }
            var start = codeByName.TryGetValue(hodKey, out var hc) ? hc : "name:" + hodKey;
            return Descendants(start, kids).Where(monthCodes.Contains).ToList();
        }

        private static List<string> Descendants(string root, Dictionary<string, List<string>> children)
        {
            var seen = new HashSet<string> { root };
            var result = new List<string>();
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var cur = stack.Pop();
                if (!children.TryGetValue(cur, out var list)) continue;
                foreach (var c in list)
                    if (seen.Add(c)) { result.Add(c); stack.Push(c); }
            }
            return result;
        }

        // ── rows ─────────────────────────────────────────────────────────────
        private record Plan(string Name, string? DayType, int? PlanIn, int? PlanOut, string? Mode, int Buffer, string? Remarks);
        private record Actual(int? In, int? Out, string? Status);

        public async Task<bool> HasDataForAsync(DateOnly date, CancellationToken ct = default)
        {
            if (await _db.P360DailyRecords.AnyAsync(r => r.Date == date, ct)) return true;
            var ds = date.ToString("yyyy-MM-dd");
            return await _db.AttendanceDailies.AnyAsync(a => a.Date == ds && (a.InTime != null || a.OutTime != null), ct);
        }

        public async Task<List<MtdRow>> BuildRowsAsync(IReadOnlyCollection<string>? codes, DateOnly upto, CancellationToken ct = default)
        {
            var from = MonthStart(upto);
            HashSet<string>? scope = codes == null ? null : codes.Select(NormCode).ToHashSet();

            // Roster for the month
            var rosterRows = await _db.RosterEntries.AsNoTracking()
                .Where(r => r.Date >= from && r.Date <= upto).ToListAsync(ct);
            var roster = new Dictionary<(string, DateOnly), Plan>();
            foreach (var r in rosterRows)
            {
                var c = NormCode(r.EmployeeCode);
                if (scope != null && !scope.Contains(c)) continue;
                roster[(c, r.Date)] = new Plan(r.EmployeeName, r.DayType, Mins(r.ShiftInTime), Mins(r.ShiftOutTime),
                                               r.Location, r.LateBufferMinutes, r.SpecialRemarks);
            }

            // Presence 360 actuals (latest upload wins)
            var p360 = await _db.P360DailyRecords.AsNoTracking()
                .Where(r => r.Date >= from && r.Date <= upto).ToListAsync(ct);
            var actual = new Dictionary<(string, DateOnly), Actual>();
            var p360Info = new Dictionary<string, P360DailyRecord>();
            foreach (var r in p360.OrderBy(x => x.UpdatedAt))
            {
                if (scope != null && !scope.Contains(r.EmpCode)) continue;
                actual[(r.EmpCode, r.Date)] = new Actual(Mins(r.InTime), Mins(r.OutTime), r.Status);
                p360Info[r.EmpCode] = r;
            }

            // HRMS attendance (Biometric API etc.) for days Presence 360 doesn't cover
            var fromS = from.ToString("yyyy-MM-dd"); var toS = upto.ToString("yyyy-MM-dd");
            var api = await _db.AttendanceDailies.AsNoTracking()
                .Where(a => string.Compare(a.Date, fromS) >= 0 && string.Compare(a.Date, toS) <= 0)
                .Select(a => new { a.EmployeeId, a.Date, a.InTime, a.OutTime, a.EffectiveStatus }).ToListAsync(ct);
            var emps = await _db.Employees.AsNoTracking()
                .Select(e => new { e.Id, e.EmpCode, e.Name, e.ReportingManagerId }).ToListAsync(ct);
            var codeById = emps.ToDictionary(e => e.Id, e => NormCode(e.EmpCode));
            foreach (var a in api)
            {
                if (!codeById.TryGetValue(a.EmployeeId, out var c)) continue;
                if (scope != null && !scope.Contains(c)) continue;
                if (!DateOnly.TryParseExact(a.Date, "yyyy-MM-dd", out var d)) continue;
                actual.TryAdd((c, d), new Actual(Mins(a.InTime), Mins(a.OutTime), a.EffectiveStatus));
            }

            // No roster for this month → build the plan from Presence 360 itself.
            if (roster.Count == 0)
            {
                foreach (var r in p360)
                {
                    if (scope != null && !scope.Contains(r.EmpCode)) continue;
                    var st = (r.Status ?? "").ToUpperInvariant();
                    string? dayType = st.StartsWith("WO") || st.StartsWith("POW") ? "Week Off"
                                    : st == "H" || st.StartsWith("POH") ? "Holiday" : null;
                    roster[(r.EmpCode, r.Date)] = new Plan(Regex.Replace(r.EmployeeName ?? "", @"\s+", " ").Trim(), dayType,
                        Mins(r.ShiftIn), Mins(r.ShiftOut), "OFFICE", 10, null);
                }
            }

            // Reports To: Master Sheet → HRMS Employee Master → Presence 360
            var master = (await _db.ReviewEmployees.AsNoTracking().ToListAsync(ct))
                .GroupBy(m => m.EmpCode).ToDictionary(g => g.Key, g => g.First());
            var nameById = emps.ToDictionary(e => e.Id, e => e.Name);
            var hrmsMgr = emps.Where(e => e.ReportingManagerId.HasValue && nameById.ContainsKey(e.ReportingManagerId.Value))
                .GroupBy(e => NormCode(e.EmpCode)).ToDictionary(g => g.Key, g => nameById[g.First().ReportingManagerId!.Value]);
            string? ReportsTo(string code) =>
                master.TryGetValue(code, out var m) ? m.ManagerName
                : hrmsMgr.TryGetValue(code, out var hm) ? hm
                : p360Info.TryGetValue(code, out var p) ? p.ManagerName : null;

            var rows = new List<MtdRow>();
            foreach (var ((code, date), plan) in roster)
            {
                actual.TryGetValue((code, date), out var act);
                rows.Add(Compute(code, date, plan, act, ReportsTo(code)));
            }
            return rows
                .OrderBy(r => r.Name, StringComparer.Ordinal)
                .ThenBy(r => r.Date)
                .ToList();
        }

        private static MtdRow Compute(string code, DateOnly date, Plan p, Actual? a, string? reportsTo)
        {
            var dayType = (p.DayType ?? "").Trim();
            var dl = dayType.ToLowerInvariant();
            string plan = dl.Length == 0 ? "Working"
                        : dl == "week off" || dl == "weekoff" ? "Week Off"
                        : dl == "holiday" ? "Holiday"
                        : dayType;
            bool halfDay  = dl.Contains("half");
            bool travel   = dl.Contains("travel");
            bool leave    = !halfDay && (dl.Contains("leave") || dl.Contains("comp"));
            bool restDay  = plan == "Week Off" || plan == "Holiday";
            bool working  = plan == "Working" || halfDay;

            var row = new MtdRow
            {
                Name = p.Name, Code = code, ReportsTo = reportsTo, Date = date, Plan = plan, Mode = p.Mode,
                Status = a?.Status, Grey = restDay || leave,
            };

            if (working || travel)
            {
                row.PlannedIn = p.PlanIn; row.PlannedOut = p.PlanOut;
                if (p.PlanIn.HasValue && p.PlanOut.HasValue)
                {
                    var full = p.PlanOut.Value - p.PlanIn.Value;
                    if (full < 0) full += 1440;
                    row.PlannedMins = halfDay ? full / 2 : full;
                }
            }

            int? inn = a?.In, outT = a?.Out;
            row.ActualIn = inn; row.ActualOut = outT;
            if (inn.HasValue && outT.HasValue && outT.Value > inn.Value) row.Worked = outT.Value - inn.Value;

            var gaps = new List<string>();
            if (working)
            {
                if (inn.HasValue && !outT.HasValue)
                {
                    row.Short = true;
                    gaps.Add($"Missed punch (only {Tm(inn.Value)})");
                }
                else if (inn.HasValue && row.Worked.HasValue)
                {
                    if (!halfDay && p.PlanIn.HasValue && inn.Value - p.PlanIn.Value > p.Buffer)
                    {
                        row.LateMin = inn.Value - p.PlanIn.Value;
                        row.Late = true;
                        gaps.Add($"Late {row.LateMin} min (in {Tm(inn.Value)})");
                    }
                    if (row.PlannedMins.HasValue)
                    {
                        row.Diff = row.Worked.Value - row.PlannedMins.Value;
                        if (row.Diff < 0)
                        {
                            row.Short = true; row.ShortMin = -row.Diff.Value;
                            gaps.Add($"Short {Hm(-row.Diff.Value)} hrs (worked {Hm(row.Worked.Value)})");
                        }
                        else if (row.Diff >= 30)
                        {
                            row.Extra = true; row.ExtraMin = row.Diff.Value;
                            gaps.Add($"Extra {Hm(row.Diff.Value)} hrs (worked {Hm(row.Worked.Value)})");
                        }
                    }
                }
                else if (!inn.HasValue)
                {
                    if (string.Equals((p.Mode ?? "").Trim(), "WFH", StringComparison.OrdinalIgnoreCase))
                        row.Note = "WFH – no punch";
                    else
                    {
                        row.Absent = true;
                        var st = (a?.Status ?? "").Trim();
                        gaps.Add("Absent – unplanned" + (st.Length > 0 && st != "A" ? $" [HRMS: {st}]" : ""));
                    }
                }
                if (halfDay) row.Note ??= "Half Day Leave";
            }
            else if (restDay)
            {
                if (row.Worked.HasValue && row.Worked.Value >= 30)
                {
                    row.Extra = true; row.ExtraMin = row.Worked.Value;
                    gaps.Add($"Worked on {plan} {Hm(row.Worked.Value)} hrs");
                }
            }
            else if (travel) row.Note = "Official Travel";
            else if (leave)  row.Note = "Planned leave";

            row.Gaps = gaps.Count > 0 ? string.Join("; ", gaps) : null;
            return row;
        }

        // ── workbook ─────────────────────────────────────────────────────────
        private static readonly XLColor HeaderFill = XLColor.FromHtml("#1F4E78");
        private static readonly XLColor GreyFill   = XLColor.FromHtml("#E7E6E6");
        private static readonly XLColor LateFill   = XLColor.FromHtml("#FCE4D6");
        private static readonly XLColor ShortFill  = XLColor.FromHtml("#FFF2CC");
        private static readonly XLColor ExtraFill  = XLColor.FromHtml("#E2EFDA");
        private static readonly XLColor AbsentFill = XLColor.FromHtml("#F8CBAD");

        private static readonly string[] SummaryHeaders =
        {
            "Employee", "Code", "Reports To", "Late (days)", "Late (total min)", "Hrs Not Completed (days)",
            "Short (total hrs)", "Extra Time (days)", "Extra (total hrs)", "Absent / Unplanned (days)", "Yesterday's Gaps"
        };
        private static readonly double[] SummaryWidths = { 28, 8, 24, 10, 11, 13, 11, 11, 11, 13, 55 };

        private static readonly string[] DetailHeaders =
        {
            "Employee", "Code", "Reports To", "Date", "Day", "Roster Plan", "Mode", "Planned In", "Planned Out",
            "Planned Hrs", "Actual In", "Actual Out", "Hours Worked", "Diff (hrs)", "HRMS Status", "Late (min)",
            "Late", "Hrs Not Completed", "Extra Time", "Absent / Unplanned", "Gap Details", "Note"
        };
        private static readonly double[] DetailWidths = { 26, 7, 22, 11, 6, 13, 8, 9, 9, 9, 9, 9, 9, 9, 12, 8, 7, 10, 8, 10, 45, 22 };

        public byte[] BuildWorkbook(List<MtdRow> rows, DateOnly upto)
        {
            using var wb = new XLWorkbook();
            BuildSummary(wb.Worksheets.Add("Summary"), rows, upto);
            BuildDetail(wb.Worksheets.Add("Daily Detail (MTD)"), rows);
            BuildDetail(wb.Worksheets.Add("Gaps Only"), rows.Where(r => r.Gaps != null).ToList());
            using var ms = new MemoryStream();
            wb.SaveAs(ms);
            return ms.ToArray();
        }

        private static void Header(IXLWorksheet ws, string[] headers, double[] widths)
        {
            for (int c = 0; c < headers.Length; c++)
            {
                var cell = ws.Cell(1, c + 1);
                cell.Value = headers[c];
                cell.Style.Font.Bold = true;
                cell.Style.Font.FontColor = XLColor.White;
                cell.Style.Fill.BackgroundColor = HeaderFill;
                cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
                cell.Style.Alignment.WrapText = true;
                ws.Column(c + 1).Width = widths[c];
            }
            ws.Row(1).Height = 32;
        }

        private static void Finish(IXLWorksheet ws, int lastRow, int cols)
        {
            var rng = ws.Range(1, 1, Math.Max(lastRow, 1), cols);
            rng.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            rng.Style.Border.InsideBorder  = XLBorderStyleValues.Thin;
            ws.SheetView.Freeze(1, 2);
            rng.SetAutoFilter();
        }

        private static void SetCode(IXLCell cell, string code)
        {
            if (long.TryParse(code, out var n)) cell.Value = (double)n;
            else cell.Value = code;
        }

        private static void BuildSummary(IXLWorksheet ws, List<MtdRow> rows, DateOnly upto)
        {
            Header(ws, SummaryHeaders, SummaryWidths);
            int r = 2;
            foreach (var g in rows.GroupBy(x => x.Code).OrderBy(g => g.First().Name, StringComparer.Ordinal))
            {
                var list = g.ToList();
                var first = list[0];
                int lateDays  = list.Count(x => x.Late),  lateMin  = list.Where(x => x.Late).Sum(x => x.LateMin ?? 0);
                int shortDays = list.Count(x => x.Short), shortMin = list.Sum(x => x.ShortMin);
                int extraDays = list.Count(x => x.Extra), extraMin = list.Sum(x => x.ExtraMin);
                int absDays   = list.Count(x => x.Absent);
                var yesterday = list.FirstOrDefault(x => x.Date == upto)?.Gaps;

                ws.Cell(r, 1).Value = first.Name;
                SetCode(ws.Cell(r, 2), first.Code);
                if (first.ReportsTo != null) ws.Cell(r, 3).Value = first.ReportsTo;
                ws.Cell(r, 4).Value = (double)lateDays;
                ws.Cell(r, 5).Value = (double)lateMin;
                ws.Cell(r, 6).Value = (double)shortDays;
                ws.Cell(r, 7).Value = Hm(shortMin);
                ws.Cell(r, 8).Value = (double)extraDays;
                ws.Cell(r, 9).Value = Hm(extraMin);
                ws.Cell(r, 10).Value = (double)absDays;
                if (yesterday != null) ws.Cell(r, 11).Value = yesterday;
                ws.Cell(r, 11).Style.Alignment.WrapText = true;

                if (lateDays  > 0) ws.Cell(r, 4).Style.Fill.BackgroundColor  = LateFill;
                if (shortDays > 0) ws.Cell(r, 6).Style.Fill.BackgroundColor  = ShortFill;
                if (extraDays > 0) ws.Cell(r, 8).Style.Fill.BackgroundColor  = ExtraFill;
                if (absDays   > 0) ws.Cell(r, 10).Style.Fill.BackgroundColor = AbsentFill;
                r++;
            }
            Finish(ws, r - 1, SummaryHeaders.Length);
        }

        private static void BuildDetail(IXLWorksheet ws, List<MtdRow> rows)
        {
            Header(ws, DetailHeaders, DetailWidths);
            int r = 2;
            foreach (var x in rows)
            {
                ws.Cell(r, 1).Value = x.Name;
                SetCode(ws.Cell(r, 2), x.Code);
                if (x.ReportsTo != null) ws.Cell(r, 3).Value = x.ReportsTo;
                ws.Cell(r, 4).Value = x.Date.ToDateTime(TimeOnly.MinValue);
                ws.Cell(r, 4).Style.DateFormat.Format = "dd-mmm-yy";
                ws.Cell(r, 5).Value = x.Date.ToString("ddd", CultureInfo.InvariantCulture);
                ws.Cell(r, 6).Value = x.Plan;
                if (x.Mode != null)        ws.Cell(r, 7).Value  = x.Mode;
                if (x.PlannedIn.HasValue)  ws.Cell(r, 8).Value  = Tm(x.PlannedIn.Value);
                if (x.PlannedOut.HasValue) ws.Cell(r, 9).Value  = Tm(x.PlannedOut.Value);
                if (x.PlannedMins.HasValue)ws.Cell(r, 10).Value = Hm(x.PlannedMins.Value);
                if (x.ActualIn.HasValue)   ws.Cell(r, 11).Value = Tm(x.ActualIn.Value);
                if (x.ActualOut.HasValue)  ws.Cell(r, 12).Value = Tm(x.ActualOut.Value);
                if (x.Worked.HasValue)     ws.Cell(r, 13).Value = Hm(x.Worked.Value);
                if (x.Diff.HasValue)       ws.Cell(r, 14).Value = Hm(x.Diff.Value);
                if (x.Status != null)      ws.Cell(r, 15).Value = x.Status;
                if (x.LateMin.HasValue)    ws.Cell(r, 16).Value = (double)x.LateMin.Value;
                if (x.Late)   ws.Cell(r, 17).Value = "Yes";
                if (x.Short)  ws.Cell(r, 18).Value = "Yes";
                if (x.Extra)  ws.Cell(r, 19).Value = "Yes";
                if (x.Absent) ws.Cell(r, 20).Value = "Yes";
                if (x.Gaps != null) ws.Cell(r, 21).Value = x.Gaps;
                if (x.Note != null) ws.Cell(r, 22).Value = x.Note;

                if (x.Grey)
                {
                    ws.Range(r, 1, r, DetailHeaders.Length).Style.Fill.BackgroundColor = GreyFill;
                }
                else
                {
                    XLColor? last = null;
                    if (x.Late)   { ws.Cell(r, 17).Style.Fill.BackgroundColor = LateFill;   last = LateFill; }
                    if (x.Short)  { ws.Cell(r, 18).Style.Fill.BackgroundColor = ShortFill;  last = ShortFill; }
                    if (x.Extra)  { ws.Cell(r, 19).Style.Fill.BackgroundColor = ExtraFill;  last = ExtraFill; }
                    if (x.Absent) { ws.Cell(r, 20).Style.Fill.BackgroundColor = AbsentFill; last = AbsentFill; }
                    if (last != null) ws.Cell(r, 21).Style.Fill.BackgroundColor = last;
                }
                r++;
            }
            Finish(ws, r - 1, DetailHeaders.Length);
        }

        // ── uploads ──────────────────────────────────────────────────────────
        // Roster: every month sheet of Attendance Management.xlsx
        // (Name | Employee ID | Date | Holiday/Week Off/Leave/ Official Travel |
        //  In Time | Out Time | WFH/STORE/OFFICE | Late Coming Buffer | … | Special Remarks)
        public async Task<(int Rows, List<string> Months)> ImportRosterAsync(Stream xlsx, CancellationToken ct = default)
        {
            using var wb = new XLWorkbook(xlsx);
            var entries = new List<RosterEntry>();
            var months = new List<string>();

            foreach (var ws in wb.Worksheets)
            {
                if (ws.Name.StartsWith("How", StringComparison.OrdinalIgnoreCase)) continue;
                var hdr = ws.Row(1);
                int Col(Func<string, bool> match)
                {
                    var lastCol = ws.LastColumnUsed()?.ColumnNumber() ?? 0;
                    for (int c = 1; c <= lastCol; c++)
                        if (match(hdr.Cell(c).GetString().Trim().ToLowerInvariant())) return c;
                    return 0;
                }
                int cName = Col(h => h == "name" || h == "employee name");
                int cCode = Col(h => h.Contains("employee id") || h == "employee code" || h == "emp code");
                int cDate = Col(h => h == "date");
                int cType = Col(h => h.Contains("holiday") || h.Contains("week off"));
                int cIn   = Col(h => h == "in time");
                int cOut  = Col(h => h == "out time");
                int cMode = Col(h => h.Contains("wfh") || h.Contains("store") || h == "mode");
                int cBuf  = Col(h => h.Contains("buffer"));
                int cRem  = Col(h => h.Contains("remark"));
                if (cCode == 0 || cDate == 0) continue;   // not a roster sheet

                var lastRow = ws.LastRowUsed()?.RowNumber() ?? 1;
                int count = 0;
                for (int r = 2; r <= lastRow; r++)
                {
                    var row = ws.Row(r);
                    var code = NormCode(ReadText(row.Cell(cCode)));
                    var date = ReadDate(row.Cell(cDate));
                    if (code.Length == 0 || date == null) continue;

                    var type = cType > 0 ? ReadText(row.Cell(cType)).Trim() : "";
                    var buf = cBuf > 0 ? ReadNumber(row.Cell(cBuf)) : null;
                    entries.Add(new RosterEntry
                    {
                        EmployeeName      = cName > 0 ? ReadText(row.Cell(cName)).Trim() : "",
                        EmployeeCode      = code,
                        Date              = date.Value,
                        DayType           = type.Length > 0 ? Truncate(type, 40) : null,
                        ShiftInTime       = cIn  > 0 ? ReadTime(row.Cell(cIn))  : null,
                        ShiftOutTime      = cOut > 0 ? ReadTime(row.Cell(cOut)) : null,
                        Location          = cMode > 0 ? NullIfEmpty(ReadText(row.Cell(cMode)).Trim().ToUpperInvariant()) : null,
                        LateBufferMinutes = buf.HasValue ? (int)Math.Round(buf.Value) : 10,
                        SpecialRemarks    = cRem > 0 ? NullIfEmpty(Truncate(ReadText(row.Cell(cRem)).Trim(), 300)) : null,
                        Month             = Truncate(ws.Name, 20),
                        CreatedAt         = DateTime.UtcNow,
                    });
                    count++;
                }
                if (count > 0) months.Add(ws.Name);
            }

            if (entries.Count == 0)
                throw new InvalidDataException("No roster rows found — expected month sheets with Name, Employee ID, Date, In Time, Out Time columns.");

            // Replace the roster for the dates covered by this file.
            var minD = entries.Min(e => e.Date); var maxD = entries.Max(e => e.Date);
            await _db.RosterEntries.Where(r => r.Date >= minD && r.Date <= maxD).ExecuteDeleteAsync(ct);
            foreach (var chunk in entries.Chunk(1000))
            {
                _db.RosterEntries.AddRange(chunk);
                await _db.SaveChangesAsync(ct);
                _db.ChangeTracker.Clear();
            }
            _log.LogInformation("Roster uploaded: {Rows} rows, {From}–{To}", entries.Count, minD, maxD);
            return (entries.Count, months);
        }

        // Master Sheet: "Employee Master" — Employee Code | Employee Name | Manager Name | Manager Emp Code
        public async Task<int> ImportMasterAsync(Stream xlsx, CancellationToken ct = default)
        {
            using var wb = new XLWorkbook(xlsx);
            var ws = wb.Worksheets.FirstOrDefault(w => w.Name.Trim().Equals("Employee Master", StringComparison.OrdinalIgnoreCase))
                     ?? wb.Worksheets.First();

            int hdrRow = 0, cCode = 0, cName = 0, cMgr = 0, cMgrCode = 0;
            for (int r = 1; r <= 6 && hdrRow == 0; r++)
            {
                var lastCell = ws.Row(r).LastCellUsed()?.Address.ColumnNumber ?? 0;
                for (int c = 1; c <= lastCell; c++)
                {
                    var h = ws.Cell(r, c).GetString().Trim().ToLowerInvariant();
                    if (h == "employee code") { hdrRow = r; cCode = c; }
                    else if (h == "employee name") cName = c;
                    else if (h == "manager name") cMgr = c;
                    else if (h == "manager emp code" || h == "manager code") cMgrCode = c;
                }
            }
            if (hdrRow == 0 || cName == 0)
                throw new InvalidDataException("Couldn't find the 'Employee Code' / 'Employee Name' header in the Employee Master sheet.");

            var list = new List<ReviewEmployee>();
            var lastRow = ws.LastRowUsed()?.RowNumber() ?? hdrRow;
            for (int r = hdrRow + 1; r <= lastRow; r++)
            {
                var code = NormCode(ReadText(ws.Cell(r, cCode)));
                var name = ReadText(ws.Cell(r, cName)).Trim();
                if (code.Length == 0 || name.Length == 0) continue;
                list.Add(new ReviewEmployee
                {
                    EmpCode     = code,
                    Name        = Truncate(name, 120),
                    ManagerName = cMgr > 0 ? NullIfEmpty(Truncate(ReadText(ws.Cell(r, cMgr)).Trim(), 120)) : null,
                    ManagerCode = cMgrCode > 0 ? NullIfEmpty(NormCode(ReadText(ws.Cell(r, cMgrCode)))) : null,
                    UpdatedAt   = DateTime.UtcNow,
                });
            }
            if (list.Count == 0) throw new InvalidDataException("The Employee Master sheet has no employee rows.");

            await _db.ReviewEmployees.ExecuteDeleteAsync(ct);
            _db.ReviewEmployees.AddRange(list);
            await _db.SaveChangesAsync(ct);
            return list.Count;
        }

        public async Task<ReviewDataStatus> GetStatusAsync(CancellationToken ct = default)
        {
            var rosterCount = await _db.RosterEntries.CountAsync(ct);
            DateOnly? rFrom = rosterCount > 0 ? await _db.RosterEntries.MinAsync(r => r.Date, ct) : null;
            DateOnly? rTo   = rosterCount > 0 ? await _db.RosterEntries.MaxAsync(r => r.Date, ct) : null;
            var months = await _db.RosterEntries.Select(r => r.Month).Distinct().ToListAsync(ct);
            var master = await _db.ReviewEmployees.CountAsync(ct);
            DateOnly? p360 = await _db.P360DailyRecords.AnyAsync(ct)
                ? await _db.P360DailyRecords.MaxAsync(r => r.Date, ct) : null;
            return new ReviewDataStatus(rosterCount, rFrom, rTo, months, master, p360);
        }

        // ── cell readers (Excel stores times as day fractions) ──────────────
        private static string ReadText(IXLCell c)
        {
            var v = c.Value;
            if (v.IsBlank) return "";
            if (v.IsNumber) { var n = v.GetNumber(); return n == Math.Floor(n) ? ((long)n).ToString(CultureInfo.InvariantCulture) : n.ToString(CultureInfo.InvariantCulture); }
            if (v.IsText) return v.GetText();
            return c.GetFormattedString();
        }

        private static double? ReadNumber(IXLCell c)
        {
            var v = c.Value;
            if (v.IsNumber) return v.GetNumber();
            if (v.IsText && double.TryParse(v.GetText(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d)) return d;
            return null;
        }

        private static DateOnly? ReadDate(IXLCell c)
        {
            var v = c.Value;
            if (v.IsDateTime) return DateOnly.FromDateTime(v.GetDateTime());
            if (v.IsNumber)
            {
                var n = v.GetNumber();
                if (n > 20000 && n < 80000) return DateOnly.FromDateTime(DateTime.FromOADate(n));
                return null;
            }
            if (v.IsText)
            {
                var s = v.GetText().Trim();
                string[] fmts = { "dd-MMM-yyyy", "d-MMM-yyyy", "dd/MM/yyyy", "d/M/yyyy", "yyyy-MM-dd", "dd-MM-yyyy", "dd-MMM-yy" };
                if (DateTime.TryParseExact(s, fmts, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                    return DateOnly.FromDateTime(dt);
            }
            return null;
        }

        private static TimeOnly? ReadTime(IXLCell c)
        {
            var v = c.Value;
            if (v.IsBlank) return null;
            TimeSpan ts;
            if (v.IsDateTime) ts = v.GetDateTime().TimeOfDay;
            else if (v.IsTimeSpan) ts = v.GetTimeSpan();
            else if (v.IsNumber) ts = TimeSpan.FromDays(v.GetNumber() - Math.Floor(v.GetNumber()));
            else if (v.IsText)
            {
                var s = v.GetText().Trim();
                if (!TimeSpan.TryParse(s, CultureInfo.InvariantCulture, out ts))
                {
                    if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)) ts = dt.TimeOfDay;
                    else return null;
                }
            }
            else return null;

            var minutes = (int)Math.Round(ts.TotalMinutes) % 1440;
            if (minutes < 0) minutes += 1440;
            return new TimeOnly(minutes / 60, minutes % 60);
        }

        private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
        private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n];
    }
}
