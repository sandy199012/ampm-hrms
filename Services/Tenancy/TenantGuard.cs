using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace AmpmHrmsPro.Services.Tenancy
{
    /// <summary>
    /// Runs on every request after authentication:
    ///  • a suspended / expired company's users are sent to a "subscription" page
    ///  • a user who must change their password can't go anywhere else first
    ///  • a platform owner can only use /Platform (unless impersonating a company)
    /// </summary>
    public class TenantGuardMiddleware
    {
        private readonly RequestDelegate _next;
        public TenantGuardMiddleware(RequestDelegate next) => _next = next;

        private static readonly string[] OpenPaths =
        {
            "/account/", "/platform/login", "/platform/logout", "/css/", "/js/", "/lib/", "/images/", "/favicon", "/api/health", "/home/error",
        };

        public async Task InvokeAsync(HttpContext ctx, ICompanyInfoService companies)
        {
            var path = (ctx.Request.Path.Value ?? "/").ToLowerInvariant();
            var user = ctx.User;
            if (user.Identity?.IsAuthenticated != true || OpenPaths.Any(p => path.StartsWith(p)))
            {
                await _next(ctx);
                return;
            }

            // Platform owner (not impersonating) → only the /Platform console.
            if (user.IsPlatformAdmin())
            {
                if (!path.StartsWith("/platform")) { ctx.Response.Redirect("/Platform"); return; }
                await _next(ctx);
                return;
            }

            if (path.StartsWith("/platform") && user.FindFirst(TenantClaims.Impersonator) == null)
            {
                ctx.Response.Redirect("/Platform/Login");
                return;
            }

            var companyId = user.GetCompanyId();
            if (companyId == 0)
            {
                // Login from before multi-company support → log in again.
                await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                ctx.Response.Redirect("/Account/Login");
                return;
            }

            var info = await companies.GetAsync(companyId);
            bool isApi = path.StartsWith("/api/");
            if (info == null || !info.CanUse)
            {
                if (user.FindFirst(TenantClaims.Impersonator) != null) { await _next(ctx); return; }   // owner may still look in
                if (isApi)
                {
                    ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await ctx.Response.WriteAsJsonAsync(new { message = info?.IsSuspended == true ? "Company account suspended." : "Subscription expired." });
                    return;
                }
                ctx.Response.Redirect("/Account/Subscription");
                return;
            }

            if (!isApi && user.HasClaim("MustChangePassword", "1"))
            {
                ctx.Response.Redirect("/Account/ChangePassword");
                return;
            }
            if (!isApi && user.HasClaim("MustSetup2FA", "1"))
            {
                ctx.Response.Redirect("/Account/TwoFactorSetup");
                return;
            }

            await _next(ctx);
        }
    }
}
