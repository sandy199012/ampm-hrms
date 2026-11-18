using System.Globalization;
using System.Text.RegularExpressions;
using AmpmHrmsPro.Data;
using AmpmHrmsPro.Models;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;

namespace AmpmHrmsPro.Services
{
    // ═══════════════════════════════════════════════════════════════════
    // LEAVE / REGULARIZATION / WORK FROM HOME / ON DUTY — Presence 360 reports
    //
    // Import: the four Presence 360 exports ("Leave Report", "Regularization
    // Data Report", "Work From Home Data Report", "On Duty Data Report") are
    // read into the Applications table (type detected from the columns).
    // Re-uploading a newer export updates the same requests (e.g. Pending →
    // Approved). Approved requests then show in attendance:
    //   Leave → L (CL) / HD (L-CL) · Regularization → corrected in/out
    //   WFH → P (WFH) · On Duty → P (OD)
    //
    // Export: the same four reports, column for column, from HRMS data —
    // imported requests and requests applied in HRMS / the mobile app alike.
    // ═══════════════════════════════════════════════════════════════════
    public static class ApplicationReports
    {
        public const string SourceP360 = "P360Import";
        public static readonly SemaphoreSlim RecomputeLock = new(1, 1);

        public static readonly string[] LeaveCols =
        { "ID", "Employee Name", "Employee Code", "Department", "Manager Name", "Leave Type", "leave Duration", "From date", "To Date",
          "Comp Off Date", "No Of Leave", "Pending At", "Approval Level", "Status", "Apply Date and Time" };
        public static readonly string[] RegCols =
        { "#", "Employee Name", "Employee Code", "Punch In Time", "Punch Out Time", "Requested Punch In Date/Time", "Requested Punch Out Date/Time",
          "Pending At", "Approval Level", "Request Reason", "Approver Remarks", "Attachment", "Status", "Submission Date", "Applied By", "All Levels Approver" };
        public static readonly string[] WfhCols =
        { "#", "Employee Name", "Employee Code", "Request From Date", "Request To Date", "Pending At", "Approval Level", "From Location",
          "Request Reason", "Approver Remarks", "Status", "Submission Date", "Applied By", "All Levels Approver" };
        public static readonly string[] OdCols =
        { "#", "Employee Name", "Employee Code", "Requested Punch In Date/Time", "Requested Punch Out Date/Time", "Pending At", "Approval Level",
          "From Location", "To Location", "Duration", "Compensate As", "Customer Name", "Request Reason", "Approver Remarks", "Status",
          "Submission Date", "Applied By", "All Levels Approver" };

        public static string Title(string type) => type switch
        {
            "Leave" => "Leave Report", "Regularisation" => "Regularization Data Report",
            "WFH" => "Work From Home Data Report", "OD" => "On Duty Data Report", _ => type
        };

        // ── Import ───────────────────────────────────────────────────────
        public class ImportResult
        {
            public Dictionary<string, int> Created { get; } = new();
            public Dictionary<string, int> Updated { get; } = new();
            public List<string> Files { get; } = new();
            public List<string> NotFound { get; } = new();
            public List<string> Warnings { get; } = new();
            public List<string> LeaveTypesCreated { get; } = new();
            public List<(int EmployeeId, DateTime From, DateTime To)> ToRecompute { get; } = new();
            public int Total => Created.Values.Sum() + Updated.Values.Sum();
        }

        static string NameKey(string? s) => Regex.Replace((s ?? "").ToUpperInvariant(), "[^A-Z]", "");
        static string CodeKey(string? c) { var t = (c ?? "").Trim(); if (t.EndsWith(".0")) t = t[..^2]; t = t.TrimStart('0'); return t.ToUpperInvariant(); }

        static string? Text(IXLCell c)
        {
            if (c.IsEmpty()) return null;
            var v = c.Value;
            string s = v.IsNumber ? v.GetNumber().ToString(CultureInfo.InvariantCulture)
                     : v.IsDateTime ? v.GetDateTime().ToString("dd-MM-yyyy HH:mm:ss", CultureInfo.InvariantCulture)
                     : c.GetString();
            s = s.Trim();
            return s.Length == 0 || s == "-" ? null : s;
        }

        static readonly string[] DateFormats = { "dd-MM-yyyy", "d-M-yyyy", "dd/MM/yyyy", "d/M/yyyy", "yyyy-MM-dd", "dd-MMM-yyyy" };
        static readonly string[] DateTimeFormats =
        { "dd-MM-yyyy HH:mm:ss", "dd-MM-yyyy HH:mm", "d-M-yyyy H:mm:ss", "d-M-yyyy H:mm", "dd/MM/yyyy HH:mm:ss", "dd/MM/yyyy HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm" };

        static DateTime? ParseDate(IXLCell c)
        {
            if (c.IsEmpty()) return null;
            if (c.Value.IsDateTime) return c.Value.GetDateTime();
            if (c.Value.IsNumber) { var n = c.Value.GetNumber(); return n > 20000 && n < 80000 ? DateTime.FromOADate(n) : null; }
            return ParseDateText(c.GetString());
        }

        /// <summary>"22-10-2026", "22-10-2026 09:30", "2026-10-22 09:30:00", short leave "22-10-2026 (09:30)".</summary>
        public static DateTime? ParseDateText(string? text)
        {
            var s = (text ?? "").Trim();
            if (s.Length == 0 || s == "-") return null;
            var m = Regex.Match(s, @"^(.+?)\s*\((\d{1,2}:\d{2})\)$");
            if (m.Success) s = m.Groups[1].Value + " " + m.Groups[2].Value;
            if (DateTime.TryParseExact(s, DateTimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)) return dt;
            if (DateTime.TryParseExact(s, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out dt)) return dt;
            if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out dt)) return dt;
            return null;
        }

        static string? TimeText(IXLCell c)
        {
            if (c.IsEmpty()) return null;
            if (c.Value.IsDateTime) return c.Value.GetDateTime().ToString("HH:mm:ss");
            if (c.Value.IsTimeSpan) return c.Value.GetTimeSpan().ToString(@"hh\:mm\:ss");
            if (c.Value.IsNumber) { var n = c.Value.GetNumber(); return TimeSpan.FromDays(n - Math.Floor(n)).ToString(@"hh\:mm\:ss"); }
            var s = c.GetString().Trim();
            return s.Length == 0 || s == "-" ? null : s;
        }

        static string MapStatus(string? s) => (s ?? "").Trim().ToLowerInvariant() switch
        {
            "approved" => "Approved", "rejected" => "Rejected", "revoked" => "Revoked",
            "cancelled" or "canceled" or "withdrawn" => "Cancelled", _ => "Pending",
        };

        static string DayPartFor(string? duration) => (duration ?? "").ToLowerInvariant() switch
        {
            var d when d.Contains("first half") => "FirstHalf",
            var d when d.Contains("second half") => "SecondHalf",
            _ => "Single",
        };

        /// <summary>"ASHOK KUMAR(approved)\nX(pending)" → the approver who decided last.</summary>
        static string? DecidedBy(string? chain)
        {
            if (string.IsNullOrWhiteSpace(chain)) return null;
            var m = Regex.Matches(chain, @"([^\n()]+)\((approved|rejected|revoked)\)", RegexOptions.IgnoreCase);
            return m.Count > 0 ? m[^1].Groups[1].Value.Trim() : null;
        }

        // ── One report row, whether it came from an Excel export or the API ──
        public interface IReportRow
        {
            bool Has(string header);
            string? Text(string header);
            DateTime? Date(string header);
            string? Time(string header);
        }

        sealed class ExcelRow : IReportRow
        {
            readonly IXLWorksheet _ws; readonly int _r; readonly Dictionary<string, int> _col;
            public ExcelRow(IXLWorksheet ws, int r, Dictionary<string, int> col) { _ws = ws; _r = r; _col = col; }
            public bool Has(string h) => _col.ContainsKey(h);
            IXLCell? Cell(string h) => _col.TryGetValue(h, out var c) ? _ws.Cell(_r, c) : null;
            public string? Text(string h) => Cell(h) is { } c ? ApplicationReports.Text(c) : null;
            public DateTime? Date(string h) => Cell(h) is { } c ? ParseDate(c) : null;
            public string? Time(string h) => Cell(h) is { } c ? TimeText(c) : null;
        }

        /// <summary>A row built from an API record, keyed by the same column names as the Excel report.</summary>
        public sealed class DictRow : IReportRow
        {
            readonly Dictionary<string, string?> _d;
            public DictRow(Dictionary<string, string?> d) => _d = new Dictionary<string, string?>(d, StringComparer.OrdinalIgnoreCase);
            public bool Has(string h) => _d.ContainsKey(h);
            public string? Text(string h)
            {
                if (!_d.TryGetValue(h, out var v) || v == null) return null;
                v = v.Trim();
                return v.Length == 0 || v == "-" ? null : v;
            }
            public DateTime? Date(string h) => ParseDateText(Text(h));
            public string? Time(string h)
            {
                var t = Text(h);
                if (t == null) return null;
                var d = ParseDateText(t);
                return d.HasValue ? d.Value.ToString("HH:mm:ss") : t;
            }
        }

        public static Task<ImportResult> ImportAsync(AppDbContext db, IEnumerable<(string FileName, Stream Data)> files) =>
            new Importer(db, SourceP360).ImportFilesAsync(files);

        /// <summary>Same upsert as the Excel import, for rows fetched from the Presence 360 API.</summary>
        public static async Task<ImportResult> ImportRowsAsync(AppDbContext db, IEnumerable<(string Type, IReportRow Row)> rows, string source, string label)
        {
            var imp = new Importer(db, source);
            await imp.LoadAsync();
            var n = 0;
            foreach (var (type, row) in rows)
            {
                n++;
                imp.Result.Created.TryAdd(type, 0); imp.Result.Updated.TryAdd(type, 0);
                imp.AddRow(type, row, $"{label} #{n}");
            }
            return await imp.FinishAsync();
        }

        sealed class Importer
        {
            readonly AppDbContext db;
            readonly string source;
            public readonly ImportResult Result = new();
            Dictionary<string, int> byCode = new();
            Dictionary<string, int> byName = new();
            List<LeaveType> leaveTypes = new();
            List<Application> existing = new();
            readonly HashSet<string> notFound = new();

            public Importer(AppDbContext db, string source) { this.db = db; this.source = source; }

            public async Task LoadAsync()
            {
                var emps = await db.Employees.AsNoTracking().Select(e => new { e.Id, e.EmpCode, e.Name, e.IsActive }).ToListAsync();
                byCode = new Dictionary<string, int>();
                foreach (var e in emps.OrderByDescending(e => e.IsActive)) byCode.TryAdd(CodeKey(e.EmpCode), e.Id);
                byName = emps.GroupBy(e => NameKey(e.Name)).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.First().Id);
                leaveTypes = await db.LeaveTypes.ToListAsync();
                existing = await db.Applications.ToListAsync();
            }

            public async Task<ImportResult> FinishAsync()
            {
                Result.NotFound.AddRange(notFound.OrderBy(x => x));
                await db.SaveChangesAsync();
                return Result;
            }

            int? Emp(string? code, string? name)
            {
                if (byCode.TryGetValue(CodeKey(code), out var id)) return id;
                if (byName.TryGetValue(NameKey(name), out id)) return id;
                notFound.Add($"{code} — {name}");
                return null;
            }
            int? Person(string? name) => byName.TryGetValue(NameKey(name), out var id) ? id : null;

            LeaveType LeaveTypeFor(string name)
            {
                var key = NameKey(name).Replace("LEAVE", "");
                // "Bereavement Leave (Bl)" → alias BL; active types win over switched-off duplicates
                var paren = Regex.Match(name, @"\(([^)]+)\)\s*$");
                var parenKey = paren.Success ? NameKey(paren.Groups[1].Value) : null;
                LeaveType? Find(IEnumerable<LeaveType> pool) =>
                    pool.FirstOrDefault(t => NameKey(t.Name).Replace("LEAVE", "") == key)
                    ?? pool.FirstOrDefault(t => NameKey(t.Alias) == key)
                    ?? (parenKey == null ? null : pool.FirstOrDefault(t => NameKey(t.Alias) == parenKey));
                var lt = Find(leaveTypes.Where(t => t.IsActive)) ?? Find(leaveTypes);
                if (lt != null) return lt;
                var words = Regex.Split(name.Trim(), @"\s+").Where(w => w.Length > 0).ToList();
                var alias = string.Concat(words.Select(w => char.ToUpperInvariant(w[0])));
                if (alias.Length < 2) alias = NameKey(name)[..Math.Min(3, NameKey(name).Length)];
                while (leaveTypes.Any(t => t.Alias == alias)) alias += "X";
                lt = new LeaveType
                {
                    Name = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name.Trim().ToLowerInvariant()),
                    Alias = alias.Length > 10 ? alias[..10] : alias, Frequency = "On Request",
                };
                db.LeaveTypes.Add(lt); leaveTypes.Add(lt);
                Result.LeaveTypesCreated.Add($"{lt.Name} ({lt.Alias})");
                return lt;
            }

            public async Task<ImportResult> ImportFilesAsync(IEnumerable<(string FileName, Stream Data)> files)
            {
                await LoadAsync();
                foreach (var (fileName, data) in files)
                {
                    using var wb = new XLWorkbook(data);
                    var ws = wb.Worksheets.First();
                    // header row = first row that has an "Employee Code" cell
                    int hdr = 0;
                    for (int r = 1; r <= 10 && hdr == 0; r++)
                        if (ws.Row(r).CellsUsed().Any(c => c.GetString().Trim().Equals("Employee Code", StringComparison.OrdinalIgnoreCase))) hdr = r;
                    if (hdr == 0) { Result.Warnings.Add($"{fileName}: no 'Employee Code' column — skipped."); continue; }

                    var col = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    foreach (var c in ws.Row(hdr).CellsUsed()) col.TryAdd(c.GetString().Trim(), c.Address.ColumnNumber);

                    string type = col.ContainsKey("Leave Type") ? "Leave"
                                : col.ContainsKey("Request From Date") ? "WFH"
                                : col.ContainsKey("Punch In Time") && col.ContainsKey("Requested Punch In Date/Time") ? "Regularisation"
                                : col.ContainsKey("Requested Punch In Date/Time") ? "OD"
                                : "";
                    if (type == "") { Result.Warnings.Add($"{fileName}: not a Leave / Regularization / WFH / On Duty report — skipped."); continue; }
                    Result.Files.Add($"{Title(type)} — {fileName}");
                    Result.Created.TryAdd(type, 0); Result.Updated.TryAdd(type, 0);

                    var last = ws.LastRowUsed()?.RowNumber() ?? hdr;
                    for (int r = hdr + 1; r <= last; r++)
                        AddRow(type, new ExcelRow(ws, r, col), $"{Title(type)} row {r}");
                }
                return await FinishAsync();
            }

            public void AddRow(string type, IReportRow row, string label)
            {
                var code = row.Text("Employee Code");
                var name = row.Text("Employee Name");
                if (code == null && name == null) return;
                var empId = Emp(code, name);
                if (empId == null) return;

                DateTime? from, to; TimeSpan? reqIn = null, reqOut = null;
                if (type == "Leave")
                {
                    var a = row.Date("From date");
                    var b = row.Date("To Date");
                    from = a?.Date; to = b?.Date;
                    // Short leave carries times ("22-10-2026 (09:30)" → 09:30–11:30)
                    if (a.HasValue && a.Value.TimeOfDay > TimeSpan.Zero) reqIn = a.Value.TimeOfDay;
                    if (b.HasValue && b.Value.TimeOfDay > TimeSpan.Zero) reqOut = b.Value.TimeOfDay;
                }
                else if (type == "WFH") { from = row.Date("Request From Date"); to = row.Date("Request To Date"); }
                else
                {
                    var a = row.Date("Requested Punch In Date/Time");
                    var b = row.Date("Requested Punch Out Date/Time");
                    from = a?.Date; to = (type == "OD" ? b?.Date : a?.Date) ?? from;
                    if (type == "Regularisation")
                    {
                        if (a.HasValue && a.Value.TimeOfDay > TimeSpan.Zero) reqIn = a.Value.TimeOfDay;
                        if (b.HasValue && b.Value.TimeOfDay > TimeSpan.Zero) reqOut = b.Value.TimeOfDay;
                    }
                }
                if (from == null) { Result.Warnings.Add($"{label}: {name} — date missing, skipped."); return; }
                to ??= from;
                if (to < from) to = from;

                var submittedCol = type == "Leave" ? "Apply Date and Time" : "Submission Date";
                var submitted = row.Date(submittedCol) ?? DateTime.Now;
                var fromS = from.Value.ToString("yyyy-MM-dd");

                // Same request = same employee, type, start date and submission time.
                // (Excel exports carry seconds, the API only minutes → match within a minute.)
                var app = existing.FirstOrDefault(x => x.EmployeeId == empId && x.Type == type && x.FromDate == fromS
                                                       && Math.Abs((x.AppliedOn - submitted).TotalSeconds) < 60);
                bool isNew = app == null;
                if (isNew)
                {
                    app = new Application { EmployeeId = empId.Value, Type = type, Source = source, CreatedAt = DateTime.Now };
                    db.Applications.Add(app); existing.Add(app);
                }
                var oldStatus = app!.Status;
                app.FromDate = fromS;
                app.ToDate = to.Value.ToString("yyyy-MM-dd");
                app.AppliedOn = submitted;
                app.Status = MapStatus(row.Text("Status"));
                // Only overwrite what this source actually carries (the API has
                // fewer columns than the Excel export — keep the rest).
                if (row.Has("Pending At")) app.PendingAt = Trunc(row.Text("Pending At"), 100);
                if (row.Has("Approval Level")) app.ApprovalLevel = Trunc(row.Text("Approval Level"), 10);
                if (row.Has("All Levels Approver")) app.ApproverChain = Trunc(row.Text("All Levels Approver")?.Replace("\n", " ").Trim(), 500);
                if (row.Has("Applied By")) app.AppliedBy = Trunc(row.Text("Applied By"), 120);
                if (row.Has("Approver Remarks")) app.Remarks = Trunc(row.Text("Approver Remarks"), 500);
                app.ApproverEmployeeId = Person(app.PendingAt) ?? Person(DecidedBy(app.ApproverChain)) ?? app.ApproverEmployeeId;
                if (app.Status != "Pending")
                {
                    app.DecisionByEmployeeId = Person(DecidedBy(app.ApproverChain)) ?? app.DecisionByEmployeeId;
                    app.DecisionAt ??= submitted;
                }

                switch (type)
                {
                    case "Leave":
                        var ltName = row.Text("Leave Type") ?? "Leave";
                        var lt = LeaveTypeFor(ltName);
                        app.LeaveType = lt;
                        if (lt.Id > 0) app.LeaveTypeId = lt.Id;
                        if (row.Has("leave Duration")) app.DurationLabel = Trunc(row.Text("leave Duration"), 30);
                        app.DayPart = DayPartFor(app.DurationLabel);
                        app.DurationDays = decimal.TryParse(row.Text("No Of Leave"), NumberStyles.Any, CultureInfo.InvariantCulture, out var n)
                            ? n : (decimal)((to.Value - from.Value).TotalDays + 1);
                        if (row.Has("Comp Off Date")) app.CompOffDate = row.Date("Comp Off Date")?.ToString("yyyy-MM-dd");
                        app.RequestedInTime = reqIn;
                        app.RequestedOutTime = reqOut;
                        app.Reason ??= ltName;
                        break;
                    case "WFH":
                        if (row.Has("From Location")) app.FromLocation = Trunc(row.Text("From Location"), 300);
                        if (row.Has("Request Reason")) app.Reason = Trunc(row.Text("Request Reason"), 500);
                        app.DurationDays = (decimal)((to.Value - from.Value).TotalDays + 1);
                        break;
                    case "OD":
                        if (row.Has("From Location")) app.FromLocation = Trunc(row.Text("From Location"), 300);
                        if (row.Has("To Location")) app.ToLocation = Trunc(row.Text("To Location"), 300);
                        if (row.Has("Duration")) app.DurationLabel = Trunc(row.Text("Duration"), 30);
                        app.DayPart = DayPartFor(app.DurationLabel);
                        if (row.Has("Compensate As")) app.CompensateAs = Trunc(row.Text("Compensate As"), 40);
                        if (row.Has("Customer Name")) app.CustomerName = Trunc(row.Text("Customer Name"), 150);
                        if (row.Has("Request Reason")) app.Reason = Trunc(row.Text("Request Reason"), 500);
                        app.DurationDays = app.DayPart == "Single" ? (decimal)((to.Value - from.Value).TotalDays + 1) : 0.5m;
                        break;
                    case "Regularisation":
                        if (row.Has("Punch In Time")) app.OriginalInTime = Trunc(row.Time("Punch In Time"), 10);
                        if (row.Has("Punch Out Time")) app.OriginalOutTime = Trunc(row.Time("Punch Out Time"), 10);
                        app.RequestedInTime = reqIn;
                        app.RequestedOutTime = reqOut;
                        if (row.Has("Attachment")) app.Attachment = Trunc(row.Text("Attachment"), 300);
                        if (row.Has("Request Reason")) app.Reason = Trunc(row.Text("Request Reason"), 500);
                        app.DurationDays = 1;
                        break;
                }

                if (isNew) Result.Created[type]++; else Result.Updated[type]++;
                if (app.Status == "Approved" || oldStatus == "Approved")
                    Result.ToRecompute.Add((empId.Value, from.Value.Date, to.Value.Date));
            }
        }

        static string? Trunc(string? s, int n) => s == null ? null : s.Length > n ? s[..n] : s;

        // ── Export (same columns as the Presence 360 reports) ───────────
        static string D(string? ymd) =>
            DateTime.TryParseExact(ymd, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.ToString("dd-MM-yyyy") : (ymd ?? "");
        static string DT(DateTime d) => d.ToString("dd-MM-yyyy HH:mm:ss", CultureInfo.InvariantCulture);
        static string Dash(string? s) => string.IsNullOrWhiteSpace(s) ? "-" : s;

        public static string LeaveDurationLabel(Application a) =>
            a.DurationLabel ?? (a.DayPart == "FirstHalf" ? "First Half Day" : a.DayPart == "SecondHalf" ? "Second Half Day" : a.FromDate == a.ToDate ? "Single" : "Multiple");
        public static string PendingAt(Application a) => a.Status == "Pending" ? (a.PendingAt ?? a.Approver?.Name ?? "-") : "-";
        public static string Level(Application a) => a.Status == "Pending" ? (a.ApprovalLevel ?? "1") : "-";
        public static string Chain(Application a) =>
            a.ApproverChain ?? ((a.Status == "Pending" ? a.Approver?.Name : a.DecisionBy?.Name ?? a.Approver?.Name) is string n ? $"{n}({a.Status.ToLowerInvariant()})" : "");

        public static List<object?[]> Rows(string type, List<Application> apps)
        {
            int i = 0;
            return apps.Select(a =>
            {
                i++;
                var e = a.Employee!;
                return type switch
                {
                    "Leave" => new object?[] { i, e.Name, e.EmpCode, e.Department?.Name, e.ReportingManager?.Name, a.LeaveType?.Name, LeaveDurationLabel(a),
                        a.RequestedInTime.HasValue ? $"{D(a.FromDate)} ({a.RequestedInTime.Value:hh\\:mm})" : D(a.FromDate),
                        a.RequestedOutTime.HasValue ? $"{D(a.ToDate)} ({a.RequestedOutTime.Value:hh\\:mm})" : D(a.ToDate), a.CompOffDate != null ? D(a.CompOffDate) : null, a.DurationDays, PendingAt(a), Level(a), a.Status, DT(a.AppliedOn) },
                    "Regularisation" => new object?[] { i, e.Name, e.EmpCode, a.OriginalInTime, a.OriginalOutTime,
                        a.RequestedInTime.HasValue ? $"{D(a.FromDate)} {a.RequestedInTime.Value:hh\\:mm\\:ss}" : D(a.FromDate),
                        a.RequestedOutTime.HasValue ? $"{D(a.ToDate)} {a.RequestedOutTime.Value:hh\\:mm\\:ss}" : null,
                        PendingAt(a), Level(a), a.Reason, a.Remarks, a.Attachment, a.Status, DT(a.AppliedOn), a.AppliedBy ?? e.Name, Chain(a) },
                    "WFH" => new object?[] { i, e.Name, e.EmpCode, D(a.FromDate), D(a.ToDate), PendingAt(a), Level(a), a.FromLocation,
                        a.Reason, a.Remarks, a.Status, DT(a.AppliedOn), a.AppliedBy ?? e.Name, Chain(a) },
                    _ => new object?[] { i, e.Name, e.EmpCode, D(a.FromDate), D(a.ToDate), PendingAt(a), Level(a), a.FromLocation, a.ToLocation,
                        a.DurationLabel ?? (a.DayPart == "FirstHalf" ? "First Half" : a.DayPart == "SecondHalf" ? "Second Half" : "Full Day"),
                        a.CompensateAs ?? "attendance", Dash(a.CustomerName), a.Reason, a.Remarks, a.Status, DT(a.AppliedOn), a.AppliedBy ?? e.Name, Chain(a) },
                };
            }).ToList();
        }

        public static string[] Columns(string type) => type switch
        { "Leave" => LeaveCols, "Regularisation" => RegCols, "WFH" => WfhCols, _ => OdCols };

        public static byte[] BuildWorkbook(string type, List<Application> apps)
        {
            var cols = Columns(type);
            using var wb = new XLWorkbook();
            var ws = wb.Worksheets.Add("Worksheet");
            for (int c = 0; c < cols.Length; c++) ws.Cell(1, c + 1).Value = cols[c];
            var hdr = ws.Range(1, 1, 1, cols.Length);
            hdr.Style.Font.Bold = true;
            hdr.Style.Fill.BackgroundColor = XLColor.FromHtml("#D9E1F2");
            int r = 2;
            foreach (var row in Rows(type, apps))
            {
                for (int c = 0; c < row.Length; c++)
                {
                    var cell = ws.Cell(r, c + 1);
                    switch (row[c])
                    {
                        case null: break;
                        case int n: cell.Value = n; break;
                        case decimal m: cell.Value = (double)m; break;
                        default: cell.Value = row[c]!.ToString(); break;
                    }
                }
                r++;
            }
            ws.Range(1, 1, Math.Max(1, r - 1), cols.Length).SetAutoFilter();
            ws.SheetView.FreezeRows(1);
            ws.Columns().AdjustToContents(1, Math.Min(r, 200));
            foreach (var c in ws.ColumnsUsed()) if (c.Width > 45) c.Width = 45;
            using var ms = new MemoryStream();
            wb.SaveAs(ms);
            return ms.ToArray();
        }
    }
}
