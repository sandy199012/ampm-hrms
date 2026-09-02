using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AmpmHrmsPro.Models
{
    // ═══════════════════════════════════════════
    // OT (OVERTIME) MODULE — mirror of the Comp-Off module but for Worker
    // category employees. Key differences:
    //
    //   OTType      — "Pay" (hours → salary multiplier), "Leave" (hours →
    //                 time-off like Comp-Off), or "Both" (admin decides per
    //                 approval). Rule carries the default; individual ledger
    //                 rows track which type was actually applied.
    //
    //   OT sources  — two auto-trigger paths (set per rule):
    //       1. Shift-OT   : worked > shift duration on a normal workday
    //       2. Holiday/WO : worked on Holiday or Week-Off (full worked
    //                       minutes count as OT, with optional separate rate)
    //
    //   Assignment  — same three surfaces as Comp-Off: Category / Grade /
    //                 Employee-wise, all writing Employee.OTRuleId directly.
    //
    // Mobile differentiation: employees with an OTRuleId see the OT menu;
    // employees with a CompOffRuleId see the Comp-Off menu. The mobile
    // profile endpoint returns feature flags for both.
    // ═══════════════════════════════════════════

    public class OTRule
    {
        [Key] public int Id { get; set; }

        [Required, MaxLength(100)]
        public string Name { get; set; } = "";

        [MaxLength(300)]
        public string? Description { get; set; }

        // ── What the worker earns for OT ──────────────────────────────────
        // "Pay"   → extra pay; OTRateMultiplier applied to hourly wage
        // "Leave" → time-off (like Comp-Off, stored as OT days not pay)
        // "Both"  → both pay and time-off; admin selects on approval
        [Required, MaxLength(10)]
        public string OTType { get; set; } = "Pay";

        // ── Triggers ──────────────────────────────────────────────────────
        // CountAfterShiftHours: any minutes worked beyond the employee's
        // assigned shift duration count as OT on regular workdays.
        public bool CountAfterShiftHours { get; set; } = true;
        public bool CountHolidays        { get; set; } = true;
        public bool CountWeekOffs        { get; set; } = true;

        // ── Thresholds & caps ─────────────────────────────────────────────
        // MinOTMinutesPerDay: minimum OT minutes in a day to qualify at all.
        // OT below this threshold is ignored (e.g. a 15-min overshoot).
        public int MinOTMinutesPerDay   { get; set; } = 30;
        public int? MaxOTMinutesPerDay  { get; set; }       // null = no cap

        // ── Pay rate multipliers (relevant when OTType is Pay or Both) ────
        // NormalOTMultiplier: rate for shift-overshoot OT on regular days
        // HolidayOTMultiplier: rate for working on Holiday / Week-Off
        [Column(TypeName = "decimal(4,2)")]
        public decimal NormalOTMultiplier  { get; set; } = 1.5m;

        [Column(TypeName = "decimal(4,2)")]
        public decimal HolidayOTMultiplier { get; set; } = 2.0m;

        // ── Leave conversion (relevant when OTType is Leave or Both) ──────
        // How many OT minutes = 1 leave day. Default 480 = 8-hour workday.
        public int MinutesPerOTLeaveDay { get; set; } = 480;

        // ── Slab rounding ─────────────────────────────────────────────────
        // When true, after-shift OT minutes are rounded to 30-min slabs:
        //   extra ≤ 30 min → 0 OT   (MinOTMinutesPerDay threshold handles this)
        //   extra 31–45 min → 30 min
        //   extra 46–75 min → 60 min
        //   extra 76–105 min → 90 min  (etc.)
        // When false, exact minutes are credited.
        public bool UseSlabRounding { get; set; } = true;

        // ── Retail rule flag ──────────────────────────────────────────────
        // When true, week-off / Sunday OT is calculated using the retail
        // method (In-time + 9 h boundary) instead of the flat "all worked
        // minutes = OT" rule that applies to non-retail workers.
        public bool IsRetailRule { get; set; } = false;

        public bool IsActive   { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }

    public class OTLedger
    {
        [Key] public int Id { get; set; }

        public int EmployeeId { get; set; }
        [ForeignKey("EmployeeId")]
        public Employee? Employee { get; set; }

        public int? OTRuleId { get; set; }
        [ForeignKey("OTRuleId")]
        public OTRule? OTRule { get; set; }

        [Required, MaxLength(10)]
        public string Date { get; set; } = "";  // YYYY-MM-DD

        // OT minutes earned on this date
        public int OTMinutes { get; set; }

        // "Shift" = shift-overshoot OT; "Holiday" = worked on Holiday/WO
        [Required, MaxLength(20)]
        public string OTKind { get; set; } = "Shift";

        // Mirrors OTRule.OTType at credit time so a rule change doesn't
        // retroactively alter already-earned records.
        [Required, MaxLength(10)]
        public string OTType { get; set; } = "Pay";

        [Required, MaxLength(20)]
        public string Source { get; set; } = "Manual"; // Auto | Manual

        // Pending  → logged, awaiting supervisor approval
        // Approved → approved; ready to be paid / converted to leave
        // Paid     → pay processed (Pay/Both types)
        // Converted→ converted to leave (Leave/Both types)
        // Cancelled
        [Required, MaxLength(20)]
        public string Status { get; set; } = "Pending";

        [MaxLength(300)]
        public string? Remarks { get; set; }

        public int? CreatedByEmployeeId { get; set; }
        [ForeignKey("CreatedByEmployeeId")]
        public Employee? CreatedByEmployee { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }

    // Read-side DTO — company-wide OT summary card per employee.
    // Named class (not ValueTuple) so it survives round-trip through
    // dynamic ViewBag without losing property names.
    public class OTSummaryRow
    {
        public Employee Employee  { get; set; } = null!;
        public int      OTMinutes { get; set; }  // total Approved/Pending minutes
    }
}
