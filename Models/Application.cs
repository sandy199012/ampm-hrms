using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AmpmHrmsPro.Models
{
    // ═══════════════════════════════════════════
    // APPLICATION — one shared table for every kind of employee request
    // that needs manager approval and affects attendance: Leave,
    // Regularisation (correcting a mispunch), Work From Home, and On Duty.
    // The uploaded target report treats all four exactly the same way
    // (Application Tracker / Date-wise Applications sheets list them
    // together with a common Status/Approver/Remarks shape), so one table
    // with a Type discriminator mirrors that instead of four near-identical
    // tables.
    // ═══════════════════════════════════════════
    public class Application
    {
        [Key] public int Id { get; set; }

        public int EmployeeId { get; set; }
        [ForeignKey("EmployeeId")] public Employee? Employee { get; set; }

        [Required, MaxLength(20)] public string Type { get; set; } = "Leave"; // Leave, Regularisation, WFH, OD

        // Only set when Type = Leave — which leave type is being applied for.
        public int? LeaveTypeId { get; set; }
        [ForeignKey("LeaveTypeId")] public LeaveType? LeaveType { get; set; }

        [Required, MaxLength(10)] public string FromDate { get; set; } = ""; // YYYY-MM-DD
        [Required, MaxLength(10)] public string ToDate { get; set; } = "";   // YYYY-MM-DD
        public decimal DurationDays { get; set; } = 1;
        [MaxLength(20)] public string DayPart { get; set; } = "Single"; // Single, FirstHalf, SecondHalf

        // Only meaningful for Type = Regularisation — the corrected in/out
        // times the employee is requesting in place of the raw punch (or
        // missing punch).
        public TimeSpan? RequestedInTime { get; set; }
        public TimeSpan? RequestedOutTime { get; set; }

        [MaxLength(500)] public string? Reason { get; set; }

        public DateTime AppliedOn { get; set; } = DateTime.Now;

        // Who this is routed to for approval — defaults to the employee's
        // Reporting Manager, escalates to Department Head if unset (same
        // pattern the FRD's approval flowchart described: manager, then
        // department head, with a Send Back option modeled by Status =
        // "Pending" plus Remarks explaining what needs fixing).
        public int? ApproverEmployeeId { get; set; }
        [ForeignKey("ApproverEmployeeId")] public Employee? Approver { get; set; }

        [Required, MaxLength(20)] public string Status { get; set; } = "Pending"; // Pending, Approved, Rejected, Revoked, Cancelled
        [MaxLength(100)] public string? PendingAt { get; set; } // display name of whoever it's currently awaiting action from

        [MaxLength(500)] public string? Remarks { get; set; } // approver's note, or the employee's own note on cancel/revoke

        public DateTime? DecisionAt { get; set; }
        public int? DecisionByEmployeeId { get; set; }
        [ForeignKey("DecisionByEmployeeId")] public Employee? DecisionBy { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }
}
