using System;
using System.ComponentModel.DataAnnotations;

namespace AmpmHrmsPro.Models
{
    // ── OutlookMailAccount ─────────────────────────────────────────────────────
    // The Outlook / Microsoft 365 mailbox that HRMS sends every email from,
    // connected once via "Connect Outlook" (Microsoft sign-in, OAuth).
    // Single row. Tokens are encrypted with ASP.NET DataProtection (keys are
    // persisted in the DB, so they survive Render restarts).
    public class OutlookMailAccount
    {
        public int Id { get; set; }

        [MaxLength(200)] public string Email       { get; set; } = "";
        [MaxLength(200)] public string DisplayName { get; set; } = "";

        // Encrypted refresh token — lets HRMS get new access tokens without
        // the user signing in again (Microsoft rotates it on every refresh).
        public string RefreshTokenProtected { get; set; } = "";

        // Encrypted short-lived access token (~1 hour) + its expiry (UTC).
        public string? AccessTokenProtected { get; set; }
        public DateTime? AccessTokenExpiresAtUtc { get; set; }

        public DateTime ConnectedAt { get; set; } = DateTime.UtcNow;
        [MaxLength(120)] public string ConnectedBy { get; set; } = "";

        public DateTime? LastSentAt { get; set; }
        [MaxLength(500)] public string? LastError { get; set; }
    }
}
