using AmpmHrmsPro.Data;
using AmpmHrmsPro.Models;
using AmpmHrmsPro.Services.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace AmpmHrmsPro.Services
{
    // ═══════════════════════════════════════════════════════════════════
    // MONTHLY LEAVE CREDIT — on the 1st of every month (India time) each
    // active employee's leave balance row for the year gets that month's
    // credit:
    //   • CL  0.833 per month
    //   • EL  1.5 per month — or 1.25 for employees whose EL was 1.25 last
    //         month (the staff group on the 15-days-a-year rate)
    // Only a month that is still 0 is credited, so a value HR typed or
    // uploaded is never overwritten, and each company/month runs only once
    // (LeaveAccrualRuns) — a month HR later sets back to 0 stays 0.
    // January: a new-year row is opened from last year's row — EL and SL
    // carry their closing balance forward, CL lapses (starts at 0).
    // The server sleeps often, so the check repeats every hour and catches
    // up as soon as it wakes.
    // ═══════════════════════════════════════════════════════════════════
    public class LeaveAccrualHostedService : BackgroundService
    {
        public const decimal ClPerMonth = 0.833m;
        public const decimal ElPerMonth = 1.5m;
        public const decimal ElStaffRate = 1.25m;

        static readonly string[] Mon = { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };

        private readonly IServiceProvider _services;
        private readonly ILogger<LeaveAccrualHostedService> _log;
        public LeaveAccrualHostedService(IServiceProvider services, ILogger<LeaveAccrualHostedService> log) { _services = services; _log = log; }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); } catch (OperationCanceledException) { return; }
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await TenantJobs.ForEachActiveCompanyAsync(_services, async (sp, companyId) =>
                    {
                        var db = sp.GetRequiredService<AppDbContext>();
                        db.AuditEnabled = false;
                        var today = IndiaTime.Today;
                        var n = await CreditMonthAsync(db, today.Year, today.Month, stoppingToken);
                        if (n >= 0)
                            _log.LogInformation("Leave credit {Month}-{Year} (company {Company}): {Rows} balance row(s) credited.",
                                Mon[today.Month - 1], today.Year, companyId, n);
                    }, _log, stoppingToken);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { _log.LogError(ex, "Monthly leave credit failed"); }

                try { await Task.Delay(TimeSpan.FromHours(1), stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }

        static decimal GetEarned(AppDbContext db, LeaveBalance row, int month) =>
            (decimal)db.Entry(row).Property("Earned" + Mon[month - 1]).CurrentValue!;
        static void SetEarned(AppDbContext db, LeaveBalance row, int month, decimal v) =>
            db.Entry(row).Property("Earned" + Mon[month - 1]).CurrentValue = v;

        /// <summary>
        /// Credits one month for the current company. Returns the number of rows credited,
        /// or -1 when that month was already done.
        /// </summary>
        public static async Task<int> CreditMonthAsync(AppDbContext db, int year, int month, CancellationToken ct = default)
        {
            if (await db.LeaveAccrualRuns.AnyAsync(r => r.Year == year && r.Month == month, ct)) return -1;

            var monthEnd = new DateTime(year, month, 1).AddMonths(1).AddDays(-1).ToString("yyyy-MM-dd");
            var empIds = (await db.Employees.AsNoTracking()
                    .Where(e => e.IsActive)
                    .Select(e => new { e.Id, e.DOJ }).ToListAsync(ct))
                .Where(e => string.IsNullOrWhiteSpace(e.DOJ) || string.Compare(e.DOJ, monthEnd, StringComparison.Ordinal) <= 0)
                .Select(e => e.Id).ToHashSet();

            var codes = new[] { "EL", "CL", "SL" };
            var rows = await db.LeaveBalances.Where(b => b.Year == year && codes.Contains(b.LeaveTypeCode)).ToListAsync(ct);
            var prevRows = await db.LeaveBalances.AsNoTracking().Where(b => b.Year == year - 1 && codes.Contains(b.LeaveTypeCode)).ToListAsync(ct);
            var prevByKey = prevRows.GroupBy(b => (b.EmployeeId, b.LeaveTypeCode)).ToDictionary(g => g.Key, g => g.First());

            // January → open the new year's rows from last year's
            if (month == 1)
            {
                var have = rows.Select(r => (r.EmployeeId, r.LeaveTypeCode)).ToHashSet();
                foreach (var p in prevByKey.Values)
                {
                    if (!empIds.Contains(p.EmployeeId) || have.Contains((p.EmployeeId, p.LeaveTypeCode))) continue;
                    var row = new LeaveBalance
                    {
                        EmployeeId = p.EmployeeId,
                        LeaveTypeCode = p.LeaveTypeCode,
                        Year = year,
                        CarryForward = p.LeaveTypeCode == "CL" ? 0m : p.Balance,
                        UpdatedAt = DateTime.Now
                    };
                    db.LeaveBalances.Add(row);
                    rows.Add(row);
                }
            }

            var byKey = rows.GroupBy(b => (b.EmployeeId, b.LeaveTypeCode)).ToDictionary(g => g.Key, g => g.First());
            int credited = 0;
            foreach (var row in rows)
            {
                if (row.LeaveTypeCode == "SL" || !empIds.Contains(row.EmployeeId)) continue;
                if (GetEarned(db, row, month) != 0m) continue;

                decimal amount;
                if (row.LeaveTypeCode == "CL") amount = ClPerMonth;
                else
                {
                    decimal last = 0m;
                    if (month > 1) last = GetEarned(db, row, month - 1);
                    else if (prevByKey.TryGetValue((row.EmployeeId, "EL"), out var p)) last = p.EarnedDec;
                    amount = last == ElStaffRate ? ElStaffRate : ElPerMonth;
                }
                SetEarned(db, row, month, amount);
                row.UpdatedAt = DateTime.Now;
                credited++;
            }

            db.LeaveAccrualRuns.Add(new LeaveAccrualRun { Year = year, Month = month, RowsCredited = credited, RanAt = DateTime.UtcNow });
            await db.SaveChangesAsync(ct);
            return credited;
        }
    }
}
