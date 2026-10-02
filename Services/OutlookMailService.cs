// ─────────────────────────────────────────────────────────────────────────────
// Services/OutlookMailService.cs
//
// "Connect Outlook" — send every HRMS email from a Microsoft 365 / Outlook
// mailbox via Microsoft Graph, instead of SMTP.
//
//   1. Admin clicks "Connect Outlook" on Attendance > Email Notifications.
//   2. Microsoft sign-in page → user logs in with their Outlook account.
//   3. Microsoft redirects back to /OutlookMail/Callback with a code; we swap
//      it for an access token + refresh token and store them (encrypted).
//   4. SmartEmailSender (registered as IEmailSender) sends through Graph
//      /me/sendMail whenever an account is connected, else falls back to SMTP.
//
// One-time requirement: an app registration in Microsoft Entra ID (Azure).
// Its values come from configuration — on Render as environment variables:
//   MicrosoftGraph__ClientId, MicrosoftGraph__ClientSecret,
//   MicrosoftGraph__TenantId (optional, default "organizations"),
//   MicrosoftGraph__RedirectUri (optional, else built from the request).
// ─────────────────────────────────────────────────────────────────────────────
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AmpmHrmsPro.Data;
using AmpmHrmsPro.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AmpmHrmsPro.Services
{
    // ─── Shared mail types ────────────────────────────────────────────────────
    public record MailAttachment(string FileName, string ContentType, byte[] Content);

    public class MailRequest
    {
        public List<string> To  { get; set; } = new();
        public List<string> Cc  { get; set; } = new();
        public List<string> Bcc { get; set; } = new();
        public string Subject   { get; set; } = "";
        public string HtmlBody  { get; set; } = "";
        public List<MailAttachment> Attachments { get; set; } = new();
    }

    public class MicrosoftGraphOptions
    {
        public string? ClientId     { get; set; }
        public string? ClientSecret { get; set; }
        public string  TenantId     { get; set; } = "organizations";
        public string? RedirectUri  { get; set; }
    }

    // ─── Outlook / Graph ──────────────────────────────────────────────────────
    public interface IOutlookMailService
    {
        bool IsConfigured { get; }
        Task<OutlookMailAccount?> GetAccountAsync(CancellationToken ct = default);
        string BuildSignInUrl(string redirectUri, string state);
        Task<OutlookMailAccount> CompleteSignInAsync(string code, string redirectUri, string connectedBy, CancellationToken ct = default);
        Task DisconnectAsync(CancellationToken ct = default);
        Task<(bool Success, string Message)> SendAsync(MailRequest req, CancellationToken ct = default);
    }

    public class OutlookMailService : IOutlookMailService
    {
        // offline_access → refresh token; Mail.Send → send as the signed-in
        // user; User.Read → read their name + email address.
        private const string Scopes = "offline_access https://graph.microsoft.com/Mail.Send https://graph.microsoft.com/User.Read";
        private const string Graph  = "https://graph.microsoft.com/v1.0";

        // Graph /sendMail takes attachments inline only up to ~3 MB each.
        private const int MaxInlineAttachmentBytes = 3 * 1024 * 1024;

        // Several background jobs can send at once — refresh the token once.
        private static readonly SemaphoreSlim TokenLock = new(1, 1);

        private readonly AppDbContext          _db;
        private readonly IHttpClientFactory    _http;
        private readonly MicrosoftGraphOptions _opts;
        private readonly IDataProtector       _protector;
        private readonly ILogger<OutlookMailService> _log;

        public OutlookMailService(
            AppDbContext db,
            IHttpClientFactory http,
            IOptions<MicrosoftGraphOptions> opts,
            IDataProtectionProvider dp,
            ILogger<OutlookMailService> log)
        {
            _db        = db;
            _http      = http;
            _opts      = opts.Value;
            _protector = dp.CreateProtector("AmpmHrmsPro.OutlookMail.Tokens.v1");
            _log       = log;
        }

        public bool IsConfigured =>
            !string.IsNullOrWhiteSpace(_opts.ClientId) && !string.IsNullOrWhiteSpace(_opts.ClientSecret);

        private string Tenant => string.IsNullOrWhiteSpace(_opts.TenantId) ? "organizations" : _opts.TenantId.Trim();
        private string AuthBase => $"https://login.microsoftonline.com/{Uri.EscapeDataString(Tenant)}/oauth2/v2.0";

        public Task<OutlookMailAccount?> GetAccountAsync(CancellationToken ct = default) =>
            _db.OutlookMailAccounts.OrderByDescending(a => a.Id).FirstOrDefaultAsync(ct);

        // ── Sign-in ───────────────────────────────────────────────────────────
        public string BuildSignInUrl(string redirectUri, string state)
        {
            var q = new Dictionary<string, string>
            {
                ["client_id"]     = _opts.ClientId ?? "",
                ["response_type"] = "code",
                ["redirect_uri"]  = redirectUri,
                ["response_mode"] = "query",
                ["scope"]         = Scopes,
                ["state"]         = state,
                ["prompt"]        = "select_account",
            };
            return $"{AuthBase}/authorize?" + string.Join("&",
                q.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
        }

        public async Task<OutlookMailAccount> CompleteSignInAsync(
            string code, string redirectUri, string connectedBy, CancellationToken ct = default)
        {
            var tok = await RequestTokenAsync(new Dictionary<string, string>
            {
                ["grant_type"]   = "authorization_code",
                ["code"]         = code,
                ["redirect_uri"] = redirectUri,
            }, ct);

            if (string.IsNullOrEmpty(tok.RefreshToken))
                throw new InvalidOperationException(
                    "Microsoft did not return a refresh token (offline_access). Check the app's API permissions.");

            // Who signed in?
            var client = _http.CreateClient();
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{Graph}/me?$select=displayName,mail,userPrincipalName");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tok.AccessToken);
            using var resp = await client.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"Could not read the signed-in user's profile: {GraphError(body)}");

            using var me = JsonDocument.Parse(body);
            string? Prop(string n) => me.RootElement.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var email = Prop("mail") ?? Prop("userPrincipalName") ?? "";
            var name  = Prop("displayName") ?? email;

            // Single connected account — replace whatever was there.
            _db.OutlookMailAccounts.RemoveRange(_db.OutlookMailAccounts);
            var acct = new OutlookMailAccount
            {
                Email                   = email,
                DisplayName             = name,
                RefreshTokenProtected   = _protector.Protect(tok.RefreshToken),
                AccessTokenProtected    = _protector.Protect(tok.AccessToken),
                AccessTokenExpiresAtUtc = DateTime.UtcNow.AddSeconds(tok.ExpiresIn - 120),
                ConnectedAt             = DateTime.UtcNow,
                ConnectedBy             = connectedBy,
            };
            _db.OutlookMailAccounts.Add(acct);
            await _db.SaveChangesAsync(ct);
            _log.LogInformation("Outlook mail connected: {Email} by {User}", email, connectedBy);
            return acct;
        }

        public async Task DisconnectAsync(CancellationToken ct = default)
        {
            _db.OutlookMailAccounts.RemoveRange(_db.OutlookMailAccounts);
            await _db.SaveChangesAsync(ct);
        }

        // ── Send ──────────────────────────────────────────────────────────────
        public async Task<(bool Success, string Message)> SendAsync(MailRequest req, CancellationToken ct = default)
        {
            var acct = await GetAccountAsync(ct);
            if (acct == null) return (false, "Outlook is not connected.");
            if (!IsConfigured) return (false, "Microsoft app (ClientId/ClientSecret) is not configured on the server.");

            static List<string> Clean(IEnumerable<string> xs) =>
                xs.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim())
                  .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            var to  = Clean(req.To);
            var cc  = Clean(req.Cc);
            var bcc = Clean(req.Bcc);
            if (!to.Any() && !cc.Any() && !bcc.Any()) return (false, "No recipients.");
            if (!to.Any()) to.Add(acct.Email);   // BCC-only blast: visible To = sender

            foreach (var a in req.Attachments)
                if (a.Content.Length > MaxInlineAttachmentBytes)
                    return (false, $"Attachment '{a.FileName}' is {a.Content.Length / 1024 / 1024.0:0.0} MB — Outlook send limit is 3 MB per file.");

            object Recips(List<string> xs) => xs.Select(x => new { emailAddress = new { address = x } }).ToList();

            var message = new Dictionary<string, object>
            {
                ["subject"]       = req.Subject,
                ["body"]          = new { contentType = "HTML", content = req.HtmlBody },
                ["toRecipients"]  = Recips(to),
                ["ccRecipients"]  = Recips(cc),
                ["bccRecipients"] = Recips(bcc),
            };
            if (req.Attachments.Any())
            {
                message["attachments"] = req.Attachments.Select(a => new Dictionary<string, object>
                {
                    ["@odata.type"]  = "#microsoft.graph.fileAttachment",
                    ["name"]         = a.FileName,
                    ["contentType"]  = a.ContentType,
                    ["contentBytes"] = Convert.ToBase64String(a.Content),
                }).ToList();
            }
            var payload = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["message"]         = message,
                ["saveToSentItems"] = true,
            });

            try
            {
                var token  = await GetAccessTokenAsync(acct, ct);
                var client = _http.CreateClient();
                using var httpReq = new HttpRequestMessage(HttpMethod.Post, $"{Graph}/me/sendMail")
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                };
                httpReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var resp = await client.SendAsync(httpReq, ct);

                if (resp.IsSuccessStatusCode)
                {
                    acct.LastSentAt = DateTime.UtcNow;
                    acct.LastError  = null;
                    await _db.SaveChangesAsync(ct);
                    return (true, $"Sent from {acct.Email} to {to.Count + cc.Count + bcc.Count} recipient(s).");
                }

                var err = GraphError(await resp.Content.ReadAsStringAsync(ct));
                acct.LastError = Truncate($"{(int)resp.StatusCode}: {err}", 500);
                await _db.SaveChangesAsync(ct);
                return (false, $"Outlook send failed — {err}");
            }
            catch (Exception ex)
            {
                acct.LastError = Truncate(ex.Message, 500);
                try { await _db.SaveChangesAsync(ct); } catch { /* best effort */ }
                return (false, $"Outlook send failed — {ex.Message}");
            }
        }

        // ── Tokens ────────────────────────────────────────────────────────────
        private async Task<string> GetAccessTokenAsync(OutlookMailAccount acct, CancellationToken ct)
        {
            if (acct.AccessTokenProtected != null && acct.AccessTokenExpiresAtUtc > DateTime.UtcNow)
                return _protector.Unprotect(acct.AccessTokenProtected);

            await TokenLock.WaitAsync(ct);
            try
            {
                // Another job may have refreshed while we waited.
                await _db.Entry(acct).ReloadAsync(ct);
                if (acct.AccessTokenProtected != null && acct.AccessTokenExpiresAtUtc > DateTime.UtcNow)
                    return _protector.Unprotect(acct.AccessTokenProtected);

                var tok = await RequestTokenAsync(new Dictionary<string, string>
                {
                    ["grant_type"]    = "refresh_token",
                    ["refresh_token"] = _protector.Unprotect(acct.RefreshTokenProtected),
                }, ct);

                acct.AccessTokenProtected    = _protector.Protect(tok.AccessToken);
                acct.AccessTokenExpiresAtUtc = DateTime.UtcNow.AddSeconds(tok.ExpiresIn - 120);
                if (!string.IsNullOrEmpty(tok.RefreshToken))
                    acct.RefreshTokenProtected = _protector.Protect(tok.RefreshToken);
                await _db.SaveChangesAsync(ct);
                return tok.AccessToken;
            }
            finally { TokenLock.Release(); }
        }

        private record TokenResult(string AccessToken, string? RefreshToken, int ExpiresIn);

        private async Task<TokenResult> RequestTokenAsync(Dictionary<string, string> form, CancellationToken ct)
        {
            form["client_id"]     = _opts.ClientId ?? "";
            form["client_secret"] = _opts.ClientSecret ?? "";
            form["scope"]         = Scopes;

            var client = _http.CreateClient();
            using var resp = await client.PostAsync($"{AuthBase}/token", new FormUrlEncodedContent(form), ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (!resp.IsSuccessStatusCode || !root.TryGetProperty("access_token", out var at))
            {
                var desc = root.TryGetProperty("error_description", out var d) ? d.GetString() : body;
                // AADSTS70000/700082 etc. = refresh token expired/revoked → reconnect.
                throw new InvalidOperationException(
                    $"Microsoft sign-in token error — please reconnect Outlook. {FirstLine(desc)}");
            }

            var refresh = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
            var expires = root.TryGetProperty("expires_in", out var ei) && ei.TryGetInt32(out var s) ? s : 3600;
            return new TokenResult(at.GetString() ?? "", refresh, expires);
        }

        private static string GraphError(string body)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("error", out var e) &&
                    e.ValueKind == JsonValueKind.Object &&
                    e.TryGetProperty("message", out var m))
                    return m.GetString() ?? body;
            }
            catch { /* not JSON */ }
            return Truncate(body, 300);
        }

        private static string FirstLine(string? s) => (s ?? "").Split('\n')[0].Trim();
        private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n];
    }

    // ─── SmartEmailSender ─────────────────────────────────────────────────────
    // Registered as IEmailSender. Every existing email in HRMS (Daily Alert,
    // Birthday, Weekly Report, Test Email, Attendance Review) goes through
    // here: Outlook if connected, otherwise the old SMTP settings.
    public class SmartEmailSender : IEmailSender
    {
        private readonly IOutlookMailService _outlook;
        private readonly SmtpEmailSender     _smtp;

        public SmartEmailSender(IOutlookMailService outlook, SmtpEmailSender smtp)
        {
            _outlook = outlook;
            _smtp    = smtp;
        }

        public Task<(bool Success, string Message)> SendAsync(
            EmailSettings settings, IEnumerable<string> to, string subject, string htmlBody,
            IEnumerable<string>? bcc = null) =>
            SendAsync(settings, new MailRequest
            {
                To       = to.ToList(),
                Bcc      = (bcc ?? Enumerable.Empty<string>()).ToList(),
                Subject  = subject,
                HtmlBody = htmlBody,
            });

        public async Task<(bool Success, string Message)> SendAsync(EmailSettings? settings, MailRequest req)
        {
            if (_outlook.IsConfigured && await _outlook.GetAccountAsync() != null)
                return await _outlook.SendAsync(req);

            if (settings == null)
                return (false, "No email account: connect Outlook (Email Notifications page) or configure SMTP.");
            return await _smtp.SendAsync(settings, req);
        }
    }
}
