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
            // Same balances as the home screen: EL / CL / SL for the year plus this
            // month's Short Leave, Bereavement and Week Off Leave (Services/EmployeeLeaveSummary.cs)
            var items = await AmpmHrmsPro.Services.EmployeeLeaveSummary.BuildAsync(_db, CurrentEmpId, year);
            return Ok(new
            {
                balances = items.Select(b => new
                {
                    leaveType = b.Name,
                    alias = b.Alias,
                    entitlement = b.CarryForward + b.Earned,
                    accrualMethod = b.Period,
                    accruedSoFar = b.Earned,
                    carryForward = b.CarryForward,
                    taken = b.Consumed,
                    pending = b.Pending,
                    balance = b.Balance,
                    period = b.Period,
                    note = b.Note,
                    inPolicy = true,
                })
            });
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
