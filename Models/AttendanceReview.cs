using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AmpmHrmsPro.Models
{
    // ── RosterEntry ────────────────────────────────────────────────────────────
    // One row per employee per calendar day, sourced from the monthly Excel
    // roster (Attendance Management.xlsx).  Null ShiftInTime / ShiftOutTime
    // means the employee is on a non-working day (week-off, holiday, leave,
    // official travel) as indicated by DayType.
    public class RosterEntry
    {
        public int    Id           { get; set; }

        [Required, MaxLength(120)]
        public string EmployeeName { get; set; } = "";

        [Required, MaxLength(20)]
        public string EmployeeCode { get; set; } = "";

        // The calendar date this entry covers.
        public DateOnly Date { get; set; }

        // Null = normal working day.
        // Non-null values: "Week Off", "Holiday", "Leave", "Official Travel"
        [MaxLength(40)]
        public string? DayType { get; set; }

        // Planned shift start / end — null when DayType is set.
        public TimeOnly? ShiftInTime  { get; set; }
        public TimeOnly? ShiftOutTime { get; set; }

        // Working location as per roster: WFH / STORE / OFFICE
        [MaxLength(20)]
        public string? Location { get; set; }

        // Minutes of grace before "Late Coming" is triggered (default 10).
        public int LateBufferMinutes { get; set; } = 10;

        [MaxLength(300)]
        public string? SpecialRemarks { get; set; }

        // The sheet/month this row was loaded from, e.g. "Sep-2026".
        [MaxLength(20)]
        public string Month { get; set; } = "";

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    // ── AttendanceImport ───────────────────────────────────────────────────────
    // One row per manual CSV upload of the Presence 360 daily report.
    // Tracks who uploaded, when, and how many records were processed so the
    // admin can audit the import history.
    public class AttendanceImport
    {
        public int    Id               { get; set; }

        [Required, MaxLength(260)]
        public string FileName         { get; set; } = "";

        // Earliest and latest attendance dates found in the uploaded CSV.
        public DateOnly FromDate        { get; set; }
        public DateOnly ToDate          { get; set; }

        public int  RecordsProcessed   { get; set; }
        public int  GapsDetected       { get; set; }

        public DateTime ImportedAt     { get; set; } = DateTime.UtcNow;

        [MaxLength(120)]
        public string ImportedBy       { get; set; } = "";

        // Convenience nav — EF will populate the gaps via ImportId FK.
        public ICollection<AttendanceGapLog> Gaps { get; set; } = new List<AttendanceGapLog>();
    }

    // ── AttendanceGapLog ───────────────────────────────────────────────────────
    // One row per gap detected during an import.  GapType is one of:
    //   "Late"        — arrived after ShiftInTime + LateBufferMinutes
    //   "ShortHours"  — actual hours < planned (ShiftOut - ShiftIn)
    //   "ExtraTime"   — actual hours > planned + 30 min
    //   "Absent"      — absent/unplanned (no planned leave in roster)
    public class AttendanceGapLog
    {
        public int Id       { get; set; }

        // FK to the import batch that generated this gap.
        public int ImportId { get; set; }
        [ForeignKey(nameof(ImportId))]
        public AttendanceImport? Import { get; set; }

        [Required, MaxLength(120)]
        public string EmployeeName  { get; set; } = "";

        [Required, MaxLength(20)]
        public string EmployeeCode  { get; set; } = "";

        [MaxLength(80)]
        public string? Department   { get; set; }

        [MaxLength(120)]
        public string? ManagerName  { get; set; }

        [MaxLength(200)]
        public string? ManagerEmail { get; set; }

        public DateOnly Date { get; set; }

        // "Late" | "ShortHours" | "ExtraTime" | "Absent"
        [Required, MaxLength(20)]
        public string GapType { get; set; } = "";

        // Actual punch data from Presence 360.
        public TimeOnly? ActualInTime  { get; set; }
        public TimeOnly? ActualOutTime { get; set; }

        [Column(TypeName = "decimal(5,2)")]
        public decimal? ActualHours   { get; set; }

        // Planned shift from the roster.
        public TimeOnly? PlannedInTime  { get; set; }
        public TimeOnly? PlannedOutTime { get; set; }

        [Column(TypeName = "decimal(5,2)")]
        public decimal? PlannedHours  { get; set; }

        // Derived gap metrics — only the relevant field is populated per GapType.
        public int? LateByMinutes  { get; set; }   // Late
        public int? ShortByMinutes { get; set; }   // ShortHours
        public int? ExtraByMinutes { get; set; }   // ExtraTime

        public bool      EmailSent   { get; set; } = false;
        public DateTime? EmailSentAt { get; set; }

        public DateTime  CreatedAt   { get; set; } = DateTime.UtcNow;
    }
}
