using AmpmHrmsPro.Data;
using Microsoft.EntityFrameworkCore;

namespace AmpmHrmsPro.Services.Tenancy
{
    /// <summary>
    /// Background work is company-blind by default (no logged-in user), so
    /// every timer job runs once PER ACTIVE COMPANY in its own DI scope with
    /// that company selected. One company's failure never stops the others.
    /// </summary>
    public static class TenantJobs
    {
        public static async Task ForEachActiveCompanyAsync(
            IServiceProvider root, Func<IServiceProvider, int, Task> work, ILogger? log = null, CancellationToken ct = default)
        {
            List<int> ids;
            using (var s = root.CreateScope())
            {
                var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
                var now = DateTime.UtcNow;
                ids = await db.Companies.AsNoTracking()
                    .Where(c => c.Status != "Suspended")
                    .Where(c => (c.SubscriptionEndsAt ?? c.TrialEndsAt) == null || (c.SubscriptionEndsAt ?? c.TrialEndsAt) > now)
                    .Select(c => c.Id).ToListAsync(ct);
            }

            foreach (var id in ids)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    using var scope = root.CreateScope();
                    scope.ServiceProvider.GetRequiredService<ITenantContext>().Use(id);
                    await work(scope.ServiceProvider, id);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log?.LogError(ex, "Background job failed for company {CompanyId}", id);
                }
            }
        }

        /// <summary>New DI scope bound to one company — for fire-and-forget work started from a request.</summary>
        public static IServiceScope CreateScopeFor(this IServiceScopeFactory factory, int companyId)
        {
            var scope = factory.CreateScope();
            scope.ServiceProvider.GetRequiredService<ITenantContext>().Use(companyId);
            return scope;
        }
    }
}
