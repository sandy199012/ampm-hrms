using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AmpmHrmsPro.Models
{
    // ═══════════════════════════════════════════
    // LEAVE POLICY MASTER — a named, reusable set of accrual/carry-forward/
    // encashment rules (e.g. "Corporate Staff Leave Policy"), one rule per
    // Leave Type, assigned to employees the same way Week-Off Policy is
    // (Employee.LeavePolicyId). This is the RULES layer; Leave Type (see
    // MasterData.cs) stays the simple "what leave options exist" master.
    //
    // Example this was built from: EL for corporate staff accrues 1.41
    // days/month, 17 days/year, leave year runs Jan-Dec, up to 45 EL
    // carries forward into the next year, anything above 45 becomes
    // encashable (but only when Admin/HR manually processes it — never
    // automatic).
    // ═══════════════════════════════════════════
    public class LeavePolicy
    {
        [Key] public int Id { get; set; }
        [Required, MaxLength(80)] public string Name { get; set; } = "";
        [MaxLength(200)] public string? Description { get; set; }
        public bool IsActive { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.Now;

        public List<LeavePolicyRule> Rules { get; set; } = new();
    }

    // One line of a Leave Policy — the full accrual/carry-forward/
    // encashment rule for a single Leave Type within that policy.
    public class LeavePolicyRule
    {
        [Key] public int Id { get; set; }
        public int LeavePolicyId { get; set; }
        [ForeignKey("LeavePolicyId")] public LeavePolicy? LeavePolicy { get; set; }

        public int LeaveTypeId { get; set; }
        [ForeignKey("LeaveTypeId")] public LeaveType? LeaveType { get; set; }

        // How the balance is credited.
        [MaxLength(20)] public string AccrualMethod { get; set; } = "Monthly"; // Monthly, Yearly, OneTime
        public decimal? MonthlyAccrualDays { get; set; }   // e.g. 1.41 — used when AccrualMethod = Monthly
        public decimal AnnualEntitlementDays { get; set; } // e.g. 17 — full-year total (monthly credits should sum to this; any rounding difference is reconciled in the final month by the accrual job)
        public int CycleStartMonth { get; set; } = 1;       // 1=January .. 12=December — when the leave year resets and balance calculation restarts

        // What happens to the balance at cycle-end.
        public decimal? CarryForwardLimit { get; set; }     // e.g. 45 — max balance carried into the next cycle; null = carry forward everything, no cap
        [MaxLength(20)] public string ExcessHandling { get; set; } = "Encashment"; // Encashment, Lapse, CarryForwardAll — what happens to balance ABOVE CarryForwardLimit
        [MaxLength(20)] public string EncashmentTrigger { get; set; } = "Manual"; // Manual (Admin/HR initiates it whenever), AutoYearEnd — only meaningful when ExcessHandling = Encashment
    }
}
