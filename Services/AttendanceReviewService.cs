// ─────────────────────────────────────────────────────────────────────────────
// Services/AttendanceReviewService.cs
// Requires NuGet: ClosedXML (0.102+)  — add to .csproj:
//   <PackageReference Include="ClosedXML" Version="0.102.2" />
// ─────────────────────────────────────────────────────────────────────────────
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AmpmHrmsPro.Data;
using AmpmHrmsPro.Models;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AmpmHrmsPro.Services
{
    // ─── Configuration ────────────────────────────────────────────────────────
    public class PilotManager
    {
        public string Name       { get; set; } = "";
        public string Email      { get; set; } = "";
        public List<string> CcEmails { get; set; } = new();
    }

    public class AttendanceReviewOptions
    {
        public List<PilotManager> PilotManagers { get; set; } = new();
        // HoDs ("Final Emailer List = Yes" in the master sheet). A gap rolls up
        // the reporting chain to the first person found in this list or in
        // PilotManagers. Pilot managers are always treated as HoDs.
        public List<string> HodNames { get; set; } = new();
        // Hour (24h) at which the daily auto-email fires — default 7:30
        public int EmailScheduleHour   { get; set; } = 7;
        public int EmailScheduleMinute { get; set; } = 30;
    }

    // ─── Service interface ────────────────────────────────────────────────────
    public interface IAttendanceReviewService
    {
        /// <summary>Parse a Presence 360 CSV, detect gaps, save to DB. Returns the new ImportId.</summary>
        Task<int> ImportCsvAsync(Stream csvStream, string fileName, string importedBy, CancellationToken ct = default);

        /// <summary>All gap logs for a given import batch.</summary>
        Task<List<AttendanceGapLog>> GetGapsAsync(int importId, CancellationToken ct = default);

        /// <summary>All import batches, newest first.</summary>
        Task<List<AttendanceImport>> GetImportsAsync(CancellationToken ct = default);

        /// <summary>Generate an Excel workbook for a manager's gaps and return the bytes.</summary>
        Task<byte[]> GenerateExcelAsync(string managerEmail, IEnumerable<AttendanceGapLog> gaps);

        /// <summary>Send emails for all unsent gaps in the given import. Returns (sent, failed).</summary>
        Task<(int Sent, int Failed)> SendEmailsForImportAsync(int importId, CancellationToken ct = default);

        /// <summary>Called by the daily background service: finds yesterday's unsent gaps and sends.</summary>
        Task<(int Sent, int Failed)> SendDailyEmailsAsync(CancellationToken ct = default);

        /// <summary>Reason the last failed email failed, if any.</summary>
        string? LastSendError { get; }
    }

    // ─── Implementation ───────────────────────────────────────────────────────
    public class AttendanceReviewService : IAttendanceReviewService
    {
        private readonly AppDbContext                   _db;
        private readonly AttendanceReviewOptions        _opts;
        private readonly ILogger<AttendanceReviewService> _log;
        private readonly IEmailSender                   _email;

        /// <summary>Reason the last failed email failed (shown on the Gap Report page).</summary>
        public string? LastSendError { get; private set; }

        // Pilot manager lookup: normalize name → PilotManager config
        private Dictionary<string, PilotManager> _pilotByName = new(StringComparer.OrdinalIgnoreCase);
        private HashSet<string>                  _pilotEmails  = new(StringComparer.OrdinalIgnoreCase);
        // normalized HoD name → display name ("Manish Rana")
        private Dictionary<string, string>       _hodByName    = new(StringComparer.OrdinalIgnoreCase);

        public AttendanceReviewService(
            AppDbContext db,
            IOptions<AttendanceReviewOptions> opts,
            ILogger<AttendanceReviewService> log,
            IEmailSender email)
        {
            _db   = db;
            _opts = opts.Value;
            _log  = log;
            _email = email;

            foreach (var h in _opts.HodNames)
                if (!string.IsNullOrWhiteSpace(h)) _hodByName[NormName(h)] = Pretty(h);

            foreach (var pm in _opts.PilotManagers)
            {
                _pilotByName[NormName(pm.Name)] = pm;
                _pilotEmails.Add(pm.Email);
                _hodByName[NormName(pm.Name)] = Pretty(pm.Name);
            }
        }

        // Presence 360 names carry double spaces / trailing dots
        // ("JAIDEEP  SINGH", "MD SAFIQ  .") — collapse to a single clean form.
        private static string NormName(string? s) =>
            System.Text.RegularExpressions.Regex.Replace(s ?? "", @"\s+", " ")
                .Trim().TrimEnd('.').Trim();

        // "FAZEL  MIRZA" → "Fazel Mirza"
        private static string Pretty(string? s) =>
            CultureInfo.InvariantCulture.TextInfo.ToTitleCase(NormName(s).ToLowerInvariant());

        // Walk up the reporting chain (employee → manager → manager's manager…)
        // until we hit a HoD. Falls back to the direct manager.
        private string ResolveHod(string? directManager, Dictionary<string, string> managerOf)
        {
            var m    = NormName(directManager);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (m.Length > 0 && seen.Add(m))
            {
                if (_hodByName.TryGetValue(m, out var hod)) return hod;
                if (!managerOf.TryGetValue(m, out var up)) break;
                m = up;
            }
            var direct = Pretty(directManager);
            return direct.Length > 0 ? direct : "Unassigned";
        }

        // ── 1. CSV Import ─────────────────────────────────────────────────────
        public async Task<int> ImportCsvAsync(
            Stream csvStream, string fileName, string importedBy,
            CancellationToken ct = default)
        {
            var rows = ParseCsv(csvStream);
            if (rows.Count == 0)
                throw new InvalidDataException("No attendance rows found in the uploaded CSV.");

            var dates = rows.Select(r => r.Date).Distinct().ToList();

            // Load roster for those dates and employee codes
            var empCodes   = rows.Select(r => r.EmpCode).Distinct().ToList();
            var rosterDict = await _db.RosterEntries
                .Where(r => empCodes.Contains(r.EmployeeCode) && dates.Contains(r.Date))
                .ToDictionaryAsync(r => $"{r.EmployeeCode}|{r.Date:yyyy-MM-dd}", ct);

            // Create import record
            var import = new AttendanceImport
            {
                FileName         = Path.GetFileName(fileName),
                FromDate         = dates.Min(),
                ToDate           = dates.Max(),
                ImportedAt       = DateTime.UtcNow,
                ImportedBy       = importedBy,
                RecordsProcessed = rows.Count
            };
            _db.AttendanceImports.Add(import);
            await _db.SaveChangesAsync(ct);  // get ImportId

            // Detect gaps
            var gaps = new List<AttendanceGapLog>();
            foreach (var row in rows)
            {
                var key    = $"{row.EmpCode}|{row.Date:yyyy-MM-dd}";
                rosterDict.TryGetValue(key, out var roster);

                var gapsForRow = DetectGaps(row, roster, import.Id);
                gaps.AddRange(gapsForRow);
            }

            // Reporting chain from the CSV itself: employee name → manager name
            // (managers are employees too, so their own manager is in the file).
            var managerOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rw in rows)
            {
                var emp = NormName(rw.EmpName);
                var mgr = NormName(rw.ManagerName);
                if (emp.Length > 0 && mgr.Length > 0 && !managerOf.ContainsKey(emp))
                    managerOf[emp] = mgr;
            }

            // Resolve HoD for each gap; email goes to the HoD if they're a pilot.
            var hodCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in gaps)
            {
                var key = NormName(g.ManagerName);
                if (!hodCache.TryGetValue(key, out var hod))
                    hodCache[key] = hod = ResolveHod(g.ManagerName, managerOf);

                g.HodName     = hod;
                g.ManagerName = string.IsNullOrEmpty(key) ? "Unassigned" : Pretty(g.ManagerName);

                if (_pilotByName.TryGetValue(NormName(hod), out var pm))
                    g.ManagerEmail = pm.Email;
                // else: HoD not in pilot list — gap saved but no email sent
            }

            import.GapsDetected = gaps.Count;
            _db.AttendanceGapLogs.AddRange(gaps);
            await _db.SaveChangesAsync(ct);

            _log.LogInformation("AttendanceReview: imported {File}, {Rows} rows, {Gaps} gaps",
                fileName, rows.Count, gaps.Count);

            return import.Id;
        }

        // ── 2. Queries ────────────────────────────────────────────────────────
        public Task<List<AttendanceGapLog>> GetGapsAsync(int importId, CancellationToken ct = default) =>
            _db.AttendanceGapLogs
               .Where(g => g.ImportId == importId)
               .OrderBy(g => g.HodName).ThenBy(g => g.ManagerName)
               .ThenBy(g => g.EmployeeName).ThenBy(g => g.Date)
               .ToListAsync(ct);

        public Task<List<AttendanceImport>> GetImportsAsync(CancellationToken ct = default) =>
            _db.AttendanceImports
               .OrderByDescending(i => i.ImportedAt)
               .ToListAsync(ct);

        // ── 3. Excel generation ───────────────────────────────────────────────
        // Sheet 1 "Summary": HoD → Reporting Manager gap counts (with HoD totals).
        // Then one detail sheet per HoD (or a single "Gap Details" sheet when
        // the workbook is for one HoD).
        public Task<byte[]> GenerateExcelAsync(string managerEmail, IEnumerable<AttendanceGapLog> gaps)
        {
            var list = gaps.ToList();
            using var wb = new XLWorkbook();
            var headerFill = XLColor.FromHtml("#1e3a5f");

            void StyleHeader(IXLWorksheet ws, int row, int cols)
            {
                var rng = ws.Range(row, 1, row, cols);
                rng.Style.Font.Bold = true;
                rng.Style.Font.FontColor = XLColor.White;
                rng.Style.Fill.BackgroundColor = headerFill;
            }

            // ── Summary sheet ──
            var sum = wb.Worksheets.Add("Summary");
            string[] sh = { "HoD", "Reporting Manager", "Employees", "Late Coming",
                            "Short Hours", "Extra Time", "Absent / Unplanned", "Total Gaps" };
            for (int c = 0; c < sh.Length; c++) sum.Cell(1, c + 1).Value = sh[c];
            StyleHeader(sum, 1, sh.Length);

            int sr = 2;
            var byHod = list.GroupBy(g => g.HodName ?? g.ManagerName ?? "Unassigned")
                            .OrderBy(g => g.Key).ToList();
            foreach (var hod in byHod)
            {
                foreach (var mg in hod.GroupBy(g => g.ManagerName ?? "Unassigned").OrderBy(g => g.Key))
                {
                    WriteSummaryRow(sum, sr++, hod.Key, mg.Key, mg.ToList(), bold: false);
                }
                WriteSummaryRow(sum, sr, hod.Key, "HoD Total", hod.ToList(), bold: true);
                sum.Range(sr, 1, sr, sh.Length).Style.Fill.BackgroundColor = XLColor.FromHtml("#eef2f7");
                sr++;
            }
            WriteSummaryRow(sum, sr, "GRAND TOTAL", "", list, bold: true);
            sum.Range(sr, 1, sr, sh.Length).Style.Fill.BackgroundColor = XLColor.FromHtml("#dbe4f0");
            sum.SheetView.FreezeRows(1);
            sum.Columns().AdjustToContents();

            // ── Detail sheet(s) ──
            if (byHod.Count <= 1)
                WriteDetailSheet(wb, "Gap Details", list, StyleHeader);
            else
            {
                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Summary" };
                foreach (var hod in byHod)
                {
                    var name = SafeSheetName(hod.Key);
                    var baseName = name; int n = 2;
                    while (!used.Add(name)) name = SafeSheetName($"{baseName} {n++}");
                    WriteDetailSheet(wb, name, hod.ToList(), StyleHeader);
                }
            }

            using var ms = new MemoryStream();
            wb.SaveAs(ms);
            return Task.FromResult(ms.ToArray());
        }

        private static void WriteSummaryRow(IXLWorksheet ws, int row, string hod, string mgr,
                                            List<AttendanceGapLog> g, bool bold)
        {
            ws.Cell(row, 1).Value = hod;
            ws.Cell(row, 2).Value = mgr;
            ws.Cell(row, 3).Value = (double)g.Select(x => x.EmployeeCode).Distinct().Count();
            ws.Cell(row, 4).Value = (double)g.Count(x => x.GapType == "Late");
            ws.Cell(row, 5).Value = (double)g.Count(x => x.GapType == "ShortHours");
            ws.Cell(row, 6).Value = (double)g.Count(x => x.GapType == "ExtraTime");
            ws.Cell(row, 7).Value = (double)g.Count(x => x.GapType == "Absent");
            ws.Cell(row, 8).Value = (double)g.Count;
            if (bold) ws.Range(row, 1, row, 8).Style.Font.Bold = true;
        }

        private static void WriteDetailSheet(XLWorkbook wb, string sheetName,
            List<AttendanceGapLog> gaps, Action<IXLWorksheet, int, int> styleHeader)
        {
            var ws = wb.Worksheets.Add(sheetName);
            string[] headers = {
                "HoD", "Reporting Manager", "Emp Code", "Employee Name", "Department",
                "Date", "Day", "Gap Type",
                "Actual In", "Actual Out", "Actual Hours",
                "Planned In", "Planned Out", "Planned Hours",
                "Late By (min)", "Short By (min)", "Extra By (min)"
            };
            for (int c = 0; c < headers.Length; c++) ws.Cell(1, c + 1).Value = headers[c];
            styleHeader(ws, 1, headers.Length);

            int row = 2;
            foreach (var g in gaps.OrderBy(x => x.ManagerName).ThenBy(x => x.EmployeeName).ThenBy(x => x.Date))
            {
                ws.Cell(row, 1).Value  = g.HodName ?? "";
                ws.Cell(row, 2).Value  = g.ManagerName ?? "";
                ws.Cell(row, 3).Value  = g.EmployeeCode;
                ws.Cell(row, 4).Value  = g.EmployeeName;
                ws.Cell(row, 5).Value  = g.Department ?? "";
                ws.Cell(row, 6).Value  = g.Date.ToString("dd-MMM-yyyy");
                ws.Cell(row, 7).Value  = g.Date.DayOfWeek.ToString();
                ws.Cell(row, 8).Value  = GapLabel(g.GapType);
                ws.Cell(row, 9).Value  = g.ActualInTime.HasValue   ? g.ActualInTime.Value.ToString("HH:mm")   : "-";
                ws.Cell(row, 10).Value = g.ActualOutTime.HasValue  ? g.ActualOutTime.Value.ToString("HH:mm")  : "-";
                ws.Cell(row, 11).Value = g.ActualHours.HasValue    ? g.ActualHours.Value.ToString("0.00")     : "-";
                ws.Cell(row, 12).Value = g.PlannedInTime.HasValue  ? g.PlannedInTime.Value.ToString("HH:mm")  : "-";
                ws.Cell(row, 13).Value = g.PlannedOutTime.HasValue ? g.PlannedOutTime.Value.ToString("HH:mm") : "-";
                ws.Cell(row, 14).Value = g.PlannedHours.HasValue   ? g.PlannedHours.Value.ToString("0.00")    : "-";
                ws.Cell(row, 15).Value = g.LateByMinutes.HasValue  ? g.LateByMinutes.Value.ToString()  : "";
                ws.Cell(row, 16).Value = g.ShortByMinutes.HasValue ? g.ShortByMinutes.Value.ToString() : "";
                ws.Cell(row, 17).Value = g.ExtraByMinutes.HasValue ? g.ExtraByMinutes.Value.ToString() : "";

                if (g.GapType == "Absent")
                    ws.Range(row, 1, row, headers.Length).Style.Fill.BackgroundColor = XLColor.FromHtml("#fff0f0");
                row++;
            }
            ws.SheetView.FreezeRows(1);
            ws.RangeUsed()?.SetAutoFilter();
            ws.Columns().AdjustToContents();
        }

        // Excel sheet names: max 31 chars, no : \ / ? * [ ]
        private static string SafeSheetName(string s)
        {
            var clean = new string((s ?? "Sheet").Where(ch => ":\\/?*[]".IndexOf(ch) < 0).ToArray()).Trim();
            if (clean.Length == 0) clean = "Sheet";
            return clean.Length > 31 ? clean[..31] : clean;
        }

        private static string GapLabel(string gapType) => gapType switch
        {
            "Late"       => "Late Coming",
            "ShortHours" => "Short Hours",
            "ExtraTime"  => "Extra Time",
            _            => "Absent / Unplanned"
        };

        // ── 4. Send emails for a specific import ──────────────────────────────
        public async Task<(int Sent, int Failed)> SendEmailsForImportAsync(int importId, CancellationToken ct = default)
        {
            var gaps = await _db.AttendanceGapLogs
                .Where(g => g.ImportId == importId && !g.EmailSent && !string.IsNullOrEmpty(g.ManagerEmail))
                .ToListAsync(ct);
            return await SendGapsEmailAsync(gaps, ct);
        }

        // ── 5. Daily auto-send: yesterday's unsent gaps ───────────────────────
        public async Task<(int Sent, int Failed)> SendDailyEmailsAsync(CancellationToken ct = default)
        {
            var yesterday = DateOnly.FromDateTime(DateTime.Today.AddDays(-1));
            var gaps = await _db.AttendanceGapLogs
                .Where(g => g.Date == yesterday && !g.EmailSent && !string.IsNullOrEmpty(g.ManagerEmail))
                .ToListAsync(ct);

            return await SendGapsEmailAsync(gaps, ct);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Private helpers
        // ─────────────────────────────────────────────────────────────────────

        private async Task<(int Sent, int Failed)> SendGapsEmailAsync(
            List<AttendanceGapLog> gaps, CancellationToken ct)
        {
            if (gaps.Count == 0) return (0, 0);

            // SMTP settings are only a fallback — SmartEmailSender sends via the
            // connected Outlook account when there is one.
            var settings = await _db.EmailSettingsList.FirstOrDefaultAsync(ct);
            LastSendError = null;

            // Group by manager email
            var byManager = gaps
                .GroupBy(g => g.ManagerEmail!)
                .ToList();

            int sent = 0, failed = 0;

            foreach (var grp in byManager)
            {
                var managerEmail = grp.Key;

                // Only send to pilot managers
                if (!_pilotEmails.Contains(managerEmail))
                    continue;

                var mgGaps = grp.ToList();
                var managerName = mgGaps.First().HodName ?? mgGaps.First().ManagerName ?? managerEmail;

                // Pilot config gives the clean display name + CC list
                var pm = _opts.PilotManagers.FirstOrDefault(p =>
                    string.Equals(p.Email, managerEmail, StringComparison.OrdinalIgnoreCase));
                if (pm != null) managerName = pm.Name;
                var ccList = pm?.CcEmails ?? new List<string>();

                // Get date range for subject
                var dateMin = mgGaps.Min(g => g.Date);
                var dateMax = mgGaps.Max(g => g.Date);
                var dateRange = dateMin == dateMax
                    ? dateMin.ToString("dd MMM yyyy")
                    : $"{dateMin:dd MMM} – {dateMax:dd MMM yyyy}";

                var subject = $"Attendance Review — {dateRange} | Your Team Gaps";
                var body    = BuildEmailHtml(managerName, mgGaps, dateRange);
                var excel   = await GenerateExcelAsync(managerEmail, mgGaps);

                var req = new MailRequest
                {
                    To       = new List<string> { managerEmail },
                    Cc       = ccList.ToList(),
                    Subject  = subject,
                    HtmlBody = body,
                    Attachments =
                    {
                        new MailAttachment(
                            $"AttendanceGaps_{dateRange.Replace(" ", "_").Replace("–", "to")}.xlsx",
                            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                            excel)
                    }
                };

                var (ok, message) = await _email.SendAsync(settings, req);
                if (ok)
                {
                    var now = DateTime.UtcNow;
                    foreach (var g in mgGaps) { g.EmailSent = true; g.EmailSentAt = now; }
                    sent++;
                }
                else
                {
                    _log.LogError("AttendanceReview: failed to send email to {Email}: {Msg}", managerEmail, message);
                    LastSendError = message;
                    failed++;
                }
            }

            await _db.SaveChangesAsync(ct);
            return (sent, failed);
        }

        // ── CSV Parsing ───────────────────────────────────────────────────────

        private record AttendanceRow(
            string EmpName, string EmpCode, string Department, string ManagerName,
            DateOnly Date, TimeOnly? InTime, TimeOnly? OutTime, decimal? WorkingHours,
            string AttendanceType,
            TimeOnly? ShiftIn, TimeOnly? ShiftOut);

        private List<AttendanceRow> ParseCsv(Stream stream)
        {
            var rows = new List<AttendanceRow>();
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);

            // Presence 360 puts an "Organization: ..." line and a blank line
            // before the real header — scan ahead for the header row.
            string? headerLine = null;
            for (int i = 0; i < 10; i++)
            {
                var l = reader.ReadLine();
                if (l == null) break;
                if (l.Contains("Employee Code", StringComparison.OrdinalIgnoreCase) &&
                    l.Contains("Attendance Date", StringComparison.OrdinalIgnoreCase))
                { headerLine = l.TrimStart('﻿'); break; }
            }
            if (headerLine == null) return rows;
            var headers = SplitCsvLine(headerLine);
            var idx = BuildHeaderIndex(headers);

            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var cols = SplitCsvLine(line);
                if (cols.Length < 5) continue;

                string Get(string col) => idx.TryGetValue(col, out var i) && i < cols.Length
                    ? cols[i].Trim().Trim('"') : "";

                var empCode  = Get("Employee Code");
                var empName  = Get("Employee Name");
                if (string.IsNullOrWhiteSpace(empCode) && string.IsNullOrWhiteSpace(empName)) continue;

                var dateStr  = Get("Attendance Date");
                if (!TryParseDate(dateStr, out var date)) continue;

                var attType  = Get("Attendance Type").ToLowerInvariant();
                var inStr    = Get("In Time");
                var outStr   = Get("Out Time");
                var hoursStr = Get("Working Hours");
                var shiftIn  = Get("Shift Start Time");
                var shiftOut = Get("Shift End Time");

                rows.Add(new AttendanceRow(
                    EmpName:        empName,
                    EmpCode:        empCode,
                    Department:     Get("Department"),
                    ManagerName:    Get("Manager Name"),
                    Date:           date,
                    InTime:         TryParseTime(inStr, out var t1)   ? t1   : null,
                    OutTime:        TryParseTime(outStr, out var t2)   ? t2   : null,
                    WorkingHours:   TryParseHours(hoursStr, out var h) ? h   : null,
                    AttendanceType: attType,
                    ShiftIn:        TryParseTime(shiftIn, out var s1)  ? s1  : null,
                    ShiftOut:       TryParseTime(shiftOut, out var s2) ? s2  : null
                ));
            }
            return rows;
        }

        private static Dictionary<string, int> BuildHeaderIndex(string[] headers)
        {
            var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < headers.Length; i++)
                d[headers[i].Trim().Trim('"')] = i;
            return d;
        }

        private static string[] SplitCsvLine(string line)
        {
            // Simple CSV split — handles quoted fields with commas
            var result = new List<string>();
            var sb     = new StringBuilder();
            bool inQ   = false;
            foreach (char c in line)
            {
                if (c == '"') { inQ = !inQ; }
                else if (c == ',' && !inQ) { result.Add(sb.ToString()); sb.Clear(); }
                else sb.Append(c);
            }
            result.Add(sb.ToString());
            return result.ToArray();
        }

        private static bool TryParseDate(string s, out DateOnly d)
        {
            d = default;
            if (string.IsNullOrWhiteSpace(s)) return false;
            string[] fmts = { "dd-MMM-yyyy", "dd/MM/yyyy", "MM/dd/yyyy", "yyyy-MM-dd",
                              "dd-MM-yyyy", "d-MMM-yyyy", "dd MMM yyyy" };
            foreach (var f in fmts)
                if (DateOnly.TryParseExact(s.Trim(), f, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out d)) return true;
            // Try DateTime fallback
            if (DateTime.TryParse(s.Trim(), out var dt)) { d = DateOnly.FromDateTime(dt); return true; }
            return false;
        }

        private static bool TryParseTime(string s, out TimeOnly t)
        {
            t = default;
            if (string.IsNullOrWhiteSpace(s)) return false;
            string[] fmts = { "HH:mm", "H:mm", "HH:mm:ss", "h:mm tt", "h:mm:ss tt" };
            foreach (var f in fmts)
                if (TimeOnly.TryParseExact(s.Trim(), f, CultureInfo.InvariantCulture, DateTimeStyles.None, out t)) return true;
            if (TimeOnly.TryParse(s.Trim(), out t)) return true;
            return false;
        }

        private static bool TryParseHours(string s, out decimal h)
        {
            h = 0;
            if (string.IsNullOrWhiteSpace(s)) return false;
            // "8:30" → 8.5 hrs
            if (s.Contains(':'))
            {
                var parts = s.Split(':');
                if (parts.Length >= 2 && int.TryParse(parts[0], out var hrs) &&
                    int.TryParse(parts[1], out var mins))
                { h = hrs + mins / 60m; return true; }
            }
            return decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out h);
        }

        // ── Gap Detection ─────────────────────────────────────────────────────

        private List<AttendanceGapLog> DetectGaps(AttendanceRow row, RosterEntry? roster, int importId)
        {
            var gaps = new List<AttendanceGapLog>();

            // Determine planned shift — roster wins; fall back to Presence 360 shift columns
            var plannedIn  = roster?.ShiftInTime  ?? row.ShiftIn;
            var plannedOut = roster?.ShiftOutTime ?? row.ShiftOut;
            var buffer     = roster?.LateBufferMinutes ?? 10;
            var dayType    = roster?.DayType; // "Week Off" / "Holiday" / "Leave" / "Official Travel" / null

            // Skip days explicitly marked as non-working in roster
            if (!string.IsNullOrWhiteSpace(dayType) &&
                (dayType.Equals("Week Off", StringComparison.OrdinalIgnoreCase) ||
                 dayType.Equals("Holiday",  StringComparison.OrdinalIgnoreCase)))
                return gaps;

            // Attendance type from Presence 360
            var at = row.AttendanceType; // "present" / "absent" / "leave" / "week-off"

            // If Presence 360 says week-off, skip (regardless of roster)
            if (at == "week-off") return gaps;

            // Absent / unplanned
            if (at == "absent")
            {
                // If roster marks a planned leave or travel — not a gap
                if (dayType != null &&
                    (dayType.Contains("Leave", StringComparison.OrdinalIgnoreCase) ||
                     dayType.Contains("Official Travel", StringComparison.OrdinalIgnoreCase)))
                    return gaps;

                // Unplanned absence
                gaps.Add(MakeGap(row, roster, importId, "Absent",
                    plannedIn, plannedOut, lateBy: null, shortBy: null, extraBy: null));
                return gaps;
            }

            // Leave — Presence 360 itself marks this as a leave (approved or
            // otherwise recorded in the biometric system). Always skip; no gap.
            // (Unplanned absences arrive as "absent", handled above.)
            if (at == "leave") return gaps;

            // Present — check Late, Short Hours, Extra Time
            if (at == "present" || at.StartsWith("present"))
            {
                decimal? plannedHours = null;
                if (plannedIn.HasValue && plannedOut.HasValue)
                {
                    var span = plannedOut.Value.ToTimeSpan() - plannedIn.Value.ToTimeSpan();
                    if (span.TotalMinutes > 0) plannedHours = (decimal)span.TotalHours;
                }

                // Late Coming
                if (row.InTime.HasValue && plannedIn.HasValue)
                {
                    var lateBy = (int)(row.InTime.Value.ToTimeSpan() - plannedIn.Value.ToTimeSpan())
                                     .TotalMinutes;
                    if (lateBy > buffer)
                    {
                        gaps.Add(MakeGap(row, roster, importId, "Late",
                            plannedIn, plannedOut, lateBy: lateBy, shortBy: null, extraBy: null));
                    }
                }

                // Short Hours / Extra Time
                var actual = row.WorkingHours;
                if (actual.HasValue && plannedHours.HasValue)
                {
                    var diff = actual.Value - plannedHours.Value;
                    var diffMin = (int)(diff * 60);

                    if (diffMin < 0) // short
                    {
                        gaps.Add(MakeGap(row, roster, importId, "ShortHours",
                            plannedIn, plannedOut, null, shortBy: -diffMin, null));
                    }
                    else if (diffMin >= 30) // extra — only if 30+ min over
                    {
                        gaps.Add(MakeGap(row, roster, importId, "ExtraTime",
                            plannedIn, plannedOut, null, null, extraBy: diffMin));
                    }
                }
            }

            return gaps;
        }

        private AttendanceGapLog MakeGap(
            AttendanceRow row, RosterEntry? roster, int importId, string gapType,
            TimeOnly? plannedIn, TimeOnly? plannedOut,
            int? lateBy, int? shortBy, int? extraBy)
        {
            decimal? plannedHours = null;
            if (plannedIn.HasValue && plannedOut.HasValue)
            {
                var span = plannedOut.Value.ToTimeSpan() - plannedIn.Value.ToTimeSpan();
                if (span.TotalMinutes > 0) plannedHours = (decimal)Math.Round(span.TotalHours, 2);
            }

            return new AttendanceGapLog
            {
                ImportId       = importId,
                EmployeeName   = NormName(row.EmpName),
                EmployeeCode   = row.EmpCode,
                Department     = row.Department,
                ManagerName    = NormName(row.ManagerName),
                ManagerEmail   = null,  // set by caller after pilot-manager lookup
                Date           = row.Date,
                GapType        = gapType,
                ActualInTime   = row.InTime,
                ActualOutTime  = row.OutTime,
                ActualHours    = row.WorkingHours.HasValue
                                     ? (decimal)Math.Round((double)row.WorkingHours.Value, 2)
                                     : null,
                PlannedInTime  = plannedIn,
                PlannedOutTime = plannedOut,
                PlannedHours   = plannedHours,
                LateByMinutes  = lateBy,
                ShortByMinutes = shortBy,
                ExtraByMinutes = extraBy,
                CreatedAt      = DateTime.UtcNow
            };
        }

        // ── Email HTML body ───────────────────────────────────────────────────

        private static string BuildEmailHtml(string managerName, List<AttendanceGapLog> gaps, string dateRange)
        {
            var sb = new StringBuilder();
            sb.Append($@"
<!DOCTYPE html>
<html>
<head><meta charset='utf-8'>
<style>
  body {{ font-family: Arial, sans-serif; font-size: 13px; color: #222; }}
  h2   {{ color: #1e3a5f; }}
  table {{ border-collapse: collapse; width: 100%; margin-top: 16px; }}
  th   {{ background: #1e3a5f; color: #fff; padding: 8px 10px; text-align: left; font-size: 12px; }}
  td   {{ padding: 7px 10px; border-bottom: 1px solid #e0e0e0; font-size: 12px; }}
  tr:nth-child(even) {{ background: #f7f8fa; }}
  .badge-late  {{ background:#fff3cd; color:#856404; padding:2px 7px; border-radius:10px; font-size:11px; }}
  .badge-short {{ background:#d1ecf1; color:#0c5460; padding:2px 7px; border-radius:10px; font-size:11px; }}
  .badge-extra {{ background:#d4edda; color:#155724; padding:2px 7px; border-radius:10px; font-size:11px; }}
  .badge-absent{{ background:#f8d7da; color:#721c24; padding:2px 7px; border-radius:10px; font-size:11px; }}
</style>
</head>
<body>
<h2>Attendance Review — {dateRange}</h2>
<p>Hi {managerName},</p>
<p>Below is the attendance gap summary for your department (all reporting managers under you). The detailed Excel is attached.</p>
<h3 style='color:#1e3a5f;margin-bottom:0;'>Summary by Reporting Manager</h3>
<table>
<tr>
  <th>Reporting Manager</th><th>Employees</th><th>Late Coming</th><th>Short Hours</th>
  <th>Extra Time</th><th>Absent / Unplanned</th><th>Total</th>
</tr>");

            foreach (var mg in gaps.GroupBy(x => x.ManagerName ?? "Unassigned").OrderBy(x => x.Key))
            {
                sb.Append($@"
<tr>
  <td>{mg.Key}</td>
  <td>{mg.Select(x => x.EmployeeCode).Distinct().Count()}</td>
  <td>{mg.Count(x => x.GapType == "Late")}</td>
  <td>{mg.Count(x => x.GapType == "ShortHours")}</td>
  <td>{mg.Count(x => x.GapType == "ExtraTime")}</td>
  <td>{mg.Count(x => x.GapType == "Absent")}</td>
  <td><b>{mg.Count()}</b></td>
</tr>");
            }

            sb.Append(@"
</table>
<h3 style='color:#1e3a5f;margin:24px 0 0;'>Gap Details</h3>
<table>
<tr>
  <th>Reporting Manager</th><th>Employee</th><th>Date</th><th>Gap</th>
  <th>Actual In</th><th>Actual Out</th><th>Actual Hrs</th>
  <th>Planned In</th><th>Planned Out</th><th>Details</th>
</tr>");

            foreach (var g in gaps.OrderBy(x => x.ManagerName).ThenBy(x => x.Date).ThenBy(x => x.EmployeeName))
            {
                var badgeClass = g.GapType switch
                {
                    "Late"       => "badge-late",
                    "ShortHours" => "badge-short",
                    "ExtraTime"  => "badge-extra",
                    _            => "badge-absent"
                };
                var gapLabel = g.GapType switch
                {
                    "Late"       => "Late Coming",
                    "ShortHours" => "Short Hours",
                    "ExtraTime"  => "Extra Time",
                    _            => "Absent/Unplanned"
                };
                var detail = g.GapType switch
                {
                    "Late"       => $"Late by {g.LateByMinutes} min",
                    "ShortHours" => $"Short by {g.ShortByMinutes} min",
                    "ExtraTime"  => $"Extra {g.ExtraByMinutes} min",
                    _            => "No planned leave"
                };

                sb.Append($@"
<tr>
  <td>{g.ManagerName}</td>
  <td>{g.EmployeeName} <span style='color:#888;font-size:11px;'>({g.EmployeeCode})</span></td>
  <td>{g.Date:dd MMM yyyy}</td>
  <td><span class='{badgeClass}'>{gapLabel}</span></td>
  <td>{(g.ActualInTime.HasValue  ? g.ActualInTime.Value.ToString("HH:mm")  : "—")}</td>
  <td>{(g.ActualOutTime.HasValue ? g.ActualOutTime.Value.ToString("HH:mm") : "—")}</td>
  <td>{(g.ActualHours.HasValue   ? g.ActualHours.Value.ToString("0.00")    : "—")}</td>
  <td>{(g.PlannedInTime.HasValue  ? g.PlannedInTime.Value.ToString("HH:mm")  : "—")}</td>
  <td>{(g.PlannedOutTime.HasValue ? g.PlannedOutTime.Value.ToString("HH:mm") : "—")}</td>
  <td>{detail}</td>
</tr>");
            }

            sb.Append(@"
</table>
<p style='margin-top:24px;color:#888;font-size:11px;'>
  This is an automated email from AMPM HRMS &mdash; Attendance Review module.<br>
  Please do not reply to this email.
</p>
</body></html>");

            return sb.ToString();
        }
    }
}
