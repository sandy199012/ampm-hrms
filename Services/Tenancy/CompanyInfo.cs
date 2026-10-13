using AmpmHrmsPro.Data;
using AmpmHrmsPro.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace AmpmHrmsPro.Services.Tenancy
{
    /// <summary>Cached, read-only view of a company used on every page (branding, status, policy).</summary>
    public record CompanyInfo(
        int Id, string Code, string Name, string? LogoDataUrl, string PrimaryColor, string AccentColor,
        string Status, string Plan, int MaxEmployees, DateTime? TrialEndsAt, DateTime? SubscriptionEndsAt,
        int SessionTimeoutMinutes, int PasswordExpiryDays, bool Require2FAForAdmins, string? EmailSenderName, string? EmailFooter)
    {
        public DateTime? EndsAt => SubscriptionEndsAt ?? TrialEndsAt;
        public bool IsExpired => EndsAt.HasValue && EndsAt.Value < DateTime.UtcNow;
        public bool IsSuspended => Status.Equals("Suspended", StringComparison.OrdinalIgnoreCase);
        public bool CanUse => !IsSuspended && !IsExpired;
        public int? DaysLeft => EndsAt.HasValue ? (int)Math.Ceiling((EndsAt.Value - DateTime.UtcNow).TotalDays) : null;
        public string Initial => string.IsNullOrWhiteSpace(Name) ? "H" : Name.Trim()[..1].ToUpperInvariant();
    }

    public interface ICompanyInfoService
    {
        Task<CompanyInfo?> GetAsync(int companyId);
        void Invalidate(int companyId);
    }

    public class CompanyInfoService : ICompanyInfoService
    {
        private readonly IServiceScopeFactory _scopes;
        private readonly IMemoryCache _cache;
        public CompanyInfoService(IServiceScopeFactory scopes, IMemoryCache cache) { _scopes = scopes; _cache = cache; }

        public async Task<CompanyInfo?> GetAsync(int companyId)
        {
            if (companyId <= 0) return null;
            return await _cache.GetOrCreateAsync($"company:{companyId}", async e =>
            {
                e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
                using var scope = _scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var c = await db.Companies.AsNoTracking().FirstOrDefaultAsync(x => x.Id == companyId);
                return c == null ? null : From(c);
            });
        }

        public void Invalidate(int companyId) => _cache.Remove($"company:{companyId}");

        public static CompanyInfo From(Company c) => new(
            c.Id, c.Code, c.Name, c.LogoDataUrl, c.PrimaryColor, c.AccentColor, c.Status, c.Plan, c.MaxEmployees,
            c.TrialEndsAt, c.SubscriptionEndsAt, c.SessionTimeoutMinutes, c.PasswordExpiryDays, c.Require2FAForAdmins,
            c.EmailSenderName, c.EmailFooter);
    }
}
