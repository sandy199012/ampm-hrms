using Microsoft.EntityFrameworkCore;
using AmpmHrmsPro.Data;
using AmpmHrmsPro.Models;

namespace AmpmHrmsPro.Services
{
    // ═══════════════════════════════════════════
    // OT LEDGER ENGINE — tracks and manages overtime earned by Worker-
    // category employees. Companion to OTEngine.cs (which handles the OT
    // report calculation from raw punches); this engine manages the
    // approval-workflow ledger.
    //
    //   TryAutoOTAsync  — called from AttendanceEngine.RecomputeDayAsync
    //                     after every recompute. Detects:
    //                     a) Worked on Holiday/Week-Off (POW status)
    //                     b) Worked beyond shift duration on normal day
    //                     Upserts an Auto OTLedger row idempotently.
    //
    //   ManualOTAsync   — Admin/HR manually logs OT for off-record cases.
    //   ApproveOTAsync  — Supervisor/Admin approves a Pending OT row.
    //   MarkPaidAsync   — Payroll marks an Approved (Pay-type) OT as Paid.
    // ═══════════════════════════════════════════
    public static class OTLedgerEngine
    {
        // ── Auto OT — called once per employee per attendance recompute ───
        // shiftDurationMinutes: expected work minutes for the day (shift
        // InTime→OutTime duration), used for shift-overshoot detection.
        // Pass null when shift info isn't available — only Holiday/WO OT
        // will be detected in that case.
        public static async Task TryAutoOTAsync(
            AppDbContext db,
            int employeeId,
            DateTime date,
            string effectiveStatus,
            bool isHoliday,
            bool isWeekOff,
            int? workedMinutes,
            int? shiftDurationMinutes)
        {
            string dateStr = date.ToString("yyyy-MM-dd");
            var employee = await db.Employees.Include(e => e.OTRule)
                .FirstOrDefaultAsync(e => e.Id == employeeId);
            var rule = employee?.OTRule;

            if (rule == null || !rule.IsActive)
            {
                await CancelStaleAutoOTAsync(db, employeeId, dateStr, "no active OT rule");
                return;
            }

            int worked = workedMinutes ?? 0;
            bool isPOW = effectiveStatus.StartsWith("POW");

            int rawExtra = 0;
            string otKind = "Shift";

            if (isPOW && !rule.IsRetailRule &&
                ((isHoliday && rule.CountHolidays) || (isWeekOff && rule.CountWeekOffs)))
            {
                // Non-retail: worked on an off-day → entire worked time is OT
                rawExtra = worked;
                otKind   = isHoliday ? "Holiday" : "WeekOff";
            }
            else if ((!isPOW || rule.IsRetailRule) && rule.CountAfterShiftHours
                     && shiftDurationMinutes.HasValue && worked > shiftDurationMinutes.Value)
            {
                // Regular workday (or retail employee on any day): OT = worked beyond shift/9h duration
                rawExtra = worked - shiftDurationMinutes.Value;
                otKind   = "Shift";
            }

            // Check minimum threshold against raw extra minutes
            if (rawExtra < rule.MinOTMinutesPerDay)
            {
                await CancelStaleAutoOTAsync(db, employeeId, dateStr, "OT below minimum threshold");
                return;
            }

            // Apply slab rounding if enabled (31–45 → 30 min, 46–75 → 60 min, etc.)
            int otMinutes = (rule.UseSlabRounding && otKind == "Shift")
                ? ApplySlabRounding(rawExtra)
                : rawExtra;

            if (rule.MaxOTMinutesPerDay.HasValue)
                otMinutes = Math.Min(otMinutes, rule.MaxOTMinutesPerDay.Value);

            // Upsert
            var existing = await db.OTLedgers.FirstOrDefaultAsync(
                l => l.EmployeeId == employeeId && l.Date == dateStr && l.Source == "Auto");

            if (existing != null)
            {
                if (existing.OTMinutes == otMinutes && existing.OTKind == otKind) return;
                existing.OTMinutes = otMinutes;
                existing.OTKind    = otKind;
                existing.OTRuleId  = rule.Id;
                await db.SaveChangesAsync();
            }
            else
            {
                db.OTLedgers.Add(new OTLedger
                {
                    EmployeeId = employeeId,
                    OTRuleId   = rule.Id,
                    Date       = dateStr,
                    OTMinutes  = otMinutes,
                    OTKind     = otKind,
                    OTType     = rule.OTType,
                    Source     = "Auto",
                    Status     = "Pending",
                    Remarks    = $"Auto: {otKind} OT on {dateStr}",
                });
                try
                {
                    await db.SaveChangesAsync();
                }
                catch (DbUpdateException)
                {
                    // Concurrent recompute hit the unique index — other call
                    // already inserted this row, no-op here.
                    var addedEntry = db.ChangeTracker.Entries<OTLedger>()
                        .FirstOrDefault(e => e.State == EntityState.Added);
                    if (addedEntry != null) addedEntry.State = EntityState.Detached;
                    bool isDuplicate = await db.OTLedgers.AnyAsync(
                        l => l.EmployeeId == employeeId && l.Date == dateStr && l.Source == "Auto");
                    if (!isDuplicate) throw;
                }
            }
        }

        // ── Manual OT ────────────────────────────────────────────────────
        public static async Task ManualOTAsync(
            AppDbContext db, int employeeId, int? ruleId, string date,
            int otMinutes, string otKind, string? remarks, int createdByEmployeeId)
        {
            var rule = ruleId.HasValue
                ? await db.OTRules.FindAsync(ruleId.Value)
                : (await db.Employees.Include(e => e.OTRule)
                                     .FirstOrDefaultAsync(e => e.Id == employeeId))?.OTRule;
            db.OTLedgers.Add(new OTLedger
            {
                EmployeeId          = employeeId,
                OTRuleId            = rule?.Id,
                Date                = date,
                OTMinutes           = otMinutes,
                OTKind              = otKind,
                OTType              = rule?.OTType ?? "Pay",
                Source              = "Manual",
                Status              = "Pending",
                Remarks             = remarks,
                CreatedByEmployeeId = createdByEmployeeId,
            });
            await db.SaveChangesAsync();
        }

        // ── Approve ───────────────────────────────────────────────────────
        public static async Task<bool> ApproveOTAsync(AppDbContext db, int ledgerId, string? remarks)
        {
            var l = await db.OTLedgers.FindAsync(ledgerId);
            if (l == null || l.Status != "Pending") return false;
            l.Status  = "Approved";
            if (!string.IsNullOrWhiteSpace(remarks))
                l.Remarks = (l.Remarks ?? "") + " | " + remarks;
            await db.SaveChangesAsync();
            return true;
        }

        // ── Mark Paid (for Pay / Both type) ───────────────────────────────
        public static async Task<bool> MarkPaidAsync(AppDbContext db, int ledgerId)
        {
            var l = await db.OTLedgers.FindAsync(ledgerId);
            if (l == null || l.Status != "Approved") return false;
            l.Status = "Paid";
            await db.SaveChangesAsync();
            return true;
        }

        // ── Read helpers ──────────────────────────────────────────────────
        public static async Task<List<OTLedger>> GetLedgerAsync(AppDbContext db, int employeeId)
            => await db.OTLedgers.Include(l => l.OTRule)
                .Where(l => l.EmployeeId == employeeId)
                .OrderByDescending(l => l.Date).ToListAsync();

        public static string FormatOTMinutes(int minutes)
        {
            int h = minutes / 60, m = minutes % 60;
            return m == 0 ? $"{h}h" : $"{h}h {m}m";
        }

        // ── Slab rounding helper ──────────────────────────────────────────
        // Rounds extra minutes to the company's 30-min OT slabs:
        //   ≤ 30 min → 0   |  31–45 → 30  |  46–75 → 60  |  76–105 → 90 …
        static int ApplySlabRounding(int extraMinutes)
            => extraMinutes > 30 ? ((extraMinutes + 14) / 30) * 30 : 0;

        // ── Private ───────────────────────────────────────────────────────
        static async Task CancelStaleAutoOTAsync(AppDbContext db, int employeeId, string dateStr, string reason)
        {
            var stale = await db.OTLedgers.FirstOrDefaultAsync(
                l => l.EmployeeId == employeeId && l.Date == dateStr
                     && l.Source == "Auto" && l.Status == "Pending");
            if (stale == null) return;
            stale.Status  = "Cancelled";
            stale.Remarks = (stale.Remarks ?? "") + $" [auto-cancelled: {reason}]";
            await db.SaveChangesAsync();
        }
    }
}
