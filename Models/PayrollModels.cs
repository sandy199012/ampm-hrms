using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AmpmHrmsPro.Models
{
    // ═══════════════════════════════════════════════════════════════════
    // MONTHLY PAYROLL — one PayrollRun per month; one Payslip per employee
    // in it, with one PayslipLine per salary component (earning or
    // deduction). Built by Services/PayrollEngine.cs from the employee's
    // salary structure + that month's attendance (loss-of-pay days).
    // Draft runs can be regenerated and edited; Finalised ones are locked
    // and visible to the employee under My Payslips.
    // ═══════════════════════════════════════════════════════════════════
    public class PayrollRun
    {
        [Key] public int Id { get; set; }
        public int Year { get; set; }
        public int Month { get; set; }
        [Required, MaxLength(20)] public string Status { get; set; } = "Draft"; // Draft, Finalised
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? GeneratedAt { get; set; }
        public DateTime? FinalisedAt { get; set; }
        [MaxLength(120)] public string? FinalisedBy { get; set; }
        public List<Payslip> Payslips { get; set; } = new();
    }

    public class Payslip
    {
        [Key] public int Id { get; set; }
        public int PayrollRunId { get; set; }
        [ForeignKey("PayrollRunId")] public PayrollRun? PayrollRun { get; set; }
        public int EmployeeId { get; set; }
        [ForeignKey("EmployeeId")] public Employee? Employee { get; set; }
        public int Year { get; set; }
        public int Month { get; set; }

        [Column(TypeName = "decimal(6,2)")] public decimal DaysInMonth { get; set; }
        [Column(TypeName = "decimal(6,2)")] public decimal LopDays { get; set; }
        [Column(TypeName = "decimal(6,2)")] public decimal LeaveDays { get; set; }
        [Column(TypeName = "decimal(6,2)")] public decimal PaidDays { get; set; }
        /// <summary>Set by HR on the edit screen — wins over the attendance-based figure.</summary>
        [Column(TypeName = "decimal(6,2)")] public decimal? PaidDaysOverride { get; set; }

        [Column(TypeName = "decimal(12,2)")] public decimal GrossRate { get; set; }
        [Column(TypeName = "decimal(12,2)")] public decimal GrossEarned { get; set; }
        [Column(TypeName = "decimal(12,2)")] public decimal TotalDeductions { get; set; }
        [Column(TypeName = "decimal(12,2)")] public decimal NetPay { get; set; }

        // Snapshot of the employee master at generation time (the slip stays as issued)
        [MaxLength(20)]  public string EmpCode { get; set; } = "";
        [MaxLength(120)] public string EmployeeName { get; set; } = "";
        [MaxLength(120)] public string? FatherOrHusbandName { get; set; }
        [MaxLength(100)] public string? Designation { get; set; }
        [MaxLength(100)] public string? Department { get; set; }
        [MaxLength(40)]  public string? PFNumber { get; set; }
        [MaxLength(20)]  public string? PANNumber { get; set; }
        [MaxLength(40)]  public string? BankAccount { get; set; }
        [MaxLength(40)]  public string? ESICNumber { get; set; }
        [MaxLength(10)]  public string? DOJ { get; set; }
        [MaxLength(20)]  public string? UANNumber { get; set; }

        [MaxLength(300)] public string? Remarks { get; set; }
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        public List<PayslipLine> Lines { get; set; } = new();
    }

    public class PayslipLine
    {
        [Key] public int Id { get; set; }
        public int PayslipId { get; set; }
        [ForeignKey("PayslipId")] public Payslip? Payslip { get; set; }
        public int? SalaryComponentId { get; set; }
        [MaxLength(20)] public string? Code { get; set; }
        [Required, MaxLength(80)] public string Name { get; set; } = "";
        [Required, MaxLength(20)] public string Type { get; set; } = "Earning"; // Earning, Deduction
        [Column(TypeName = "decimal(12,2)")] public decimal Rate { get; set; }     // full-month amount
        [Column(TypeName = "decimal(12,2)")] public decimal Amount { get; set; }   // paid this month
        /// <summary>Typed by HR — kept as-is when the payslip is recalculated.</summary>
        public bool IsManual { get; set; }
        public int DisplayOrder { get; set; }
    }
}
