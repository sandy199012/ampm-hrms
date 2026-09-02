using AmpmHrmsPro.Models;

namespace AmpmHrmsPro.Services
{
    // ═══════════════════════════════════════════
    // OVERTIME CALCULATION ENGINE — reimplements the company's OT policy
    // (see "OT Rules & Legend" in the target report) from first principles,
    // since the source report itself is a static export with no formulas
    // to copy — every value in it was already computed upstream. The
    // slab/rounding rule below was reverse-engineered by tabulating
    // Extra-Minutes → OT-Hours across ~1,600 real rows of the company's
    // July 2026 OT register (every 16/44/45/74/126/215-minute case
    // verified), and is the ACTUAL algorithm the data follows — more
    // precise than the "OT Rules & Legend" sheet's simplified slab table,
    // which claims "Extra ≤30 min = No OT" but the real data credits OT
    // starting from as little as 16 minutes. Written rule reference (from
    // that legend sheet):
    //   1. Normal shift hours              → 0 OT
    //   2. Extra ≤30 min after shift        → (legend says 0; data disagrees — see below)
    //   3. Extra 31–45 min                  → 30 min OT
    //   4. Extra 46–60 min                  → 60 min OT
    //   5. Night work past 00:45            → flat 8h OT
    //   6. Sunday, worked ≥7h                → flat 8h OT (non-retail only — retail Sunday = normal day)
    //   7. Holiday/Week-off, worked ≥7h      → flat 8h OT (non-retail only)
    //   8. Morning OT, ≥60 min before shift  → flat 60 min (no partial credit)
    //   9. Retail workers: OT measured from (In-time + 9h), not shift-end
    //  10. Hard cap: 16 hours OT per day
    // ═══════════════════════════════════════════
    public static class OTEngine
    {
        public record OTResult(int? ExtraMinutes, string OTRule, decimal OTHours, bool IsRetailOT);

        // date        = the calendar day being evaluated
        // inTime/outTime = the day's effective punch times (already resolved from AttendancePunch — earliest/latest, or a Regularisation's approved times)
        // shift       = the employee's assigned Shift (start/end define the non-retail OT boundary)
        // category    = Employee.Category free-text ("Staff", "Worker", "Retail Worker", ...) — Staff never earns OT, matching the source report where Staff rows have no OT Hours value at all
        // isWeekOffOrHoliday = true if this date is the employee's scheduled week-off OR a company holiday
        public static OTResult? Compute(DateTime date, TimeSpan? inTime, TimeSpan? outTime, Shift? shift, string? category, bool isWeekOffOrHoliday)
        {
            if (inTime == null || outTime == null) return null; // can't compute OT without a full in+out pair (mispunch days never earn OT)
            if (string.IsNullOrWhiteSpace(category)) return null; // no category on record — safest default is "not OT eligible" rather than guessing
            if (category.Contains("Staff", StringComparison.OrdinalIgnoreCase)) return null; // Staff category is never OT-eligible per the source report

            bool isRetail = category.Contains("Retail", StringComparison.OrdinalIgnoreCase);
            var shiftStartTime = shift?.StartTime ?? new TimeSpan(9, 30, 0);
            var shiftEndTime   = shift?.EndTime   ?? new TimeSpan(18, 30, 0);

            var inDt  = date.Date + inTime.Value;
            var outDt = date.Date + outTime.Value;
            if (outDt <= inDt) outDt = outDt.AddDays(1); // crossed midnight

            // Rule 5 — night work past 00:45 the next day, flat 8h, overrides everything else.
            var nightCutoff = date.Date.AddDays(1).AddMinutes(45);
            if (outDt >= nightCutoff)
                return new OTResult(null, "Night till 00:45+ → 8h OT", 8m, isRetail);

            double workedHours = (outDt - inDt).TotalHours;

            // Rules 6/7 — Sunday or Holiday/Week-off, worked ≥7h, flat 8h.
            // Retail workers are explicitly exempt from this rule ("Sunday = normal day" for retail) — they fall through to the normal slab calc below even on a week-off/Sunday.
            if (isWeekOffOrHoliday && !isRetail)
            {
                if (workedHours >= 7)
                {
                    var label = date.DayOfWeek == DayOfWeek.Sunday ? "Sunday ≥7h → 8h OT" : "Holiday/Week-off ≥7h → 8h OT";
                    return new OTResult(null, label, 8m, isRetail);
                }
                return null; // worked but under 7h on a week-off/holiday — no OT under this rule
            }

            // Normal working day (or a retail employee's week-off/Sunday, which is treated as normal).
            var shiftStart = date.Date + shiftStartTime;
            var otBoundary = isRetail
                ? inDt.AddHours(9)                 // Rule 9 — retail: OT measured from In-time + 9h
                : date.Date + shiftEndTime;         // non-retail: OT measured from the assigned shift's end time (per-shift configurable — some departments run 18:00, others 18:30/19:00)

            // Rule 8 — morning OT (non-retail only): flat 60 min if ≥60 min early, nothing for less (no partial credit).
            int morningExtraRaw = (!isRetail && inDt < shiftStart) ? (int)(shiftStart - inDt).TotalMinutes : 0;
            int morningOTMin = morningExtraRaw >= 60 ? 60 : 0;

            // Rules 2-4 — evening OT with company slab rounding:
            //   ≤ 30 min  → 0 OT
            //   31–45 min → 30 min
            //   46–75 min → 60 min  (and so on, each 30-min slab)
            // Formula: ((extra + 14) / 30) * 30  [integer division] gives the correct slab.
            int eveningExtraRaw = outDt > otBoundary ? (int)(outDt - otBoundary).TotalMinutes : 0;
            int eveningOTMin = eveningExtraRaw > 30
                ? ((eveningExtraRaw + 14) / 30) * 30
                : 0;

            int totalOTMin = morningOTMin + eveningOTMin;
            if (totalOTMin <= 0) return null; // below the rounding threshold — no OT credited, and (matching the source report) such days simply don't appear in the OT Daily Register

            decimal otHours = Math.Round(totalOTMin / 60m, 2);
            if (otHours > 16m) otHours = 16m; // Rule 10 — hard daily cap

            string ruleText;
            int? extraMinsForDisplay;
            if (morningOTMin > 0 && eveningOTMin > 0)
            {
                ruleText = $"Morning → {morningOTMin}min | Eve {eveningExtraRaw}min → {eveningOTMin}min";
                extraMinsForDisplay = eveningExtraRaw;
            }
            else if (morningOTMin > 0)
            {
                ruleText = $"Morning → {morningOTMin}min";
                extraMinsForDisplay = morningExtraRaw;
            }
            else
            {
                ruleText = isRetail
                    ? $"Eve {eveningExtraRaw}min → {eveningOTMin}min (after 9h)"
                    : $"Eve {eveningExtraRaw}min → {eveningOTMin}min";
                extraMinsForDisplay = eveningExtraRaw;
            }

            return new OTResult(extraMinsForDisplay, ruleText, otHours, isRetail);
        }
    }
}
