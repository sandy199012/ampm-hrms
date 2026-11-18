using AmpmHrmsPro.Data;
using AmpmHrmsPro.Models;
using Microsoft.EntityFrameworkCore;

namespace AmpmHrmsPro.Services
{
    /// <summary>One leave balance line as the employee sees it (mobile app + web).</summary>
    public class LeaveBalanceItem
    {
        public string Alias { get; set; } = "";
        public string Name { get; set; } = "";
        public int Year { get; set; }
        public decimal CarryForward { get; set; }
        public decimal Earned { get; set; }
        public decimal Consumed { get; set; }
        public decimal Pending { get; set; }
        public decimal Balance { get; set; }
        /// <summary>"Year" = running yearly balance (EL/CL/SL); "Month" = this month's quota, lapses at month end; "Occasion" = per occasion.</summary>
        public string Period { get; set; } = "Year";
        public string? Note { get; set; }
    }

    // ═══════════════════════════════════════════════════════════════════
    // EMPLOYEE LEAVE SUMMARY — every leave balance an employee should see:
    //   • EL / CL / SL   — yearly rows in LeaveBalances (uploaded + monthly credit)
    //   • Short Leave    — Staff: 2 per month, unused ones lapse at month end
    //   • Bereavement    — Staff: 5 days a year (days from the leave type master)
    //   • Week Off Leave — Retail staff & retail workers (no fixed Sunday off):
    //                      credit = number of Sundays in the month (from the
    //                      joining date if they joined this month); unused
    //                      ones lapse when the next month starts
    // Monthly ones are worked out live from approved applications of the
    // current month, so nothing has to be credited or expired by a job.
    // ═══════════════════════════════════════════════════════════════════
    public static class EmployeeLeaveSummary
    {
        public const decimal ShortLeavePerMonth = 2m;
        static readonly string[] YearlyOrder = { "EL", "CL", "SL" };

        public static bool IsStaff(Employee e) => string.Equals((e.Category ?? "").Trim(), "Staff", StringComparison.OrdinalIgnoreCase);

        public static bool IsRetail(Employee e) =>
            (e.Grade?.Name ?? "").Contains("RETAIL", StringComparison.OrdinalIgnoreCase) ||
            (e.Department?.Name ?? "").Contains("RETAIL", StringComparison.OrdinalIgnoreCase);

        public static int SundaysBetween(DateTime from, DateTime to)
        {
            int n = 0;
            for (var d = from.Date; d <= to.Date; d = d.AddDays(1)) if (d.DayOfWeek == DayOfWeek.Sunday) n++;
            return n;
        }

        static bool IsBereavement(LeaveType t) =>
            t.Alias == "BL" || t.Alias.StartsWith("BL(") || t.Name.Trim().StartsWith("Bereavement", StringComparison.OrdinalIgnoreCase);

        public static async Task<List<LeaveBalanceItem>> BuildAsync(AppDbContext db, int employeeId, int? year = null, CancellationToken ct = default)
        {
            var today = IndiaTime.Today;
            int y = year ?? today.Year;
            var result = new List<LeaveBalanceItem>();

            var e = await db.Employees.AsNoTracking()
                .Include(x => x.Grade).Include(x => x.Department)
                .FirstOrDefaultAsync(x => x.Id == employeeId, ct);
            if (e == null) return result;

            string gender = (e.Gender ?? "").Trim();
            var types = await db.LeaveTypes.AsNoTracking().ToListAsync(ct);
            bool GenderOk(LeaveType t) => t.Gender == "All" || string.IsNullOrEmpty(t.Gender) || t.Gender == gender;
            var activeByAlias = types.Where(t => t.IsActive).GroupBy(t => t.Alias).ToDictionary(g => g.Key, g => g.First());

            var apps = await db.Applications.AsNoTracking()
                .Where(a => a.EmployeeId == employeeId && a.Type == "Leave" && a.LeaveTypeId != null
                            && (a.Status == "Approved" || a.Status == "Pending")
                            && a.FromDate.StartsWith(y.ToString()))
                .Select(a => new { a.LeaveTypeId, a.Status, a.FromDate, a.DurationDays })
                .ToListAsync(ct);

            decimal Sum(IEnumerable<int> typeIds, string status, string prefix) =>
                apps.Where(a => typeIds.Contains(a.LeaveTypeId!.Value) && a.Status == status && a.FromDate.StartsWith(prefix)).Sum(a => a.DurationDays);
            List<int> IdsFor(Func<LeaveType, bool> pick) => types.Where(pick).Select(t => t.Id).ToList();

            // ── Yearly balances (EL / CL / SL …) from the LeaveBalances table ──
            var rows = await db.LeaveBalances.AsNoTracking()
                .Where(b => b.EmployeeId == employeeId && b.Year == y).ToListAsync(ct);
            foreach (var b in rows.OrderBy(b => Array.IndexOf(YearlyOrder, b.LeaveTypeCode) is var i && i < 0 ? 99 : i).ThenBy(b => b.LeaveTypeCode))
            {
                activeByAlias.TryGetValue(b.LeaveTypeCode, out var lt);
                if (lt != null && !GenderOk(lt)) continue;
                result.Add(new LeaveBalanceItem
                {
                    Alias = b.LeaveTypeCode,
                    Name = lt?.Name ?? b.LeaveTypeCode,
                    Year = y,
                    CarryForward = b.CarryForward,
                    Earned = Math.Round(b.TotalEarned, 2),
                    Consumed = b.TotalConsumed,
                    Pending = Sum(IdsFor(t => t.Alias == b.LeaveTypeCode), "Pending", y.ToString()),
                    Balance = Math.Round(b.Balance, 2),
                    Period = "Year",
                });
            }

            // Monthly quotas only make sense for the running year
            if (y != today.Year) return result;
            string monthPrefix = today.ToString("yyyy-MM");
            string monName = today.ToString("MMM");
            var monthStart = new DateTime(today.Year, today.Month, 1);
            var monthEnd = monthStart.AddMonths(1).AddDays(-1);
            DateTime? doj = DateTime.TryParse(e.DOJ, out var dj) ? dj.Date : null;
            if (doj > monthEnd) return result;

            // ── Short Leave — Staff, 2 a month ──
            if (IsStaff(e) && activeByAlias.TryGetValue("SHL", out var shl) && GenderOk(shl))
            {
                var ids = IdsFor(t => t.Alias == "SHL");
                decimal taken = Sum(ids, "Approved", monthPrefix);
                result.Add(new LeaveBalanceItem
                {
                    Alias = "SHL", Name = $"{shl.Name} ({monName})", Year = y,
                    Earned = ShortLeavePerMonth, Consumed = taken, Pending = Sum(ids, "Pending", monthPrefix),
                    Balance = ShortLeavePerMonth - taken, Period = "Month",
                    Note = $"{ShortLeavePerMonth:0.#} per month — unused ones lapse at month end",
                });
            }

            // ── Bereavement Leave — Staff, 5 days a year (days from the leave type master) ──
            var bl = activeByAlias.GetValueOrDefault("BL") ?? types.FirstOrDefault(t => t.IsActive && IsBereavement(t));
            if (IsStaff(e) && bl != null && GenderOk(bl))
            {
                var ids = IdsFor(IsBereavement);
                decimal perYear = bl.DefaultAnnualDays > 0 ? bl.DefaultAnnualDays : 5m;
                decimal taken = Sum(ids, "Approved", y.ToString());
                result.Add(new LeaveBalanceItem
                {
                    Alias = "BL", Name = bl.Name, Year = y,
                    Earned = perYear, Consumed = taken, Pending = Sum(ids, "Pending", y.ToString()),
                    Balance = perYear - taken, Period = "Year",
                    Note = $"{perYear:0.#} days per year",
                });
            }

            // ── Week Off Leave — retail staff & retail workers: Sundays of this month ──
            if (IsRetail(e) && activeByAlias.TryGetValue("WL", out var wl) && GenderOk(wl))
            {
                var ids = IdsFor(t => t.Alias == "WL");
                var from = doj.HasValue && doj > monthStart ? doj.Value : monthStart;
                decimal credit = SundaysBetween(from, monthEnd);
                decimal taken = Sum(ids, "Approved", monthPrefix);
                result.Add(new LeaveBalanceItem
                {
                    Alias = "WL", Name = $"{wl.Name} ({monName})", Year = y,
                    Earned = credit, Consumed = taken, Pending = Sum(ids, "Pending", monthPrefix),
                    Balance = credit - taken, Period = "Month",
                    Note = $"{credit:0} Sunday(s) in {monName} — unused ones lapse when the next month starts",
                });
            }
            return result;
        }
    }
}
