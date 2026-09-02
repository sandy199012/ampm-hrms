using AmpmHrmsPro.Data;
using AmpmHrmsPro.Models;
using Microsoft.EntityFrameworkCore;

namespace AmpmHrmsPro.Services
{
    // ═══════════════════════════════════════════
    // LEAVE BALANCE ENGINE — debits / credits the LeaveBalance table when
    // a leave application is Approved, Rejected (after prior approval), or
    // Revoked.  Does NOT call SaveChanges — callers must save so this can
    // participate in their existing EF transaction.
    // ═══════════════════════════════════════════
    public static class LeaveBalanceEngine
    {
        /// <summary>
        /// Deduct <paramref name="durationDays"/> from the relevant
        /// ConsumedXxx column(s) in LeaveBalance, split proportionally
        /// across calendar months that fall inside [fromDate, toDate].
        /// Does NOT call SaveChanges.
        /// </summary>
        public static async Task ConsumeAsync(
            AppDbContext db,
            int employeeId,
            string leaveTypeAlias,
            string fromDateStr,
            string toDateStr,
            decimal durationDays)
        {
            await AdjustAsync(db, employeeId, leaveTypeAlias, fromDateStr, toDateStr, durationDays, +1);
        }

        /// <summary>
        /// Refund (reverse) a previously consumed leave — mirrors ConsumeAsync
        /// with the sign flipped.  Safe to call even if nothing was consumed
        /// (balance simply will not go negative).
        /// Does NOT call SaveChanges.
        /// </summary>
        public static async Task RefundAsync(
            AppDbContext db,
            int employeeId,
            string leaveTypeAlias,
            string fromDateStr,
            string toDateStr,
            decimal durationDays)
        {
            await AdjustAsync(db, employeeId, leaveTypeAlias, fromDateStr, toDateStr, durationDays, -1);
        }

        // ──────────────────────────────────────────────────────────────────
        // internals
        // ──────────────────────────────────────────────────────────────────

        static async Task AdjustAsync(
            AppDbContext db,
            int employeeId,
            string leaveTypeAlias,
            string fromDateStr,
            string toDateStr,
            decimal durationDays,
            int sign)   // +1 = consume, -1 = refund
        {
            if (durationDays <= 0) return;
            if (!DateTime.TryParse(fromDateStr, out var from)) return;
            if (!DateTime.TryParse(toDateStr,   out var to))   return;
            if (from > to) return;

            // Split total days proportionally across (year, month) buckets.
            var buckets = SplitByMonth(from, to, durationDays);

            foreach (var (year, month, days) in buckets)
            {
                var row = await db.LeaveBalances
                    .FirstOrDefaultAsync(b =>
                        b.EmployeeId    == employeeId &&
                        b.LeaveTypeCode == leaveTypeAlias &&
                        b.Year          == year);

                if (row == null) continue;   // no row to credit — nothing to do

                AdjustMonth(row, month, sign * days);
                row.UpdatedAt = DateTime.Now;
            }
        }

        /// <summary>
        /// Split <paramref name="totalDays"/> proportionally across calendar
        /// months between <paramref name="from"/> and <paramref name="to"/>.
        /// Uses the ratio of calendar days per month so that (e.g.) a 5-day
        /// leave spanning 3 days in Jan and 7 days in Feb distributes
        /// 5 × 3/10 in Jan and 5 × 7/10 in Feb.
        /// </summary>
        static List<(int Year, int Month, decimal Days)> SplitByMonth(
            DateTime from, DateTime to, decimal totalDays)
        {
            var result = new List<(int, int, decimal)>();

            // Collect calendar-day counts per (year, month) segment
            var segments = new List<(int Year, int Month, int CalendarDays)>();
            var cursor = new DateTime(from.Year, from.Month, 1);
            while (cursor <= to)
            {
                var segStart = cursor > from ? cursor : from;
                var segEnd   = new DateTime(cursor.Year, cursor.Month,
                                   DateTime.DaysInMonth(cursor.Year, cursor.Month));
                if (segEnd > to) segEnd = to;

                int calDays = (int)(segEnd - segStart).TotalDays + 1;
                segments.Add((cursor.Year, cursor.Month, calDays));

                cursor = cursor.AddMonths(1);
            }

            int totalCal = segments.Sum(s => s.CalendarDays);
            if (totalCal == 0) return result;

            // Distribute totalDays proportionally; last bucket gets remainder
            // to avoid rounding drift.
            decimal assigned = 0m;
            for (int i = 0; i < segments.Count; i++)
            {
                var (yr, mo, cal) = segments[i];
                decimal portion;
                if (i == segments.Count - 1)
                    portion = totalDays - assigned;
                else
                    portion = Math.Round(totalDays * cal / totalCal, 3, MidpointRounding.AwayFromZero);

                if (portion > 0)
                    result.Add((yr, mo, portion));
                assigned += portion;
            }

            return result;
        }

        /// <summary>
        /// Add <paramref name="delta"/> (positive = debit, negative = credit)
        /// to the ConsumedXxx column for the given month.
        /// Floors the result at 0 so a refund never creates negative consumed.
        /// </summary>
        static void AdjustMonth(LeaveBalance b, int month, decimal delta)
        {
            switch (month)
            {
                case  1: b.ConsumedJan = Math.Max(0, b.ConsumedJan + delta); break;
                case  2: b.ConsumedFeb = Math.Max(0, b.ConsumedFeb + delta); break;
                case  3: b.ConsumedMar = Math.Max(0, b.ConsumedMar + delta); break;
                case  4: b.ConsumedApr = Math.Max(0, b.ConsumedApr + delta); break;
                case  5: b.ConsumedMay = Math.Max(0, b.ConsumedMay + delta); break;
                case  6: b.ConsumedJun = Math.Max(0, b.ConsumedJun + delta); break;
                case  7: b.ConsumedJul = Math.Max(0, b.ConsumedJul + delta); break;
                case  8: b.ConsumedAug = Math.Max(0, b.ConsumedAug + delta); break;
                case  9: b.ConsumedSep = Math.Max(0, b.ConsumedSep + delta); break;
                case 10: b.ConsumedOct = Math.Max(0, b.ConsumedOct + delta); break;
                case 11: b.ConsumedNov = Math.Max(0, b.ConsumedNov + delta); break;
                case 12: b.ConsumedDec = Math.Max(0, b.ConsumedDec + delta); break;
            }
        }
    }
}
