using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using AmpmHrmsPro.Data;
using AmpmHrmsPro.Models;
using AmpmHrmsPro.Services;

namespace AmpmHrmsPro.Controllers.Api
{
    // ═══════════════════════════════════════════
    // MOBILE MANAGER — the Manager/HOD side of the app: their team's
    // attendance, pending approvals (their own direct reports, PLUS any
    // application left unassigned that lands on their desk because they
    // head the applicant's department — mirrors the escalation Application.cs
    // already documents), and team-scoped Leave/Regularisation/OT/Reports.
    // An "admin"/"hr" mobile login sees EVERYONE, not just a team — that's
    // what makes the Admin dashboard "full" per the requirement, reusing
    // the exact same endpoints rather than a separate Admin API.
    // ═══════════════════════════════════════════
    [ApiController]
    [Route("api/mobile/manager")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public class MobileManagerController : ControllerBase
    {
        readonly AppDbContext _db;
        public MobileManagerController(AppDbContext db) => _db = db;

        int CurrentEmpId => int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
        bool IsFullAccess => User.IsInRole("admin") || User.IsInRole("hr");

        // Direct reports + everyone in a department this employee heads.
        // Full-access roles (admin/hr) get every active employee — same
        // "sees everything" behavior the existing web Admin side already has.
        async Task<List<int>> GetTeamEmployeeIdsAsync()
        {
            if (IsFullAccess) return await _db.Employees.Where(e => e.IsActive).Select(e => e.Id).ToListAsync();

            var direct = await _db.Employees.Where(e => e.IsActive && e.ReportingManagerId == CurrentEmpId).Select(e => e.Id).ToListAsync();
            var headedDeptIds = await _db.Departments.Where(d => d.HeadEmployeeId == CurrentEmpId).Select(d => d.Id).ToListAsync();
            var deptWide = headedDeptIds.Any()
                ? await _db.Employees.Where(e => e.IsActive && e.DepartmentId != null && headedDeptIds.Contains(e.DepartmentId!.Value)).Select(e => e.Id).ToListAsync()
                : new List<int>();
            return direct.Union(deptWide).Distinct().ToList();
        }

        [HttpGet("team")]
        public async Task<IActionResult> Team()
        {
            var ids = await GetTeamEmployeeIdsAsync();
            var team = await _db.Employees.Include(e => e.Department).Include(e => e.Designation)
                .Where(e => ids.Contains(e.Id)).OrderBy(e => e.Name).ToListAsync();
            return Ok(team.Select(e => new { id = e.Id, empCode = e.EmpCode, name = e.Name, department = e.Department?.Name, designation = e.Designation?.Name, photoUrl = e.PhotoUrl }));
        }

        [HttpGet("team-attendance")]
        public async Task<IActionResult> TeamAttendance(string? date)
        {
            var d = DateTime.TryParse(date, out var dd) ? dd.Date : DateTime.Today;
            var dateStr = d.ToString("yyyy-MM-dd");
            var ids = await GetTeamEmployeeIdsAsync();

            var team = await _db.Employees.Where(e => ids.Contains(e.Id)).OrderBy(e => e.Name).ToListAsync();
            var daily = await _db.AttendanceDailies.Where(x => ids.Contains(x.EmployeeId) && x.Date == dateStr).ToDictionaryAsync(x => x.EmployeeId);

            return Ok(team.Select(e => new
            {
                employeeId = e.Id,
                empCode = e.EmpCode,
                name = e.Name,
                status = daily.TryGetValue(e.Id, out var rec) ? rec.EffectiveStatus : "—",
                inTime = daily.TryGetValue(e.Id, out var r1) ? r1.InTime?.ToString(@"hh\:mm") : null,
                outTime = daily.TryGetValue(e.Id, out var r2) ? r2.OutTime?.ToString(@"hh\:mm") : null,
            }));
        }

        [HttpGet("approvals")]
        public async Task<IActionResult> Approvals(string? status)
        {
            status = string.IsNullOrWhiteSpace(status) ? "Pending" : status;
            var headedDeptIds = await _db.Departments.Where(d => d.HeadEmployeeId == CurrentEmpId).Select(d => d.Id).ToListAsync();

            var q = _db.Applications.Include(a => a.Employee).ThenInclude(e => e!.Department)
                .Include(a => a.LeaveType).Where(a => a.Status == status);

            if (!IsFullAccess)
                q = q.Where(a => a.ApproverEmployeeId == CurrentEmpId
                    || (a.ApproverEmployeeId == null && a.Employee!.DepartmentId != null && headedDeptIds.Contains(a.Employee!.DepartmentId!.Value)));

            var rows = await q.OrderByDescending(a => a.AppliedOn).Take(200).ToListAsync();
            return Ok(rows.Select(a => new
            {
                id = a.Id,
                employeeId = a.EmployeeId,
                employeeName = a.Employee?.Name,
                empCode = a.Employee?.EmpCode,
                department = a.Employee?.Department?.Name,
                type = a.Type,
                leaveType = a.LeaveType?.Alias,
                fromDate = a.FromDate,
                toDate = a.ToDate,
                durationDays = a.DurationDays,
                reason = a.Reason,
                status = a.Status,
                appliedOn = a.AppliedOn,
            }));
        }

        async Task<(bool Allowed, AmpmHrmsPro.Models.Application? App)> CanDecide(int id)
        {
            var app = await _db.Applications.Include(a => a.Employee).FirstOrDefaultAsync(a => a.Id == id);
            if (app == null) return (false, null);
            if (IsFullAccess) return (true, app);
            if (app.ApproverEmployeeId == CurrentEmpId) return (true, app);
            var headedDeptIds = await _db.Departments.Where(d => d.HeadEmployeeId == CurrentEmpId).Select(d => d.Id).ToListAsync();
            if (app.ApproverEmployeeId == null && app.Employee?.DepartmentId != null && headedDeptIds.Contains(app.Employee.DepartmentId.Value)) return (true, app);
            return (false, app);
        }

        public record DecisionRequest(string? Remarks);

        [HttpPost("approve/{id}")]
        public async Task<IActionResult> Approve(int id, DecisionRequest req)
        {
            var (allowed, app) = await CanDecide(id);
            if (app == null) return NotFound();
            if (!allowed) return Forbid();

            // ── CompOff Credit Approval ──
            if (app.Type == "CompOff")
            {
                app.Status = "Approved";
                app.Remarks = req.Remarks;
                app.DecisionAt = DateTime.Now;
                app.DecisionByEmployeeId = CurrentEmpId;
                app.PendingAt = null;
                await CompOffEngine.ApproveCompOffCreditAsync(_db, app);
                NotificationHelper.Notify(_db, app.EmployeeId,
                    "Comp-Off credit approved",
                    $"Your comp-off credit of {app.DurationDays:0.#} day(s) for {app.FromDate} has been approved.",
                    "Approval", app.Id);
                await _db.SaveChangesAsync();
                return Ok(new { success = true });
            }

            // ── Leave / Regularisation / WFH / OD ──
            if (app.Type == "Leave")
            {
                await _db.Entry(app).Reference(a => a.LeaveType).LoadAsync();
                if (app.LeaveType?.IsCompOff == true)
                {
                    var (ok, message) = await CompOffEngine.TryConsumeAsync(_db, app.EmployeeId, app.Id, app.DurationDays);
                    if (!ok) return BadRequest(new { message });
                }
                else if (app.LeaveType?.IsCompOff == false && !string.IsNullOrEmpty(app.LeaveType?.Alias))
                {
                    await LeaveBalanceEngine.ConsumeAsync(_db, app.EmployeeId, app.LeaveType.Alias, app.FromDate, app.ToDate, app.DurationDays);
                }
            }

            app.Status = "Approved";
            app.Remarks = req.Remarks;
            app.DecisionAt = DateTime.Now;
            app.DecisionByEmployeeId = CurrentEmpId;
            app.PendingAt = null;
            NotificationHelper.Notify(_db, app.EmployeeId, $"{app.Type} application approved", req.Remarks, "Approval", app.Id);
            await _db.SaveChangesAsync();

            await AttendanceEngine.RecomputeRangeAsync(_db, app.EmployeeId, DateTime.Parse(app.FromDate), DateTime.Parse(app.ToDate));
            return Ok(new { success = true });
        }

        [HttpPost("reject/{id}")]
        public async Task<IActionResult> Reject(int id, DecisionRequest req)
        {
            var (allowed, app) = await CanDecide(id);
            if (app == null) return NotFound();
            if (!allowed) return Forbid();

            bool wasApproved = app.Status == "Approved";
            app.Status = "Rejected";
            app.Remarks = req.Remarks;
            app.DecisionAt = DateTime.Now;
            app.DecisionByEmployeeId = CurrentEmpId;
            app.PendingAt = null;
            NotificationHelper.Notify(_db, app.EmployeeId, $"{app.Type} application rejected", req.Remarks, "Approval", app.Id);
            await _db.SaveChangesAsync();

            // CompOff Credit rejection — undo ledger if previously approved.
            if (app.Type == "CompOff")
            {
                if (wasApproved) await CompOffEngine.RevokeCompOffCreditAsync(_db, app);
                return Ok(new { success = true });
            }

            await _db.Entry(app).Reference(a => a.LeaveType).LoadAsync();
            await CompOffEngine.RefundAsync(_db, app.Id);
            if (wasApproved && app.Type == "Leave" && app.LeaveType?.IsCompOff == false && !string.IsNullOrEmpty(app.LeaveType?.Alias))
            {
                await LeaveBalanceEngine.RefundAsync(_db, app.EmployeeId, app.LeaveType.Alias, app.FromDate, app.ToDate, app.DurationDays);
                await _db.SaveChangesAsync();
            }

            await AttendanceEngine.RecomputeRangeAsync(_db, app.EmployeeId, DateTime.Parse(app.FromDate), DateTime.Parse(app.ToDate));
            return Ok(new { success = true });
        }

        [HttpGet("team-applications")]
        public async Task<IActionResult> TeamApplications(string? type, int? year)
        {
            int y = year ?? DateTime.Today.Year;
            var ids = await GetTeamEmployeeIdsAsync();
            var q = _db.Applications.Include(a => a.Employee).Include(a => a.LeaveType)
                .Where(a => ids.Contains(a.EmployeeId) && string.Compare(a.FromDate, $"{y}-12-31") <= 0 && string.Compare(a.ToDate, $"{y}-01-01") >= 0);
            if (!string.IsNullOrWhiteSpace(type)) q = q.Where(a => a.Type == type);

            var rows = await q.OrderByDescending(a => a.FromDate).Take(300).ToListAsync();
            return Ok(rows.Select(a => new
            {
                id = a.Id, employeeName = a.Employee?.Name, empCode = a.Employee?.EmpCode,
                type = a.Type, leaveType = a.LeaveType?.Alias, fromDate = a.FromDate, toDate = a.ToDate,
                durationDays = a.DurationDays, status = a.Status,
            }));
        }

        [HttpGet("reports")]
        public async Task<IActionResult> Reports(int? year, int? month)
        {
            int y = year ?? DateTime.Today.Year, m = month ?? DateTime.Today.Month;
            var fromStr = new DateTime(y, m, 1).ToString("yyyy-MM-dd");
            var toStr = new DateTime(y, m, DateTime.DaysInMonth(y, m)).ToString("yyyy-MM-dd");

            var ids = await GetTeamEmployeeIdsAsync();
            var team = await _db.Employees.Include(e => e.Shift).Where(e => ids.Contains(e.Id)).OrderBy(e => e.Name).ToListAsync();
            var dailyByEmp = (await _db.AttendanceDailies.Where(d => ids.Contains(d.EmployeeId) && string.Compare(d.Date, fromStr) >= 0 && string.Compare(d.Date, toStr) <= 0).ToListAsync())
                .ToLookup(d => d.EmployeeId);

            return Ok(team.Select(e =>
            {
                var recs = dailyByEmp[e.Id].ToList();
                return new
                {
                    employeeId = e.Id,
                    name = e.Name,
                    present = recs.Count(r => ExcelReportBuilder.IsPresentFamily(r.EffectiveStatus)),
                    absent = recs.Count(r => r.EffectiveStatus == "A"),
                    late = recs.Count(r => ExcelReportBuilder.IsLate(r, e.Shift)),
                    missingPunch = recs.Count(r => ExcelReportBuilder.IsMispunch(r.EffectiveStatus)),
                    otHours = recs.Where(r => r.OTHours.HasValue).Sum(r => r.OTHours!.Value),
                };
            }));
        }
    }
}
