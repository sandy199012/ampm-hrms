using System.Security.Claims;
using AmpmHrmsPro.Data;
using AmpmHrmsPro.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace AmpmHrmsPro.Services.Tenancy
{
    // ═══════════════════════════════════════════════════════════════════
    // MODULES (what a company's plan includes) and PERMISSIONS (what a
    // user inside that company may do).
    // ═══════════════════════════════════════════════════════════════════

    public static class Modules
    {
        public const string Attendance       = "attendance";
        public const string AttendanceReview = "attendance_review";
        public const string Leave            = "leave";
        public const string CompOff          = "compoff";
        public const string OT               = "ot";
        public const string Payroll          = "payroll";
        public const string Reports          = "reports";
        public const string MobileApp        = "mobile";
        public const string EmailAlerts      = "email";

        public static readonly (string Key, string Label, string Hint)[] All =
        {
            (Attendance,       "Attendance",            "Biometric/API sync, bulk import, manual entry, kiosk"),
            (AttendanceReview, "Attendance Review",     "Roster + Presence 360 gap analysis, HoD emails"),
            (Leave,            "Leave Balance",         "EL/CL balances, bulk upload"),
            (CompOff,          "Comp-Off",              "Comp-off rules and ledger"),
            (OT,               "Overtime",              "OT rules, ledger, approvals"),
            (Payroll,          "Salary & Tax",          "Salary structures, TDS, tax declarations"),
            (Reports,          "Reports",               "Attendance, leave, OT and compliance reports"),
            (MobileApp,        "Mobile App & Kiosk",    "Employee/manager mobile app and face kiosk"),
            (EmailAlerts,      "Email Notifications",   "Daily alerts, birthdays, weekly reports"),
        };

        public static readonly string AllKeys = string.Join(",", All.Select(m => m.Key));

        public static HashSet<string> Parse(string? csv) =>
            (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                       .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public record PlanPreset(string Name, int MaxEmployees, string Modules, int? TrialDays);

    public static class Plans
    {
        public static readonly PlanPreset[] All =
        {
            new("Trial",      25,  Tenancy.Modules.AllKeys, 14),
            new("Starter",    50,  string.Join(",", Tenancy.Modules.Attendance, Tenancy.Modules.Leave, Tenancy.Modules.Reports, Tenancy.Modules.MobileApp), null),
            new("Business",   250, Tenancy.Modules.AllKeys, null),
            new("Enterprise", 0,   Tenancy.Modules.AllKeys, null),
        };
        public static PlanPreset? Find(string? name) => All.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    public static class Perm
    {
        public const string Dashboard        = "dashboard.view";
        public const string EmployeesView    = "employees.view";
        public const string EmployeesManage  = "employees.manage";
        public const string EmployeesDelete  = "employees.delete";
        public const string MastersView      = "masters.view";
        public const string MastersManage    = "masters.manage";
        public const string AttendanceView   = "attendance.view";
        public const string AttendanceManage = "attendance.manage";
        public const string ReviewView       = "attendancereview.view";
        public const string ReviewManage     = "attendancereview.manage";
        public const string LeaveView        = "leave.view";
        public const string LeaveManage      = "leave.manage";
        public const string CompOffView      = "compoff.view";
        public const string CompOffManage    = "compoff.manage";
        public const string OTView           = "ot.view";
        public const string OTManage         = "ot.manage";
        public const string PayrollView      = "payroll.view";
        public const string PayrollManage    = "payroll.manage";
        public const string Reports          = "reports.view";
        public const string Settings         = "settings.manage";
        public const string Users            = "users.manage";
        public const string Audit            = "audit.view";

        /// <summary>Permission catalogue for the role editor: (group, key, label, module or null = always available).</summary>
        public static readonly (string Group, string Key, string Label, string? Module)[] Catalogue =
        {
            ("General",           Dashboard,        "View HR dashboard",                  null),
            ("Employees",         EmployeesView,    "View employees",                     null),
            ("Employees",         EmployeesManage,  "Add / edit employees, reset passwords", null),
            ("Employees",         EmployeesDelete,  "Delete employees",                   null),
            ("Masters",           MastersView,      "View masters & policies",            null),
            ("Masters",           MastersManage,    "Edit masters, shifts, holidays, policies", null),
            ("Attendance",        AttendanceView,   "View attendance",                    Modules.Attendance),
            ("Attendance",        AttendanceManage, "Import / edit attendance, recompute", Modules.Attendance),
            ("Attendance Review", ReviewView,       "View gap analysis",                  Modules.AttendanceReview),
            ("Attendance Review", ReviewManage,     "Upload roster/CSV, send HoD emails", Modules.AttendanceReview),
            ("Leave",             LeaveView,        "View leave balances",                Modules.Leave),
            ("Leave",             LeaveManage,      "Edit leave balances",                Modules.Leave),
            ("Comp-Off",          CompOffView,      "View comp-off",                      Modules.CompOff),
            ("Comp-Off",          CompOffManage,    "Manage comp-off rules & credits",    Modules.CompOff),
            ("Overtime",          OTView,           "View OT",                            Modules.OT),
            ("Overtime",          OTManage,         "Manage OT rules, approve / pay OT",  Modules.OT),
            ("Salary & Tax",      PayrollView,      "View salary & tax",                  Modules.Payroll),
            ("Salary & Tax",      PayrollManage,    "Edit salary structures, tax, review declarations", Modules.Payroll),
            ("Reports",           Reports,          "View & export reports",              Modules.Reports),
            ("Administration",    Settings,         "Company settings, email, API & kiosk settings", null),
            ("Administration",    Users,            "Manage users, roles & security",     null),
            ("Administration",    Audit,            "View audit log",                     null),
        };

        public static readonly string[] AllKeys = Catalogue.Select(c => c.Key).ToArray();

        /// <summary>Module a permission belongs to (null = core, always on).</summary>
        public static string? ModuleOf(string perm) => Catalogue.FirstOrDefault(c => c.Key == perm).Module;

        /// <summary>Built-in login roles → default permissions (keeps the old admin/hr behaviour).</summary>
        public static HashSet<string> ForBaseRole(string? role) => (role ?? "").ToLowerInvariant() switch
        {
            "admin" => AllKeys.ToHashSet(),
            "hr"    => AllKeys.Where(k => k != EmployeesDelete && k != Users && k != Audit).ToHashSet(),
            _       => new HashSet<string>(),
        };
    }

    /// <summary>Snapshot of the current user's rights + their company's plan.</summary>
    public class AccessInfo
    {
        public static readonly AccessInfo None = new();
        public HashSet<string> Permissions { get; init; } = new();
        public HashSet<string> Modules { get; init; } = new();
        public bool Can(string perm) =>
            Permissions.Contains(perm) && (Perm.ModuleOf(perm) is not { } m || Modules.Contains(m));
        public bool HasModule(string module) => Modules.Contains(module);
        public bool AnyAdmin => Permissions.Any(Can);
    }

    public interface IAccessService
    {
        Task<AccessInfo> GetAsync(ClaimsPrincipal user);
        AccessInfo Current { get; }          // after GetAsync ran in this request (filter / layout)
        void Invalidate(int companyId);
    }

    public class AccessService : IAccessService
    {
        private readonly AppDbContext _db;
        private readonly IMemoryCache _cache;
        private AccessInfo? _current;

        public AccessService(AppDbContext db, IMemoryCache cache) { _db = db; _cache = cache; }

        public AccessInfo Current => _current ?? AccessInfo.None;

        private static string Key(int companyId, int empId) => $"access:{companyId}:{empId}:{Version(companyId)}";
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, int> _versions = new();
        private static int Version(int companyId) => _versions.GetOrAdd(companyId, 0);
        public void Invalidate(int companyId) => _versions.AddOrUpdate(companyId, 1, (_, v) => v + 1);

        public async Task<AccessInfo> GetAsync(ClaimsPrincipal user)
        {
            if (_current != null) return _current;
            var companyId = user.GetCompanyId();
            var empId = user.GetEmployeeId();
            if (companyId == 0 || empId == null) return _current = AccessInfo.None;

            _current = await _cache.GetOrCreateAsync(Key(companyId, empId.Value), async e =>
            {
                e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(2);
                var emp = await _db.Employees.AsNoTracking()
                    .Where(x => x.Id == empId.Value)
                    .Select(x => new { x.Role, x.CustomRoleId })
                    .FirstOrDefaultAsync();
                var perms = Perm.ForBaseRole(emp?.Role);
                if (emp?.CustomRoleId is int rid)
                {
                    var role = await _db.AppRoles.AsNoTracking().FirstOrDefaultAsync(r => r.Id == rid && r.IsActive);
                    if (role != null) perms.UnionWith(Tenancy.Modules.Parse(role.Permissions));
                }
                var modulesCsv = await _db.Companies.AsNoTracking()
                    .Where(c => c.Id == companyId).Select(c => c.Modules).FirstOrDefaultAsync();
                return new AccessInfo { Permissions = perms, Modules = Tenancy.Modules.Parse(modulesCsv) };
            }) ?? AccessInfo.None;
            return _current;
        }
    }
}
