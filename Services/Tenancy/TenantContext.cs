using System.Security.Claims;

namespace AmpmHrmsPro.Services.Tenancy
{
    /// <summary>
    /// "Which company is this request / job working for?" — read by
    /// AppDbContext's query filter on every query. Web requests take it from
    /// the login cookie / JWT ("CompanyId" claim); background jobs, the kiosk
    /// API and the login page set it explicitly with <see cref="Use"/>.
    /// 0 = no company → every company-owned table looks empty (safe default).
    /// </summary>
    public interface ITenantContext
    {
        int CompanyId { get; }
        bool HasCompany { get; }
        void Use(int companyId);
    }

    public class TenantContext : ITenantContext
    {
        public const string ClaimType = "CompanyId";
        private readonly IHttpContextAccessor _http;
        private int? _explicit;

        public TenantContext(IHttpContextAccessor http) => _http = http;

        public int CompanyId
        {
            get
            {
                if (_explicit.HasValue) return _explicit.Value;
                var ctx = _http.HttpContext;
                if (ctx == null) return 0;
                var claim = ctx.User?.FindFirst(ClaimType)?.Value;
                return int.TryParse(claim, out var id) ? id : 0;
            }
        }

        public bool HasCompany => CompanyId > 0;

        public void Use(int companyId) => _explicit = companyId;
    }

    public static class TenantClaims
    {
        public const string CompanyId   = TenantContext.ClaimType;
        public const string CompanyCode = "CompanyCode";
        public const string Impersonator = "ImpersonatorPlatformUser";
        public const string PlatformRole = "superadmin";

        public static int GetCompanyId(this ClaimsPrincipal user) =>
            int.TryParse(user.FindFirst(CompanyId)?.Value, out var id) ? id : 0;

        public static int? GetEmployeeId(this ClaimsPrincipal user) =>
            int.TryParse(user.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;

        public static bool IsPlatformAdmin(this ClaimsPrincipal user) =>
            user.Identity?.IsAuthenticated == true && user.IsInRole(PlatformRole);
    }
}

namespace AmpmHrmsPro.Services.Tenancy
{
    /// <summary>Name of the software itself (shown on the login page / owner console). Set from "Platform:ProductName".</summary>
    public static class PlatformBrand
    {
        public static string ProductName { get; set; } = "HRMS Pro";
    }
}
