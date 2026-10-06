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

        // Direct reporting manager (from Presence 360 "Manager Name").
        [MaxLength(120)]
        public string? ManagerName  { get; set; }

        // HoD = first manager up the reporting chain who is in the HoD /
        // pilot list (e.g. Shivam → Sunny Malik → MANISH RANA).
        [MaxLength(120)]
        public string? HodName      { get; set; }

        // Email of the HoD who receives this gap (pilot HoDs only).
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

    // ── GapReportRecipient ─────────────────────────────────────────────────────
    // Who receives the Gap Analysis email: one row per HoD, maintained by the
    // admin on Attendance Review → HoD Emails. A HoD without a row here gets
    // NO email.
    public class GapReportRecipient
    {
        public int Id { get; set; }

        [Required, MaxLength(120)]
        public string HodName { get; set; } = "";

        [Required, MaxLength(200)]
        public string Email { get; set; } = "";

        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }

    // ── P360DailyRecord ────────────────────────────────────────────────────────
    // Every row of every uploaded Presence 360 report (not only the gaps), so
    // the Month-to-Date Gap Analysis can show each employee's full month.
    // One row per employee per date — a later upload of the same date replaces it.
    public class P360DailyRecord
    {
        public int Id { get; set; }

        // Employee code without leading zeros ("00109" → "109") so it matches
        // the roster / master sheet "Employee ID" numbers.
        [Required, MaxLength(20)] public string EmpCode { get; set; } = "";
        [MaxLength(120)] public string EmployeeName { get; set; } = "";
        [MaxLength(120)] public string? ManagerName { get; set; }
        [MaxLength(80)]  public string? Department  { get; set; }

        public DateOnly Date { get; set; }
        public TimeOnly? InTime  { get; set; }
        public TimeOnly? OutTime { get; set; }

        // Presence 360 "Status" (P, HD, A, WO, H, POH, POW, A (MIS), L (CL) …)
        // — shown as "HRMS Status" in the report.
        [MaxLength(30)] public string? Status { get; set; }
        // present / absent / leave / week-off
        [MaxLength(20)] public string? AttendanceType { get; set; }

        public TimeOnly? ShiftIn  { get; set; }
        public TimeOnly? ShiftOut { get; set; }

        public int ImportId { get; set; }
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }

    // ── ReviewEmployee ─────────────────────────────────────────────────────────
    // The "Employee Master" sheet of Master Sheet Employee Attendance
    // Management.xlsx — who reports to whom. Decides "Reports To" and which
    // employees go into each HoD's Gap Analysis (the HoD's whole team, every
    // level below them).
    public class ReviewEmployee
    {
        public int Id { get; set; }
        [Required, MaxLength(20)] public string EmpCode { get; set; } = "";   // no leading zeros
        [Required, MaxLength(120)] public string Name { get; set; } = "";
        [MaxLength(120)] public string? ManagerName { get; set; }
        [MaxLength(20)]  public string? ManagerCode { get; set; }             // no leading zeros
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }

    // ── GapReportSend ──────────────────────────────────────────────────────────
    // One row per Gap Analysis email sent (HoD + report date), so the daily
    // job never sends the same HoD the same day's report twice.
    public class GapReportSend
    {
        public int Id { get; set; }
        [Required, MaxLength(120)] public string HodName { get; set; } = "";
        public DateOnly ReportDate { get; set; }
        [MaxLength(200)] public string Email { get; set; } = "";
        public DateTime SentAt { get; set; } = DateTime.UtcNow;
    }
}
