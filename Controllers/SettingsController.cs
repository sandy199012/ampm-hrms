using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AmpmHrmsPro.Data;
using AmpmHrmsPro.Models;
using AmpmHrmsPro.Services.Tenancy;

namespace AmpmHrmsPro.Controllers
{
    // Company profile & branding (white-label) and the company's security policy.
    [Authorize]
    [RequirePermission(Perm.Settings)]
    public class SettingsController : Controller
    {
        private readonly AppDbContext _db;
        private readonly ICompanyInfoService _companies;
        private readonly ICompanyProvisioning _provisioning;

        public SettingsController(AppDbContext db, ICompanyInfoService companies, ICompanyProvisioning provisioning)
        { _db = db; _companies = companies; _provisioning = provisioning; }

        // Companies is a global table — always pick the logged-in user's own row.
        private Task<Company> MyCompanyAsync() => _db.Companies.FirstAsync(c => c.Id == User.GetCompanyId());

        private static string? Hex(string? c) =>
            c != null && System.Text.RegularExpressions.Regex.IsMatch(c.Trim(), "^#[0-9a-fA-F]{6}$") ? c.Trim().ToUpperInvariant() : null;

        [HttpGet]
        public async Task<IActionResult> Company()
        {
            var c = await MyCompanyAsync();
            ViewBag.ActiveEmployees = await _provisioning.ActiveEmployeeCountAsync(c.Id);
            return View(c);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Company(string name, string? legalName, string? contactName, string? contactEmail, string? contactPhone,
            string? address, string? gstin, string? website, string? primaryColor, string? accentColor,
            string? emailSenderName, string? emailFooter, IFormFile? logo, bool removeLogo = false)
        {
            var c = await MyCompanyAsync();
            if (!string.IsNullOrWhiteSpace(name)) c.Name = name.Trim();
            c.LegalName = legalName?.Trim(); c.ContactName = contactName?.Trim(); c.ContactEmail = contactEmail?.Trim();
            c.ContactPhone = contactPhone?.Trim(); c.Address = address?.Trim(); c.Gstin = gstin?.Trim(); c.Website = website?.Trim();
            c.PrimaryColor = Hex(primaryColor) ?? c.PrimaryColor;
            c.AccentColor = Hex(accentColor) ?? c.AccentColor;
            c.EmailSenderName = string.IsNullOrWhiteSpace(emailSenderName) ? null : emailSenderName.Trim();
            c.EmailFooter = string.IsNullOrWhiteSpace(emailFooter) ? null : emailFooter.Trim();

            if (removeLogo) c.LogoDataUrl = null;
            else if (logo != null && logo.Length > 0)
            {
                var type = logo.ContentType?.ToLowerInvariant();
                if (type is not ("image/png" or "image/jpeg" or "image/webp"))
                { TempData["Error"] = "Logo must be a PNG, JPG or WEBP image."; return RedirectToAction(nameof(Company)); }
                if (logo.Length > 300 * 1024)
                { TempData["Error"] = "Logo must be smaller than 300 KB."; return RedirectToAction(nameof(Company)); }
                using var ms = new MemoryStream();
                await logo.CopyToAsync(ms);
                c.LogoDataUrl = $"data:{type};base64,{Convert.ToBase64String(ms.ToArray())}";
            }

            _db.Audit("SettingsChanged", "Company profile / branding updated", "Company", c.Id.ToString());
            await _db.SaveChangesAsync();
            _companies.Invalidate(c.Id);
            TempData["Success"] = "Company profile saved.";
            return RedirectToAction(nameof(Company));
        }

        [HttpGet]
        public async Task<IActionResult> Security() => View(await MyCompanyAsync());

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Security(int passwordMinLength, bool passwordRequireComplex, int passwordExpiryDays,
            int maxFailedLogins, int lockoutMinutes, int sessionTimeoutMinutes, bool require2FAForAdmins)
        {
            var c = await MyCompanyAsync();
            c.PasswordMinLength = Math.Clamp(passwordMinLength, 6, 32);
            c.PasswordRequireComplex = passwordRequireComplex;
            c.PasswordExpiryDays = Math.Clamp(passwordExpiryDays, 0, 365);
            c.MaxFailedLogins = Math.Clamp(maxFailedLogins, 0, 20);
            c.LockoutMinutes = Math.Clamp(lockoutMinutes, 1, 1440);
            c.SessionTimeoutMinutes = Math.Clamp(sessionTimeoutMinutes, 0, 1440);
            c.Require2FAForAdmins = require2FAForAdmins;
            _db.Audit("SettingsChanged", "Security policy updated", "Company", c.Id.ToString());
            await _db.SaveChangesAsync();
            _companies.Invalidate(c.Id);
            TempData["Success"] = "Security policy saved.";
            return RedirectToAction(nameof(Security));
        }
    }
}
