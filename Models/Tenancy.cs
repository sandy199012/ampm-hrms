using System.ComponentModel.DataAnnotations;

namespace AmpmHrmsPro.Models
{
    // ═══════════════════════════════════════════════════════════════════
    // MULTI-COMPANY (SaaS) MODELS
    //
    // One deployment serves many client companies. Every business table
    // carries a hidden "CompanyId" column (added in AppDbContext, not on
    // each class) and EF Core filters every query to the logged-in user's
    // company — so a controller that says _db.Employees.ToList() only ever
    // sees its own company's employees.
    //
    // The tables in THIS file are the exceptions that sit above the
    // companies: the company list itself and the platform owner's logins.
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>A client company (tenant). Global — not filtered by company.</summary>
    public class Company
    {
        public int Id { get; set; }

        /// <summary>Short code typed on the login page, e.g. "AMPM". Unique, upper-case.</summary>
        [Required, MaxLength(20)] public string Code { get; set; } = "";
        [Required, MaxLength(150)] public string Name { get; set; } = "";
        [MaxLength(200)] public string? LegalName { get; set; }

        // ── Contact ──
        [MaxLength(120)] public string? ContactName  { get; set; }
        [MaxLength(120)] public string? ContactEmail { get; set; }
        [MaxLength(20)]  public string? ContactPhone { get; set; }
        [MaxLength(300)] public string? Address      { get; set; }
        [MaxLength(20)]  public string? Gstin        { get; set; }
        [MaxLength(150)] public string? Website      { get; set; }

        // ── Branding (white-label) ──
        /// <summary>Logo as a data: URL — kept in the DB because Render's disk is wiped on every deploy.</summary>
        public string? LogoDataUrl { get; set; }
        [MaxLength(9)]   public string PrimaryColor { get; set; } = "#4F6EF7";
        [MaxLength(9)]   public string AccentColor  { get; set; } = "#17C3B2";
        /// <summary>Name shown as the sender of notification emails.</summary>
        [MaxLength(120)] public string? EmailSenderName { get; set; }
        [MaxLength(300)] public string? EmailFooter     { get; set; }

        // ── Subscription ──
        [Required, MaxLength(20)] public string Plan   { get; set; } = "Trial";   // Trial, Starter, Business, Enterprise
        [Required, MaxLength(20)] public string Status { get; set; } = "Active";  // Active, Suspended
        /// <summary>0 = unlimited.</summary>
        public int MaxEmployees { get; set; } = 25;
        /// <summary>Comma list of enabled module keys (see Services/Tenancy/Permissions.cs → Modules).</summary>
        [MaxLength(500)] public string Modules { get; set; } = "";
        public DateTime? TrialEndsAt        { get; set; }
        public DateTime? SubscriptionEndsAt { get; set; }
        [MaxLength(500)] public string? Notes { get; set; }

        // ── Security policy ──
        public int  PasswordMinLength      { get; set; } = 8;
        public bool PasswordRequireComplex { get; set; } = true;   // upper + lower + digit + symbol
        public int  PasswordExpiryDays     { get; set; } = 0;      // 0 = never
        public int  MaxFailedLogins        { get; set; } = 5;
        public int  LockoutMinutes         { get; set; } = 15;
        public int  SessionTimeoutMinutes  { get; set; } = 60;     // idle timeout
        public bool Require2FAForAdmins    { get; set; } = false;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? LastActivityAt { get; set; }
    }

    /// <summary>The software owner's login for the /Platform console. Global.</summary>
    public class PlatformUser
    {
        public int Id { get; set; }
        [Required, MaxLength(120)] public string Email { get; set; } = "";
        [Required, MaxLength(100)] public string Name  { get; set; } = "";
        [Required] public string PasswordHash { get; set; } = "";
        public bool MustChangePassword { get; set; }
        public bool IsActive { get; set; } = true;
        public int FailedLoginCount { get; set; }
        public DateTime? LockoutUntil { get; set; }
        public DateTime? LastLoginAt { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// A company-defined role ("Payroll Executive", "Plant Supervisor") — a
    /// named set of permission keys. Assigned via Employee.CustomRoleId and
    /// granted ON TOP OF the employee's base login role.
    /// </summary>
    public class AppRole
    {
        public int Id { get; set; }
        [Required, MaxLength(60)] public string Name { get; set; } = "";
        [MaxLength(200)] public string? Description { get; set; }
        /// <summary>Comma list of permission keys, e.g. "attendance.view,reports.view".</summary>
        [MaxLength(2000)] public string Permissions { get; set; } = "";
        public bool IsActive { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>Who changed what, when. Written automatically on SaveChanges + for logins.</summary>
    public class AuditLog
    {
        public long Id { get; set; }
        public DateTime At { get; set; } = DateTime.UtcNow;
        public int? UserId { get; set; }
        [MaxLength(120)] public string? UserName { get; set; }
        [Required, MaxLength(30)] public string Action { get; set; } = "";   // Create, Update, Delete, Login, LoginFailed, Logout, ...
        [MaxLength(60)]  public string? Entity   { get; set; }
        [MaxLength(60)]  public string? EntityId { get; set; }
        /// <summary>JSON of changed fields (old → new); secrets masked.</summary>
        public string? Changes { get; set; }
        [MaxLength(300)] public string? Summary { get; set; }
        [MaxLength(60)]  public string? IpAddress { get; set; }
    }
}
