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
    // MOBILE APPLICATIONS — self-service Leave / Regularisation / WFH for
    // the logged-in employee. Unlike the existing (Admin/HR-only)
    // ApplicationsController, EmployeeId here is ALWAYS the token holder —
    // a mobile user can only ever apply for themselves, never on anyone
    // else's behalf. Every application still lands in the same shared
    // Application table and follows the same approval flow (routed to
    // ReportingManagerId, recomputes attendance on decision) — approving
    // one is done from MobileManagerController or the existing web Admin
    // screen, whichever the approver is using.
    // ═══════════════════════════════════════════
    [ApiController]
    [Route("api/mobile")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public class MobileApplicationsController : ControllerBase
    {
        readonly AppDbContext _db;
        public MobileApplicationsController(AppDbContext db) => _db = db;

        int CurrentEmpId => int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);

        // ── shared apply logic for Leave / Regularisation / WFH ──
        public record ApplyRequest(string FromDate, string ToDate, string? DayPart, int? LeaveTypeId,
            string? RequestedInTime, string? RequestedOutTime, string? Reason);

        async Task<IActionResult> ApplyCore(string type, ApplyRequest req)
        {
            if (string.Compare(req.FromDate, req.ToDate) > 0)
                return BadRequest(new { message = "From Date can't be after To Date." });

            var employee = await _db.Employees.Include(e => e.ReportingManager).FirstOrDefaultAsync(e => e.Id == CurrentEmpId);
            if (employee == null) return NotFound();

            var from = DateTime.Parse(req.FromDate);
            var to = DateTime.Parse(req.ToDate);
            decimal duration = req.DayPart == "FirstHalf" || req.DayPart == "SecondHalf" ? 0.5m : (decimal)(to - from).Days + 1;

            if (type == "Leave" && req.LeaveTypeId.HasValue)
            {
                var lt = await _db.LeaveTypes.FindAsync(req.LeaveTypeId.Value);
                if (lt != null && lt.IsCompOff)
                {
                    decimal available = await CompOffEngine.GetAvailableBalanceAsync(_db, CurrentEmpId);
                    if (available < duration)
                        return BadRequest(new { message = $"Insufficient Comp-Off balance — available {available:0.#} day(s), requested {duration:0.#}." });
                }
            }

            var app = new Application
            {
                EmployeeId = CurrentEmpId,
                Type = type,
                LeaveTypeId = type == "Leave" ? req.LeaveTypeId : null,
                FromDate = req.FromDate,
                ToDate = req.ToDate,
                DurationDays = duration,
                DayPart = req.DayPart ?? "Single",
                Reason = req.Reason,
                Status = "Pending",
                AppliedOn = DateTime.Now,
                CreatedAt = DateTime.Now,
                ApproverEmployeeId = employee.ReportingManagerId,
                PendingAt = employee.ReportingManager?.Name ?? "HR",
            };

            if (type == "Regularisation")
            {
                if (TimeSpan.TryParse(req.RequestedInTime, out var inT)) app.RequestedInTime = inT;
                if (TimeSpan.TryParse(req.RequestedOutTime, out var outT)) app.RequestedOutTime = outT;
            }

            _db.Applications.Add(app);
            await _db.SaveChangesAsync();

            if (app.ApproverEmployeeId.HasValue)
                NotificationHelper.Notify(_db, app.ApproverEmployeeId.Value, "New application awaiting your approval",
                    $"{employee.Name} applied for {type} ({req.FromDate} to {req.ToDate}).", "Approval", app.Id);
            await _db.SaveChangesAsync();

            return Ok(new { success = true, id = app.Id, message = "Submitted — pending approval." });
        }

        static object ToDto(Application a) => new
        {
            id = a.Id,
            type = a.Type,
            leaveType = a.LeaveType?.Name,
            leaveTypeAlias = a.LeaveType?.Alias,
            fromDate = a.FromDate,
            toDate = a.ToDate,
            durationDays = a.DurationDays,
            dayPart = a.DayPart,
            requestedInTime = a.RequestedInTime?.ToString(@"hh\:mm"),
            requestedOutTime = a.RequestedOutTime?.ToString(@"hh\:mm"),
            reason = a.Reason,
            status = a.Status,
            pendingAt = a.PendingAt,
            remarks = a.Remarks,
            appliedOn = a.AppliedOn,
            decisionAt = a.DecisionAt,
        };

        async Task<IActionResult> MineCore(string type, int? year)
        {
            int y = year ?? DateTime.Today.Year;
            var rows = await _db.Applications.Include(a => a.LeaveType)
                .Where(a => a.EmployeeId == CurrentEmpId && a.Type == type
                    && string.Compare(a.FromDate, $"{y}-12-31") <= 0 && string.Compare(a.ToDate, $"{y}-01-01") >= 0)
                .OrderByDescending(a => a.FromDate).ToListAsync();
            return Ok(rows.Select(ToDto));
        }

        // ── Leave ──
        [HttpGet("leave-types")]
        public async Task<IActionResult> LeaveTypes()
            => Ok(await _db.LeaveTypes.OrderBy(t => t.Name)
                .Select(t => new { id = t.Id, name = t.Name, alias = t.Alias }).ToListAsync());

        [HttpPost("leave/apply")] public Task<IActionResult> ApplyLeave(ApplyRequest req) => ApplyCore("Leave", req);
        [HttpGet("leave/mine")] public Task<IActionResult> MyLeave(int? year) => MineCore("Leave", year);

        [HttpGet("leave/balance")]
        public async Task<IActionResult> LeaveBalance(int? year)
        {
            int y = year ?? DateTime.Today.Year;
            var employee = await _db.Employees
                .Include(e => e.LeavePolicy).ThenInclude(p => p!.Rules).ThenInclude(r => r.LeaveType)
                .FirstOrDefaultAsync(e => e.Id == CurrentEmpId);
            if (employee == null) return Ok(new { balances = Array.Empty<object>() });

            // All active leave types filtered by employee gender — so Male employees
            // don't see Maternity Leave, Female don't see Paternity Leave, etc.
            string empGender = employee.Gender ?? "Other";
            var allLeaveTypes = await _db.LeaveTypes
                .Where(lt => lt.IsActive && !lt.IsCompOff
                    && (lt.Gender == "All" || lt.Gender == empGender))
                .OrderBy(lt => lt.Name)
                .ToListAsync();

            var apps = await _db.Applications
                .Where(a => a.EmployeeId == CurrentEmpId && a.Type == "Leave"
                    && (a.Status == "Approved" || a.Status == "Pending"))
                .ToListAsync();

            // Build a quick lookup: LeaveTypeId → policy rule (null when no policy assigned)
            var rulesById = employee.LeavePolicy?.Rules
                .Where(r => r.LeaveType != null)
                .ToDictionary(r => r.LeaveTypeId) ?? new Dictionary<int, LeavePolicyRule>();

            var results = new List<object>();
            foreach (var lt in allLeaveTypes)
            {
                decimal accrued = 0;
                decimal entitlement = 0;
                string accrualMethod = "OnRequest";

                if (rulesById.TryGetValue(lt.Id, out var rule))
                {
                    entitlement = rule.AnnualEntitlementDays;
                    accrualMethod = rule.AccrualMethod;
                    var cycleStart = new DateTime(y, rule.CycleStartMonth, 1);
                    var cycleEnd = cycleStart.AddYears(1).AddDays(-1);
                    var effStart = cycleStart;
                    if (!string.IsNullOrWhiteSpace(employee.DOJ) && DateTime.TryParse(employee.DOJ, out var doj) && doj > effStart) effStart = doj;
                    var asOf = DateTime.Today < cycleEnd ? DateTime.Today : cycleEnd;
                    if (asOf >= effStart)
                    {
                        if (rule.AccrualMethod == "Monthly")
                        {
                            int monthsElapsed = Math.Max(0, Math.Min(12, (asOf.Year - effStart.Year) * 12 + (asOf.Month - effStart.Month) + 1));
                            accrued = Math.Min(monthsElapsed * (rule.MonthlyAccrualDays ?? 0), rule.AnnualEntitlementDays);
                        }
                        else accrued = rule.AnnualEntitlementDays;
                    }
                }

                // taken/pending across any cycle (simple: whole year of applications)
                var ltApps = apps.Where(a => a.LeaveTypeId == lt.Id).ToList();
                decimal taken = ltApps.Where(a => a.Status == "Approved").Sum(a => a.DurationDays);
                decimal pending = ltApps.Where(a => a.Status == "Pending").Sum(a => a.DurationDays);

                results.Add(new
                {
                    leaveType = lt.Name,
                    alias = lt.Alias,
                    entitlement,
                    accrualMethod,
                    accruedSoFar = Math.Round(accrued, 2),
                    taken,
                    pending,
                    balance = Math.Round(accrued - taken, 2),
                    inPolicy = rulesById.ContainsKey(lt.Id),
                });
            }
            return Ok(new { balances = results });
        }

        // ── Regularisation ──
        [HttpPost("regularisation/apply")] public Task<IActionResult> ApplyRegularisation(ApplyRequest req) => ApplyCore("Regularisation", req);
        [HttpGet("regularisation/mine")] public Task<IActionResult> MyRegularisation(int? year) => MineCore("Regularisation", year);

        // ── WFH ──
        [HttpPost("wfh/apply")] public Task<IActionResult> ApplyWfh(ApplyRequest req) => ApplyCore("WFH", req);
        [HttpGet("wfh/mine")] public Task<IActionResult> MyWfh(int? year) => MineCore("WFH", year);

        // ── CompOff Credits — employee's own credit Applications (pending + history) ──
        // Called by the mobile Leave tab so the employee can see which worked days are
        // awaiting HOD/manager approval and which have already been credited to their balance.
        [HttpGet("compoff/my-credits")]
        public async Task<IActionResult> MyCompOffCredits()
        {
            var rows = await _db.Applications
                .Where(a => a.EmployeeId == CurrentEmpId && a.Type == "CompOff")
                .OrderByDescending(a => a.FromDate)
                .Take(50)
                .ToListAsync();
            return Ok(rows.Select(a => new
            {
                id         = a.Id,
                date       = a.FromDate,
                days       = a.DurationDays,
                status     = a.Status,
                reason     = a.Reason,
                pendingAt  = a.PendingAt,
                remarks    = a.Remarks,
                appliedOn  = a.AppliedOn,
                decisionAt = a.DecisionAt,
            }));
        }
    }
}
