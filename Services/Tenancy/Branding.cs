using AmpmHrmsPro.Data;
using Microsoft.EntityFrameworkCore;

namespace AmpmHrmsPro.Services.Tenancy
{
    /// <summary>
    /// White-label helpers. Email templates write "{{BRAND}}" / "{{COMPANY}}"
    /// and the mail senders swap in the current company's names, so no
    /// template has a client's name baked in.
    /// </summary>
    public static class Branding
    {
        public const string BrandToken = "{{BRAND}}";
        public const string CompanyToken = "{{COMPANY}}";

        /// <summary>Current company's display name (falls back to the product name).</summary>
        public static string CompanyName(this AppDbContext db) =>
            db.Companies.AsNoTracking().Where(c => c.Id == db.CurrentCompanyId).Select(c => c.Name).FirstOrDefault()
            ?? PlatformBrand.ProductName;

        /// <summary>Name used as the e-mail sender ("ABC Pvt Ltd HR").</summary>
        public static async Task<string> SenderNameAsync(this AppDbContext db)
        {
            var c = await db.Companies.AsNoTracking().Where(x => x.Id == db.CurrentCompanyId)
                .Select(x => new { x.Name, x.EmailSenderName }).FirstOrDefaultAsync();
            return c == null ? PlatformBrand.ProductName
                 : !string.IsNullOrWhiteSpace(c.EmailSenderName) ? c.EmailSenderName! : c.Name + " HR";
        }

        public static async Task<MailRequest> ApplyBrandAsync(this AppDbContext db, MailRequest req)
        {
            var sender = req.FromName ??= await db.SenderNameAsync();
            var company = db.CompanyName();
            req.Subject  = (req.Subject ?? "").Replace(BrandToken, sender).Replace(CompanyToken, company);
            req.HtmlBody = (req.HtmlBody ?? "").Replace(BrandToken, sender).Replace(CompanyToken, company);
            return req;
        }
    }
}
