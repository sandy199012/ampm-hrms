using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using AmpmHrmsPro.Models;
using AmpmHrmsPro.Services.Tenancy;

namespace AmpmHrmsPro.Data
{
    // ═══════════════════════════════════════════════════════════════════
    // MULTI-COMPANY PLUMBING for AppDbContext
    //
    //  1. Every entity except the few global ones gets a shadow "CompanyId"
    //     column and a query filter "CompanyId == current company", so ALL
    //     existing queries are automatically scoped — no controller change.
    //  2. SaveChanges stamps CompanyId on new rows and refuses to move a
    //     row to another company.
    //  3. SaveChanges writes an AuditLog row for every create / update /
    //     delete of business data (secrets masked, high-volume tables skipped).
    //
    // To deliberately read across companies (platform console, login page)
    // use .IgnoreQueryFilters().
    // ═══════════════════════════════════════════════════════════════════
    public partial class AppDbContext
    {
        public const string TenantColumn = "CompanyId";

        /// <summary>Tables that are NOT per-company.</summary>
        private static readonly HashSet<Type> GlobalTypes = new()
        {
            typeof(DataProtectionKey), typeof(Company), typeof(PlatformUser),
        };

        /// <summary>High-volume / machine-written tables — not written to the audit log row by row.</summary>
        private static readonly HashSet<Type> NotAudited = new()
        {
            typeof(AuditLog), typeof(AttendancePunch), typeof(AttendanceDaily), typeof(P360DailyRecord),
            typeof(AttendanceGapLog), typeof(RosterEntry), typeof(Notification), typeof(GapReportSend),
            typeof(ReviewEmployee), typeof(CompOffConsumption), typeof(FaceProfile),
        };

        private static readonly string[] SecretWords = { "password", "secret", "token", "apikey", "hash", "clientkey" };
        private static readonly HashSet<string> NoiseProps = new(StringComparer.OrdinalIgnoreCase)
        {
            "LastLoginAt", "FailedLoginCount", "LockoutUntil", "LastSeenAt", "LastActivityAt", "UpdatedAt",
            "LastSyncAt", "LastSyncedAt", "LastRunAt", "LastSentAt", "LastSyncStatus", "LastSyncMessage",
        };

        public static bool IsTenantType(Type t) => !GlobalTypes.Contains(t);

        /// <summary>Company the current request / job works for (0 = none).</summary>
        public int CurrentCompanyId => _tenant?.CompanyId ?? 0;

        /// <summary>Turn off automatic audit rows (bulk jobs that log a summary themselves).</summary>
        public bool AuditEnabled { get; set; } = true;

        private void ConfigureTenancy(ModelBuilder mb)
        {
            var setFilter = typeof(AppDbContext).GetMethod(nameof(SetTenantFilter), BindingFlags.NonPublic | BindingFlags.Instance)!;
            foreach (var et in mb.Model.GetEntityTypes().Where(e => !e.IsOwned() && e.BaseType == null).ToList())
            {
                if (!IsTenantType(et.ClrType) || et.ClrType == typeof(Dictionary<string, object>)) continue;
                mb.Entity(et.ClrType).Property<int>(TenantColumn);
                setFilter.MakeGenericMethod(et.ClrType).Invoke(this, new object[] { mb });
                mb.Entity(et.ClrType).HasIndex(TenantColumn);
            }
        }

        private void SetTenantFilter<T>(ModelBuilder mb) where T : class =>
            mb.Entity<T>().HasQueryFilter(e => EF.Property<int>(e, TenantColumn) == CurrentCompanyId);

        // ── Save pipeline ────────────────────────────────────────────────
        private bool _writingAudit;

        public override int SaveChanges(bool acceptAllChangesOnSuccess)
        {
            var pending = BeforeSave();
            var result = base.SaveChanges(acceptAllChangesOnSuccess);
            if (pending.Count > 0) WriteAudit(pending, sync: true).GetAwaiter().GetResult();
            return result;
        }

        public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
        {
            var pending = BeforeSave();
            var result = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
            if (pending.Count > 0) await WriteAudit(pending, sync: false, cancellationToken);
            return result;
        }

        private sealed record PendingAudit(EntityEntry Entry, string Action, Dictionary<string, object?> Changes, int CompanyId);

        private List<PendingAudit> BeforeSave()
        {
            var list = new List<PendingAudit>();
            if (_writingAudit) return list;
            var cid = CurrentCompanyId;

            foreach (var entry in ChangeTracker.Entries().ToList())
            {
                if (entry.State is EntityState.Unchanged or EntityState.Detached) continue;
                var type = entry.Metadata.ClrType;
                if (!IsTenantType(type) || entry.Metadata.FindProperty(TenantColumn) == null) continue;

                var tenantProp = entry.Property(TenantColumn);
                if (entry.State == EntityState.Added)
                {
                    if (tenantProp.CurrentValue is not int v || v == 0)
                    {
                        if (cid == 0)
                            throw new InvalidOperationException($"No company selected while saving a new {type.Name}. (Background job or login code must call ITenantContext.Use.)");
                        tenantProp.CurrentValue = cid;
                    }
                }
                else if (entry.State == EntityState.Modified && tenantProp.IsModified)
                {
                    tenantProp.CurrentValue = tenantProp.OriginalValue;   // rows never move between companies
                    tenantProp.IsModified = false;
                }

                if (!AuditEnabled || NotAudited.Contains(type)) continue;
                var changes = DescribeChanges(entry);
                if (entry.State == EntityState.Modified && changes.Count == 0) continue;
                var action = entry.State switch { EntityState.Added => "Create", EntityState.Deleted => "Delete", _ => "Update" };
                list.Add(new PendingAudit(entry, action, changes, (int)(tenantProp.CurrentValue ?? cid)));
            }
            return list;
        }

        private static Dictionary<string, object?> DescribeChanges(EntityEntry entry)
        {
            var d = new Dictionary<string, object?>();
            foreach (var p in entry.Properties)
            {
                var name = p.Metadata.Name;
                if (name == TenantColumn || p.Metadata.IsPrimaryKey() || NoiseProps.Contains(name)) continue;
                bool secret = SecretWords.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase));
                object? Show(object? v) => v == null ? null : secret ? "••••" : Trim(v);

                switch (entry.State)
                {
                    case EntityState.Added:
                        if (p.CurrentValue != null && !(p.CurrentValue is string s && s.Length == 0)) d[name] = Show(p.CurrentValue);
                        break;
                    case EntityState.Modified:
                        if (p.IsModified && !Equals(p.OriginalValue, p.CurrentValue))
                            d[name] = new[] { Show(p.OriginalValue), Show(p.CurrentValue) };
                        break;
                    case EntityState.Deleted:
                        if (name is "Name" or "EmpCode" or "Code" or "Title" or "Alias" or "Email" && p.OriginalValue != null) d[name] = Show(p.OriginalValue);
                        break;
                }
            }
            return d;
        }

        private static object Trim(object v)
        {
            var s = v switch
            {
                DateTime dt => dt.ToString("yyyy-MM-dd HH:mm"),
                DateOnly dd => dd.ToString("yyyy-MM-dd"),
                TimeOnly t  => t.ToString("HH:mm"),
                byte[] b    => $"[{b.Length} bytes]",
                _ => v.ToString() ?? "",
            };
            return s.Length > 200 ? s[..200] + "…" : s;
        }

        private async Task WriteAudit(List<PendingAudit> pending, bool sync, CancellationToken ct = default)
        {
            try
            {
                var (userId, userName, ip) = CurrentActor();
                foreach (var p in pending)
                {
                    var key = string.Join(",", p.Entry.Metadata.FindPrimaryKey()?.Properties
                        .Select(k => p.Entry.Property(k.Name).CurrentValue?.ToString()) ?? Array.Empty<string?>());
                    var label = p.Changes.TryGetValue("Name", out var n) && n is not object[] ? n?.ToString()
                              : p.Changes.TryGetValue("EmpCode", out var c) && c is not object[] ? c?.ToString() : null;
                    var json = JsonSerializer.Serialize(p.Changes);
                    var log = new AuditLog
                    {
                        At = DateTime.UtcNow, UserId = userId, UserName = userName, IpAddress = ip,
                        Action = p.Action, Entity = p.Entry.Metadata.ClrType.Name, EntityId = key,
                        Changes = json.Length > 4000 ? json[..4000] : json,
                        Summary = $"{p.Action} {Humanize(p.Entry.Metadata.ClrType.Name)}{(label != null ? " – " + label : "")}",
                    };
                    AuditLogs.Add(log);
                    Entry(log).Property(TenantColumn).CurrentValue = p.CompanyId;
                }
                _writingAudit = true;
                if (sync) base.SaveChanges(); else await base.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                // Never let auditing break the user's action.
                Console.WriteLine($"⚠️ Audit write failed: {ex.Message}");
                foreach (var e in ChangeTracker.Entries<AuditLog>().Where(e => e.State == EntityState.Added).ToList())
                    e.State = EntityState.Detached;
            }
            finally { _writingAudit = false; }
        }

        /// <summary>Queue a custom audit row (login, export, email sent …); saved with the next SaveChanges.</summary>
        public void Audit(string action, string summary, string? entity = null, string? entityId = null, int? companyId = null, int? userId = null, string? userName = null)
        {
            var (uid, uname, ip) = CurrentActor();
            var log = new AuditLog
            {
                At = DateTime.UtcNow, UserId = userId ?? uid, UserName = userName ?? uname, IpAddress = ip,
                Action = action, Summary = summary.Length > 300 ? summary[..300] : summary, Entity = entity, EntityId = entityId,
            };
            AuditLogs.Add(log);
            var cid = companyId ?? CurrentCompanyId;
            if (cid > 0) Entry(log).Property(TenantColumn).CurrentValue = cid;
            else Entry(log).State = EntityState.Detached;   // nowhere to file it
        }

        private (int? UserId, string? UserName, string? Ip) CurrentActor()
        {
            try
            {
                var ctx = _http?.HttpContext;
                if (ctx == null) return (null, "System", null);
                var user = ctx.User;
                var ip = ctx.Connection.RemoteIpAddress?.ToString();
                if (user?.Identity?.IsAuthenticated != true) return (null, null, ip);
                var name = user.FindFirst(ClaimTypes.Name)?.Value;
                var imp = user.FindFirst(TenantClaims.Impersonator)?.Value;
                if (imp != null) name = $"{imp} (as {name})";
                return (user.GetEmployeeId(), name, ip);
            }
            catch { return (null, "System", null); }   // request already finished (background work)
        }

        private static string Humanize(string typeName) =>
            System.Text.RegularExpressions.Regex.Replace(typeName, "(?<=[a-z])([A-Z])", " $1");
    }
}
