using System;
using System.ComponentModel.DataAnnotations;

namespace AmpmHrmsPro.Models
{
    // ── ConnectedMailAccount ───────────────────────────────────────────────────
    // The mailbox HRMS sends every email from, connected once by signing in
    // on Attendance > Email Notifications — either "Connect Outlook"
    // (Microsoft 365, via Microsoft Graph) or "Connect Gmail" (Google, via the
    // Gmail API). Single row: connecting one replaces the other.
    //
    // Stored in the "OutlookMailAccounts" table (its original name, from when
    // only Outlook was supported) — see AppDbContext.OnModelCreating.
    // Tokens are encrypted with ASP.NET DataProtection (keys persisted in DB).
    public class ConnectedMailAccount
    {
        public int Id { get; set; }

        // "Outlook" | "Gmail"
        [MaxLength(20)] public string Provider { get; set; } = MailProviders.Outlook;

        [MaxLength(200)] public string Email       { get; set; } = "";
        [MaxLength(200)] public string DisplayName { get; set; } = "";

        // Encrypted refresh token — lets HRMS get new access tokens without
        // the user signing in again.
        public string RefreshTokenProtected { get; set; } = "";

        // Encrypted short-lived access token (~1 hour) + its expiry (UTC).
        public string? AccessTokenProtected { get; set; }
        public DateTime? AccessTokenExpiresAtUtc { get; set; }

        public DateTime ConnectedAt { get; set; } = DateTime.UtcNow;
        [MaxLength(120)] public string ConnectedBy { get; set; } = "";

        public DateTime? LastSentAt { get; set; }
        [MaxLength(500)] public string? LastError { get; set; }
    }

    public static class MailProviders
    {
        public const string Outlook = "Outlook";
        public const string Gmail   = "Gmail";
        // "Gmail Quick Connect": a small Google Apps Script the user deploys
        // in their own Gmail account; HRMS posts emails to its web-app URL
        // over HTTPS (works on Render's free plan, which blocks SMTP).
        // For this provider RefreshTokenProtected holds the encrypted script
        // URL — there are no OAuth tokens.
        public const string GmailScript = "GmailScript";

        public static string Label(string provider) =>
            provider == GmailScript ? "Gmail" : provider;
    }
}
