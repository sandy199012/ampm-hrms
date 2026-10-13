using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AmpmHrmsPro.Data;
using AmpmHrmsPro.Models;
using AmpmHrmsPro.Services;
using AmpmHrmsPro.Services.Tenancy;

namespace AmpmHrmsPro.Controllers
{
    // ═══════════════════════════════════════════════════════════════════
    // USERS & ACCESS — who can log in and what they may do:
    //   • base login role (Employee / Manager / HR / Admin)
    //   • optional custom role (company-defined permission set)
    //   • unlock, reset password, reset 2-step verification
    // plus the custom-role editor and the audit log.
    // ═══════════════════════════════════════════════════════════════════
    [Authorize]
    [RequirePermission(Perm.Users)]
    public class UsersController : Controller
    {
        private readonly AppDbContext _db;
        private readonly IAccessService _access;
        public UsersController(AppDbContext db, IAccessService access) { _db = db; _access = access; }

        private int CompanyId => User.GetCompanyId();

        // ── Users ────────────────────────────────────────────────────────
        public record UserRow(int Id, string EmpCode, string Name, string Role, string? CustomRole, bool IsActive,
                              bool Locked, bool TwoFactor, bool MustChange, DateTime? LastLogin, string? Department);

        public async Task<IActionResult> Index(string? q, string? filter)
        {
            var roles = await _db.AppRoles.AsNoTracking().ToDictionaryAsync(r => r.Id, r => r.Name);
            var query = _db.Employees.AsNoTracking().Include(e => e.Department).AsQueryable();
            if (!string.IsNullOrWhiteSpace(q))
                query = query.Where(e => e.Name.Contains(q) || e.EmpCode.Contains(q) || e.Email.Contains(q));
            var now = DateTime.UtcNow;
            query = filter switch
            {
                "privileged" => query.Where(e => e.Role == "admin" || e.Role == "hr" || e.CustomRoleId != null),
                "locked"     => query.Where(e => e.LockoutUntil > now),
                "inactive"   => query.Where(e => !e.IsActive),
                _            => query.Where(e => e.IsActive),
            };
            var list = await query.OrderBy(e => e.Role == "admin" ? 0 : e.Role == "hr" ? 1 : e.CustomRoleId != null ? 2 : 3)
                .ThenBy(e => e.Name).Take(500).ToListAsync();
            var rows = list.Select(e => new UserRow(e.Id, e.EmpCode, e.Name, e.Role,
                e.CustomRoleId is int rid && roles.TryGetValue(rid, out var rn) ? rn : null, e.IsActive,
                e.LockoutUntil > now, e.TwoFactorEnabled, e.MustChangePassword, e.LastLoginAt, e.Department?.Name)).ToList();
            ViewBag.Q = q; ViewBag.Filter = filter ?? "active";
            ViewBag.Roles = await _db.AppRoles.AsNoTracking().Where(r => r.IsActive).OrderBy(r => r.Name).ToListAsync();
            return View(rows);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> SetAccess(int id, string role, int? customRoleId)
        {
            var emp = await _db.Employees.FirstOrDefaultAsync(e => e.Id == id);
            if (emp == null) return NotFound();
            role = role is "admin" or "hr" or "manager" ? role : "employee";

            if (emp.Id == User.GetEmployeeId() && emp.Role == "admin" && role != "admin")
            { TempData["Error"] = "You can't remove your own Admin role (ask another admin)."; return RedirectToAction(nameof(Index)); }
            if (emp.Role == "admin" && role != "admin" &&
                await _db.Employees.CountAsync(e => e.Role == "admin" && e.IsActive) <= 1)
            { TempData["Error"] = "At least one active Admin is required."; return RedirectToAction(nameof(Index)); }
            if (customRoleId.HasValue && !await _db.AppRoles.AnyAsync(r => r.Id == customRoleId))
                customRoleId = null;

            emp.Role = role;
            emp.CustomRoleId = customRoleId;
            await _db.SaveChangesAsync();
            _access.Invalidate(CompanyId);
            TempData["Success"] = $"Access updated for {emp.Name}. It applies from their next page load.";
            return RedirectToAction(nameof(Index), new { q = emp.EmpCode });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Unlock(int id)
        {
            var emp = await _db.Employees.FirstOrDefaultAsync(e => e.Id == id);
            if (emp == null) return NotFound();
            emp.LockoutUntil = null; emp.FailedLoginCount = 0;
            _db.Audit("Unlock", $"Unlocked {emp.Name} ({emp.EmpCode})", "Employee", emp.Id.ToString());
            await _db.SaveChangesAsync();
            TempData["Success"] = $"{emp.Name} can sign in again.";
            return RedirectToAction(nameof(Index), new { q = emp.EmpCode });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> ResetPassword(int id)
        {
            var emp = await _db.Employees.FirstOrDefaultAsync(e => e.Id == id);
            if (emp == null) return NotFound();
            var pwd = PasswordPolicy.Generate();
            emp.PasswordHash = BCrypt.Net.BCrypt.HashPassword(pwd);
            emp.MustChangePassword = true;
            emp.PasswordChangedAt = DateTime.UtcNow;
            emp.LockoutUntil = null; emp.FailedLoginCount = 0;
            _db.Audit("PasswordReset", $"Password reset for {emp.Name} ({emp.EmpCode})", "Employee", emp.Id.ToString());
            await _db.SaveChangesAsync();
            TempData["Success"] = $"Temporary password for {emp.Name} ({emp.EmpCode}): {pwd} — they must change it at login.";
            return RedirectToAction(nameof(Index), new { q = emp.EmpCode });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Reset2FA(int id)
        {
            var emp = await _db.Employees.FirstOrDefaultAsync(e => e.Id == id);
            if (emp == null) return NotFound();
            emp.TwoFactorEnabled = false; emp.TwoFactorSecret = null;
            _db.Audit("2FAReset", $"2-step verification reset for {emp.Name} ({emp.EmpCode})", "Employee", emp.Id.ToString());
            await _db.SaveChangesAsync();
            TempData["Success"] = $"2-step verification removed for {emp.Name}. They can set it up again from My Security.";
            return RedirectToAction(nameof(Index), new { q = emp.EmpCode });
        }

        // ── Custom roles ─────────────────────────────────────────────────
        public async Task<IActionResult> Roles()
        {
            var roles = await _db.AppRoles.AsNoTracking().OrderBy(r => r.Name).ToListAsync();
            ViewBag.Counts = await _db.Employees.Where(e => e.CustomRoleId != null && e.IsActive)
                .GroupBy(e => e.CustomRoleId!.Value).Select(g => new { g.Key, N = g.Count() })
                .ToDictionaryAsync(x => x.Key, x => x.N);
            return View(roles);
        }

        [HttpGet]
        public async Task<IActionResult> Role(int? id)
        {
            var role = id.HasValue ? await _db.AppRoles.FirstOrDefaultAsync(r => r.Id == id) : new AppRole();
            if (role == null) return NotFound();
            ViewBag.Modules = (await _access.GetAsync(User)).Modules;
            return View(role);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Role(int id, string name, string? description, string[]? permissions, bool isActive = true)
        {
            if (string.IsNullOrWhiteSpace(name)) { TempData["Error"] = "Role name is required."; return RedirectToAction(nameof(Role), new { id }); }
            var perms = (permissions ?? Array.Empty<string>()).Where(p => Perm.AllKeys.Contains(p)).Distinct().ToList();
            // Security admins only — a custom role can't hand out user/role management.
            perms.Remove(Perm.Users);
            if (await _db.AppRoles.AnyAsync(r => r.Name == name.Trim() && r.Id != id))
            { TempData["Error"] = "Another role already has that name."; return RedirectToAction(nameof(Role), new { id }); }

            AppRole role;
            if (id == 0) { role = new AppRole(); _db.AppRoles.Add(role); }
            else role = await _db.AppRoles.FirstAsync(r => r.Id == id);
            role.Name = name.Trim();
            role.Description = description?.Trim();
            role.Permissions = string.Join(",", perms);
            role.IsActive = isActive;
            await _db.SaveChangesAsync();
            _access.Invalidate(CompanyId);
            TempData["Success"] = $"Role '{role.Name}' saved.";
            return RedirectToAction(nameof(Roles));
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteRole(int id)
        {
            var role = await _db.AppRoles.FirstOrDefaultAsync(r => r.Id == id);
            if (role == null) return NotFound();
            foreach (var e in await _db.Employees.Where(e => e.CustomRoleId == id).ToListAsync()) e.CustomRoleId = null;
            _db.AppRoles.Remove(role);
            await _db.SaveChangesAsync();
            _access.Invalidate(CompanyId);
            TempData["Success"] = $"Role '{role.Name}' deleted.";
            return RedirectToAction(nameof(Roles));
        }

        // ── Audit log ────────────────────────────────────────────────────
        [RequirePermission(Perm.Audit)]
        public async Task<IActionResult> Audit(DateTime? from, DateTime? to, string? user, string? action, string? q, int page = 1, bool export = false)
        {
            var query = _db.AuditLogs.AsNoTracking().AsQueryable();
            if (from.HasValue) { var f = from.Value.Date.AddHours(-5.5); query = query.Where(a => a.At >= f); }
            if (to.HasValue)   { var t = to.Value.Date.AddDays(1).AddHours(-5.5); query = query.Where(a => a.At < t); }
            if (!string.IsNullOrWhiteSpace(user))   query = query.Where(a => a.UserName != null && a.UserName.Contains(user));
            if (!string.IsNullOrWhiteSpace(action)) query = query.Where(a => a.Action == action);
            if (!string.IsNullOrWhiteSpace(q))      query = query.Where(a => (a.Summary != null && a.Summary.Contains(q)) || (a.Changes != null && a.Changes.Contains(q)) || a.Entity == q);
            query = query.OrderByDescending(a => a.Id);

            if (export)
            {
                var all = await query.Take(20000).ToListAsync();
                var sb = new StringBuilder("When (IST),User,Action,Record,Summary,Changes,IP\n");
                static string Csv(string? s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
                foreach (var a in all)
                    sb.AppendLine(string.Join(",", Csv(IndiaTime.FromUtc(a.At).ToString("yyyy-MM-dd HH:mm:ss")), Csv(a.UserName), Csv(a.Action),
                        Csv($"{a.Entity} {a.EntityId}".Trim()), Csv(a.Summary), Csv(a.Changes), Csv(a.IpAddress)));
                return File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray(), "text/csv", $"AuditLog_{IndiaTime.Today:yyyy-MM-dd}.csv");
            }

            const int size = 50;
            page = Math.Max(1, page);
            ViewBag.Total = await query.CountAsync();
            ViewBag.Page = page; ViewBag.Size = size;
            ViewBag.Actions = await _db.AuditLogs.Select(a => a.Action).Distinct().OrderBy(a => a).ToListAsync();
            ViewBag.From = from; ViewBag.To = to; ViewBag.User = user; ViewBag.Action = action; ViewBag.Q = q;
            return View(await query.Skip((page - 1) * size).Take(size).ToListAsync());
        }
    }
}
