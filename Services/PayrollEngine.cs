using System.Globalization;
using AmpmHrmsPro.Data;
using AmpmHrmsPro.Models;
using Microsoft.EntityFrameworkCore;

namespace AmpmHrmsPro.Services
{
    // ═══════════════════════════════════════════════════════════════════
    // PAYROLL ENGINE — builds the monthly payslips.
    //
    //   Rate      = the component's full-month amount from the employee's
    //               salary structure (Salary > Employee Structure)
    //   Paid days = days in month − loss-of-pay days, where LOP comes from
    //               attendance: Absent / Miss punch = 1, Half day = ½,
    //               Leave Without Pay = 1, days before joining / after
    //               leaving = 1. Paid leave (CL / EL / SL …), week offs and
    //               holidays are paid. HR can override paid days per slip.
    //   Earnings  = Rate × Paid days ÷ Days in month (rounded to ₹1);
    //               ARREAR is never pro-rated.
    //   E.P.F.    = 12% of earned Basic, wage capped at ₹15,000 (max ₹1,800)
    //   E.S.I.C.  = 0.75% of earned gross, only when the full-month gross
    //               is ₹21,000 or less
    //   Advance, I.Tax, LWF, Other … = typed by HR on the payslip.
    // Any amount HR types is marked manual and kept on recalculation.
    // ═══════════════════════════════════════════════════════════════════
    public static class PayrollEngine
    {
        public const decimal PfWageCeiling = 15000m;
        public const decimal PfRate = 0.12m;
        public const decimal EsicGrossLimit = 21000m;
        public const decimal EsicRate = 0.0075m;

        static string Up(string? s) => (s ?? "").Trim().ToUpperInvariant();
        public static bool IsCode(PayslipLine l, params string[] codes) => codes.Contains(Up(l.Code));
        static bool IsCode(SalaryComponent c, params string[] codes) => codes.Contains(Up(c.Code));

        public static async Task<PayrollRun> GetOrCreateRunAsync(AppDbContext db, int year, int month)
        {
            var run = await db.PayrollRuns.FirstOrDefaultAsync(r => r.Year == year && r.Month == month);
            if (run != null) return run;
            run = new PayrollRun { Year = year, Month = month };
            db.PayrollRuns.Add(run);
            await db.SaveChangesAsync();
            return run;
        }

        /// <summary>(Re)builds every payslip of a draft run. Returns (created/updated, skipped-without-structure).</summary>
        public static async Task<(int Done, List<string> NoStructure)> GenerateAsync(AppDbContext db, PayrollRun run, int? onlyEmployeeId = null)
        {
            if (run.Status == "Finalised") throw new InvalidOperationException("This month is finalised — reopen it first.");
            var monthStart = new DateTime(run.Year, run.Month, 1);
            var monthEnd = monthStart.AddMonths(1).AddDays(-1);
            string ms = monthStart.ToString("yyyy-MM-dd"), me = monthEnd.ToString("yyyy-MM-dd");

            var emps = (await db.Employees.Include(e => e.Department).Include(e => e.Designation)
                    .Where(e => onlyEmployeeId == null || e.Id == onlyEmployeeId).ToListAsync())
                .Where(e => (string.IsNullOrEmpty(e.DOJ) || string.Compare(e.DOJ, me) <= 0)
                         && (e.IsActive || (LeftOn(e) is string left && string.Compare(left, ms) >= 0)))
                .ToList();

            var structures = await db.EmployeeSalaryStructures.Include(s => s.Components).ThenInclude(c => c.SalaryComponent)
                .Where(s => string.Compare(s.EffectiveFrom, me) <= 0 && (s.EffectiveTo == null || string.Compare(s.EffectiveTo, ms) >= 0))
                .ToListAsync();
            var structByEmp = structures.GroupBy(s => s.EmployeeId)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(s => s.EffectiveFrom).First());

            var components = await db.SalaryComponents.Where(c => c.IsActive).OrderBy(c => c.DisplayOrder).ThenBy(c => c.Id).ToListAsync();
            var existing = await db.Payslips.Include(p => p.Lines)
                .Where(p => p.PayrollRunId == run.Id && (onlyEmployeeId == null || p.EmployeeId == onlyEmployeeId)).ToListAsync();
            var dailies = await db.AttendanceDailies.AsNoTracking()
                .Where(a => string.Compare(a.Date, ms) >= 0 && string.Compare(a.Date, me) <= 0)
                .Select(a => new { a.EmployeeId, a.Date, a.EffectiveStatus }).ToListAsync();
            var dailyByEmp = dailies.GroupBy(d => d.EmployeeId).ToDictionary(g => g.Key, g => g.Select(x => (x.Date, x.EffectiveStatus)).ToList());

            int done = 0; var none = new List<string>();
            foreach (var e in emps)
            {
                if (!structByEmp.TryGetValue(e.Id, out var st)) { if (e.IsActive || onlyEmployeeId != null) none.Add($"{e.Name} ({e.EmpCode})"); continue; }
                var slip = existing.FirstOrDefault(p => p.EmployeeId == e.Id);
                if (slip == null)
                {
                    slip = new Payslip { PayrollRunId = run.Id, EmployeeId = e.Id, Year = run.Year, Month = run.Month };
                    db.Payslips.Add(slip);
                }
                Snapshot(slip, e);
                var (lop, leave) = LopDays(e, monthStart, monthEnd, dailyByEmp.GetValueOrDefault(e.Id) ?? new());
                slip.DaysInMonth = DateTime.DaysInMonth(run.Year, run.Month);
                slip.LopDays = lop; slip.LeaveDays = leave;
                BuildLines(slip, st, components);
                Recalculate(slip);
                done++;
            }
            // Employees who no longer qualify (left before the month, structure removed) drop out of a draft
            var keep = emps.Where(e => structByEmp.ContainsKey(e.Id)).Select(e => e.Id).ToHashSet();
            foreach (var p in existing.Where(p => !keep.Contains(p.EmployeeId))) db.Payslips.Remove(p);

            run.GeneratedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return (done, none);
        }

        static string? LeftOn(Employee e) => !string.IsNullOrWhiteSpace(e.DateOfLeaving) ? e.DateOfLeaving : e.InactivationDate;

        static void Snapshot(Payslip s, Employee e)
        {
            static string? T(string? v, int n) => string.IsNullOrWhiteSpace(v) ? null : (v.Trim().Length > n ? v.Trim()[..n] : v.Trim());
            s.EmpCode = e.EmpCode; s.EmployeeName = T(e.Name, 120) ?? "";
            s.FatherOrHusbandName = T(e.FatherOrHusbandName, 120);
            s.Designation = T(e.Designation?.Name, 100); s.Department = T(e.Department?.Name, 100);
            s.PFNumber = T(e.PFNumber, 40); s.PANNumber = T(e.PANNumber, 20); s.BankAccount = T(e.AccountNumber, 40);
            s.ESICNumber = T(e.ESICNumber, 40); s.DOJ = T(e.DOJ, 10); s.UANNumber = T(e.UANNumber, 20);
        }

        /// <summary>Loss-of-pay and paid-leave days for the month, from attendance.</summary>
        public static (decimal Lop, decimal Leave) LopDays(Employee e, DateTime monthStart, DateTime monthEnd, List<(string Date, string Status)> days)
        {
            decimal lop = 0, leave = 0;
            DateTime? doj = DateTime.TryParse(e.DOJ, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dj) ? dj.Date : null;
            DateTime? left = DateTime.TryParse(LeftOn(e), CultureInfo.InvariantCulture, DateTimeStyles.None, out var lf) ? lf.Date : null;
            var byDate = days.GroupBy(d => d.Date).ToDictionary(g => g.Key, g => g.First().Status ?? "");
            for (var d = monthStart; d <= monthEnd; d = d.AddDays(1))
            {
                if ((doj.HasValue && d < doj) || (left.HasValue && d > left)) { lop += 1; continue; }
                if (!byDate.TryGetValue(d.ToString("yyyy-MM-dd"), out var st)) continue;   // no attendance row → not cut
                var s = st.Trim().ToUpperInvariant();
                bool lwp = s.Contains("LWP") || s.Contains("LOP");
                if (s == "A" || s.StartsWith("A (") || s.StartsWith("A(")) lop += 1;
                else if (s.StartsWith("L") && !s.StartsWith("LATE")) { if (lwp) lop += 1; else leave += 1; }
                else if (s == "HD") lop += 0.5m;
                else if (s.StartsWith("HD")) { if (lwp) lop += 0.5m; else leave += 0.5m; }
            }
            return (lop, leave);
        }

        /// <summary>Makes sure the slip has one line per active component, with the structure's full-month rate.</summary>
        static void BuildLines(Payslip slip, EmployeeSalaryStructure st, List<SalaryComponent> components)
        {
            var bd = PayrollTaxEngine.BuildBreakdown(st);
            var rateById = bd.Lines.GroupBy(l => l.ComponentId).ToDictionary(g => g.Key, g => g.Sum(x => x.Monthly));
            int order = 0;
            foreach (var c in components)
            {
                var line = slip.Lines.FirstOrDefault(l => l.SalaryComponentId == c.Id);
                if (line == null) { line = new PayslipLine { SalaryComponentId = c.Id }; slip.Lines.Add(line); }
                line.Code = c.Code; line.Name = c.Name; line.Type = c.ComponentType;
                line.Rate = c.ComponentType == "Earning" ? Math.Round(rateById.GetValueOrDefault(c.Id), 0) : 0;
                line.DisplayOrder = order++;
            }
            // components switched off since the last run
            foreach (var l in slip.Lines.Where(l => l.SalaryComponentId != null && !components.Any(c => c.Id == l.SalaryComponentId)).ToList())
                if (!l.IsManual || l.Amount == 0) slip.Lines.Remove(l);
        }

        /// <summary>Paid days → earnings → PF / ESIC → totals. Manual lines are left as typed.</summary>
        public static void Recalculate(Payslip s)
        {
            s.PaidDays = s.PaidDaysOverride ?? Math.Max(0, s.DaysInMonth - s.LopDays);
            decimal factor = s.DaysInMonth == 0 ? 0 : s.PaidDays / s.DaysInMonth;

            foreach (var l in s.Lines.Where(l => l.Type == "Earning" && !l.IsManual))
                l.Amount = IsCode(l, "ARREAR", "ARREAR4") ? l.Rate : Math.Round(l.Rate * factor, 0, MidpointRounding.AwayFromZero);

            var earnings = s.Lines.Where(l => l.Type == "Earning").ToList();
            s.GrossRate = earnings.Sum(l => l.Rate);
            s.GrossEarned = earnings.Sum(l => l.Amount);
            var basic = earnings.Where(l => IsCode(l, "BASIC")).Sum(l => l.Amount);

            foreach (var l in s.Lines.Where(l => l.Type == "Deduction" && !l.IsManual))
            {
                if (IsCode(l, "EPF", "PF"))
                    l.Amount = Math.Round(Math.Min(basic, PfWageCeiling) * PfRate, 0, MidpointRounding.AwayFromZero);
                else if (IsCode(l, "ESIC", "ESI"))
                    l.Amount = s.GrossRate > 0 && s.GrossRate <= EsicGrossLimit ? Math.Ceiling(s.GrossEarned * EsicRate) : 0;
                else l.Amount = 0;
            }
            s.TotalDeductions = s.Lines.Where(l => l.Type == "Deduction").Sum(l => l.Amount);
            s.NetPay = s.GrossEarned - s.TotalDeductions;
            s.UpdatedAt = DateTime.UtcNow;
        }

        // ── Amount in words (Indian numbering) ──────────────────────────────
        static readonly string[] Ones = { "", "One", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight", "Nine", "Ten",
            "Eleven", "Twelve", "Thirteen", "Fourteen", "Fifteen", "Sixteen", "Seventeen", "Eighteen", "Nineteen" };
        static readonly string[] Tens = { "", "", "Twenty", "Thirty", "Forty", "Fifty", "Sixty", "Seventy", "Eighty", "Ninety" };

        static string Two(long n) => n < 20 ? Ones[n] : Tens[n / 10] + (n % 10 > 0 ? " " + Ones[n % 10] : "");
        static string Three(long n) => n >= 100 ? Ones[n / 100] + " Hundred" + (n % 100 > 0 ? " " + Two(n % 100) : "") : Two(n);

        public static string InWords(decimal amount)
        {
            long n = (long)Math.Round(Math.Abs(amount), 0, MidpointRounding.AwayFromZero);
            if (n == 0) return "Rupees Zero Only";
            var parts = new List<string>();
            if (n >= 10000000) { parts.Add(Three(n / 10000000) + " Crore"); n %= 10000000; }
            if (n >= 100000) { parts.Add(Two(n / 100000) + " Lakh"); n %= 100000; }
            if (n >= 1000) { parts.Add(Two(n / 1000) + " Thousand"); n %= 1000; }
            if (n > 0) parts.Add(Three(n));
            return "Rupees " + string.Join(" ", parts) + " Only";
        }
    }
}
