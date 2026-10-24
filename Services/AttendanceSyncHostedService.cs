using AmpmHrmsPro.Data;
using AmpmHrmsPro.Services.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace AmpmHrmsPro.Services
{
    // ═══════════════════════════════════════════════════════════════════
    // ATTENDANCE AUTO-SYNC — self-healing check, 1 minute after every start
    // and then every 15 minutes, per company, over the last 62 days:
    //   • a day with punches / a Presence 360 record but no attendance row
    //   • a row older than its newest punch, P360 record or approved request
    //   • a row whose status differs from the Presence 360 status
    // → those employees are recomputed with the fast bulk engine.
    // The free server restarts often; anything a restart interrupted (an
    // upload's background recompute, an API sync) is finished here.
    // ═══════════════════════════════════════════════════════════════════
    public class AttendanceSyncHostedService : BackgroundService
    {
        private readonly IServiceProvider _services;
        private readonly ILogger<AttendanceSyncHostedService> _log;
        public AttendanceSyncHostedService(IServiceProvider services, ILogger<AttendanceSyncHostedService> log) { _services = services; _log = log; }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); } catch (OperationCanceledException) { return; }
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await TenantJobs.ForEachActiveCompanyAsync(_services, async (sp, companyId) =>
                    {
                        var db = sp.GetRequiredService<AppDbContext>();
                        db.AuditEnabled = false;
                        var (emps, days, fixedRows) = await HealAsync(db, 62, stoppingToken);
                        if (fixedRows > 0)
                            _log.LogInformation("Attendance auto-sync (company {Company}): {Days} employee-day(s) out of date for {Emps} employee(s) — {Rows} rows recomputed.",
                                companyId, days, emps, fixedRows);
                    }, _log, stoppingToken);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { _log.LogError(ex, "Attendance auto-sync failed"); }

                try { await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }

        static string CodeKey(string? c) { var t = (c ?? "").Trim().TrimStart('0'); return t.Length == 0 ? "0" : t.ToUpperInvariant(); }

        /// <summary>Finds out-of-date employee-days in the last <paramref name="days"/> days and recomputes them.</summary>
        public static async Task<(int Employees, int Days, int RowsRecomputed)> HealAsync(AppDbContext db, int days, CancellationToken ct = default)
        {
            var today = IndiaTime.Today;
            var from = today.AddDays(-(days - 1));
            string fromS = from.ToString("yyyy-MM-dd"), toS = today.ToString("yyyy-MM-dd");
            var dFrom = DateOnly.FromDateTime(from); var dTo = DateOnly.FromDateTime(today);
            var tol = TimeSpan.FromMinutes(1);

            var emps = await db.Employees.AsNoTracking().Select(e => new { e.Id, e.EmpCode }).ToListAsync(ct);
            var keyToEmp = new Dictionary<string, int>();
            foreach (var e in emps) keyToEmp.TryAdd(CodeKey(e.EmpCode), e.Id);

            var daily = (await db.AttendanceDailies.AsNoTracking()
                    .Where(d => string.Compare(d.Date, fromS) >= 0 && string.Compare(d.Date, toS) <= 0)
                    .Select(d => new { d.EmployeeId, d.Date, d.UpdatedAt, d.RawStatus }).ToListAsync(ct))
                .GroupBy(d => (d.EmployeeId, d.Date)).ToDictionary(g => g.Key, g => g.First());

            var need = new HashSet<(int EmpId, string Date)>();
            void Check(int empId, string date, DateTime changedAt, string? p360Status = null)
            {
                if (string.Compare(date, toS) > 0) return;
                if (!daily.TryGetValue((empId, date), out var d)) { need.Add((empId, date)); return; }
                if (d.UpdatedAt + tol < changedAt) { need.Add((empId, date)); return; }
                if (p360Status != null)
                {
                    var s = p360Status.Trim(); if (s.Length > 20) s = s[..20];
                    if (s.Length > 0 && !string.Equals(d.RawStatus, s, StringComparison.Ordinal)) need.Add((empId, date));
                }
            }

            var punchEnd = today.AddDays(1);
            var punchDays = await db.AttendancePunches.AsNoTracking()
                .Where(p => p.PunchDateTime >= from && p.PunchDateTime < punchEnd)
                .GroupBy(p => new { p.EmployeeId, Day = p.PunchDateTime.Date })
                .Select(g => new { g.Key.EmployeeId, g.Key.Day, Last = g.Max(p => p.SyncedAt) }).ToListAsync(ct);
            foreach (var p in punchDays) Check(p.EmployeeId, p.Day.ToString("yyyy-MM-dd"), p.Last);

            var p360 = await db.P360DailyRecords.AsNoTracking().Where(x => x.Date >= dFrom && x.Date <= dTo)
                .Select(x => new { x.EmpCode, x.Date, x.UpdatedAt, x.Status }).ToListAsync(ct);
            foreach (var x in p360)
                if (keyToEmp.TryGetValue(CodeKey(x.EmpCode), out var eid))
                    Check(eid, x.Date.ToString("yyyy-MM-dd"), x.UpdatedAt, x.Status);

            var apps = await db.Applications.AsNoTracking()
                .Where(a => a.Status == "Approved" && a.Type != "CompOff" && string.Compare(a.ToDate, fromS) >= 0 && string.Compare(a.FromDate, toS) <= 0)
                .Select(a => new { a.EmployeeId, a.FromDate, a.ToDate, a.CreatedAt, a.DecisionAt }).ToListAsync(ct);
            foreach (var a in apps)
            {
                if (!DateTime.TryParse(a.FromDate, out var f) || !DateTime.TryParse(a.ToDate, out var t)) continue;
                if (f < from) f = from;
                for (var d = f; d <= t && d <= today; d = d.AddDays(1))
                    Check(a.EmployeeId, d.ToString("yyyy-MM-dd"), a.DecisionAt ?? a.CreatedAt);
            }

            if (need.Count == 0) return (0, 0, 0);
            int rows = 0;
            // Recompute employee by employee from their earliest out-of-date day
            // (bulk engine, so each employee is a handful of queries).
            foreach (var g in need.GroupBy(n => n.EmpId))
            {
                ct.ThrowIfCancellationRequested();
                var start = DateTime.Parse(g.Min(x => x.Date));
                var end = DateTime.Parse(g.Max(x => x.Date));
                rows += await AttendanceEngine.RecomputeBulkAsync(db, start, end, new[] { g.Key }, ct);
            }
            return (need.Select(n => n.EmpId).Distinct().Count(), need.Count, rows);
        }
    }
}
