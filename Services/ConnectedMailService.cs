// ─────────────────────────────────────────────────────────────────────────────
// Services/ConnectedMailService.cs
//
// "Connect Outlook" / "Connect Gmail" — send every HRMS email from a mailbox
// the admin signs into once, instead of configuring SMTP.
//
//   1. Admin clicks Connect Outlook or Connect Gmail on
//      Attendance > Email Notifications.
//   2. Microsoft / Google sign-in page → user logs in.
//   3. Provider redirects back with a code; we swap it for an access token +
//      refresh token and store them (encrypted). One account at a time.
//   4. SmartEmailSender (registered as IEmailSender) sends through
//      Microsoft Graph /me/sendMail or Gmail API users/me/messages/send
//      whenever an account is connected, else falls back to SMTP.
//
// One-time requirement per provider — an OAuth app registration. Values come
// from configuration (on Render: environment variables):
//   Outlook: MicrosoftGraph__ClientId, MicrosoftGraph__ClientSecret,
//            MicrosoftGraph__TenantId (optional, default "organizations")
//   Gmail:   GoogleMail__ClientId, GoogleMail__ClientSecret
//   Optional fixed redirect URIs: MicrosoftGraph__RedirectUri, GoogleMail__RedirectUri
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

    public class GoogleMailOptions
    {
        public string? ClientId     { get; set; }
        public string? ClientSecret { get; set; }
        public string? RedirectUri  { get; set; }
    }

    // ─── Connected mailbox (Outlook or Gmail) ─────────────────────────────────
    public interface IConnectedMailService
    {
        bool IsConfigured(string provider);
        Task<ConnectedMailAccount?> GetAccountAsync(CancellationToken ct = default);
        string BuildSignInUrl(string provider, string redirectUri, string state);
        Task<ConnectedMailAccount> CompleteSignInAsync(string provider, string code, string redirectUri, string connectedBy, CancellationToken ct = default);
        Task DisconnectAsync(CancellationToken ct = default);
        Task<(bool Success, string Message)> SendAsync(MailRequest req, CancellationToken ct = default);
    }

    public class ConnectedMailService : IConnectedMailService
    {
        // ── Outlook / Microsoft Graph ──
        // offline_access → refresh token; Mail.Send → send as the signed-in
        // user; User.Read → read their name + email address.
        private const string MsScopes = "offline_access https://graph.microsoft.com/Mail.Send https://graph.microsoft.com/User.Read";
        private const string MsGraph  = "https://graph.microsoft.com/v1.0";
        // Graph /sendMail takes attachments inline only up to ~3 MB each.
        private const int MsMaxAttachmentBytes = 3 * 1024 * 1024;

        // ── Gmail ──
        // gmail.send → send only (cannot read the mailbox); openid/email/profile
        // → read the signed-in user's name + address.
        private const string GoogleScopes   = "openid email profile https://www.googleapis.com/auth/gmail.send";
        private const string GoogleAuthUrl  = "https://accounts.google.com/o/oauth2/v2/auth";
        private const string GoogleTokenUrl = "https://oauth2.googleapis.com/token";
        private const string GoogleUserInfo = "https://openidconnect.googleapis.com/v1/userinfo";
        private const string GmailSendUrl   = "https://gmail.googleapis.com/gmail/v1/users/me/messages/send";
        private const int GmailMaxTotalBytes = 20 * 1024 * 1024;

        // Several background jobs can send at once — refresh the token once.
        private static readonly SemaphoreSlim TokenLock = new(1, 1);

        private readonly AppDbContext          _db;
        private readonly IHttpClientFactory    _http;
        private readonly MicrosoftGraphOptions _ms;
        private readonly GoogleMailOptions     _google;
        private readonly IDataProtector        _protector;
        private readonly ILogger<ConnectedMailService> _log;

        public ConnectedMailService(
            AppDbContext db,
            IHttpClientFactory http,
            IOptions<MicrosoftGraphOptions> ms,
            IOptions<GoogleMailOptions> google,
            IDataProtectionProvider dp,
            ILogger<ConnectedMailService> log)
        {
            _db        = db;
            _http      = http;
            _ms        = ms.Value;
            _google    = google.Value;
            // Same purpose string as the original Outlook-only version, so an
            // already-connected Outlook account keeps working after upgrade.
            _protector = dp.CreateProtector("AmpmHrmsPro.OutlookMail.Tokens.v1");
            _log       = log;
        }

        public bool IsConfigured(string provider) => provider switch
        {
            MailProviders.Outlook => !string.IsNullOrWhiteSpace(_ms.ClientId) && !string.IsNullOrWhiteSpace(_ms.ClientSecret),
            MailProviders.Gmail   => !string.IsNullOrWhiteSpace(_google.ClientId) && !string.IsNullOrWhiteSpace(_google.ClientSecret),
            _ => false
        };

        private string MsTenant => string.IsNullOrWhiteSpace(_ms.TenantId) ? "organizations" : _ms.TenantId.Trim();
        private string MsAuthBase => $"https://login.microsoftonline.com/{Uri.EscapeDataString(MsTenant)}/oauth2/v2.0";

        public Task<ConnectedMailAccount?> GetAccountAsync(CancellationToken ct = default) =>
            _db.MailAccounts.OrderByDescending(a => a.Id).FirstOrDefaultAsync(ct);

        // ── Sign-in ───────────────────────────────────────────────────────────
        public string BuildSignInUrl(string provider, string redirectUri, string state)
        {
            Dictionary<string, string> q;
            string baseUrl;
            if (provider == MailProviders.Gmail)
            {
                baseUrl = GoogleAuthUrl;
                q = new Dictionary<string, string>
                {
                    ["client_id"]              = _google.ClientId ?? "",
                    ["response_type"]          = "code",
                    ["redirect_uri"]           = redirectUri,
                    ["scope"]                  = GoogleScopes,
                    ["state"]                  = state,
                    ["access_type"]            = "offline",          // → refresh token
                    ["prompt"]                 = "consent select_account",
                    ["include_granted_scopes"] = "true",
                };
            }
            else
            {
                baseUrl = $"{MsAuthBase}/authorize";
                q = new Dictionary<string, string>
                {
                    ["client_id"]     = _ms.ClientId ?? "",
                    ["response_type"] = "code",
                    ["redirect_uri"]  = redirectUri,
                    ["response_mode"] = "query",
                    ["scope"]         = MsScopes,
                    ["state"]         = state,
                    ["prompt"]        = "select_account",
                };
            }
            return baseUrl + "?" + string.Join("&", q.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
        }

        public async Task<ConnectedMailAccount> CompleteSignInAsync(
            string provider, string code, string redirectUri, string connectedBy, CancellationToken ct = default)
        {
            var tok = await RequestTokenAsync(provider, new Dictionary<string, string>
            {
                ["grant_type"]   = "authorization_code",
                ["code"]         = code,
                ["redirect_uri"] = redirectUri,
            }, ct);

            if (string.IsNullOrEmpty(tok.RefreshToken))
                throw new InvalidOperationException(provider == MailProviders.Gmail
                    ? "Google did not return a refresh token. Remove 'AMPM HRMS' from your Google Account → Security → Third-party access, then connect again."
                    : "Microsoft did not return a refresh token (offline_access). Check the app's API permissions.");

            // Who signed in?
            var profileUrl = provider == MailProviders.Gmail
                ? GoogleUserInfo
                : $"{MsGraph}/me?$select=displayName,mail,userPrincipalName";
            var client = _http.CreateClient();
            using var req = new HttpRequestMessage(HttpMethod.Get, profileUrl);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tok.AccessToken);
            using var resp = await client.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"Could not read the signed-in user's profile: {ApiError(body)}");

            using var me = JsonDocument.Parse(body);
            string? Prop(string n) => me.RootElement.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var email = provider == MailProviders.Gmail
                ? Prop("email") ?? ""
                : Prop("mail") ?? Prop("userPrincipalName") ?? "";
            var name = (provider == MailProviders.Gmail ? Prop("name") : Prop("displayName")) ?? email;

            // Single connected account — replace whatever was there.
            _db.MailAccounts.RemoveRange(_db.MailAccounts);
            var acct = new ConnectedMailAccount
            {
                Provider                = provider,
                Email                   = email,
                DisplayName             = name,
                RefreshTokenProtected   = _protector.Protect(tok.RefreshToken),
                AccessTokenProtected    = _protector.Protect(tok.AccessToken),
                AccessTokenExpiresAtUtc = DateTime.UtcNow.AddSeconds(tok.ExpiresIn - 120),
                ConnectedAt             = DateTime.UtcNow,
                ConnectedBy             = connectedBy,
            };
            _db.MailAccounts.Add(acct);
            await _db.SaveChangesAsync(ct);
            _log.LogInformation("{Provider} mail connected: {Email} by {User}", provider, email, connectedBy);
            return acct;
        }

        public async Task DisconnectAsync(CancellationToken ct = default)
        {
            _db.MailAccounts.RemoveRange(_db.MailAccounts);
            await _db.SaveChangesAsync(ct);
        }

        // ── Send ──────────────────────────────────────────────────────────────
        public async Task<(bool Success, string Message)> SendAsync(MailRequest req, CancellationToken ct = default)
        {
            var acct = await GetAccountAsync(ct);
            if (acct == null) return (false, "No email account is connected.");
            if (!IsConfigured(acct.Provider))
                return (false, $"{acct.Provider} app (ClientId/ClientSecret) is not configured on the server.");

            static List<string> Clean(IEnumerable<string> xs) =>
                xs.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim())
                  .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            var to  = Clean(req.To);
            var cc  = Clean(req.Cc);
            var bcc = Clean(req.Bcc);
            if (!to.Any() && !cc.Any() && !bcc.Any()) return (false, "No recipients.");
            if (!to.Any()) to.Add(acct.Email);   // BCC-only blast: visible To = sender

            if (acct.Provider == MailProviders.Gmail)
            {
                var total = req.Attachments.Sum(a => (long)a.Content.Length);
                if (total > GmailMaxTotalBytes)
                    return (false, $"Attachments are {total / 1024 / 1024.0:0.0} MB — Gmail send limit is 20 MB.");
            }
            else
            {
                foreach (var a in req.Attachments)
                    if (a.Content.Length > MsMaxAttachmentBytes)
                        return (false, $"Attachment '{a.FileName}' is {a.Content.Length / 1024 / 1024.0:0.0} MB — Outlook send limit is 3 MB per file.");
            }

            try
            {
                var token = await GetAccessTokenAsync(acct, ct);
                var (url, payload) = acct.Provider == MailProviders.Gmail
                    ? (GmailSendUrl, GmailPayload(acct, to, cc, bcc, req))
                    : ($"{MsGraph}/me/sendMail", GraphPayload(to, cc, bcc, req));

                var client = _http.CreateClient();
                using var httpReq = new HttpRequestMessage(HttpMethod.Post, url)
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

                var err = ApiError(await resp.Content.ReadAsStringAsync(ct));
                acct.LastError = Truncate($"{(int)resp.StatusCode}: {err}", 500);
                await _db.SaveChangesAsync(ct);
                return (false, $"{acct.Provider} send failed — {err}");
            }
            catch (Exception ex)
            {
                acct.LastError = Truncate(ex.Message, 500);
                try { await _db.SaveChangesAsync(ct); } catch { /* best effort */ }
                return (false, $"{acct.Provider} send failed — {ex.Message}");
            }
        }

        // Microsoft Graph sendMail JSON
        private static string GraphPayload(List<string> to, List<string> cc, List<string> bcc, MailRequest req)
        {
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
            return JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["message"]         = message,
                ["saveToSentItems"] = true,
            });
        }

        // Gmail API wants the whole RFC 2822 message, base64url-encoded.
        private static string GmailPayload(ConnectedMailAccount acct, List<string> to, List<string> cc, List<string> bcc, MailRequest req)
        {
            var boundary = "=_ampm_" + Guid.NewGuid().ToString("N");
            var sb = new StringBuilder();
            var fromName = string.IsNullOrWhiteSpace(acct.DisplayName) ? "AMPM HRMS" : acct.DisplayName;
            var fromDisplay = fromName.All(c => c >= 32 && c < 127)
                ? "\"" + fromName.Replace("\"", "") + "\""
                : EncodeWord(fromName);
            sb.Append("From: ").Append(fromDisplay).Append(" <").Append(acct.Email).Append(">\r\n");
            sb.Append("To: ").Append(string.Join(", ", to)).Append("\r\n");
            if (cc.Any())  sb.Append("Cc: ").Append(string.Join(", ", cc)).Append("\r\n");
            if (bcc.Any()) sb.Append("Bcc: ").Append(string.Join(", ", bcc)).Append("\r\n");   // Gmail delivers + strips it
            sb.Append("Subject: ").Append(EncodeWord(req.Subject)).Append("\r\n");
            sb.Append("MIME-Version: 1.0\r\n");
            sb.Append("Content-Type: multipart/mixed; boundary=\"").Append(boundary).Append("\"\r\n\r\n");

            sb.Append("--").Append(boundary).Append("\r\n");
            sb.Append("Content-Type: text/html; charset=UTF-8\r\n");
            sb.Append("Content-Transfer-Encoding: base64\r\n\r\n");
            sb.Append(Base64Lines(Encoding.UTF8.GetBytes(req.HtmlBody)));

            foreach (var a in req.Attachments)
            {
                var fn = EncodeWord(a.FileName.Replace("\"", ""));
                sb.Append("--").Append(boundary).Append("\r\n");
                sb.Append("Content-Type: ").Append(a.ContentType).Append("; name=\"").Append(fn).Append("\"\r\n");
                sb.Append("Content-Disposition: attachment; filename=\"").Append(fn).Append("\"\r\n");
                sb.Append("Content-Transfer-Encoding: base64\r\n\r\n");
                sb.Append(Base64Lines(a.Content));
            }
            sb.Append("--").Append(boundary).Append("--\r\n");

            var raw = Convert.ToBase64String(Encoding.UTF8.GetBytes(sb.ToString()))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
            return JsonSerializer.Serialize(new Dictionary<string, string> { ["raw"] = raw });
        }

        // RFC 2047: non-ASCII header text (e.g. the "—" in subjects) → =?UTF-8?B?...?=
        private static string EncodeWord(string s) =>
            s.All(c => c >= 32 && c < 127)
                ? s
                : $"=?UTF-8?B?{Convert.ToBase64String(Encoding.UTF8.GetBytes(s))}?=";

        private static string Base64Lines(byte[] bytes) =>
            Convert.ToBase64String(bytes, Base64FormattingOptions.InsertLineBreaks) + "\r\n";

        // ── Tokens ────────────────────────────────────────────────────────────
        private async Task<string> GetAccessTokenAsync(ConnectedMailAccount acct, CancellationToken ct)
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

                var tok = await RequestTokenAsync(acct.Provider, new Dictionary<string, string>
                {
                    ["grant_type"]    = "refresh_token",
                    ["refresh_token"] = _protector.Unprotect(acct.RefreshTokenProtected),
                }, ct);

                acct.AccessTokenProtected    = _protector.Protect(tok.AccessToken);
                acct.AccessTokenExpiresAtUtc = DateTime.UtcNow.AddSeconds(tok.ExpiresIn - 120);
                if (!string.IsNullOrEmpty(tok.RefreshToken))   // Microsoft rotates it; Google usually doesn't
                    acct.RefreshTokenProtected = _protector.Protect(tok.RefreshToken);
                await _db.SaveChangesAsync(ct);
                return tok.AccessToken;
            }
            finally { TokenLock.Release(); }
        }

        private record TokenResult(string AccessToken, string? RefreshToken, int ExpiresIn);

        private async Task<TokenResult> RequestTokenAsync(string provider, Dictionary<string, string> form, CancellationToken ct)
        {
            string tokenUrl;
            if (provider == MailProviders.Gmail)
            {
                tokenUrl              = GoogleTokenUrl;
                form["client_id"]     = _google.ClientId ?? "";
                form["client_secret"] = _google.ClientSecret ?? "";
            }
            else
            {
                tokenUrl              = $"{MsAuthBase}/token";
                form["client_id"]     = _ms.ClientId ?? "";
                form["client_secret"] = _ms.ClientSecret ?? "";
                form["scope"]         = MsScopes;
            }

            var client = _http.CreateClient();
            using var resp = await client.PostAsync(tokenUrl, new FormUrlEncodedContent(form), ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

            JsonDocument doc;
            try { doc = JsonDocument.Parse(body); }
            catch { throw new InvalidOperationException($"{provider} sign-in token error — unexpected response: {Truncate(body, 200)}"); }

            using (doc)
            {
                var root = doc.RootElement;
                if (!resp.IsSuccessStatusCode || !root.TryGetProperty("access_token", out var at))
                {
                    var desc = root.TryGetProperty("error_description", out var d) ? d.GetString()
                             : root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString()
                             : body;
                    // invalid_grant / AADSTS700082 = refresh token expired or revoked → reconnect.
                    throw new InvalidOperationException(
                        $"{provider} sign-in token error — please disconnect and connect again. {FirstLine(desc)}");
                }

                var refresh = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
                var expires = root.TryGetProperty("expires_in", out var ei) && ei.TryGetInt32(out var secs) ? secs : 3600;
                return new TokenResult(at.GetString() ?? "", refresh, expires);
            }
        }

        // Graph and Gmail both return { "error": { "message": "..." } }
        private static string ApiError(string body)
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
    // Registered as IEmailSender. Every email in HRMS (Daily Alert, Birthday,
    // Weekly Report, Test Email, Attendance Review) goes through here: the
    // connected Outlook/Gmail account if there is one, otherwise SMTP.
    public class SmartEmailSender : IEmailSender
    {
        private readonly IConnectedMailService _mail;
        private readonly SmtpEmailSender       _smtp;

        public SmartEmailSender(IConnectedMailService mail, SmtpEmailSender smtp)
        {
            _mail = mail;
            _smtp = smtp;
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
            var acct = await _mail.GetAccountAsync();
            if (acct != null && _mail.IsConfigured(acct.Provider))
                return await _mail.SendAsync(req);

            if (settings == null)
                return (false, "No email account: connect Outlook or Gmail (Email Notifications page), or configure SMTP.");
            return await _smtp.SendAsync(settings, req);
        }
    }
}
