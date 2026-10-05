// Controllers/MailAccountController.cs
// "Connect Outlook" / "Connect Gmail" flow for the Email Notifications page.
//
// Redirect URIs to register with the providers:
//   Outlook (Azure):        https://<your-host>/OutlookMail/Callback
//   Gmail (Google Cloud):   https://<your-host>/MailAccount/GoogleCallback
using System.Security.Cryptography;
using AmpmHrmsPro.Models;
using AmpmHrmsPro.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace AmpmHrmsPro.Controllers
{
    [Authorize(Roles = "admin,hr")]
    public class MailAccountController : Controller
    {
        private const string StateCookie = "ampm_mail_state";

        private readonly IConnectedMailService _mail;
        private readonly MicrosoftGraphOptions _ms;
        private readonly GoogleMailOptions     _google;
        private readonly ILogger<MailAccountController> _log;

        public MailAccountController(
            IConnectedMailService mail,
            IOptions<MicrosoftGraphOptions> ms,
            IOptions<GoogleMailOptions> google,
            ILogger<MailAccountController> log)
        {
            _mail   = mail;
            _ms     = ms.Value;
            _google = google.Value;
            _log    = log;
        }

        // Must match the redirect URI registered with the provider exactly.
        // Render terminates SSL at its proxy, so force https for non-local hosts.
        private string RedirectUriFor(string provider)
        {
            var configured = provider == MailProviders.Gmail ? _google.RedirectUri : _ms.RedirectUri;
            if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim();

            var host   = Request.Host.Host;
            var local  = host == "localhost" || host == "127.0.0.1";
            var scheme = local ? Request.Scheme : "https";
            var action = provider == MailProviders.Gmail ? nameof(GoogleCallback) : nameof(OutlookCallback);
            return Url.Action(action, "MailAccount", null, scheme)!;
        }

        private IActionResult BackToSettings() => RedirectToAction("EmailSettings", "Attendance");

        // GET /MailAccount/Connect?provider=Outlook|Gmail → provider sign-in page
        public IActionResult Connect(string provider)
        {
            provider = provider == MailProviders.Gmail ? MailProviders.Gmail : MailProviders.Outlook;
            if (!_mail.IsConfigured(provider))
            {
                TempData["Error"] = $"{provider} app is not set up on the server yet — see the setup steps on this page.";
                return BackToSettings();
            }

            var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            Response.Cookies.Append(StateCookie, $"{provider}:{state}", new CookieOptions
            {
                HttpOnly = true,
                Secure   = Request.IsHttps,
                SameSite = SameSiteMode.Lax,   // must survive the redirect back from the provider
                MaxAge   = TimeSpan.FromMinutes(15),
            });
            return Redirect(_mail.BuildSignInUrl(provider, RedirectUriFor(provider), state));
        }

        // Kept at the original URL so the Azure redirect URI stays valid.
        [HttpGet("/OutlookMail/Callback")]
        public Task<IActionResult> OutlookCallback(string? code, string? state, string? error, string? error_description) =>
            CompleteAsync(MailProviders.Outlook, code, state, error, error_description);

        // GET /MailAccount/GoogleCallback
        public Task<IActionResult> GoogleCallback(string? code, string? state, string? error) =>
            CompleteAsync(MailProviders.Gmail, code, state, error, null);

        private async Task<IActionResult> CompleteAsync(
            string provider, string? code, string? state, string? error, string? errorDescription)
        {
            var expected = Request.Cookies[StateCookie];
            Response.Cookies.Delete(StateCookie);

            if (!string.IsNullOrEmpty(error))
            {
                TempData["Error"] = $"{provider} sign-in was not completed: {errorDescription ?? error}";
                return BackToSettings();
            }
            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state) || expected != $"{provider}:{state}")
            {
                TempData["Error"] = $"{provider} sign-in expired or was invalid — please click Connect {provider} again.";
                return BackToSettings();
            }

            try
            {
                var acct = await _mail.CompleteSignInAsync(provider, code, RedirectUriFor(provider), User.Identity?.Name ?? "admin");
                TempData["Success"] = $"{provider} connected — HRMS emails will now be sent from {acct.Email}.";
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "{Provider} connect failed", provider);
                TempData["Error"] = $"Could not connect {provider}: {ex.Message}";
            }
            return BackToSettings();
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Disconnect()
        {
            await _mail.DisconnectAsync();
            TempData["Success"] = "Email account disconnected. Emails will use SMTP settings (if configured).";
            return BackToSettings();
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> SendTest(string to)
        {
            if (string.IsNullOrWhiteSpace(to))
            {
                TempData["Error"] = "Enter an address to send the test email to.";
                return BackToSettings();
            }
            var (ok, msg) = await _mail.SendAsync(new MailRequest
            {
                To       = new List<string> { to.Trim() },
                Subject  = "AMPM HRMS — test email",
                HtmlBody = "<p>This is a test email from AMPM HRMS, sent through the connected email account.</p>"
            });
            TempData[ok ? "Success" : "Error"] = ok ? $"Test email sent. {msg}" : msg;
            return BackToSettings();
        }
    }
}
