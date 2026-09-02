using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AmpmHrmsPro.Models
{
    // ═══════════════════════════════════════════
    // SALARY COMPONENT MASTER — admin-defined earning/deduction building
    // blocks. Deliberately NOT an arbitrary formula language (too much
    // untestable risk with zero compile access in this project) — instead a
    // fixed, finite set of CalculationTypes covers realistic Indian-payroll
    // customization: a flat monthly amount, or a percentage of Basic / CTC
    // / Gross-so-far (see PayrollTaxEngine.ComputeSalaryBreakdown for the
    // exact multi-pass resolution order that keeps this well-defined).
    // ═══════════════════════════════════════════
    public class SalaryComponent
    {
        [Key] public int Id { get; set; }
        [Required, MaxLength(80)] public string Name { get; set; } = "";
        [MaxLength(20)] public string? Code { get; set; }
        [Required, MaxLength(20)] public string ComponentType { get; set; } = "Earning"; // Earning, Deduction (payslip-level, e.g. PF/ESI/Professional Tax — NOT an income-tax section)
        [Required, MaxLength(30)] public string CalculationType { get; set; } = "Fixed"; // Fixed, PercentOfBasic, PercentOfCTC, PercentOfGross

        // Flat monthly amount OR percent, depending on CalculationType.
        public decimal DefaultValue { get; set; } = 0;

        // Exactly one active component should be marked IsBasic — the
        // "Basic" wage that PercentOfBasic and the HRA exemption formula
        // both key off. Must be Fixed or PercentOfCTC (never PercentOfBasic
        // itself — that would be circular). Enforced in SalaryController.
        public bool IsBasic { get; set; } = false;

        // Whether this component's amount is added into taxable Gross
        // Salary for the income-tax computation. Almost all earnings are;
        // a few reimbursement-style components an employer might add
        // (e.g. a fixed conveyance/telephone reimbursement) are not.
        public bool IsTaxable { get; set; } = true;

        // Marks the component that represents House Rent Allowance, so the
        // tax engine knows which EmployeeSalaryComponent row to read for
        // the Section 10(13A) HRA exemption. At most one per structure
        // should be marked, same rule as IsBasic.
        public bool IsHRA { get; set; } = false;

        public int DisplayOrder { get; set; } = 0;
        public bool IsActive { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }

    // A reusable named structure (e.g. one per Grade) — Admin builds it
    // once, then assigns/copies it onto many employees instead of
    // re-entering every component per employee.
    public class SalaryStructureTemplate
    {
        [Key] public int Id { get; set; }
        [Required, MaxLength(80)] public string Name { get; set; } = "";
        [MaxLength(200)] public string? Description { get; set; }
        public int? GradeId { get; set; }
        [ForeignKey("GradeId")] public Grade? Grade { get; set; }
        public bool IsActive { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.Now;

        public List<SalaryStructureTemplateItem> Items { get; set; } = new();
    }

    public class SalaryStructureTemplateItem
    {
        [Key] public int Id { get; set; }
        public int SalaryStructureTemplateId { get; set; }
        [ForeignKey("SalaryStructureTemplateId")] public SalaryStructureTemplate? Template { get; set; }

        public int SalaryComponentId { get; set; }
        [ForeignKey("SalaryComponentId")] public SalaryComponent? SalaryComponent { get; set; }

        [Required, MaxLength(30)] public string CalculationType { get; set; } = "Fixed";
        public decimal Value { get; set; } = 0;
        public int DisplayOrder { get; set; } = 0;
    }

    // ═══════════════════════════════════════════
    // EMPLOYEE SALARY STRUCTURE — the actual, versioned structure assigned
    // to one employee. A revision (increment, promotion, etc.) closes the
    // previous row's EffectiveTo and adds a new row with EffectiveTo = null
    // (current) — it never edits history in place, so past TDS/payroll
    // numbers stay reproducible against whatever structure was live then.
    // ═══════════════════════════════════════════
    public class EmployeeSalaryStructure
    {
        [Key] public int Id { get; set; }
        public int EmployeeId { get; set; }
        [ForeignKey("EmployeeId")] public Employee? Employee { get; set; }

        [Required, MaxLength(10)] public string EffectiveFrom { get; set; } = ""; // YYYY-MM-DD
        [MaxLength(10)] public string? EffectiveTo { get; set; } // YYYY-MM-DD, null = current

        public decimal AnnualCTC { get; set; } = 0;
        public int? SourceTemplateId { get; set; }
        [ForeignKey("SourceTemplateId")] public SalaryStructureTemplate? SourceTemplate { get; set; }

        public int? CreatedByEmployeeId { get; set; }
        [ForeignKey("CreatedByEmployeeId")] public Employee? CreatedByEmployee { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.Now;

        public List<EmployeeSalaryComponent> Components { get; set; } = new();
    }

    public class EmployeeSalaryComponent
    {
        [Key] public int Id { get; set; }
        public int EmployeeSalaryStructureId { get; set; }
        [ForeignKey("EmployeeSalaryStructureId")] public EmployeeSalaryStructure? EmployeeSalaryStructure { get; set; }

        public int SalaryComponentId { get; set; }
        [ForeignKey("SalaryComponentId")] public SalaryComponent? SalaryComponent { get; set; }

        [Required, MaxLength(30)] public string CalculationType { get; set; } = "Fixed";
        public decimal Value { get; set; } = 0;          // the flat amount or percent, as entered for this employee
        public decimal MonthlyAmount { get; set; } = 0;   // resolved ₹/month, cached at assignment time for fast reads
        public int DisplayOrder { get; set; } = 0;
    }

    // ═══════════════════════════════════════════
    // TAX SLAB SETTINGS — one row per Financial Year + Regime, fully
    // admin-editable so a future Union Budget change never requires a code
    // change. Seeded with figures verified via web search for FY 2026-27
    // (see SeedData.cs) — never hardcoded inside the calculation engine.
    // ═══════════════════════════════════════════
    public class TaxSlabSettings
    {
        [Key] public int Id { get; set; }
        [Required, MaxLength(10)] public string FinancialYear { get; set; } = ""; // e.g. "2026-27"
        [Required, MaxLength(10)] public string Regime { get; set; } = "New"; // Old, New
        public decimal StandardDeduction { get; set; } = 0;
        public decimal Rebate87AIncomeLimit { get; set; } = 0; // taxable income must be <= this to qualify for the rebate
        public decimal Rebate87AMaxAmount { get; set; } = 0;
        public decimal CessPercent { get; set; } = 4;
        public bool IsActive { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.Now;

        public List<TaxSlab> Slabs { get; set; } = new();
        public List<TaxSurchargeSlab> SurchargeSlabs { get; set; } = new();
    }

    public class TaxSlab
    {
        [Key] public int Id { get; set; }
        public int TaxSlabSettingsId { get; set; }
        [ForeignKey("TaxSlabSettingsId")] public TaxSlabSettings? TaxSlabSettings { get; set; }
        public decimal FromAmount { get; set; } = 0;
        public decimal? ToAmount { get; set; } // null = no upper limit (top slab)
        public decimal RatePercent { get; set; } = 0;
        public int DisplayOrder { get; set; } = 0;
    }

    // Surcharge is a percentage of the TAX amount (not income), tiered by
    // taxable income. Old and New regime share the same tiers except the
    // top one (25% capped in New Regime vs 37% in Old) — kept as its own
    // per-regime list so every tier stays independently editable.
    public class TaxSurchargeSlab
    {
        [Key] public int Id { get; set; }
        public int TaxSlabSettingsId { get; set; }
        [ForeignKey("TaxSlabSettingsId")] public TaxSlabSettings? TaxSlabSettings { get; set; }
        public decimal FromAmount { get; set; } = 0;
        public decimal? ToAmount { get; set; }
        public decimal RatePercent { get; set; } = 0;
        public int DisplayOrder { get; set; } = 0;
    }

    // ═══════════════════════════════════════════
    // TAX SECTION MASTER — the fixed-but-admin-editable list of deduction
    // sections an employee can declare investments against (80C, 80D,
    // etc.). Limits are editable so a future Budget change to a cap never
    // needs a code change. Same "no arbitrary formula" philosophy as
    // SalaryComponent — a bounded, seeded list rather than free-form rules.
    // ═══════════════════════════════════════════
    public class TaxSectionMaster
    {
        [Key] public int Id { get; set; }
        [Required, MaxLength(20)] public string Code { get; set; } = ""; // "80C", "80D", ...
        [Required, MaxLength(120)] public string Name { get; set; } = "";
        [MaxLength(300)] public string? Description { get; set; }
        public decimal? MaxLimit { get; set; } // null = no fixed cap enforced here
        [Required, MaxLength(10)] public string ApplicableRegime { get; set; } = "Old"; // Old, Both — New Regime allows almost none of these
        public bool RequiresDocument { get; set; } = true;
        public int DisplayOrder { get; set; } = 0;
        public bool IsActive { get; set; } = true;
    }

    // ═══════════════════════════════════════════
    // INCOME TAX DECLARATION — one header per employee per financial year.
    // HRA gets its own dedicated fields rather than a generic
    // TaxSectionMaster row because its exemption isn't a capped declared
    // amount — it's the Section 10(13A) 3-way-minimum formula, which needs
    // rent paid + city type + the employee's actual Basic/HRA from their
    // live salary structure, computed by PayrollTaxEngine, not declared.
    // ═══════════════════════════════════════════
    public class TaxDeclarationHeader
    {
        [Key] public int Id { get; set; }
        public int EmployeeId { get; set; }
        [ForeignKey("EmployeeId")] public Employee? Employee { get; set; }

        [Required, MaxLength(10)] public string FinancialYear { get; set; } = "";
        [Required, MaxLength(10)] public string RegimeChoice { get; set; } = "Auto"; // Old, New, Auto (engine recommends whichever gives lower tax)

        // HRA — Section 10(13A)
        public decimal AnnualRentPaid { get; set; } = 0;
        public bool IsMetroCity { get; set; } = false;
        [MaxLength(300)] public string? RentReceiptDocumentUrl { get; set; }

        [Required, MaxLength(20)] public string Status { get; set; } = "Draft"; // Draft, Submitted
        public DateTime? SubmittedAt { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime UpdatedAt { get; set; } = DateTime.Now;

        public List<TaxDeclarationItem> Items { get; set; } = new();
    }

    public class TaxDeclarationItem
    {
        [Key] public int Id { get; set; }
        public int TaxDeclarationHeaderId { get; set; }
        [ForeignKey("TaxDeclarationHeaderId")] public TaxDeclarationHeader? Header { get; set; }

        public int TaxSectionMasterId { get; set; }
        [ForeignKey("TaxSectionMasterId")] public TaxSectionMaster? Section { get; set; }

        [MaxLength(200)] public string? Description { get; set; }
        public decimal DeclaredAmount { get; set; } = 0;
        public decimal? ApprovedAmount { get; set; }
        [MaxLength(300)] public string? DocumentUrl { get; set; }

        [Required, MaxLength(20)] public string Status { get; set; } = "Pending"; // Pending, Approved, Rejected
        [MaxLength(300)] public string? AdminRemarks { get; set; }
        public int? ReviewedByEmployeeId { get; set; }
        [ForeignKey("ReviewedByEmployeeId")] public Employee? ReviewedByEmployee { get; set; }
        public DateTime? ReviewedAt { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }
}
