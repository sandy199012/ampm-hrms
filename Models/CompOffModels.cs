using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AmpmHrmsPro.Models
{
    // ═══════════════════════════════════════════
    // COMP-OFF RULE — the customizable "recipe" for how compensatory off is
    // earned and how long it lives. Assignment to employees is intentionally
    // NOT modeled here (no Category/Grade fields on the rule itself) — per
    // the FRD conversation, Admin wanted exactly ONE assignment scope per
    // employee with no automatic precedence to resolve, so a rule is just a
    // named, reusable definition; Employee.CompOffRuleId is the single
    // source of truth for "which rule applies to whom" (same pattern as
    // Employee.WeekOffPolicyId / LeavePolicyId). The three assignment
    // *methods* (Category-wise / Grade-wise / Employee-wise, all wanted
    // explicitly) all just end up setting that one FK — see
    // CompOffController's Assign actions.
    // ═══════════════════════════════════════════
    public class CompOffRule
    {
        [Key] public int Id { get; set; }
        [Required, MaxLength(100)] public string Name { get; set; } = "";
        [MaxLength(300)] public string? Description { get; set; }

        // ── Earning ── how many hours worked on a qualifying off-day earns
        // a full vs half comp-off day — same FullDayThresholdHours /
        // HalfDayThresholdHours convention as Shift, so this reads exactly
        // like every other "hours worked" threshold in the app.
        // MinHoursForHalfDay = 0 disables half-day credit entirely (anything
        // under the full-day threshold earns nothing under that rule).
        public decimal MinHoursForFullDay { get; set; } = 8m;
        public decimal MinHoursForHalfDay { get; set; } = 4m;

        // ── Which kind of off-day counts ── at least one should be true for
        // AutoCredit to ever fire; both false just means this rule is
        // manual-entry-only (still a valid, deliberate configuration).
        public bool CountHolidays { get; set; } = true;
        public bool CountWeekOffs { get; set; } = true;

        // Whether the system auto-detects "worked on a Holiday/Week-Off" from
        // attendance and credits comp-off by itself. Even when true, Admin/HR
        // can still log a manual entry (e.g. an off-site event that never hit
        // biometric attendance) — the two mechanisms don't disable each other.
        public bool AutoCredit { get; set; } = true;

        // Earned comp-off not used within this many days of being earned
        // expires automatically. Admin-editable per rule, per the FRD answer.
        public int ExpiryDays { get; set; } = 90;

        // Optional safety cap on how much UNUSED comp-off an employee on this
        // rule can be sitting on at once — null means no cap. When set,
        // auto-credit clips (rather than blocks) a new credit that would
        // push the balance over the cap.
        public decimal? MaxOpenBalance { get; set; }

        public bool IsActive { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }

    // ═══════════════════════════════════════════
    // COMP-OFF LEDGER — one row per earned comp-off "instance" (one worked
    // off-day, or one manual entry), tracked separately from the
    // once-a-year LeaveType balance formula every other leave type uses
    // (see ReportsController.LeaveReports's "Balance has no persisted
    // ledger" comment) — comp-off can't be computed on the fly like that
    // because each credit has its OWN expiry date tied to when it was
    // earned, not a shared annual cycle. Consumption happens through the
    // existing Leave-application flow (LeaveType.IsCompOff = true), debited
    // FIFO (earliest-expiring first) via CompOffEngine when that application
    // is approved — see CompOffConsumption below for the audit trail linking
    // an Application back to exactly which ledger rows it drew from.
    // ═══════════════════════════════════════════
    public class CompOffLedger
    {
        [Key] public int Id { get; set; }

        public int EmployeeId { get; set; }
        [ForeignKey("EmployeeId")] public Employee? Employee { get; set; }

        // The rule in effect at the moment this was earned — kept even if
        // the employee's CompOffRuleId later changes or the rule itself is
        // edited, so a past credit's expiry/history stays exactly as it was
        // granted (editing a rule must never retroactively reach into
        // already-earned credits).
        public int? CompOffRuleId { get; set; }
        [ForeignKey("CompOffRuleId")] public CompOffRule? CompOffRule { get; set; }

        [Required, MaxLength(10)] public string EarnedDate { get; set; } = ""; // YYYY-MM-DD — the off-day actually worked
        public decimal EarnedDays { get; set; } = 1;
        public decimal UsedDays { get; set; } = 0;

        [Required, MaxLength(10)] public string Source { get; set; } = "Manual"; // Auto, Manual

        // Recomputed by CompOffEngine.Sweep — never trust this without a
        // sweep first; it exists so list screens don't need to recompute
        // Available/Used/Expired from scratch on every page load.
        [Required, MaxLength(20)] public string Status { get; set; } = "Available"; // Available, Used, Expired, Cancelled

        [Required, MaxLength(10)] public string ExpiryDate { get; set; } = ""; // YYYY-MM-DD — snapshotted as EarnedDate + Rule.ExpiryDays at credit time

        [MaxLength(300)] public string? Remarks { get; set; }

        // Who logged a Manual entry — null for Auto (the system).
        public int? CreatedByEmployeeId { get; set; }
        [ForeignKey("CreatedByEmployeeId")] public Employee? CreatedByEmployee { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }

    // ═══════════════════════════════════════════
    // COMP-OFF CONSUMPTION — the audit trail linking one Leave Application
    // (against a Comp-Off leave type) back to the exact CompOffLedger rows
    // it drew down, and how much of each. Exists so Revoke can refund
    // precisely (and only) what a given application actually consumed,
    // instead of guessing.
    // ═══════════════════════════════════════════
    public class CompOffConsumption
    {
        [Key] public int Id { get; set; }

        public int ApplicationId { get; set; }
        [ForeignKey("ApplicationId")] public Application? Application { get; set; }

        public int CompOffLedgerId { get; set; }
        [ForeignKey("CompOffLedgerId")] public CompOffLedger? CompOffLedger { get; set; }

        public decimal DaysConsumed { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }

    // Plain view-model row for CompOffController's company-wide Ledger
    // summary — deliberately NOT a ValueTuple: a ValueTuple round-tripped
    // through the dynamic ViewBag has bitten this codebase before (its
    // element names don't survive reliably), so every ViewBag payload here
    // uses a named class instead. Lives in AmpmHrmsPro.Models (like every
    // other view model in this app, e.g. LeaveBalanceRow) so views pick it
    // up automatically via _ViewImports.cshtml's "@using AmpmHrmsPro.Models"
    // without needing a controller-namespace import.
    public class CompOffBalanceRow
    {
        public Employee Employee { get; set; } = null!;
        public decimal Balance { get; set; }
    }
}
