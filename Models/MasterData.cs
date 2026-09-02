using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AmpmHrmsPro.Models
{
    // ═══════════════════════════════════════════
    // SIMPLE MASTERS — Department, Designation, Grade, Employment Type
    // all share the same shape (Name/Code/Active) and are managed from one
    // shared Admin screen (see AdminController.Masters / SaveMaster).
    // ═══════════════════════════════════════════
    public class Department
    {
        [Key] public int Id { get; set; }
        [Required, MaxLength(80)] public string Name { get; set; } = "";
        [MaxLength(20)] public string? Code { get; set; }

        // Set once employees exist — the person this department's approvals
        // and requests escalate to, above the Reporting Manager.
        public int? HeadEmployeeId { get; set; }
        [ForeignKey("HeadEmployeeId")] public Employee? Head { get; set; }

        public bool IsActive { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }

    public class Designation
    {
        [Key] public int Id { get; set; }
        [Required, MaxLength(80)] public string Name { get; set; } = "";
        [MaxLength(20)] public string? Code { get; set; }
        public bool IsActive { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }

    public class Grade
    {
        [Key] public int Id { get; set; }
        [Required, MaxLength(40)] public string Name { get; set; } = "";
        [MaxLength(20)] public string? Code { get; set; }
        public bool IsActive { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }

    public class EmploymentType
    {
        [Key] public int Id { get; set; }
        [Required, MaxLength(40)] public string Name { get; set; } = ""; // Permanent, Probation, Contract, Consultant, Intern, Trainee, Part-Time, Temporary
        public bool IsActive { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }

    // ═══════════════════════════════════════════
    // LOCATION / BRANCH
    // ═══════════════════════════════════════════
    public class Location
    {
        [Key] public int Id { get; set; }
        [Required, MaxLength(80)] public string Name { get; set; } = "";
        [MaxLength(20)] public string? Code { get; set; }
        [MaxLength(200)] public string? Address { get; set; }
        [MaxLength(60)] public string? City { get; set; }
        [MaxLength(60)] public string? State { get; set; }
        public bool IsActive { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }

    // ═══════════════════════════════════════════
    // WEEK-OFF POLICY MASTER — a named, reusable rule (e.g. "Sat-Sun",
    // "1st & 3rd Saturday + all Sundays") built from one or more WeekOffRule
    // rows, so Admin can express both simple "every Sunday" patterns and
    // occurrence-based patterns like "corporate staff get 1st & 3rd Saturday
    // off, factory staff get only Sunday off" — each as its own named policy
    // assigned to the relevant employees.
    // ═══════════════════════════════════════════
    public class WeekOffPolicy
    {
        [Key] public int Id { get; set; }
        [Required, MaxLength(60)] public string Name { get; set; } = "";
        [MaxLength(200)] public string? Description { get; set; }

        public bool IsActive { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.Now;

        public List<WeekOffRule> Rules { get; set; } = new();
    }

    // One line of a Week-Off Policy — either "every <Day>" (RuleType =
    // Weekly) or "the Nth occurrence(s) of <Day> in the month" (RuleType =
    // NthOccurrence, with Occurrences holding a comma-separated list of
    // 1-5, where 5 means "5th if the month has one, else nothing" and "L"
    // means "the last <Day> of the month" whichever week that falls in).
    // A policy is simply the union of all its rules — e.g. "1st & 3rd
    // Saturday + all Sunday" is two rows: (Saturday, NthOccurrence, "1,3")
    // and (Sunday, Weekly, null).
    public class WeekOffRule
    {
        [Key] public int Id { get; set; }
        public int WeekOffPolicyId { get; set; }
        [ForeignKey("WeekOffPolicyId")] public WeekOffPolicy? WeekOffPolicy { get; set; }

        [Required, MaxLength(10)] public string DayOfWeek { get; set; } = "Sunday";
        [Required, MaxLength(20)] public string RuleType { get; set; } = "Weekly"; // Weekly, NthOccurrence
        [MaxLength(20)] public string? Occurrences { get; set; } // e.g. "1,3" or "L" — only for NthOccurrence
    }

    // ═══════════════════════════════════════════
    // SHIFT MASTER
    // ═══════════════════════════════════════════
    public class Shift
    {
        [Key] public int Id { get; set; }
        [Required, MaxLength(50)] public string Name { get; set; } = "";
        public TimeSpan StartTime { get; set; } = new TimeSpan(9, 30, 0);
        public TimeSpan EndTime   { get; set; } = new TimeSpan(18, 30, 0);
        public int GraceMinutes { get; set; } = 10;
        public decimal HalfDayThresholdHours { get; set; } = 4;
        public decimal FullDayThresholdHours { get; set; } = 8;
        [MaxLength(20)] public string ShiftType { get; set; } = "General"; // General, Fixed, Flexible, Rotational, Night, Split
        public bool IsActive { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }

    // ═══════════════════════════════════════════
    // HOLIDAY MASTER
    // ═══════════════════════════════════════════
    public class Holiday
    {
        [Key] public int Id { get; set; }
        [Required, MaxLength(100)] public string Name { get; set; } = "";
        [Required, MaxLength(10)] public string Date { get; set; } = ""; // YYYY-MM-DD
        [MaxLength(30)] public string Type { get; set; } = "Company"; // National, Festival, Company, Optional, Restricted, Regional
        public bool IsActive { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }

    // ═══════════════════════════════════════════
    // LEAVE TYPE MASTER — fully admin-editable, same design that worked
    // well in the previous build.
    // ═══════════════════════════════════════════
    public class LeaveType
    {
        [Key] public int Id { get; set; }
        [Required, MaxLength(60)] public string Name { get; set; } = "";
        [Required, MaxLength(10)] public string Alias { get; set; } = "";
        [MaxLength(10)] public string Gender { get; set; } = "All"; // All, Male, Female
        [MaxLength(20)] public string Frequency { get; set; } = "Yearly"; // Monthly, Yearly, On Request
        public decimal DefaultAnnualDays { get; set; } = 0;
        public bool CarryForward { get; set; } = false;
        public bool Encashable   { get; set; } = false;
        public bool IsCompOff    { get; set; } = false;

        // Whether a day taken under this leave type counts toward LOP (Loss
        // of Pay) in the Attendance Reports screen — defaults to true (paid)
        // so every existing leave type keeps behaving exactly as before
        // until Admin explicitly marks one (e.g. "Leave Without Pay") as
        // unpaid. See Services/ExcelReportBuilder.cs's LopDays().
        public bool IsPaid       { get; set; } = true;

        public bool IsActive     { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }
}
