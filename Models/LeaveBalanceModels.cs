using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AmpmHrmsPro.Models
{
    /// <summary>
    /// Stores one row per employee per leave-type per year.
    /// Matches the EL/CL Excel format exactly so bulk-upload is a direct map.
    /// </summary>
    public class LeaveBalance
    {
        [Key] public int Id { get; set; }

        public int EmployeeId { get; set; }
        [ForeignKey("EmployeeId")] public Employee? Employee { get; set; }

        /// <summary>"EL" or "CL"</summary>
        [Required, MaxLength(5)] public string LeaveTypeCode { get; set; } = "";

        public int Year { get; set; } = DateTime.Now.Year;

        // ── Opening balance (carry-forward from previous year) ─────────────
        [Column(TypeName = "decimal(7,3)")] public decimal CarryForward { get; set; }

        // ── Monthly accrual (credit) ───────────────────────────────────────
        [Column(TypeName = "decimal(5,3)")] public decimal EarnedJan { get; set; }
        [Column(TypeName = "decimal(5,3)")] public decimal EarnedFeb { get; set; }
        [Column(TypeName = "decimal(5,3)")] public decimal EarnedMar { get; set; }
        [Column(TypeName = "decimal(5,3)")] public decimal EarnedApr { get; set; }
        [Column(TypeName = "decimal(5,3)")] public decimal EarnedMay { get; set; }
        [Column(TypeName = "decimal(5,3)")] public decimal EarnedJun { get; set; }
        [Column(TypeName = "decimal(5,3)")] public decimal EarnedJul { get; set; }
        [Column(TypeName = "decimal(5,3)")] public decimal EarnedAug { get; set; }
        [Column(TypeName = "decimal(5,3)")] public decimal EarnedSep { get; set; }
        [Column(TypeName = "decimal(5,3)")] public decimal EarnedOct { get; set; }
        [Column(TypeName = "decimal(5,3)")] public decimal EarnedNov { get; set; }
        [Column(TypeName = "decimal(5,3)")] public decimal EarnedDec { get; set; }

        // ── Monthly consumption (debit) ────────────────────────────────────
        [Column(TypeName = "decimal(5,3)")] public decimal ConsumedJan { get; set; }
        [Column(TypeName = "decimal(5,3)")] public decimal ConsumedFeb { get; set; }
        [Column(TypeName = "decimal(5,3)")] public decimal ConsumedMar { get; set; }
        [Column(TypeName = "decimal(5,3)")] public decimal ConsumedApr { get; set; }
        [Column(TypeName = "decimal(5,3)")] public decimal ConsumedMay { get; set; }
        [Column(TypeName = "decimal(5,3)")] public decimal ConsumedJun { get; set; }
        [Column(TypeName = "decimal(5,3)")] public decimal ConsumedJul { get; set; }
        [Column(TypeName = "decimal(5,3)")] public decimal ConsumedAug { get; set; }
        [Column(TypeName = "decimal(5,3)")] public decimal ConsumedSep { get; set; }
        [Column(TypeName = "decimal(5,3)")] public decimal ConsumedOct { get; set; }
        [Column(TypeName = "decimal(5,3)")] public decimal ConsumedNov { get; set; }
        [Column(TypeName = "decimal(5,3)")] public decimal ConsumedDec { get; set; }

        public DateTime UpdatedAt { get; set; } = DateTime.Now;

        // ── Computed helpers (not stored) ─────────────────────────────────
        [NotMapped]
        public decimal TotalEarned =>
            EarnedJan + EarnedFeb + EarnedMar + EarnedApr + EarnedMay + EarnedJun +
            EarnedJul + EarnedAug + EarnedSep + EarnedOct + EarnedNov + EarnedDec;

        [NotMapped]
        public decimal TotalConsumed =>
            ConsumedJan + ConsumedFeb + ConsumedMar + ConsumedApr + ConsumedMay + ConsumedJun +
            ConsumedJul + ConsumedAug + ConsumedSep + ConsumedOct + ConsumedNov + ConsumedDec;

        [NotMapped]
        public decimal Balance => CarryForward + TotalEarned - TotalConsumed;

        // ── Helper arrays for view binding ────────────────────────────────
        [NotMapped]
        public decimal[] EarnedByMonth => new[] {
            EarnedJan, EarnedFeb, EarnedMar, EarnedApr, EarnedMay, EarnedJun,
            EarnedJul, EarnedAug, EarnedSep, EarnedOct, EarnedNov, EarnedDec
        };

        [NotMapped]
        public decimal[] ConsumedByMonth => new[] {
            ConsumedJan, ConsumedFeb, ConsumedMar, ConsumedApr, ConsumedMay, ConsumedJun,
            ConsumedJul, ConsumedAug, ConsumedSep, ConsumedOct, ConsumedNov, ConsumedDec
        };
    }
}
