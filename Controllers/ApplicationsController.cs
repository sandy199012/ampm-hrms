using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using AmpmHrmsPro.Data;
using AmpmHrmsPro.Models;
using AmpmHrmsPro.Services;

namespace AmpmHrmsPro.Controllers
{
    // ═══════════════════════════════════════════
    // APPLICATIONS — Leave, Regularisation, WFH and On-Duty requests, all
    // through the shared Application table (see Models/Application.cs).
    // Approving/rejecting/revoking one immediately triggers an
    // AttendanceEngine recompute for the affected date range, since the
    // Attendance Register's EffectiveStatus depends on this. Managed from
    // the Admin/HR side for now — this project doesn't yet have a separate
    // employee self-service login area, so applications are logged here on
    // an employee's behalf (matches how the target report's historical
    // application data would need to be loaded regardless).
    // ═══════════════════════════════════════════
    [Authorize(Roles = "admin,hr")]
    public class ApplicationsController : Controller
    {
        readonly AppDbContext _db;
        public ApplicationsController(AppDbContext db) => _db = db;

        int CurrentEmpId => int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);

        public IActionResult Index(string? status, string? type, int? employeeId)
        {
            var q = _db.Applications.Include(a => a.Employee).Include(a => a.LeaveType).Include(a => a.Approver).AsQueryable();
            if (!string.IsNullOrWhiteSpace(status)) q = q.Where(a => a.Status == status);
            if (!string.IsNullOrWhiteSpace(type)) q = q.Where(a => a.Type == type);
            if (employeeId.HasValue) q = q.Where(a => a.EmployeeId == employeeId);

            ViewBag.Status = status; ViewBag.Type = type; ViewBag.EmployeeId = employeeId;
            ViewBag.EmployeeList = _db.Employees.Where(e => e.IsActive).OrderBy(e => e.Name).ToList();
            ViewBag.PendingCount = _db.Applications.Count(a => a.Status == "Pending");
            return View(q.OrderByDescending(a => a.AppliedOn).Take(500).ToList());
        }

        public IActionResult Apply(int? employeeId)
        {
            LoadDropdowns();
            var model = new Application { EmployeeId = employeeId ?? 0, FromDate = DateTime.Today.ToString("yyyy-MM-dd"), ToDate = DateTime.Today.ToString("yyyy-MM-dd") };
            return View(model);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult Apply(Application model)
        {
            if (model.EmployeeId <= 0) { TempData["Error"] = "Select an employee."; return RedirectToAction("Apply"); }
            if (string.Compare(model.FromDate, model.ToDate) > 0) { TempData["Error"] = "From Date can't be after To Date."; return RedirectToAction("Apply"); }

            if (model.Type == "Leave" && model.LeaveTypeId.HasValue)
            {
                var lt = _db.LeaveTypes.Find(model.LeaveTypeId.Value);
                if (lt != null && lt.IsCompOff)
                {
                    decimal available = CompOffEngine.GetAvailableBalanceAsync(_db, model.EmployeeId).GetAwaiter().GetResult();
                    if (available < model.DurationDays)
                    {
                        TempData["Error"] = $"Insufficient Comp-Off balance — available {available:0.#} day(s), requested {model.DurationDays:0.#}.";
                        return RedirectToAction("Apply");
                    }
                }
            }

            model.Id = 0;
            model.Status = "Pending";
            model.AppliedOn = DateTime.Now;
            model.CreatedAt = DateTime.Now;

            var employee = _db.Employees.Include(e => e.ReportingManager).FirstOrDefault(e => e.Id == model.EmployeeId);
            model.ApproverEmployeeId = employee?.ReportingManagerId;
            model.PendingAt = employee?.ReportingManager?.Name ?? "HR";

            if (model.Type != "Leave") model.LeaveTypeId = null;
            if (model.Type != "Regularisation") { model.RequestedInTime = null; model.RequestedOutTime = null; }

            _db.Applications.Add(model);
            _db.SaveChanges();

            if (model.ApproverEmployeeId.HasValue)
                NotificationHelper.Notify(_db, model.ApproverEmployeeId.Value, "New application awaiting your approval",
                    $"{employee?.Name} applied for {model.Type} ({model.FromDate} to {model.ToDate}).", "Approval", model.Id);
            _db.SaveChanges();

            TempData["Success"] = "Application submitted — Pending approval.";
            return RedirectToAction("Index");
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Approve(int id, string? remarks)
        {
            var app = await _db.Applications.Include(a => a.LeaveType).FirstOrDefaultAsync(a => a.Id == id);
            if (app == null) return NotFound();
            if (app.Status == "Approved") { TempData["Success"] = "Already approved."; return RedirectToAction("Index"); }

            // ── CompOff Credit Approval ─────────────────────────────────────
            // Type="CompOff" is an earned-comp-off grant request (generated
            // when attendance shows the employee worked a qualifying off-day).
            // Approving it creates the CompOffLedger entry that makes the
            // credit actually available for the employee to use.
            if (app.Type == "CompOff")
            {
                app.Status = "Approved";
                app.Remarks = remarks;
                app.DecisionAt = DateTime.Now;
                app.DecisionByEmployeeId = CurrentEmpId;
                app.PendingAt = null;
                await CompOffEngine.ApproveCompOffCreditAsync(_db, app);
                NotificationHelper.Notify(_db, app.EmployeeId,
                    "Comp-Off credit approved",
                    $"Your comp-off credit of {app.DurationDays:0.#} day(s) for {app.FromDate} has been approved and added to your balance.",
                    "Approval", app.Id);
                await _db.SaveChangesAsync();
                TempData["Success"] = "Comp-Off credit approved and added to employee's balance.";
                return RedirectToAction("Index");
            }

            // ── Leave / Regularisation / WFH / OD Approval ─────────────────
            // Comp-Off leave CONSUMPTION debits the ledger and status flip
            // together, in one transaction. Wrap in CreateExecutionStrategy
            // so SqlServerRetryingExecutionStrategy can retry on transient errors.
            string? approveError = null;
            await _db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                await using var tx = await _db.Database.BeginTransactionAsync();

                if (app.Type == "Leave" && app.LeaveType?.IsCompOff == true)
                {
                    // Comp-Off ledger uses 0.5-day granularity; round to nearest 0.5.
                    decimal days = Math.Round(app.DurationDays * 2m, MidpointRounding.AwayFromZero) / 2m;
                    var (ok, message) = await CompOffEngine.TryConsumeAsync(_db, app.EmployeeId, app.Id, days);
                    if (!ok) { await tx.RollbackAsync(); approveError = message; return; }
                }

                app.Status = "Approved";
                app.Remarks = remarks;
                app.DecisionAt = DateTime.Now;
                app.DecisionByEmployeeId = CurrentEmpId;
                app.PendingAt = null;
                NotificationHelper.Notify(_db, app.EmployeeId, $"{app.Type} application approved", remarks, "Approval", app.Id);

                // Deduct from LeaveBalance (non-CompOff leave only).
                if (app.Type == "Leave" && app.LeaveType?.IsCompOff == false && !string.IsNullOrEmpty(app.LeaveType?.Alias))
                {
                    await LeaveBalanceEngine.ConsumeAsync(
                        _db, app.EmployeeId,
                        app.LeaveType.Alias,
                        app.FromDate, app.ToDate,
                        app.DurationDays);
                }

                await _db.SaveChangesAsync();
                await tx.CommitAsync();
            });

            if (approveError != null) { TempData["Error"] = approveError; return RedirectToAction("Index"); }

            await AttendanceEngine.RecomputeRangeAsync(_db, app.EmployeeId, DateTime.Parse(app.FromDate), DateTime.Parse(app.ToDate));
            TempData["Success"] = "Application approved and attendance recomputed.";
            return RedirectToAction("Index");
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Reject(int id, string? remarks)
        {
            var app = await _db.Applications.Include(a => a.LeaveType).FirstOrDefaultAsync(a => a.Id == id);
            if (app == null) return NotFound();

            bool wasApproved = app.Status == "Approved";
            app.Status = "Rejected";
            app.Remarks = remarks;
            app.DecisionAt = DateTime.Now;
            app.DecisionByEmployeeId = CurrentEmpId;
            app.PendingAt = null;
            NotificationHelper.Notify(_db, app.EmployeeId, $"{app.Type} application rejected", remarks, "Approval", app.Id);
            _db.SaveChanges();

            // CompOff Credit rejection — if it was already approved (and a
            // ledger entry exists), revoke that credit entry too.
            if (app.Type == "CompOff")
            {
                if (wasApproved) await CompOffEngine.RevokeCompOffCreditAsync(_db, app);
                TempData["Success"] = "Comp-Off credit request rejected.";
                return RedirectToAction("Index");
            }

            // Normal leave — refund CompOff ledger (if was a Comp-Off leave)
            // and LeaveBalance consumed columns (if was a regular leave).
            await CompOffEngine.RefundAsync(_db, app.Id);
            if (wasApproved && app.Type == "Leave" && app.LeaveType?.IsCompOff == false && !string.IsNullOrEmpty(app.LeaveType?.Alias))
            {
                await LeaveBalanceEngine.RefundAsync(
                    _db, app.EmployeeId,
                    app.LeaveType.Alias,
                    app.FromDate, app.ToDate,
                    app.DurationDays);
                await _db.SaveChangesAsync();
            }

            await AttendanceEngine.RecomputeRangeAsync(_db, app.EmployeeId, DateTime.Parse(app.FromDate), DateTime.Parse(app.ToDate));
            TempData["Success"] = "Application rejected.";
            return RedirectToAction("Index");
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Revoke(int id, string? remarks)
        {
            var app = await _db.Applications.Include(a => a.LeaveType).FirstOrDefaultAsync(a => a.Id == id);
            if (app == null) return NotFound();

            app.Status = "Revoked";
            app.Remarks = remarks;
            app.DecisionAt = DateTime.Now;
            app.DecisionByEmployeeId = CurrentEmpId;
            app.PendingAt = null;
            NotificationHelper.Notify(_db, app.EmployeeId, $"{app.Type} application revoked", remarks, "Approval", app.Id);
            _db.SaveChanges();

            // CompOff Credit revocation — cancel the ledger entry that was
            // created at approval time (no-op if not yet consumed).
            if (app.Type == "CompOff")
            {
                await CompOffEngine.RevokeCompOffCreditAsync(_db, app);
                TempData["Success"] = "Comp-Off credit revoked.";
                return RedirectToAction("Index");
            }

            // Normal leave — refund CompOff ledger and LeaveBalance columns.
            await CompOffEngine.RefundAsync(_db, app.Id);
            if (app.Type == "Leave" && app.LeaveType?.IsCompOff == false && !string.IsNullOrEmpty(app.LeaveType?.Alias))
            {
                await LeaveBalanceEngine.RefundAsync(
                    _db, app.EmployeeId,
                    app.LeaveType.Alias,
                    app.FromDate, app.ToDate,
                    app.DurationDays);
                await _db.SaveChangesAsync();
            }

            await AttendanceEngine.RecomputeRangeAsync(_db, app.EmployeeId, DateTime.Parse(app.FromDate), DateTime.Parse(app.ToDate));
            TempData["Success"] = "Application revoked.";
            return RedirectToAction("Index");
        }

        void LoadDropdowns()
        {
            ViewBag.EmployeeList = _db.Employees.Where(e => e.IsActive).OrderBy(e => e.Name).ToList();
            ViewBag.LeaveTypeList = _db.LeaveTypes.Where(t => t.IsActive).OrderBy(t => t.Name).ToList();
        }
    }
}
