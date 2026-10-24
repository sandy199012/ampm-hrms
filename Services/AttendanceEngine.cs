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
        // ── One computed day (pure — no database access) ─────────────────
        public sealed class DayResult
        {
            public TimeSpan? InTime, OutTime, EffIn, EffOut;
            public string RawStatus = "A", EffectiveStatus = "A";
            public bool IsHoliday, IsWeekOff;
            public int? WorkedMinutes;
            public OTEngine.OTResult? OT;
        }

        static readonly HashSet<string> AbsenceLike = new(StringComparer.OrdinalIgnoreCase) { "A", "HD", "A (MIS)" };

        /// <summary>
        /// Status of one employee-day. When a Presence 360 record exists for the
        /// day, its Status is used as-is (P / HD / WO / H / POW / L (CL) …) so
        /// HRMS shows exactly what Presence 360 shows; an approved HRMS
        /// application can still turn a P360 absence (A / HD / A (MIS)) into
        /// leave / WFH / OD / regularised times.
        /// </summary>
        public static DayResult ComputeDay(Employee employee, DateTime date, List<AttendancePunch> dayPunches,
                                           bool isHolidayMaster, Application? approvedApp, P360DailyRecord? p360)
        {
            var r = new DayResult();
            var (inTime, outTime) = ResolveInOut(dayPunches);
            if (inTime == null && outTime == null && p360 != null)
            {
                inTime = p360.InTime?.ToTimeSpan();
                outTime = p360.OutTime?.ToTimeSpan();
            }
            r.InTime = inTime; r.OutTime = outTime;

            r.IsHoliday = isHolidayMaster;
            r.IsWeekOff = employee.WeekOffPolicy != null && WeekOffHelper.IsWeekOff(date, employee.WeekOffPolicy);

            var p360Status = string.IsNullOrWhiteSpace(p360?.Status) ? null : p360!.Status!.Trim();
            if (p360Status != null)
            {
                if (p360Status.Length > 20) p360Status = p360Status[..20];
                // Presence 360 decides holiday / week-off for its employees.
                r.IsHoliday = p360Status == "H" || p360Status.StartsWith("POH");
                r.IsWeekOff = p360Status == "WO" || p360Status.StartsWith("POW") || p360Status.Contains("WOL");
            }

            var (rawStatus, workedMinutes) = ComputeRawStatus(date, inTime, outTime, employee.Shift, r.IsWeekOff, r.IsHoliday);
            if (p360Status != null) rawStatus = p360Status;
            string effectiveStatus = rawStatus;
            TimeSpan? effInTime = inTime, effOutTime = outTime;

            bool applyApp = approvedApp != null && (p360Status == null || AbsenceLike.Contains(p360Status) || p360Status.Contains("MIS"));
            if (applyApp)
            {
                switch (approvedApp!.Type)
                {
                    case "Regularisation":
                        effInTime = approvedApp.RequestedInTime ?? inTime;
                        effOutTime = approvedApp.RequestedOutTime ?? outTime;
                        // Regularisation on an off-day with no time data at all = a full worked day
                        // (the approval is the proof of presence) so comp-off credit still fires.
                        if ((r.IsWeekOff || r.IsHoliday) && effInTime == null && effOutTime == null)
                        {
                            effectiveStatus = "POW";
                            workedMinutes = employee.Shift != null ? (int)(employee.Shift.EndTime - employee.Shift.StartTime).TotalMinutes : 480;
                        }
                        else
                        {
                            var (regStatus, regMinutes) = ComputeRawStatus(date, effInTime, effOutTime, employee.Shift, r.IsWeekOff, r.IsHoliday);
                            effectiveStatus = regStatus;
                            workedMinutes = regMinutes;
                        }
                        break;
                    case "Leave" when approvedApp.RequestedInTime.HasValue:
                        // Short leave (a couple of hours): still present if they came in.
                        effectiveStatus = inTime.HasValue || outTime.HasValue ? "P (SHL)" : rawStatus;
                        break;
                    case "Leave" when (approvedApp.LeaveType?.Name ?? "").Contains("week off", StringComparison.OrdinalIgnoreCase):
                        effectiveStatus = "WO";   // approved week-off swap (retail rotational week off)
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

            r.RawStatus = rawStatus;
            r.EffectiveStatus = effectiveStatus;
            r.WorkedMinutes = workedMinutes;
            r.EffIn = effInTime; r.EffOut = effOutTime;
            if (effInTime.HasValue && effOutTime.HasValue)
                r.OT = OTEngine.Compute(date, effInTime, effOutTime, employee.Shift, employee.Category, r.IsWeekOff || r.IsHoliday);
            return r;
        }

        static void Apply(AttendanceDaily d, DayResult r)
        {
            d.InTime = r.InTime;
            d.OutTime = r.OutTime;
            d.RawStatus = r.RawStatus;
            d.EffectiveStatus = r.EffectiveStatus;
            d.WasHoliday = r.IsHoliday;
            d.WasWeekOff = r.IsWeekOff;
            d.WorkedMinutes = r.WorkedMinutes;
            d.ExtraMinutes = r.OT?.ExtraMinutes;
            d.OTRule = r.OT?.OTRule;
            d.OTHours = r.OT?.OTHours;
            d.IsRetailOT = r.OT?.IsRetailOT ?? false;
            d.UpdatedAt = DateTime.Now;
        }

        static string CodeKey(string? c) { var t = (c ?? "").Trim().TrimStart('0'); return t.Length == 0 ? "0" : t.ToUpperInvariant(); }

        static async Task RunLedgersAsync(AppDbContext db, Employee employee, DateTime date, DayResult r, int? workedMinutes)
        {
            // Comp-Off auto-credit and OT ledger — both idempotent.
            await CompOffEngine.TryAutoCreditAsync(db, employee.Id, date, r.EffectiveStatus, r.IsHoliday, r.IsWeekOff, workedMinutes);
            int? shiftDurationMinutes = employee.Shift != null ? (int)(employee.Shift.EndTime - employee.Shift.StartTime).TotalMinutes : null;
            await OTLedgerEngine.TryAutoOTAsync(db, employee.Id, date, r.EffectiveStatus, r.IsHoliday, r.IsWeekOff, workedMinutes, shiftDurationMinutes);
        }

        public static async Task RecomputeDayAsync(AppDbContext db, int employeeId, DateTime date)
        {
            string dateStr = date.ToString("yyyy-MM-dd");
            var employee = await db.Employees
                .Include(e => e.Shift)
                .Include(e => e.WeekOffPolicy).ThenInclude(w => w!.Rules)
                .FirstOrDefaultAsync(e => e.Id == employeeId);
            if (employee == null) return;

            var dayPunches = await db.AttendancePunches
                .Where(p => p.EmployeeId == employeeId && p.PunchDateTime >= date.Date && p.PunchDateTime < date.Date.AddDays(1))
                .OrderBy(p => p.PunchDateTime).ToListAsync();
            bool isHoliday = await db.Holidays.AnyAsync(h => h.Date == dateStr && h.IsActive);
            var approvedApp = await db.Applications.Include(a => a.LeaveType)
                .Where(a => a.EmployeeId == employeeId && a.Status == "Approved" && a.Type != "CompOff"
                    && string.Compare(a.FromDate, dateStr) <= 0 && string.Compare(a.ToDate, dateStr) >= 0)
                .OrderByDescending(a => a.DecisionAt).FirstOrDefaultAsync();
            var d = DateOnly.FromDateTime(date);
            var key = CodeKey(employee.EmpCode);
            var p360 = await db.P360DailyRecords.Where(x => x.Date == d && (x.EmpCode == key || x.EmpCode == employee.EmpCode))
                .OrderByDescending(x => x.UpdatedAt).FirstOrDefaultAsync();

            var r = ComputeDay(employee, date, dayPunches, isHoliday, approvedApp, p360);
            var existing = await db.AttendanceDailies.FirstOrDefaultAsync(x => x.EmployeeId == employeeId && x.Date == dateStr);
            if (existing == null)
            {
                existing = new AttendanceDaily { EmployeeId = employeeId, Date = dateStr };
                db.AttendanceDailies.Add(existing);
            }
            Apply(existing, r);
            await db.SaveChangesAsync();
            await RunLedgersAsync(db, employee, date, r, existing.WorkedMinutes);
        }

        public static Task RecomputeRangeAsync(AppDbContext db, int employeeId, DateTime from, DateTime to) =>
            RecomputeBulkAsync(db, from, to, new[] { employeeId });

        // Recomputes every active employee (or the given ones) across a date range.
        public static Task RecomputeAllAsync(AppDbContext db, DateTime from, DateTime to, IEnumerable<int>? employeeIds = null) =>
            RecomputeBulkAsync(db, from, to, employeeIds);

        /// <summary>
        /// Fast recompute: loads punches, holidays, approved applications and
        /// Presence 360 records for the whole range ONCE, computes every
        /// employee-day in memory and saves in batches. A month for a few
        /// hundred employees takes seconds instead of the hour the day-by-day
        /// version needed (which the free server's restarts kept cutting short).
        /// Days after today are skipped.
        /// </summary>
        /// <summary>Only one bulk recompute at a time, so two never write the same employee-day.</summary>
        public static readonly SemaphoreSlim Gate = new(1, 1);

        public static async Task<int> RecomputeBulkAsync(AppDbContext db, DateTime from, DateTime to, IEnumerable<int>? employeeIds = null, CancellationToken ct = default)
        {
            await Gate.WaitAsync(ct);
            try { return await RecomputeBulkCoreAsync(db, from, to, employeeIds, ct); }
            finally { Gate.Release(); }
        }

        static async Task<int> RecomputeBulkCoreAsync(AppDbContext db, DateTime from, DateTime to, IEnumerable<int>? employeeIds, CancellationToken ct)
        {
            from = from.Date;
            var today = IndiaTime.Today;
            to = (to.Date > today ? today : to.Date);
            if (to < from) return 0;
            string fromS = from.ToString("yyyy-MM-dd"), toS = to.ToString("yyyy-MM-dd");
            var dFrom = DateOnly.FromDateTime(from); var dTo = DateOnly.FromDateTime(to);

            var empQuery = db.Employees.AsNoTracking().Include(e => e.Shift).Include(e => e.WeekOffPolicy).ThenInclude(w => w!.Rules).AsQueryable();
            var idList = employeeIds?.Distinct().ToList();
            empQuery = idList != null ? empQuery.Where(e => idList.Contains(e.Id)) : empQuery.Where(e => e.IsActive);
            var employees = await empQuery.ToListAsync(ct);
            if (employees.Count == 0) return 0;
            var ids = employees.Select(e => e.Id).ToList();

            var holidays = (await db.Holidays.AsNoTracking().Where(h => h.IsActive && string.Compare(h.Date, fromS) >= 0 && string.Compare(h.Date, toS) <= 0)
                .Select(h => h.Date).ToListAsync(ct)).ToHashSet();
            var punchEnd = to.AddDays(1);
            var punches = (await db.AttendancePunches.AsNoTracking()
                    .Where(p => ids.Contains(p.EmployeeId) && p.PunchDateTime >= from && p.PunchDateTime < punchEnd)
                    .OrderBy(p => p.PunchDateTime).ToListAsync(ct))
                .ToLookup(p => (p.EmployeeId, p.PunchDateTime.Date));
            var apps = await db.Applications.AsNoTracking().Include(a => a.LeaveType)
                .Where(a => ids.Contains(a.EmployeeId) && a.Status == "Approved" && a.Type != "CompOff"
                    && string.Compare(a.FromDate, toS) <= 0 && string.Compare(a.ToDate, fromS) >= 0)
                .ToListAsync(ct);
            var appsByEmp = apps.ToLookup(a => a.EmployeeId);

            var keyToEmp = new Dictionary<string, int>();
            foreach (var e in employees) keyToEmp.TryAdd(CodeKey(e.EmpCode), e.Id);
            var p360 = new Dictionary<(int, DateOnly), P360DailyRecord>();
            foreach (var x in (await db.P360DailyRecords.AsNoTracking().Where(x => x.Date >= dFrom && x.Date <= dTo).ToListAsync(ct)).OrderBy(x => x.UpdatedAt))
                if (keyToEmp.TryGetValue(CodeKey(x.EmpCode), out var eid)) p360[(eid, x.Date)] = x;

            var existing = (await db.AttendanceDailies.Where(d => ids.Contains(d.EmployeeId)
                    && string.Compare(d.Date, fromS) >= 0 && string.Compare(d.Date, toS) <= 0).ToListAsync(ct))
                .GroupBy(d => (d.EmployeeId, d.Date)).ToDictionary(g => g.Key, g => g.First());

            int count = 0, pending = 0;
            var ledgerWork = new List<(Employee Emp, DateTime Date, DayResult R)>();
            foreach (var emp in employees)
            {
                for (var date = from; date <= to; date = date.AddDays(1))
                {
                    var ds = date.ToString("yyyy-MM-dd");
                    var dayPunches = punches[(emp.Id, date)].ToList();
                    p360.TryGetValue((emp.Id, DateOnly.FromDateTime(date)), out var rec);
                    var app = appsByEmp[emp.Id]
                        .Where(a => string.Compare(a.FromDate, ds) <= 0 && string.Compare(a.ToDate, ds) >= 0)
                        .OrderByDescending(a => a.DecisionAt).FirstOrDefault();
                    var r = ComputeDay(emp, date, dayPunches, holidays.Contains(ds), app, rec);

                    if (!existing.TryGetValue((emp.Id, ds), out var row))
                    {
                        row = new AttendanceDaily { EmployeeId = emp.Id, Date = ds };
                        db.AttendanceDailies.Add(row);
                        existing[(emp.Id, ds)] = row;
                    }
                    Apply(row, r);
                    count++; pending++;
                    if (r.IsWeekOff || r.IsHoliday || r.EffectiveStatus.StartsWith("PO") || emp.OTRuleId != null || emp.CompOffRuleId != null)
                        ledgerWork.Add((emp, date, r));
                    if (pending >= 500) { await db.SaveChangesAsync(ct); pending = 0; }
                }
            }
            if (pending > 0) await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();

            // Ledgers only where something can change: a worked off-day, an
            // existing auto credit to re-check, or an employee on an OT rule.
            var compOffDates = (await db.Applications.AsNoTracking()
                    .Where(x => ids.Contains(x.EmployeeId) && x.Type == "CompOff" && string.Compare(x.FromDate, fromS) >= 0 && string.Compare(x.FromDate, toS) <= 0)
                    .Select(x => new { x.EmployeeId, x.FromDate }).ToListAsync(ct))
                .Select(x => (x.EmployeeId, x.FromDate)).ToHashSet();
            var otDates = (await db.OTLedgers.AsNoTracking()
                    .Where(x => ids.Contains(x.EmployeeId) && x.Source == "Auto" && string.Compare(x.Date, fromS) >= 0 && string.Compare(x.Date, toS) <= 0)
                    .Select(x => new { x.EmployeeId, x.Date }).ToListAsync(ct))
                .Select(x => (x.EmployeeId, x.Date)).ToHashSet();
            foreach (var (emp, date, r) in ledgerWork)
            {
                var ds = date.ToString("yyyy-MM-dd");
                try
                {
                    if (r.EffectiveStatus.StartsWith("PO") || compOffDates.Contains((emp.Id, ds)))
                        await CompOffEngine.TryAutoCreditAsync(db, emp.Id, date, r.EffectiveStatus, r.IsHoliday, r.IsWeekOff, r.WorkedMinutes);
                    if (emp.OTRuleId != null || otDates.Contains((emp.Id, ds)))
                    {
                        int? shiftMin = emp.Shift != null ? (int)(emp.Shift.EndTime - emp.Shift.StartTime).TotalMinutes : null;
                        await OTLedgerEngine.TryAutoOTAsync(db, emp.Id, date, r.EffectiveStatus, r.IsHoliday, r.IsWeekOff, r.WorkedMinutes, shiftMin);
                    }
                }
                catch { db.ChangeTracker.Clear(); }
            }
            return count;
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
