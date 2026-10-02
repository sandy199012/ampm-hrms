// Controllers/OutlookMailController.cs
// "Connect Outlook" flow for the Email Notifications page.
using System.Security.Cryptography;
using AmpmHrmsPro.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace AmpmHrmsPro.Controllers
{
    [Authorize(Roles = "admin,hr")]
    public class OutlookMailController : Controller
    {
        private const string StateCookie = "ampm_outlook_state";

        private readonly IOutlookMailService   _outlook;
        private readonly MicrosoftGraphOptions _opts;
        private readonly ILogger<OutlookMailController> _log;

        public OutlookMailController(
            IOutlookMailService outlook,
            IOptions<MicrosoftGraphOptions> opts,
            ILogger<OutlookMailController> log)
        {
            _outlook = outlook;
            _opts    = opts.Value;
            _log     = log;
        }

        // Must match the Redirect URI registered in Azure exactly. Render
        // terminates SSL at its proxy, so force https for any non-local host.
        private string RedirectUri
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(_opts.RedirectUri)) return _opts.RedirectUri!.Trim();
                var host   = Request.Host.Host;
                var local  = host == "localhost" || host == "127.0.0.1";
                var scheme = local ? Request.Scheme : "https";
                return Url.Action(nameof(Callback), "OutlookMail", null, scheme)!;
            }
        }

        private IActionResult BackToSettings() => RedirectToAction("EmailSettings", "Attendance");

        // GET /OutlookMail/Connect → Microsoft sign-in page
        public IActionResult Connect()
        {
            if (!_outlook.IsConfigured)
            {
                TempData["Error"] = "Microsoft app is not set up on the server yet (MicrosoftGraph ClientId / ClientSecret). See the setup steps on this page.";
                return BackToSettings();
            }

            var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            Response.Cookies.Append(StateCookie, state, new CookieOptions
            {
                HttpOnly = true,
                Secure   = Request.IsHttps,
                SameSite = SameSiteMode.Lax,   // must survive the redirect back from Microsoft
                MaxAge   = TimeSpan.FromMinutes(15),
            });
            return Redirect(_outlook.BuildSignInUrl(RedirectUri, state));
        }

        // GET /OutlookMail/Callback?code=...&state=...  (Microsoft redirects here)
        public async Task<IActionResult> Callback(string? code, string? state, string? error, string? error_description)
        {
            var expected = Request.Cookies[StateCookie];
            Response.Cookies.Delete(StateCookie);

            if (!string.IsNullOrEmpty(error))
            {
                TempData["Error"] = $"Outlook sign-in was not completed: {error_description ?? error}";
                return BackToSettings();
            }
            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state) || state != expected)
            {
                TempData["Error"] = "Outlook sign-in expired or was invalid — please click Connect Outlook again.";
                return BackToSettings();
            }

            try
            {
                var acct = await _outlook.CompleteSignInAsync(code, RedirectUri, User.Identity?.Name ?? "admin");
                TempData["Success"] = $"Outlook connected — HRMS emails will now be sent from {acct.Email}.";
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Outlook connect failed");
                TempData["Error"] = $"Could not connect Outlook: {ex.Message}";
            }
            return BackToSettings();
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Disconnect()
        {
            await _outlook.DisconnectAsync();
            TempData["Success"] = "Outlook disconnected. Emails will use SMTP settings (if configured).";
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
            var (ok, msg) = await _outlook.SendAsync(new MailRequest
            {
                To       = new List<string> { to.Trim() },
                Subject  = "AMPM HRMS — Outlook test email",
                HtmlBody = "<p>This is a test email from AMPM HRMS, sent through the connected Outlook account.</p>"
            });
            TempData[ok ? "Success" : "Error"] = ok ? $"Test email sent. {msg}" : msg;
            return BackToSettings();
        }
    }
}
