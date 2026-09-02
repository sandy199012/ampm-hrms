using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AmpmHrmsPro.Models
{
    // ═══════════════════════════════════════════
    // EMPLOYEE MASTER — every field that used to be free text (Department,
    // Designation, Location, etc.) is now a real foreign key into its own
    // master table, so Admin manages one source of truth per master and
    // every employee record stays consistent with it. Fields below the
    // "Extended Profile" marker match the company's existing HR export
    // format 1:1, so that report can be bulk-imported without remapping.
    // ═══════════════════════════════════════════
    public class Employee
    {
        [Key] public int Id { get; set; }

        // ── Identity / login ──
        [Required, MaxLength(20)] public string EmpCode { get; set; } = "";
        [Required, MaxLength(100)] public string Name { get; set; } = "";
        [Required, MaxLength(120)] public string Email { get; set; } = "";
        [Required] public string PasswordHash { get; set; } = "";
        [Required, MaxLength(20)] public string Role { get; set; } = "employee"; // admin, hr, manager, employee — SYSTEM login role, not HR level

        // ── Personal ──
        [MaxLength(15)] public string? Mobile { get; set; }
        [MaxLength(10)] public string? Gender { get; set; } // Male, Female, Other
        [MaxLength(10)] public string? DOB { get; set; }    // YYYY-MM-DD
        [MaxLength(200)] public string? Address { get; set; }
        public string? PhotoUrl { get; set; }

        // ── Employment — all proper masters, not free text ──
        [MaxLength(10)] public string? DOJ { get; set; } // YYYY-MM-DD

        public int? DepartmentId { get; set; }
        [ForeignKey("DepartmentId")] public Department? Department { get; set; }

        public int? DesignationId { get; set; }
        [ForeignKey("DesignationId")] public Designation? Designation { get; set; }

        public int? LocationId { get; set; }
        [ForeignKey("LocationId")] public Location? Location { get; set; }

        public int? GradeId { get; set; }
        [ForeignKey("GradeId")] public Grade? Grade { get; set; }

        public int? EmploymentTypeId { get; set; }
        [ForeignKey("EmploymentTypeId")] public EmploymentType? EmploymentType { get; set; }

        public int? ShiftId { get; set; }
        [ForeignKey("ShiftId")] public Shift? Shift { get; set; }

        public int? WeekOffPolicyId { get; set; }
        [ForeignKey("WeekOffPolicyId")] public WeekOffPolicy? WeekOffPolicy { get; set; }

        public int? LeavePolicyId { get; set; }
        [ForeignKey("LeavePolicyId")] public LeavePolicy? LeavePolicy { get; set; }

        // Which Comp-Off Rule this employee earns/expires under — set
        // directly by Admin, whether via the Category-wise, Grade-wise, or
        // single-Employee assignment screen (all three just write this one
        // FK; there's no separate override-precedence to resolve since only
        // one rule can ever be assigned at a time).
        public int? CompOffRuleId { get; set; }
        [ForeignKey("CompOffRuleId")] public CompOffRule? CompOffRule { get; set; }

        // OT Rule — assigned to Worker-category employees. Mutually exclusive
        // with CompOffRule in practice (the Assign UI enforces this); the DB
        // allows both so Admin can transition an employee without a hard lock.
        public int? OTRuleId { get; set; }
        [ForeignKey("OTRuleId")] public OTRule? OTRule { get; set; }

        // Self-referencing — who this employee reports to.
        public int? ReportingManagerId { get; set; }
        [ForeignKey("ReportingManagerId")] public Employee? ReportingManager { get; set; }

        [MaxLength(20)] public string Status { get; set; } = "Active"; // Active, Inactive, Resigned, Terminated, On Hold
        public bool IsActive { get; set; } = true;

        public DateTime CreatedAt { get; set; } = DateTime.Now;

        // ═══════════════════════════════════════════
        // EXTENDED PROFILE — matches the company's existing HR data-export
        // report column-for-column, so that file can be used as a bulk
        // upload template without any remapping.
        // ═══════════════════════════════════════════

        // ── Family / identity ──
        [MaxLength(100)] public string? FatherOrHusbandName { get; set; }
        [MaxLength(15)] public string? AlternateMobile { get; set; }
        [MaxLength(60)] public string? Nationality { get; set; }
        [MaxLength(60)] public string? Religion { get; set; }
        [MaxLength(100)] public string? Qualification { get; set; }
        [MaxLength(60)] public string? Country { get; set; }
        [MaxLength(60)] public string? State { get; set; }
        [MaxLength(60)] public string? City { get; set; }
        [MaxLength(10)] public string? Pincode { get; set; }

        // ── Employment / org (raw, informational — not auth-related) ──
        [MaxLength(10)] public string? GroupDOJ { get; set; }        // YYYY-MM-DD
        [MaxLength(40)] public string? CardNumber { get; set; }
        [MaxLength(40)] public string? CompanyCode { get; set; }
        [MaxLength(40)] public string? EmployeeRole { get; set; }    // HR-level "Role" from the source system (e.g. "Employee") — distinct from the system login Role above
        [MaxLength(80)] public string? WorkStation { get; set; }
        [MaxLength(40)] public string? Category { get; set; }        // Staff, Worker, etc.
        [MaxLength(80)] public string? SubDepartment { get; set; }
        [MaxLength(120)] public string? AdditionalShifts { get; set; }
        [MaxLength(10)] public string? ValidFrom { get; set; }       // YYYY-MM-DD
        [MaxLength(10)] public string? ValidTo { get; set; }         // YYYY-MM-DD
        [MaxLength(10)] public string? DateOfLeaving { get; set; }   // YYYY-MM-DD
        [MaxLength(10)] public string? InactivationDate { get; set; } // YYYY-MM-DD
        [MaxLength(30)] public string? Experience { get; set; }
        [MaxLength(20)] public string? StandardWorkingHour { get; set; }
        public bool IsAutoShift { get; set; } = false;
        public bool IsAutoInactive { get; set; } = false;

        // ── Statutory & bank ──
        [MaxLength(60)] public string? CompanyPFCode { get; set; }
        [MaxLength(80)] public string? BankName { get; set; }
        [MaxLength(30)] public string? AccountNumber { get; set; }
        [MaxLength(100)] public string? AccountHolderName { get; set; }
        [MaxLength(20)] public string? IFSCCode { get; set; }
        [MaxLength(20)] public string? UANNumber { get; set; }
        [MaxLength(20)] public string? AadharNumber { get; set; }
        [MaxLength(30)] public string? PFNumber { get; set; }
        [MaxLength(30)] public string? ESICNumber { get; set; }
        [MaxLength(15)] public string? PANNumber { get; set; }
        [MaxLength(30)] public string? IPNumber { get; set; }
        [MaxLength(30)] public string? PaymentMode { get; set; }     // Cash, Bank Transfer, Cheque

        // ── Emergency contact ──
        [MaxLength(100)] public string? EmergencyContactName { get; set; }
        [MaxLength(15)] public string? EmergencyContactMobile { get; set; }
        [MaxLength(40)] public string? EmergencyContactRelation { get; set; }

        // ── Location / attendance access ──
        [MaxLength(30)] public string? LocationType { get; set; }    // Strict Location, Any Location, etc.
        [MaxLength(300)] public string? MappedLocations { get; set; }
        [MaxLength(300)] public string? MappedSubLocations { get; set; }
        [MaxLength(300)] public string? PunchInLocation { get; set; }
        public bool AppLogin { get; set; } = true;
        public bool AttendanceAccessApp { get; set; } = true;
        public bool ActivateCheckIn { get; set; } = false;
        [MaxLength(60)] public string? Zone { get; set; }
        public bool BusAllowed { get; set; } = false;
        [MaxLength(80)] public string? BusName { get; set; }
        [MaxLength(20)] public string? FlexiHours { get; set; }

        // ── Training ──
        [MaxLength(100)] public string? TrainingGivenBy { get; set; }
        [MaxLength(60)] public string? TrainingType { get; set; }
        [MaxLength(60)] public string? TrainingUserType { get; set; }
        [MaxLength(100)] public string? ExternalTrainerName { get; set; }
        [MaxLength(120)] public string? ExternalTrainerEmail { get; set; }
        [MaxLength(10)] public string? TrainingDate { get; set; }    // YYYY-MM-DD
        [MaxLength(30)] public string? TrainingStatus { get; set; }

        // ── Notes ──
        [MaxLength(500)] public string? Remarks { get; set; }
    }
}
