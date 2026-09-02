using Microsoft.EntityFrameworkCore;
using AmpmHrmsPro.Data;
using AmpmHrmsPro.Models;

namespace AmpmHrmsPro.Services
{
    // ═══════════════════════════════════════════
    // COMP-OFF ENGINE — earns, expires, and consumes compensatory-off
    // credits. Three entry points other code actually calls:
    //
    //   TryAutoCreditAsync   — called from AttendanceEngine.RecomputeDayAsync
    //                          after every recompute, for every employee.
    //                          Detects "worked a qualifying off-day" and
    //                          upserts an Auto ledger row idempotently.
    //   TryConsumeAsync       — called when a Comp-Off leave Application is
    //                          approved. Debits FIFO (earliest-expiring
    //                          first) across Available ledger rows; fails
    //                          (and leaves everything untouched) if the
    //                          balance can't cover it.
    //   RefundAsync           — called when a previously-approved Comp-Off
    //                          leave Application is rejected/revoked.
    //                          Reverses exactly what that application
    //                          consumed, no more.
    //
    // GetAvailableBalance / GetLedger are the read-side helpers the Admin
    // and self-service ledger screens use. Every one of these sweeps
    // expiry first (SweepAsync) so Status never needs to be trusted stale.
    // ═══════════════════════════════════════════
    public static class CompOffEngine
    {
        // Recomputes Status for every non-Cancelled ledger row belonging to
        // one employee, purely from EarnedDays/UsedDays/ExpiryDate — this is
        // the single source of truth for Status, so nothing else in this
        // engine (or any controller) should ever set Status directly other
        // than "Cancelled" (a manual admin action).
        public static async Task SweepAsync(AppDbContext db, int employeeId)
        {
            string today = DateTime.Today.ToString("yyyy-MM-dd");
            var rows = await db.CompOffLedgers
                .Where(l => l.EmployeeId == employeeId && l.Status != "Cancelled")
                .ToListAsync();
            bool changed = false;
            foreach (var l in rows)
            {
                string next = l.EarnedDays - l.UsedDays <= 0 ? "Used"
                    : string.Compare(l.ExpiryDate, today) < 0 ? "Expired"
                    : "Available";
                if (l.Status != next) { l.Status = next; changed = true; }
            }
            if (changed) await db.SaveChangesAsync();
        }

        public static async Task<decimal> GetAvailableBalanceAsync(AppDbContext db, int employeeId)
        {
            await SweepAsync(db, employeeId);
            return await db.CompOffLedgers
                .Where(l => l.EmployeeId == employeeId && l.Status == "Available")
                .SumAsync(l => (decimal?)(l.EarnedDays - l.UsedDays)) ?? 0m;
        }

        // ── Auto-credit — called once per employee per attendance recompute ──
        // isHoliday/isWeekOff/workedMinutes are the SAME values
        // AttendanceEngine just computed for this day (effective, i.e.
        // post-Regularisation where applicable), passed in rather than
        // recomputed here so this never disagrees with what the Attendance
        // Register itself shows for the day.
        //
        // APPROVAL FLOW: instead of crediting the ledger directly, this
        // creates a Pending Application (Type="CompOff") routed to the
        // employee's reporting manager (HOD) for approval. Only when that
        // Application is Approved does the CompOffLedger row get created.
        // This keeps the same idempotency guarantee — recomputing the same
        // day multiple times will not duplicate the Application.
        public static async Task TryAutoCreditAsync(AppDbContext db, int employeeId, DateTime date,
            string effectiveStatus, bool isHoliday, bool isWeekOff, int? workedMinutes)
        {
            string dateStr = date.ToString("yyyy-MM-dd");
            var employee = await db.Employees
                .Include(e => e.CompOffRule)
                .Include(e => e.ReportingManager)
                .FirstOrDefaultAsync(e => e.Id == employeeId);
            var rule = employee?.CompOffRule;

            // "POW" (Present on Week-Off/Holiday) is AttendanceEngine's own
            // marker for "worked despite being a scheduled off-day".
            // If no CompOffRule is assigned but the employee is Staff (not Worker),
            // we still qualify using system defaults so the HOD can review the credit.
            bool isWorker = (employee?.Category ?? "").Equals("Worker", StringComparison.OrdinalIgnoreCase);
            bool qualifyingDay = effectiveStatus.StartsWith("POW")
                && (rule != null
                    ? (rule.IsActive && rule.AutoCredit
                        && ((isHoliday && rule.CountHolidays) || (isWeekOff && rule.CountWeekOffs)))
                    : !isWorker); // Staff with no rule: qualify on any worked holiday/week-off

            // Idempotency: one CompOff Application per employee per worked date.
            var existingApp = await db.Applications.FirstOrDefaultAsync(
                a => a.EmployeeId == employeeId && a.Type == "CompOff" && a.FromDate == dateStr);

            if (!qualifyingDay)
            {
                // Day no longer qualifies — cancel a Pending Application (it
                // was never approved, so nothing was credited yet).
                // If already Approved, cancel the ledger entry if unconsumed.
                if (existingApp != null)
                {
                    if (existingApp.Status == "Pending")
                    {
                        existingApp.Status = "Cancelled";
                        existingApp.Remarks = "[auto-cancelled: day no longer qualifies]";
                        await db.SaveChangesAsync();
                    }
                    else if (existingApp.Status == "Approved")
                    {
                        var ledger = await db.CompOffLedgers.FirstOrDefaultAsync(
                            l => l.EmployeeId == employeeId && l.EarnedDate == dateStr && l.Source == "Auto");
                        if (ledger != null && ledger.UsedDays == 0 && ledger.Status != "Cancelled")
                        {
                            ledger.Status = "Cancelled";
                            ledger.Remarks = (ledger.Remarks ?? "") + " [auto-cancelled: day no longer qualifies]";
                            await db.SaveChangesAsync();
                        }
                    }
                }
                return;
            }

            decimal hours = (workedMinutes ?? 0) / 60m;
            // Use rule thresholds when assigned; fall back to 8 h = full day / 4 h = half day.
            decimal earnedDays = rule != null
                ? (hours >= rule.MinHoursForFullDay ? 1m
                    : (rule.MinHoursForHalfDay > 0 && hours >= rule.MinHoursForHalfDay) ? 0.5m
                    : 0m)
                : (hours >= 8m ? 1m : hours >= 4m ? 0.5m : 0m);

            if (earnedDays <= 0)
            {
                // Hours below threshold — cancel or shrink existing Application.
                if (existingApp != null && existingApp.Status == "Pending")
                {
                    existingApp.Status = "Cancelled";
                    existingApp.Remarks = "[auto-cancelled: hours worked below rule's threshold]";
                    await db.SaveChangesAsync();
                }
                return;
            }

            // Optional cap — check against current open ledger balance (skip when no rule).
            if (rule?.MaxOpenBalance.HasValue == true)
            {
                decimal openBalance = await GetAvailableBalanceAsync(db, employeeId);
                if (openBalance >= rule.MaxOpenBalance.Value)
                {
                    if (existingApp != null && existingApp.Status == "Pending")
                    {
                        existingApp.Status = "Cancelled";
                        existingApp.Remarks = "[auto-cancelled: max open balance reached]";
                        await db.SaveChangesAsync();
                    }
                    return;
                }
                decimal headroom = rule.MaxOpenBalance.Value - openBalance;
                earnedDays = Math.Min(earnedDays, headroom);
            }

            if (existingApp != null)
            {
                // Update only if the Pending Application's duration changed.
                if (existingApp.Status == "Pending" && existingApp.DurationDays != earnedDays)
                {
                    existingApp.DurationDays = earnedDays;
                    await db.SaveChangesAsync();
                }
                // If already Approved/Rejected/Cancelled: don't touch it.
                return;
            }

            // Create a new Pending Application — routed to the reporting manager (HOD).
            string offDayType = isHoliday ? "Holiday" : "Week-Off";
            db.Applications.Add(new Application
            {
                EmployeeId     = employeeId,
                Type           = "CompOff",
                FromDate       = dateStr,
                ToDate         = dateStr,
                DurationDays   = earnedDays,
                DayPart        = "Single",
                Reason         = $"Worked on {offDayType} ({dateStr}) — {earnedDays:0.#} day(s) comp-off due.",
                Status         = "Pending",
                AppliedOn      = DateTime.Now,
                CreatedAt      = DateTime.Now,
                ApproverEmployeeId = employee!.ReportingManagerId,
                PendingAt          = employee.ReportingManager?.Name ?? "HR",
            });
            await db.SaveChangesAsync();

            // Notify the approver.
            if (employee.ReportingManagerId.HasValue)
                NotificationHelper.Notify(db, employee.ReportingManagerId.Value,
                    "Comp-Off credit request awaiting approval",
                    $"{employee.Name} worked on {offDayType} ({dateStr}) — {earnedDays:0.#} day(s) comp-off pending approval.",
                    "Approval", 0);
            await db.SaveChangesAsync();
        }

        // ── Called from ApplicationsController.Approve when Type="CompOff" ──
        // Creates the actual CompOffLedger entry now that a manager approved it.
        public static async Task ApproveCompOffCreditAsync(AppDbContext db, Application app)
        {
            if (!DateTime.TryParse(app.FromDate, out var earnedDate)) return;

            // Idempotent: skip if a ledger row already exists for this date.
            bool alreadyExists = await db.CompOffLedgers.AnyAsync(
                l => l.EmployeeId == app.EmployeeId && l.EarnedDate == app.FromDate && l.Source == "Auto");
            if (alreadyExists) return;

            var rule = await db.Employees
                .Where(e => e.Id == app.EmployeeId)
                .Select(e => e.CompOffRule)
                .FirstOrDefaultAsync();

            int expiryDays = rule?.ExpiryDays ?? 90;

            db.CompOffLedgers.Add(new CompOffLedger
            {
                EmployeeId    = app.EmployeeId,
                CompOffRuleId = rule?.Id,
                EarnedDate    = app.FromDate,
                EarnedDays    = app.DurationDays,
                Source        = "Auto",
                Status        = "Available",
                ExpiryDate    = earnedDate.AddDays(expiryDays).ToString("yyyy-MM-dd"),
                Remarks       = $"Approved comp-off for {app.FromDate} ({app.DurationDays:0.#} day(s)). Approved by manager.",
            });
            await db.SaveChangesAsync();
        }

        // ── Called from ApplicationsController.Revoke when Type="CompOff" ──
        // Cancels the CompOffLedger entry created at approval time, if unconsumed.
        public static async Task RevokeCompOffCreditAsync(AppDbContext db, Application app)
        {
            var ledger = await db.CompOffLedgers.FirstOrDefaultAsync(
                l => l.EmployeeId == app.EmployeeId && l.EarnedDate == app.FromDate && l.Source == "Auto");
            if (ledger == null || ledger.Status == "Cancelled") return;

            if (ledger.UsedDays == 0)
            {
                ledger.Status = "Cancelled";
                ledger.Remarks = (ledger.Remarks ?? "") + " [revoked by manager]";
            }
            else
            {
                // Partially consumed — clamp EarnedDays to what's been used
                // so no extra balance floats (same pattern as the threshold cancellation above).
                ledger.EarnedDays = ledger.UsedDays;
                ledger.Remarks = (ledger.Remarks ?? "") + " [revoked: partial consumption preserved]";
            }
            await db.SaveChangesAsync();
            await SweepAsync(db, app.EmployeeId);
        }

        // ── Manual credit — Admin/HR logging an off-attendance-record instance ──
        public static async Task ManualCreditAsync(AppDbContext db, int employeeId, int? ruleId,
            string earnedDate, decimal earnedDays, string? remarks, int createdByEmployeeId)
        {
            var rule = ruleId.HasValue ? await db.CompOffRules.FindAsync(ruleId.Value) : null;
            int expiryDays = rule?.ExpiryDays ?? 90;
            DateTime.TryParse(earnedDate, out var earned);

            db.CompOffLedgers.Add(new CompOffLedger
            {
                EmployeeId = employeeId,
                CompOffRuleId = ruleId,
                EarnedDate = earnedDate,
                EarnedDays = earnedDays,
                Source = "Manual",
                Status = "Available",
                ExpiryDate = (earned == default ? DateTime.Today : earned).AddDays(expiryDays).ToString("yyyy-MM-dd"),
                Remarks = remarks,
                CreatedByEmployeeId = createdByEmployeeId,
            });
            await db.SaveChangesAsync();
        }

        // ── Consumption — Comp-Off leave application approved ──
        public static async Task<(bool Success, string Message)> TryConsumeAsync(AppDbContext db, int employeeId, int applicationId, decimal days)
        {
            // Already consumed for this application (e.g. Approve clicked
            // twice) — treat as already-succeeded rather than double-debiting.
            if (await db.CompOffConsumptions.AnyAsync(c => c.ApplicationId == applicationId))
                return (true, "Already consumed.");

            await SweepAsync(db, employeeId);
            var available = await db.CompOffLedgers
                .Where(l => l.EmployeeId == employeeId && l.Status == "Available")
                .OrderBy(l => l.ExpiryDate) // FIFO — earliest-expiring credit spent first
                .ToListAsync();

            decimal totalAvailable = available.Sum(l => l.EarnedDays - l.UsedDays);
            if (totalAvailable < days)
                return (false, $"Insufficient Comp-Off balance — available {totalAvailable:0.#} day(s), requested {days:0.#}.");

            decimal remaining = days;
            foreach (var l in available)
            {
                if (remaining <= 0) break;
                decimal rowBalance = l.EarnedDays - l.UsedDays;
                decimal take = Math.Min(rowBalance, remaining);
                l.UsedDays += take;
                remaining -= take;
                db.CompOffConsumptions.Add(new CompOffConsumption
                {
                    ApplicationId = applicationId,
                    CompOffLedgerId = l.Id,
                    DaysConsumed = take,
                });
            }
            await db.SaveChangesAsync();
            await SweepAsync(db, employeeId);
            return (true, "Consumed.");
        }

        // ── Refund — application rejected/revoked after having consumed ──
        public static async Task RefundAsync(AppDbContext db, int applicationId)
        {
            var consumptions = await db.CompOffConsumptions.Include(c => c.CompOffLedger)
                .Where(c => c.ApplicationId == applicationId).ToListAsync();
            if (!consumptions.Any()) return; // nothing was ever consumed for this application — no-op

            int? employeeId = null;
            foreach (var c in consumptions)
            {
                if (c.CompOffLedger != null)
                {
                    c.CompOffLedger.UsedDays = Math.Max(0, c.CompOffLedger.UsedDays - c.DaysConsumed);
                    employeeId = c.CompOffLedger.EmployeeId;
                }
            }
            db.CompOffConsumptions.RemoveRange(consumptions);
            await db.SaveChangesAsync();
            if (employeeId.HasValue) await SweepAsync(db, employeeId.Value);
        }

        public static async Task<List<CompOffLedger>> GetLedgerAsync(AppDbContext db, int employeeId)
        {
            await SweepAsync(db, employeeId);
            return await db.CompOffLedgers.Where(l => l.EmployeeId == employeeId)
                .OrderByDescending(l => l.EarnedDate).ToListAsync();
        }
    }
}
