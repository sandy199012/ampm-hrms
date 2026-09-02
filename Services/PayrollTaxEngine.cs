using AmpmHrmsPro.Data;
using AmpmHrmsPro.Models;
using Microsoft.EntityFrameworkCore;

namespace AmpmHrmsPro.Services
{
    // ── Result shapes ──────────────────────────────────────────────────
    public class SalaryBreakdown
    {
        public decimal AnnualCTC { get; set; }
        public decimal MonthlyBasic { get; set; }
        public decimal AnnualBasic { get; set; }
        public decimal MonthlyHRA { get; set; }
        public decimal AnnualHRA { get; set; }
        public decimal MonthlyGrossEarnings { get; set; }
        public decimal AnnualGrossEarnings { get; set; }
        public decimal MonthlyTaxableEarnings { get; set; }
        public decimal AnnualTaxableEarnings { get; set; }
        public decimal MonthlyDeductions { get; set; }
        public decimal AnnualDeductions { get; set; }
        public decimal MonthlyNet { get; set; }
        public decimal AnnualNet { get; set; }
        public List<SalaryLine> Lines { get; set; } = new();
    }

    // Plain class, not a tuple — see Models/DashboardViewModels.cs's header
    // comment for why (ValueTuple named elements don't survive a
    // ViewBag/dynamic round-trip; a real class's properties do).
    public class SalaryLine
    {
        public int ComponentId { get; set; }
        public string Name { get; set; } = "";
        public string Type { get; set; } = "";
        public decimal Monthly { get; set; }
        public decimal Annual { get; set; }
    }

    public class RegimeTaxResult
    {
        public string Regime { get; set; } = "";
        public decimal GrossTaxableSalary { get; set; }
        public decimal StandardDeduction { get; set; }
        public decimal HRAExemption { get; set; }
        public decimal OtherDeductionsApproved { get; set; }
        public decimal TaxableIncome { get; set; }
        public decimal TaxBeforeRebate { get; set; }
        public decimal Rebate87A { get; set; }
        public decimal TaxAfterRebate { get; set; }
        public decimal Surcharge { get; set; }
        public decimal Cess { get; set; }
        public decimal FinalAnnualTax { get; set; }
    }

    public class TaxComputationResult
    {
        public int EmployeeId { get; set; }
        public string FinancialYear { get; set; } = "";
        public bool HasSalaryStructure { get; set; }
        public SalaryBreakdown? Salary { get; set; }
        public RegimeTaxResult? OldRegime { get; set; }
        public RegimeTaxResult? NewRegime { get; set; }
        public string RecommendedRegime { get; set; } = "";
        public string RegimeChoice { get; set; } = "Auto";
        public string RegimeUsed { get; set; } = "";
        public decimal FinalAnnualTax { get; set; }
        public decimal SuggestedMonthlyTDS { get; set; }
        public List<string> Notes { get; set; } = new();
    }

    public interface IPayrollTaxEngine
    {
        SalaryBreakdown? ComputeSalaryBreakdown(int employeeId, string? asOfDate = null);
        TaxComputationResult ComputeTax(int employeeId, string financialYear);
        string CurrentFinancialYear();
    }

    // ═══════════════════════════════════════════
    // PAYROLL TAX ENGINE — turns an employee's assigned salary structure
    // plus their approved investment declaration into (1) a monthly/annual
    // salary breakdown and (2) a full Old-vs-New-regime tax computation
    // with a recommended regime and a suggested monthly TDS. Every rate,
    // slab, and limit it reads comes from the DB (TaxSlabSettings /
    // TaxSectionMaster) — nothing is hardcoded here, so a future Union
    // Budget change is an Admin screen edit, never a code change.
    //
    // Known, deliberate simplifications (documented so nobody mistakes
    // this for a certified tax filing tool):
    //  - Marginal relief on the Section 87A rebate and on surcharge (the
    //    narrow-band "tax can't exceed income past a threshold" rule) is
    //    NOT applied — only the plain threshold/rate rules are.
    //  - Monthly TDS is a flat even split of the annual liability
    //    (FinalAnnualTax / 12), not a running reconciliation against a
    //    deduction ledger — there is no TdsDeductionLog in this build.
    // ═══════════════════════════════════════════
    public class PayrollTaxEngine : IPayrollTaxEngine
    {
        private readonly AppDbContext _db;
        public PayrollTaxEngine(AppDbContext db) => _db = db;

        public string CurrentFinancialYear()
        {
            var today = DateTime.Now;
            int startYear = today.Month >= 4 ? today.Year : today.Year - 1;
            return $"{startYear}-{(startYear + 1).ToString().Substring(2)}";
        }

        public SalaryBreakdown? ComputeSalaryBreakdown(int employeeId, string? asOfDate = null)
        {
            var date = asOfDate ?? DateTime.Now.ToString("yyyy-MM-dd");
            var structure = _db.EmployeeSalaryStructures
                .Include(s => s.Components).ThenInclude(c => c.SalaryComponent)
                .Where(s => s.EmployeeId == employeeId)
                .Where(s => string.Compare(s.EffectiveFrom, date) <= 0 && (s.EffectiveTo == null || string.Compare(s.EffectiveTo, date) >= 0))
                .OrderByDescending(s => s.EffectiveFrom)
                .FirstOrDefault();
            if (structure == null) return null;
            return BuildBreakdown(structure);
        }

        // Multi-pass resolution so percentage-based components never read
        // an unresolved value:
        //   Pass 1 — Fixed and PercentOfCTC (depend on nothing else)
        //   Pass 2 — PercentOfBasic (needs Basic from pass 1; the component
        //            marked IsBasic must itself be Fixed or PercentOfCTC —
        //            SalaryController refuses to save IsBasic+PercentOfBasic
        //            together, since that would be circular)
        //   Pass 3 — PercentOfGross, where "Gross" means the gross earnings
        //            resolved in passes 1-2 only — NOT including other
        //            PercentOfGross components, which keeps the whole
        //            resolution well-defined even with more than one such
        //            component (each is a % of the same base, not of each
        //            other).
        public static SalaryBreakdown BuildBreakdown(EmployeeSalaryStructure structure)
        {
            var bd = new SalaryBreakdown { AnnualCTC = structure.AnnualCTC };
            var comps = structure.Components.Where(c => c.SalaryComponent != null && c.SalaryComponent.IsActive).ToList();
            var resolved = new Dictionary<int, decimal>();

            foreach (var c in comps)
            {
                if (c.CalculationType == "Fixed") resolved[c.Id] = c.Value;
                else if (c.CalculationType == "PercentOfCTC") resolved[c.Id] = Math.Round((structure.AnnualCTC / 12m) * (c.Value / 100m), 2);
            }

            var basicComp = comps.FirstOrDefault(c => c.SalaryComponent!.IsBasic);
            var basicMonthly = (basicComp != null && resolved.TryGetValue(basicComp.Id, out var bv)) ? bv : 0;

            foreach (var c in comps)
                if (c.CalculationType == "PercentOfBasic")
                    resolved[c.Id] = Math.Round(basicMonthly * (c.Value / 100m), 2);

            var grossSoFar = comps.Where(c => c.SalaryComponent!.ComponentType == "Earning" && resolved.ContainsKey(c.Id))
                .Sum(c => resolved[c.Id]);
            foreach (var c in comps)
                if (c.CalculationType == "PercentOfGross")
                    resolved[c.Id] = Math.Round(grossSoFar * (c.Value / 100m), 2);

            foreach (var c in comps)
            {
                var monthly = resolved.TryGetValue(c.Id, out var v) ? v : 0;
                var annual = Math.Round(monthly * 12m, 2);
                var sc = c.SalaryComponent!;
                bd.Lines.Add(new SalaryLine { ComponentId = c.SalaryComponentId, Name = sc.Name, Type = sc.ComponentType, Monthly = monthly, Annual = annual });

                if (sc.ComponentType == "Earning")
                {
                    bd.MonthlyGrossEarnings += monthly;
                    bd.AnnualGrossEarnings += annual;
                    if (sc.IsTaxable) { bd.MonthlyTaxableEarnings += monthly; bd.AnnualTaxableEarnings += annual; }
                    if (sc.IsBasic) { bd.MonthlyBasic = monthly; bd.AnnualBasic = annual; }
                    if (sc.IsHRA) { bd.MonthlyHRA = monthly; bd.AnnualHRA = annual; }
                }
                else
                {
                    bd.MonthlyDeductions += monthly;
                    bd.AnnualDeductions += annual;
                }
            }
            bd.MonthlyNet = bd.MonthlyGrossEarnings - bd.MonthlyDeductions;
            bd.AnnualNet = bd.AnnualGrossEarnings - bd.AnnualDeductions;
            return bd;
        }

        // Section 10(13A) HRA exemption = least of:
        //   1. HRA actually received (annual)
        //   2. Rent paid annual − 10% of Basic annual (floored at 0)
        //   3. 50% of Basic annual (metro) or 40% (non-metro)
        public static decimal ComputeHraExemption(decimal hraReceivedAnnual, decimal basicAnnual, decimal rentPaidAnnual, bool isMetro)
        {
            if (hraReceivedAnnual <= 0 || rentPaidAnnual <= 0) return 0;
            var byRent = Math.Max(0, rentPaidAnnual - 0.10m * basicAnnual);
            var byCityCap = (isMetro ? 0.50m : 0.40m) * basicAnnual;
            return Math.Max(0, Math.Min(hraReceivedAnnual, Math.Min(byRent, byCityCap)));
        }

        private TaxSlabSettings? ResolveSlabSettings(string financialYear, string regime, out string usedFinancialYear)
        {
            var exact = _db.TaxSlabSettingsList.Include(s => s.Slabs).Include(s => s.SurchargeSlabs)
                .FirstOrDefault(s => s.FinancialYear == financialYear && s.Regime == regime && s.IsActive);
            if (exact != null) { usedFinancialYear = exact.FinancialYear; return exact; }

            // Fallback — the requested FY was never configured (e.g. a new
            // Budget year Admin hasn't added yet). Use the most recent
            // configured FY instead of failing outright; ComputeTax flags
            // this in its Notes so Admin knows to add the real year.
            var latest = _db.TaxSlabSettingsList.Include(s => s.Slabs).Include(s => s.SurchargeSlabs)
                .Where(s => s.Regime == regime && s.IsActive)
                .OrderByDescending(s => s.FinancialYear)
                .FirstOrDefault();
            usedFinancialYear = latest?.FinancialYear ?? "";
            return latest;
        }

        private static decimal ComputeSlabTax(decimal taxableIncome, List<TaxSlab> slabs)
        {
            decimal tax = 0;
            foreach (var slab in slabs.OrderBy(s => s.FromAmount))
            {
                if (taxableIncome <= slab.FromAmount) continue;
                var top = slab.ToAmount.HasValue ? Math.Min(taxableIncome, slab.ToAmount.Value) : taxableIncome;
                var slice = top - slab.FromAmount;
                if (slice > 0) tax += slice * (slab.RatePercent / 100m);
            }
            return Math.Round(tax, 2);
        }

        private static decimal ComputeSurcharge(decimal taxableIncome, decimal taxBeforeSurcharge, List<TaxSurchargeSlab> slabs)
        {
            var tier = slabs
                .Where(s => taxableIncome > s.FromAmount && (!s.ToAmount.HasValue || taxableIncome <= s.ToAmount.Value))
                .OrderByDescending(s => s.FromAmount)
                .FirstOrDefault();
            if (tier == null) return 0;
            return Math.Round(taxBeforeSurcharge * (tier.RatePercent / 100m), 2);
        }

        private RegimeTaxResult ComputeRegime(string regime, string financialYear, decimal grossTaxableAnnual,
            decimal basicAnnual, decimal hraReceivedAnnual, decimal rentPaidAnnual, bool isMetro,
            decimal approvedSectionDeductions, out string usedFy)
        {
            var settings = ResolveSlabSettings(financialYear, regime, out usedFy);
            var r = new RegimeTaxResult { Regime = regime, GrossTaxableSalary = grossTaxableAnnual };
            if (settings == null) return r;

            r.StandardDeduction = settings.StandardDeduction;
            r.HRAExemption = regime == "Old" ? ComputeHraExemption(hraReceivedAnnual, basicAnnual, rentPaidAnnual, isMetro) : 0;
            r.OtherDeductionsApproved = approvedSectionDeductions;

            r.TaxableIncome = Math.Round(Math.Max(0, grossTaxableAnnual - r.StandardDeduction - r.HRAExemption - r.OtherDeductionsApproved), 0);
            r.TaxBeforeRebate = ComputeSlabTax(r.TaxableIncome, settings.Slabs.ToList());

            if (r.TaxableIncome <= settings.Rebate87AIncomeLimit)
                r.Rebate87A = Math.Min(r.TaxBeforeRebate, settings.Rebate87AMaxAmount);
            r.TaxAfterRebate = Math.Max(0, r.TaxBeforeRebate - r.Rebate87A);

            r.Surcharge = ComputeSurcharge(r.TaxableIncome, r.TaxAfterRebate, settings.SurchargeSlabs.ToList());
            r.Cess = Math.Round((r.TaxAfterRebate + r.Surcharge) * (settings.CessPercent / 100m), 2);
            r.FinalAnnualTax = Math.Round(r.TaxAfterRebate + r.Surcharge + r.Cess, 2);
            return r;
        }

        public TaxComputationResult ComputeTax(int employeeId, string financialYear)
        {
            var result = new TaxComputationResult { EmployeeId = employeeId, FinancialYear = financialYear };

            var breakdown = ComputeSalaryBreakdown(employeeId);
            result.HasSalaryStructure = breakdown != null;
            if (breakdown == null)
            {
                result.Notes.Add("No active salary structure assigned to this employee — assign one from Salary > Employee Structure before computing tax.");
                return result;
            }
            result.Salary = breakdown;

            var header = _db.TaxDeclarationHeaders
                .Include(h => h.Items).ThenInclude(i => i.Section)
                .FirstOrDefault(h => h.EmployeeId == employeeId && h.FinancialYear == financialYear);
            result.RegimeChoice = header?.RegimeChoice ?? "Auto";

            var approvedAll = 0m;
            var approvedBothRegimesOnly = 0m;
            if (header != null)
            {
                var bySection = header.Items.Where(i => i.Status == "Approved")
                    .GroupBy(i => i.TaxSectionMasterId)
                    .Select(g => new { Section = g.First().Section, Total = g.Sum(i => i.ApprovedAmount ?? 0) });
                foreach (var s in bySection)
                {
                    var cap = s.Section?.MaxLimit;
                    var capped = cap.HasValue ? Math.Min(s.Total, cap.Value) : s.Total;
                    approvedAll += capped;
                    if (s.Section?.ApplicableRegime == "Both") approvedBothRegimesOnly += capped;
                }
            }

            var rentPaid = header?.AnnualRentPaid ?? 0;
            var isMetro = header?.IsMetroCity ?? false;

            result.OldRegime = ComputeRegime("Old", financialYear, breakdown.AnnualTaxableEarnings, breakdown.AnnualBasic,
                breakdown.AnnualHRA, rentPaid, isMetro, approvedAll, out var usedFyOld);
            result.NewRegime = ComputeRegime("New", financialYear, breakdown.AnnualTaxableEarnings, breakdown.AnnualBasic,
                breakdown.AnnualHRA, rentPaid, isMetro, approvedBothRegimesOnly, out var usedFyNew);

            if (usedFyOld == "" || usedFyNew == "")
                result.Notes.Add("Tax slab settings are missing for one or both regimes — set them up from Tax > Slab Settings before relying on this figure.");
            else
            {
                if (usedFyOld != financialYear)
                    result.Notes.Add($"No Old Regime slabs configured for FY {financialYear} — used the most recently configured year, FY {usedFyOld}, instead.");
                if (usedFyNew != financialYear)
                    result.Notes.Add($"No New Regime slabs configured for FY {financialYear} — used the most recently configured year, FY {usedFyNew}, instead.");
            }
            if (result.OldRegime.Surcharge > 0 || result.NewRegime.Surcharge > 0)
                result.Notes.Add("Marginal relief on surcharge is not applied in this estimate — for income very close to a surcharge threshold, get this verified by a CA.");

            result.RecommendedRegime = (result.OldRegime.FinalAnnualTax <= result.NewRegime.FinalAnnualTax) ? "Old" : "New";
            result.RegimeUsed = result.RegimeChoice switch
            {
                "Old" => "Old",
                "New" => "New",
                _ => result.RecommendedRegime
            };
            result.FinalAnnualTax = result.RegimeUsed == "Old" ? result.OldRegime.FinalAnnualTax : result.NewRegime.FinalAnnualTax;
            result.SuggestedMonthlyTDS = Math.Round(result.FinalAnnualTax / 12m, 2);

            return result;
        }
    }
}
