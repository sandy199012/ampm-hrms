using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;

namespace AmpmHrmsPro.Services.Tenancy
{
    /// <summary>
    /// Page needs a permission. GET/HEAD need <see cref="View"/>; POST etc. need
    /// <see cref="Manage"/> (defaults to View when not given). Action-level
    /// attributes override controller-level ones.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
    public class RequirePermissionAttribute : Attribute
    {
        public string View { get; }
        public string Manage { get; }
        public RequirePermissionAttribute(string view, string? manage = null) { View = view; Manage = manage ?? view; }
    }

    /// <summary>Self-service page that only exists when the company's plan has this module.</summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
    public class RequireModuleAttribute : Attribute
    {
        public string Module { get; }
        public RequireModuleAttribute(string module) => Module = module;
    }

    /// <summary>Global MVC filter enforcing the two attributes above.</summary>
    public class AccessFilter : IAsyncAuthorizationFilter
    {
        private readonly IAccessService _access;
        public AccessFilter(IAccessService access) => _access = access;

        public async Task OnAuthorizationAsync(AuthorizationFilterContext ctx)
        {
            if (ctx.ActionDescriptor is not ControllerActionDescriptor ad) return;
            if (ctx.ActionDescriptor.EndpointMetadata.OfType<IAllowAnonymous>().Any()) return;

            var perm = ad.MethodInfo.GetCustomAttributes(typeof(RequirePermissionAttribute), true).Cast<RequirePermissionAttribute>().FirstOrDefault()
                    ?? ad.ControllerTypeInfo.GetCustomAttributes(typeof(RequirePermissionAttribute), true).Cast<RequirePermissionAttribute>().FirstOrDefault();
            var module = ad.MethodInfo.GetCustomAttributes(typeof(RequireModuleAttribute), true).Cast<RequireModuleAttribute>().FirstOrDefault()
                      ?? ad.ControllerTypeInfo.GetCustomAttributes(typeof(RequireModuleAttribute), true).Cast<RequireModuleAttribute>().FirstOrDefault();
            if (perm == null && module == null) return;

            var user = ctx.HttpContext.User;
            if (user.Identity?.IsAuthenticated != true) { ctx.Result = new ChallengeResult(); return; }

            var access = await _access.GetAsync(user);
            if (module != null && !access.HasModule(module.Module)) { ctx.Result = Locked(module.Module); return; }
            if (perm == null) return;

            var method = ctx.HttpContext.Request.Method;
            var needed = HttpMethods.IsGet(method) || HttpMethods.IsHead(method) ? perm.View : perm.Manage;
            // "manage" implies "view"
            bool ok = access.Can(needed) || (needed == perm.View && access.Can(perm.Manage));
            if (ok) return;

            var mod = Perm.ModuleOf(needed);
            if (mod != null && !access.HasModule(mod) && access.Permissions.Contains(needed)) { ctx.Result = Locked(mod); return; }
            ctx.Result = new RedirectToActionResult("AccessDenied", "Account", null);
        }

        private static IActionResult Locked(string module) =>
            new RedirectToActionResult("ModuleLocked", "Account", new { module });
    }
}
