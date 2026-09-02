using System.Net;
using System.Net.Mail;
using System.Text;
using Microsoft.EntityFrameworkCore;
using AmpmHrmsPro.Data;
using AmpmHrmsPro.Models;

namespace AmpmHrmsPro.Services
{
    // ═══════════════════════════════════════════
    // HR EMAIL NOTIFICATIONS — three scheduled jobs, all configured from
    // Admin > Attendance > Email Notifications (EmailSettings, single row):
    //
    //   1. Daily Attendance Alert — department-wise, to each department's
    //      Head (Department.HeadEmployeeId) — Late Coming + Early Going +
    //      Miss Punch for the PREVIOUS day, combined into one email so a
    //      HOD isn't getting several separate emails every morning.
    //   2. Birthday wishes — everyone, the morning of the birthday itself.
    //   3. Weekly Attendance Report — department-wise, to each department's
    //      Head, covering the just-completed week.
    //
    // HrNotificationHostedService (bottom of this file) is what actually
    // decides WHEN to fire each — same polling-BackgroundService pattern as
    // BiometricSyncHostedService (see that file), just checking three
    // schedules instead of one fixed interval.
    // ═══════════════════════════════════════════

    public interface IEmailSender
    {
        Task<(bool Success, string Message)> SendAsync(EmailSettings settings, IEnumerable<string> to, string subject, string htmlBody, IEnumerable<string>? bcc = null);
    }

    public class SmtpEmailSender : IEmailSender
    {
        public async Task<(bool, string)> SendAsync(EmailSettings settings, IEnumerable<string> to, string subject, string htmlBody, IEnumerable<string>? bcc = null)
        {
            if (string.IsNullOrWhiteSpace(settings.SmtpHost) || string.IsNullOrWhiteSpace(settings.FromEmail))
                return (false, "SMTP host / From address not configured.");

            var toList = to.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var bccList = (bcc ?? Enumerable.Empty<string>()).Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (!toList.Any() && !bccList.Any()) return (false, "No recipients.");

            try
            {
                using var client = new SmtpClient(settings.SmtpHost, settings.SmtpPort) { EnableSsl = settings.SmtpUseSsl };
                if (!string.IsNullOrWhiteSpace(settings.SmtpUsername))
                    client.Credentials = new NetworkCredential(settings.SmtpUsername, settings.SmtpPassword);

                using var msg = new MailMessage
                {
                    From = new MailAddress(settings.FromEmail!, string.IsNullOrWhiteSpace(settings.FromName) ? "AMPM HRMS" : settings.FromName),
                    Subject = subject,
                    Body = htmlBody,
                    IsBodyHtml = true,
                };
                // A single visible "To" address is used even for a BCC-only
                // send (the birthday blast) — most SMTP servers expect at
                // least one visible recipient, and BCC keeps every actual
                // recipient's address out of everyone else's inbox.
                if (toList.Any())
                    foreach (var t in toList) msg.To.Add(t);
                else
                    msg.To.Add(settings.FromEmail!);

                foreach (var b in bccList) msg.Bcc.Add(b);

                await client.SendMailAsync(msg);
                return (true, $"Sent to {toList.Count + bccList.Count} recipient(s).");
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }
    }

    public interface IHrEmailNotificationService
    {
        Task<string> SendDailyAttendanceAlertsAsync(AppDbContext db, EmailSettings settings, DateTime forDate);
        Task<string> SendBirthdayEmailsAsync(AppDbContext db, EmailSettings settings, DateTime forDate);
        Task<string> SendWeeklyAttendanceReportsAsync(AppDbContext db, EmailSettings settings, DateTime weekStart, DateTime weekEnd);
    }

    public class HrEmailNotificationService : IHrEmailNotificationService
    {
        readonly IEmailSender _sender;
        public HrEmailNotificationService(IEmailSender sender) => _sender = sender;

        // ── Job 1: Daily Attendance Alert ──
        public async Task<string> SendDailyAttendanceAlertsAsync(AppDbContext db, EmailSettings settings, DateTime forDate)
        {
            string dateStr = forDate.ToString("yyyy-MM-dd");

            var rows = await db.AttendanceDailies
                .Where(a => a.Date == dateStr)
                .Include(a => a.Employee).ThenInclude(e => e!.Shift)
                .Where(a => a.Employee != null && a.Employee.IsActive)
                .ToListAsync();

            var departments = await db.Departments.Where(d => d.IsActive && d.HeadEmployeeId != null)
                .Include(d => d.Head).ToListAsync();

            int emailsSent = 0, deptsSkippedNoHod = 0, deptsSkippedNothingToReport = 0;

            foreach (var dept in departments)
            {
                if (string.IsNullOrWhiteSpace(dept.Head?.Email)) { deptsSkippedNoHod++; continue; }

                var deptRows = rows.Where(r => r.Employee!.DepartmentId == dept.Id).ToList();

                var late = new List<(AttendanceDaily Row, TimeSpan By)>();
                var early = new List<(AttendanceDaily Row, TimeSpan By)>();
                var missPunch = new List<AttendanceDaily>();

                foreach (var r in deptRows)
                {
                    if (r.EffectiveStatus.Contains("MIS")) missPunch.Add(r);

                    var shift = r.Employee!.Shift;
                    if (shift == null) continue;
                    // Exact-match only — "P (WFH)"/"P (OD)"/"HD (...)" variants
                    // are deliberately excluded: someone working from home or
                    // on outdoor duty has no shift-comparable in/out to judge.
                    bool normalDay = r.EffectiveStatus == "P" || r.EffectiveStatus == "HD";
                    if (!normalDay) continue;
                    // AttendanceDaily.InTime/OutTime always hold the RAW punch
                    // times, even on a day an approved Regularisation later
                    // corrected (AttendanceEngine only recomputes EffectiveStatus
                    // from the corrected times — it never writes them back over
                    // the raw ones). RawStatus != EffectiveStatus means this day
                    // was regularized, so the raw punch time this alert would
                    // otherwise flag as "late"/"early" is stale and no longer
                    // what actually counts — skip it rather than false-alarm an
                    // HOD about something already resolved.
                    if (r.RawStatus != r.EffectiveStatus) continue;

                    if (r.InTime.HasValue)
                    {
                        var cutoff = shift.StartTime + TimeSpan.FromMinutes(shift.GraceMinutes);
                        if (r.InTime.Value > cutoff) late.Add((r, r.InTime.Value - shift.StartTime));
                    }
                    if (r.OutTime.HasValue && r.OutTime.Value < shift.EndTime)
                        early.Add((r, shift.EndTime - r.OutTime.Value));
                }

                if (!late.Any() && !early.Any() && !missPunch.Any()) { deptsSkippedNothingToReport++; continue; }

                var html = BuildDailyAlertHtml(dept.Name, forDate, late, early, missPunch);
                var (ok, _) = await _sender.SendAsync(settings, new[] { dept.Head!.Email }, $"Daily Attendance Alert — {dept.Name} — {forDate:dd-MMM-yyyy}", html);
                if (ok) emailsSent++;
            }

            return $"Daily Attendance Alert ({dateStr}): {emailsSent} email(s) sent, {deptsSkippedNothingToReport} department(s) had nothing to report, {deptsSkippedNoHod} department(s) skipped (no Head configured).";
        }

        static string BuildDailyAlertHtml(string deptName, DateTime forDate, List<(AttendanceDaily Row, TimeSpan By)> late, List<(AttendanceDaily Row, TimeSpan By)> early, List<AttendanceDaily> missPunch)
        {
            var sb = new StringBuilder();
            sb.Append("<div style='font-family:Segoe UI,Arial,sans-serif;color:#222;max-width:700px'>");
            sb.Append("<h2 style='margin-bottom:2px'>Daily Attendance Alert</h2>");
            sb.Append($"<p style='color:#666;margin-top:0'>{Esc(deptName)} — {forDate:dddd, dd MMM yyyy}</p>");

            sb.Append(Section("Late Coming", late.Count, "#B45309",
                late.Select(x => $"{Esc(x.Row.Employee!.Name)} ({Esc(x.Row.Employee.EmpCode)}) — in at {FmtTime(x.Row.InTime)}, {(int)x.By.TotalMinutes} min late")));

            sb.Append(Section("Early Going", early.Count, "#B91C1C",
                early.Select(x => $"{Esc(x.Row.Employee!.Name)} ({Esc(x.Row.Employee.EmpCode)}) — out at {FmtTime(x.Row.OutTime)}, {(int)x.By.TotalMinutes} min early")));

            sb.Append(Section("Miss Punch", missPunch.Count, "#7C3AED",
                missPunch.Select(r => $"{Esc(r.Employee!.Name)} ({Esc(r.Employee.EmpCode)}) — {Esc(r.EffectiveStatus)}")));

            sb.Append("<p style='color:#999;font-size:12px;margin-top:24px'>Automated message from AMPM HRMS. Please do not reply to this email.</p>");
            sb.Append("</div>");
            return sb.ToString();
        }

        // Formats a DOB/DOJ might actually be stored in — the web form
        // always writes "yyyy-MM-dd" (an HTML date input), but a
        // bulk-imported column can arrive as free text in whatever format
        // the source export used (e.g. "17-05-1990", "5/17/1990") and is
        // stored verbatim, unvalidated — same fallback-format idea already
        // used by BiometricSyncService.TryParseDateTime for the same
        // reason. Public/static so both this file (birthday emails) and
        // AdminController's dashboard (upcoming birthdays / new joinings
        // widgets) share one parser instead of two copies drifting apart.
        public static readonly string[] FlexibleDateFormats =
        {
            "yyyy-MM-dd", "dd-MM-yyyy", "dd/MM/yyyy", "MM/dd/yyyy", "d-M-yyyy", "M/d/yyyy"
        };

        public static bool TryParseFlexibleDate(string? raw, out DateTime date)
        {
            date = default;
            if (string.IsNullOrWhiteSpace(raw)) return false;
            foreach (var fmt in FlexibleDateFormats)
                if (DateTime.TryParseExact(raw, fmt, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out date))
                    return true;
            return DateTime.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out date);
        }

        // ── Job 2: Birthdays ──
        public async Task<string> SendBirthdayEmailsAsync(AppDbContext db, EmailSettings settings, DateTime forDate)
        {
            var active = await db.Employees.Where(e => e.IsActive && e.DOB != null && e.DOB != "").ToListAsync();

            int unparseable = 0;
            var birthdayPeople = active.Where(e =>
            {
                if (!TryParseFlexibleDate(e.DOB, out var dob)) { unparseable++; return false; }
                return dob.Month == forDate.Month && dob.Day == forDate.Day;
            }).ToList();

            string unparseableNote = unparseable > 0 ? $" ({unparseable} active employee(s) have a Date of Birth that couldn't be read — check Employee Master.)" : "";

            if (!birthdayPeople.Any()) return $"Birthday emails ({forDate:yyyy-MM-dd}): nobody's birthday today.{unparseableNote}";

            var allEmails = active.Select(e => e.Email).Where(e => !string.IsNullOrWhiteSpace(e)).ToList();
            if (!allEmails.Any()) return "Birthday emails: no active employee email addresses found.";

            var names = string.Join(", ", birthdayPeople.Select(e => e.Name));
            var html = BuildBirthdayHtml(birthdayPeople);
            var subject = birthdayPeople.Count == 1
                ? $"Happy Birthday, {birthdayPeople[0].Name}!"
                : $"Happy Birthday, {names}!";

            // BCC'd in batches — some SMTP providers cap recipients per
            // message, and nobody needs to see the whole company's email
            // addresses anyway.
            int sent = 0, failed = 0;
            foreach (var batch in Batch(allEmails, 80))
            {
                var (ok, _) = await _sender.SendAsync(settings, Array.Empty<string>(), subject, html, batch);
                if (ok) sent += batch.Count; else failed += batch.Count;
            }

            return $"Birthday emails ({forDate:yyyy-MM-dd}): {names} — sent to {sent} employee(s){(failed > 0 ? $", {failed} failed" : "")}.{unparseableNote}";
        }

        static string BuildBirthdayHtml(List<Employee> people)
        {
            var sb = new StringBuilder();
            sb.Append("<div style='font-family:Segoe UI,Arial,sans-serif;color:#222;max-width:600px;text-align:center'>");
            sb.Append("<div style='font-size:48px'>&#127874;&#127881;</div>");
            sb.Append("<h1 style='color:#7C3AED;margin-bottom:4px'>Happy Birthday!</h1>");
            foreach (var p in people)
                sb.Append($"<p style='font-size:18px;font-weight:600;margin:6px 0'>{Esc(p.Name)}</p>");
            sb.Append("<p style='color:#555;margin-top:16px'>Wishing you a wonderful day ahead, from everyone at AMPM Fashions!</p>");
            sb.Append("</div>");
            return sb.ToString();
        }

        // ── Job 3: Weekly Attendance Report ──
        public async Task<string> SendWeeklyAttendanceReportsAsync(AppDbContext db, EmailSettings settings, DateTime weekStart, DateTime weekEnd)
        {
            string fromStr = weekStart.ToString("yyyy-MM-dd"), toStr = weekEnd.ToString("yyyy-MM-dd");

            var rows = await db.AttendanceDailies
                .Where(a => string.Compare(a.Date, fromStr) >= 0 && string.Compare(a.Date, toStr) <= 0)
                .Include(a => a.Employee)
                .Where(a => a.Employee != null && a.Employee.IsActive)
                .ToListAsync();

            var departments = await db.Departments.Where(d => d.IsActive && d.HeadEmployeeId != null)
                .Include(d => d.Head).ToListAsync();

            int emailsSent = 0, deptsSkippedNoHod = 0, deptsSkippedNoEmployees = 0;

            foreach (var dept in departments)
            {
                if (string.IsNullOrWhiteSpace(dept.Head?.Email)) { deptsSkippedNoHod++; continue; }

                var deptRows = rows.Where(r => r.Employee!.DepartmentId == dept.Id).ToList();
                if (!deptRows.Any()) { deptsSkippedNoEmployees++; continue; }

                // Grouped by EmployeeId (a plain int), not by the Employee
                // entity itself — grouping by a reference type only groups
                // correctly by relying on EF Core's per-query identity
                // resolution (every row for the same employee happening to
                // share the exact same tracked object instance); that holds
                // today since this query isn't AsNoTracking, but grouping by
                // the id is correct regardless of tracking behavior, so it
                // can't silently break if that ever changes.
                var byEmployee = deptRows.GroupBy(r => r.EmployeeId)
                    .Select(g => (
                        Name: g.First().Employee!.Name,
                        EmpCode: g.First().Employee!.EmpCode,
                        Present: g.Count(r => r.EffectiveStatus == "P" || r.EffectiveStatus.StartsWith("P (")),
                        HalfDay: g.Count(r => r.EffectiveStatus == "HD" || r.EffectiveStatus.StartsWith("HD (")),
                        Absent: g.Count(r => r.EffectiveStatus == "A" || r.EffectiveStatus.StartsWith("A (")),
                        Leave: g.Count(r => r.EffectiveStatus.StartsWith("L (")),
                        WeekOff: g.Count(r => r.WasWeekOff),
                        Holiday: g.Count(r => r.WasHoliday),
                        MissPunch: g.Count(r => r.EffectiveStatus.Contains("MIS"))
                    ))
                    .OrderBy(x => x.Name)
                    .ToList();

                var html = BuildWeeklyHtml(dept.Name, weekStart, weekEnd, byEmployee);
                var (ok, _) = await _sender.SendAsync(settings, new[] { dept.Head!.Email }, $"Weekly Attendance Report — {dept.Name} — {weekStart:dd MMM} to {weekEnd:dd MMM yyyy}", html);
                if (ok) emailsSent++;
            }

            return $"Weekly Attendance Report ({fromStr} to {toStr}): {emailsSent} email(s) sent, {deptsSkippedNoEmployees} department(s) had no attendance data, {deptsSkippedNoHod} department(s) skipped (no Head configured).";
        }

        static string BuildWeeklyHtml(string deptName, DateTime weekStart, DateTime weekEnd, List<(string Name, string EmpCode, int Present, int HalfDay, int Absent, int Leave, int WeekOff, int Holiday, int MissPunch)> rows)
        {
            var sb = new StringBuilder();
            sb.Append("<div style='font-family:Segoe UI,Arial,sans-serif;color:#222;max-width:800px'>");
            sb.Append("<h2 style='margin-bottom:2px'>Weekly Attendance Report</h2>");
            sb.Append($"<p style='color:#666;margin-top:0'>{Esc(deptName)} — {weekStart:dd MMM} to {weekEnd:dd MMM yyyy}</p>");
            sb.Append("<table style='border-collapse:collapse;width:100%;font-size:13px'>");
            sb.Append("<tr style='background:#F3F4F6;text-align:left'>" +
                "<th style='padding:6px 8px;border:1px solid #E5E7EB'>Employee</th>" +
                "<th style='padding:6px 8px;border:1px solid #E5E7EB'>Present</th>" +
                "<th style='padding:6px 8px;border:1px solid #E5E7EB'>Half Day</th>" +
                "<th style='padding:6px 8px;border:1px solid #E5E7EB'>Absent</th>" +
                "<th style='padding:6px 8px;border:1px solid #E5E7EB'>Leave</th>" +
                "<th style='padding:6px 8px;border:1px solid #E5E7EB'>Week Off</th>" +
                "<th style='padding:6px 8px;border:1px solid #E5E7EB'>Holiday</th>" +
                "<th style='padding:6px 8px;border:1px solid #E5E7EB'>Miss Punch</th></tr>");
            foreach (var r in rows)
            {
                sb.Append("<tr>");
                sb.Append($"<td style='padding:6px 8px;border:1px solid #E5E7EB'>{Esc(r.Name)} ({Esc(r.EmpCode)})</td>");
                sb.Append($"<td style='padding:6px 8px;border:1px solid #E5E7EB;text-align:center'>{r.Present}</td>");
                sb.Append($"<td style='padding:6px 8px;border:1px solid #E5E7EB;text-align:center'>{r.HalfDay}</td>");
                sb.Append($"<td style='padding:6px 8px;border:1px solid #E5E7EB;text-align:center'>{r.Absent}</td>");
                sb.Append($"<td style='padding:6px 8px;border:1px solid #E5E7EB;text-align:center'>{r.Leave}</td>");
                sb.Append($"<td style='padding:6px 8px;border:1px solid #E5E7EB;text-align:center'>{r.WeekOff}</td>");
                sb.Append($"<td style='padding:6px 8px;border:1px solid #E5E7EB;text-align:center'>{r.Holiday}</td>");
                sb.Append($"<td style='padding:6px 8px;border:1px solid #E5E7EB;text-align:center'>{r.MissPunch}</td>");
                sb.Append("</tr>");
            }
            sb.Append("</table>");
            sb.Append("<p style='color:#999;font-size:12px;margin-top:24px'>Automated message from AMPM HRMS. Please do not reply to this email.</p>");
            sb.Append("</div>");
            return sb.ToString();
        }

        // ── shared helpers ──
        static string Section(string title, int count, string color, IEnumerable<string> lines)
        {
            if (count == 0) return "";
            var sb = new StringBuilder();
            sb.Append($"<h3 style='color:{color};margin-bottom:4px'>{Esc(title)} ({count})</h3>");
            sb.Append("<ul style='margin-top:0'>");
            foreach (var l in lines) sb.Append($"<li>{l}</li>"); // each line's dynamic pieces are already Esc()'d individually by the caller before interpolation
            sb.Append("</ul>");
            return sb.ToString();
        }

        static string FmtTime(TimeSpan? t) => t.HasValue ? DateTime.Today.Add(t.Value).ToString("hh:mm tt") : "—";

        static string Esc(string? s) => WebUtility.HtmlEncode(s ?? "");

        static IEnumerable<List<string>> Batch(List<string> source, int size)
        {
            for (int i = 0; i < source.Count; i += size)
                yield return source.Skip(i).Take(size).ToList();
        }
    }

    // ═══════════════════════════════════════════
    // HR NOTIFICATION HOSTED SERVICE — polls every 5 minutes (fine-grained
    // enough that a configured time is never missed by more than that much,
    // coarse enough not to hammer the DB) and fires whichever of the three
    // jobs' scheduled time has arrived AND hasn't already run today — the
    // LastXRunDate fields on EmailSettings are the guard against firing
    // twice in one day (e.g. an app restart landing right after the
    // scheduled time again).
    // ═══════════════════════════════════════════
    public class HrNotificationHostedService : BackgroundService
    {
        readonly IServiceProvider _services;
        public HrNotificationHostedService(IServiceProvider services) => _services = services;

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await RunOnceAsync(stoppingToken); }
                catch (OperationCanceledException) { break; }
                catch { /* swallow — a bad cycle just retries next poll, LastActivityMessage already records the failure for Admin to see */ }

                try { await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }

        async Task RunOnceAsync(CancellationToken stoppingToken)
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var notifier = scope.ServiceProvider.GetRequiredService<IHrEmailNotificationService>();

            var settings = await db.EmailSettingsList.FirstOrDefaultAsync(stoppingToken);
            if (settings == null || !settings.IsEnabled) return;

            var now = DateTime.Now;
            string today = now.ToString("yyyy-MM-dd");

            if (settings.DailyAttendanceAlertEnabled
                && now.TimeOfDay >= settings.DailyAttendanceAlertTime
                && settings.LastDailyAlertRunDate != today)
            {
                var msg = await notifier.SendDailyAttendanceAlertsAsync(db, settings, DateTime.Today.AddDays(-1));
                settings.LastDailyAlertRunDate = today;
                settings.LastActivityAt = now;
                settings.LastActivityMessage = msg;
                await db.SaveChangesAsync(stoppingToken);
            }

            if (settings.BirthdayEnabled
                && now.TimeOfDay >= settings.BirthdayTime
                && settings.LastBirthdayRunDate != today)
            {
                var msg = await notifier.SendBirthdayEmailsAsync(db, settings, DateTime.Today);
                settings.LastBirthdayRunDate = today;
                settings.LastActivityAt = now;
                settings.LastActivityMessage = msg;
                await db.SaveChangesAsync(stoppingToken);
            }

            if (settings.WeeklyReportEnabled
                && now.DayOfWeek == settings.WeeklyReportDay
                && now.TimeOfDay >= settings.WeeklyReportTime
                && settings.LastWeeklyRunDate != today)
            {
                var weekEnd = DateTime.Today.AddDays(-1);   // the day before this run — the just-completed week's last day
                var weekStart = weekEnd.AddDays(-6);        // 7 days total ending there
                var msg = await notifier.SendWeeklyAttendanceReportsAsync(db, settings, weekStart, weekEnd);
                settings.LastWeeklyRunDate = today;
                settings.LastActivityAt = now;
                settings.LastActivityMessage = msg;
                await db.SaveChangesAsync(stoppingToken);
            }
        }
    }
}
