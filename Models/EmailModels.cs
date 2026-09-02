using System.ComponentModel.DataAnnotations;

namespace AmpmHrmsPro.Models
{
    // ═══════════════════════════════════════════
    // EMAIL / HR NOTIFICATION SETTINGS — single-row settings, same pattern
    // as BiometricApiSettings/FaceMatchApiSettings: one row holds the SMTP
    // connection plus every scheduled notification's own enable-toggle and
    // send time. HrNotificationHostedService (Services/HrEmailNotificationService.cs)
    // polls this row on a timer and fires whichever job's time has arrived
    // and hasn't already run for today — see that file for the actual
    // report-building/sending logic.
    // ═══════════════════════════════════════════
    public class EmailSettings
    {
        [Key] public int Id { get; set; }

        // ── SMTP connection ──
        public bool IsEnabled { get; set; } = false; // master switch — every job below is a no-op while this is off, even if individually enabled
        [MaxLength(200)] public string? SmtpHost { get; set; }
        public int SmtpPort { get; set; } = 587;
        [MaxLength(200)] public string? SmtpUsername { get; set; }
        [MaxLength(200)] public string? SmtpPassword { get; set; }
        public bool SmtpUseSsl { get; set; } = true;
        [MaxLength(120)] public string? FromEmail { get; set; }
        [MaxLength(100)] public string? FromName { get; set; } = "AMPM HRMS";

        // ── Daily Attendance Alert — Late Coming + Early Going + Miss Punch
        // combined into one email, department-wise, to each department's
        // Head (Department.HeadEmployeeId). Runs the morning AFTER the date
        // it covers, since early-going and miss-punch both need the full
        // day's punches to be known — can't tell someone left early until
        // the day is actually over. ──
        public bool DailyAttendanceAlertEnabled { get; set; } = true;
        public TimeSpan DailyAttendanceAlertTime { get; set; } = new TimeSpan(7, 0, 0);
        [MaxLength(10)] public string? LastDailyAlertRunDate { get; set; } // yyyy-MM-dd — the run DATE (today), not the date the report covered; guards against firing twice in one day

        // ── Birthday wishes — every active employee, the morning of the
        // birthday itself. ──
        public bool BirthdayEnabled { get; set; } = true;
        public TimeSpan BirthdayTime { get; set; } = new TimeSpan(6, 0, 0);
        [MaxLength(10)] public string? LastBirthdayRunDate { get; set; }

        // ── Weekly Attendance Report — department-wise, to each
        // department's Head, covering the just-completed week. ──
        public bool WeeklyReportEnabled { get; set; } = true;
        public DayOfWeek WeeklyReportDay { get; set; } = DayOfWeek.Monday;
        public TimeSpan WeeklyReportTime { get; set; } = new TimeSpan(7, 0, 0);
        [MaxLength(10)] public string? LastWeeklyRunDate { get; set; }

        // ── Troubleshooting — the last thing the background service
        // actually did, across any of the three jobs, shown on the settings
        // page so Admin doesn't need server log access to see whether this
        // is working. ──
        public DateTime? LastActivityAt { get; set; }
        [MaxLength(1000)] public string? LastActivityMessage { get; set; }
    }
}
