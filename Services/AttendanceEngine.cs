using Microsoft.EntityFrameworkCore;
using AmpmHrmsPro.Data;
using AmpmHrmsPro.Models;

namespace AmpmHrmsPro.Services
{
    // ═══════════════════════════════════════════
    // ATTENDANCE ENGINE — turns raw AttendancePunch rows into the single
    // computed AttendanceDaily row a report needs, for one employee/date at
    // a time. Called after every biometric sync, every Excel import, and
    // every application approval/revoke (since approving a Regularisation
    // or Leave changes that day's EffectiveStatus).
    //
    // RawStatus reflects only the punches + week-off/holiday calendar.
    // EffectiveStatus additionally applies any APPROVED application for
    // that date — this is the two-field split the source report's raw-
    // fill-vs-effective-text mismatch on regularised days revealed.
    // ═══════════════════════════════════════════
    public static class AttendanceEngine
    {
        public static async Task RecomputeDayAsync(AppDbContext db, int employeeId, DateTime date)
        {
            string dateStr = date.ToString("yyyy-MM-dd");

            var employee = await db.Employees.Include(e => e.Shift).Include(e => e.WeekOffPolicy)
                .FirstOrDefaultAsync(e => e.Id == employeeId);
            if (employee == null) return;

            var dayPunches = await db.AttendancePunches
                .Where(p => p.EmployeeId == employeeId && p.PunchDateTime >= date.Date && p.PunchDateTime < date.Date.AddDays(1))
                .OrderBy(p => p.PunchDateTime)
                .ToListAsync();

            var (inTime, outTime) = ResolveInOut(dayPunches);

            bool isHoliday = await db.Holidays.AnyAsync(h => h.Date == dateStr && h.IsActive);
            bool isWeekOff = employee.WeekOffPolicy != null && WeekOffHelper.IsWeekOff(date, employee.WeekOffPolicy);

            var (rawStatus, workedMinutes) = ComputeRawStatus(date, inTime, outTime, employee.Shift, isWeekOff, isHoliday);

            // ── Apply the day's approved application (if any) to get the effective status ──
            string effectiveStatus = rawStatus;
            TimeSpan? effInTime = inTime, effOutTime = outTime;

            var approvedApp = await db.Applications.Include(a => a.LeaveType)
                .Where(a => a.EmployeeId == employeeId && a.Status == "Approved"
                    && string.Compare(a.FromDate, dateStr) <= 0 && string.Compare(a.ToDate, dateStr) >= 0)
                .OrderByDescending(a => a.DecisionAt) // most recently approved wins if more than one somehow overlaps
                .FirstOrDefaultAsync();

            if (approvedApp != null)
            {
                switch (approvedApp.Type)
                {
                    case "Regularisation":
                        // Recompute using the approved corrected times instead of the raw punches.
                        effInTime = approvedApp.RequestedInTime ?? inTime;
                        effOutTime = approvedApp.RequestedOutTime ?? outTime;
                        // If a regularisation on a week-off/holiday has NO time data at all
                        // (no biometric punches AND no RequestedIn/Out times), treat as a full
                        // worked day — the manager's approval IS the authoritative signal of
                        // presence. Without this, ComputeRawStatus returns "WO" (no punches +
                        // isOffDay) instead of "POW", so TryAutoCreditAsync never fires and
                        // auto comp-off credit is silently skipped.
                        if ((isWeekOff || isHoliday) && effInTime == null && effOutTime == null)
                        {
                            effectiveStatus = "POW";
                            workedMinutes = employee.Shift != null
                                ? (int)(employee.Shift.EndTime - employee.Shift.StartTime).TotalMinutes
                                : 480; // default 8 h when no shift configured
                        }
                        else
                        {
                            var (regStatus, regMinutes) = ComputeRawStatus(date, effInTime, effOutTime, employee.Shift, isWeekOff, isHoliday);
                            effectiveStatus = regStatus;
                            workedMinutes = regMinutes;
                        }
                        break;
                    case "Leave":
                        string alias = approvedApp.LeaveType?.Alias ?? "L";
                        effectiveStatus = approvedApp.DayPart == "Single" ? $"L ({alias})" : $"HD (L-{alias})";
                        break;
                    case "WFH":
                        effectiveStatus = "P (WFH)";
                        break;
                    case "OD":
                        effectiveStatus = "P (OD)";
                        break;
                }
            }

            // ── OT — only computed on the EFFECTIVE in/out (a regularised day should be OT-eligible on its corrected times; a leave/WFH/OD day has no punches to compute OT from) ──
            OTEngine.OTResult? ot = null;
            if (effInTime.HasValue && effOutTime.HasValue)
                ot = OTEngine.Compute(date, effInTime, effOutTime, employee.Shift, employee.Category, isWeekOff || isHoliday);

            var existing = await db.AttendanceDailies.FirstOrDefaultAsync(d => d.EmployeeId == employeeId && d.Date == dateStr);
            if (existing == null)
            {
                existing = new AttendanceDaily { EmployeeId = employeeId, Date = dateStr };
                db.AttendanceDailies.Add(existing);
            }
            existing.InTime = inTime;
            existing.OutTime = outTime;
            existing.RawStatus = rawStatus;
            existing.EffectiveStatus = effectiveStatus;
            existing.WasHoliday = isHoliday;
            existing.WasWeekOff = isWeekOff;
            existing.WorkedMinutes = workedMinutes;
            existing.ExtraMinutes = ot?.ExtraMinutes;
            existing.OTRule = ot?.OTRule;
            existing.OTHours = ot?.OTHours;
            existing.IsRetailOT = ot?.IsRetailOT ?? false;
            existing.UpdatedAt = DateTime.Now;

            await db.SaveChangesAsync();

            // Comp-Off auto-credit — checks whether this day's EFFECTIVE
            // status (post-Regularisation, where applicable) shows the
            // employee worked a qualifying Holiday/Week-Off, and if so
            // upserts an Auto ledger credit. Idempotent: safe to call on
            // every recompute, including the same day recomputed many times
            // over (biometric re-sync, punch corrections, etc.).
            await CompOffEngine.TryAutoCreditAsync(db, employeeId, date, effectiveStatus, isHoliday, isWeekOff, existing.WorkedMinutes);

            // OT Ledger — auto-log overtime for Worker-category employees
            // who have an OTRule assigned. Shift duration is passed so the
            // engine can detect "worked beyond shift" OT on normal workdays.
            int? shiftDurationMinutes = employee.Shift != null
                ? (int)(employee.Shift.EndTime - employee.Shift.StartTime).TotalMinutes
                : (int?)null;
            await OTLedgerEngine.TryAutoOTAsync(db, employeeId, date, effectiveStatus, isHoliday, isWeekOff, existing.WorkedMinutes, shiftDurationMinutes);
        }

        public static async Task RecomputeRangeAsync(AppDbContext db, int employeeId, DateTime from, DateTime to)
        {
            for (var d = from.Date; d <= to.Date; d = d.AddDays(1))
                await RecomputeDayAsync(db, employeeId, d);
        }

        // Recomputes every active employee across a date range — used after
        // a bulk Excel import or a full biometric sync run where many
        // employees' days changed at once.
        public static async Task RecomputeAllAsync(AppDbContext db, DateTime from, DateTime to, IEnumerable<int>? employeeIds = null)
        {
            var ids = employeeIds?.ToList() ?? await db.Employees.Where(e => e.IsActive).Select(e => e.Id).ToListAsync();
            foreach (var id in ids)
                await RecomputeRangeAsync(db, id, from, to);
        }

        // Earliest punch of the day = In, latest = Out — used when the
        // punch source doesn't send an explicit Direction (see
        // BiometricApiSettings.DirectionField). If Direction IS known for
        // some punches, prefer the explicit In/Out-tagged ones.
        static (TimeSpan? In, TimeSpan? Out) ResolveInOut(List<AttendancePunch> dayPunches)
        {
            if (!dayPunches.Any()) return (null, null);

            var explicitIn = dayPunches.Where(p => p.Direction == "In").OrderBy(p => p.PunchDateTime).FirstOrDefault();
            var explicitOut = dayPunches.Where(p => p.Direction == "Out").OrderByDescending(p => p.PunchDateTime).FirstOrDefault();

            TimeSpan? inTime = explicitIn?.PunchDateTime.TimeOfDay ?? dayPunches.First().PunchDateTime.TimeOfDay;
            TimeSpan? outTime = explicitOut?.PunchDateTime.TimeOfDay
                ?? (dayPunches.Count > 1 ? dayPunches.Last().PunchDateTime.TimeOfDay : (TimeSpan?)null); // a single lone punch with no direction is treated as an In only — mispunch, not a same-time in=out

            return (inTime, outTime);
        }

        public static (string RawStatus, int? WorkedMinutes) ComputeRawStatus(DateTime date, TimeSpan? inTime, TimeSpan? outTime, Shift? shift, bool isWeekOff, bool isHoliday)
        {
            bool hasIn = inTime.HasValue, hasOut = outTime.HasValue;
            bool isOffDay = isWeekOff || isHoliday;

            int? workedMinutes = null;
            if (hasIn && hasOut)
            {
                var inDt = date.Date + inTime!.Value;
                var outDt = date.Date + outTime!.Value;
                if (outDt <= inDt) outDt = outDt.AddDays(1);
                workedMinutes = (int)(outDt - inDt).TotalMinutes;
            }

            if (!hasIn && !hasOut)
                return (isOffDay ? "WO" : "A", null);

            // Only one side of the pair recorded — a mispunch, but which
            // side is missing matters, so it's labeled rather than lumped
            // into one generic "MIS" code: missing the In-punch is a
            // MORNING mispunch (they never checked in), missing the
            // Out-punch is an EVENING mispunch (they never checked out).
            if (hasIn && !hasOut)
                return (isOffDay ? "POW (MIS-Evening)" : "A (MIS-Evening)", workedMinutes);
            if (!hasIn && hasOut)
                return (isOffDay ? "POW (MIS-Morning)" : "A (MIS-Morning)", workedMinutes);

            if (isOffDay)
                return ("POW", workedMinutes); // worked despite being a scheduled week-off/holiday

            decimal hours = (workedMinutes ?? 0) / 60m;
            decimal fullThreshold = shift?.FullDayThresholdHours ?? 8m;
            decimal halfThreshold = shift?.HalfDayThresholdHours ?? 4m;

            if (hours >= fullThreshold) return ("P", workedMinutes);
            if (hours >= halfThreshold) return ("HD", workedMinutes);
            return ("A", workedMinutes); // punched in & out, but too few hours to count as even a half day
        }
    }
}
