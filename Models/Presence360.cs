using System.ComponentModel.DataAnnotations;

namespace AmpmHrmsPro.Models
{
    // ═══════════════════════════════════════════════════════════════════
    // PRESENCE 360 API — connection to web.thepresence360.com (Staqo).
    // One row per company. Client secret, password and the access token
    // are stored encrypted (ASP.NET Data Protection, keys kept in the DB).
    // Pulls: attendance-records (in/out per day) → AttendancePunches,
    //        leave-records → Leave requests,
    //        requests-list (od, regularize) → On Duty / Regularization requests.
    // See Services/Presence360ApiService.cs.
    // ═══════════════════════════════════════════════════════════════════
    public class P360ApiSettings
    {
        [Key] public int Id { get; set; }
        public bool IsEnabled { get; set; }

        [MaxLength(200)] public string BaseUrl { get; set; } = "https://web.thepresence360.com/api";
        [MaxLength(40)] public string? ClientId { get; set; }
        [MaxLength(1000)] public string? ClientSecretEnc { get; set; }
        [MaxLength(150)] public string? Username { get; set; }
        [MaxLength(1000)] public string? PasswordEnc { get; set; }
        /// <summary>Blank = the admin's own organization; "all" = every organization the admin can see.</summary>
        [MaxLength(40)] public string? Organization { get; set; }

        [MaxLength(8000)] public string? TokenEnc { get; set; }
        public DateTime? TokenExpiresAt { get; set; }

        public bool SyncAttendance { get; set; } = true;
        public bool SyncLeaves { get; set; } = true;
        public bool SyncRequests { get; set; } = true;
        public int SyncIntervalMinutes { get; set; } = 30;
        /// <summary>Each automatic run re-reads this many past days (late approvals, corrected punches).</summary>
        public int DaysBack { get; set; } = 3;

        public DateTime? LastSyncAt { get; set; }            // UTC
        [MaxLength(20)] public string? LastSyncStatus { get; set; }
        [MaxLength(2000)] public string? LastSyncMessage { get; set; }
        [MaxLength(8000)] public string? LastSampleResponse { get; set; }
    }
}
