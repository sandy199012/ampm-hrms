using AmpmHrmsPro.Data;
using AmpmHrmsPro.Models;

namespace AmpmHrmsPro.Services
{
    // One-time starter data so the app is usable the moment it first runs —
    // every list here is just a starting point. Admin can rename, deactivate,
    // or add to any of it from the Masters screens; nothing here is fixed.
    public static class SeedData
    {
        public static void Run(AppDbContext db, IConfiguration cfg)
        {
            if (!db.Departments.Any())
            {
                var names = new[]
                {
                    "PRODUCTION - APPAREL", "PRODUCTION - LEATHER", "PRODUCTION - MTO",
                    "DESIGN - APPAREL", "DESIGN - ACCESSORIES", "SAMPLING - APPAREL",
                    "MERCHANDISING", "QUALITY", "ACCOUNTS & FINANCE", "HR", "ADMINISTRATION",
                    "IT", "WAREHOUSE & LOGISTICS", "MARKETING", "MANAGEMENT", "RETAILS",
                    "E-COMMERCE", "RESEARCH & DEVELOPMENT"
                };
                db.Departments.AddRange(names.Select(n => new Department { Name = n }));
                db.SaveChanges();
                Console.WriteLine("✅ Departments seeded");
            }

            if (!db.Designations.Any())
            {
                var names = new[] { "Executive", "Senior Executive", "Assistant Manager", "Manager", "Senior Manager", "General Manager", "Director" };
                db.Designations.AddRange(names.Select(n => new Designation { Name = n }));
                db.SaveChanges();
                Console.WriteLine("✅ Designations seeded");
            }

            if (!db.Grades.Any())
            {
                var names = new[] { "Grade 1", "Grade 2", "Grade 3", "Grade 4", "Grade 5" };
                db.Grades.AddRange(names.Select(n => new Grade { Name = n }));
                db.SaveChanges();
                Console.WriteLine("✅ Grades seeded");
            }

            if (!db.EmploymentTypes.Any())
            {
                var names = new[] { "Permanent", "Probation", "Contract", "Consultant", "Intern", "Trainee", "Part-Time", "Temporary" };
                db.EmploymentTypes.AddRange(names.Select(n => new EmploymentType { Name = n }));
                db.SaveChanges();
                Console.WriteLine("✅ Employment types seeded");
            }

            if (!db.Locations.Any())
            {
                db.Locations.Add(new Location
                {
                    Name = "Head Office",
                    Code = "HO",
                    Address = cfg["CompanyAddress"] ?? "",
                    City = "Noida",
                    State = "Uttar Pradesh"
                });
                db.SaveChanges();
                Console.WriteLine("✅ Default location seeded");
            }

            if (!db.WeekOffPolicies.Any())
            {
                db.WeekOffPolicies.Add(new WeekOffPolicy
                {
                    Name = "Corporate Staff (1st & 3rd Sat + Sunday)",
                    Description = "Head office / corporate staff — 1st & 3rd Saturday off, every Sunday off",
                    Rules = new List<WeekOffRule>
                    {
                        new() { DayOfWeek = "Saturday", RuleType = "NthOccurrence", Occurrences = "1,3" },
                        new() { DayOfWeek = "Sunday",   RuleType = "Weekly" }
                    }
                });
                db.WeekOffPolicies.Add(new WeekOffPolicy
                {
                    Name = "Factory Staff (Sunday Only)",
                    Description = "Factory / production staff — every Sunday off, all Saturdays are working days",
                    Rules = new List<WeekOffRule>
                    {
                        new() { DayOfWeek = "Sunday", RuleType = "Weekly" }
                    }
                });
                db.SaveChanges();
                Console.WriteLine("✅ Default week-off policies seeded");
            }

            if (!db.Shifts.Any())
            {
                db.Shifts.Add(new Shift { Name = "General Shift", StartTime = new TimeSpan(9, 30, 0), EndTime = new TimeSpan(18, 30, 0) });
                db.SaveChanges();
                Console.WriteLine("✅ Default shift seeded");
            }

            if (!db.LeaveTypes.Any())
            {
                db.LeaveTypes.AddRange(
                    new LeaveType { Name = "Earned Leave", Alias = "EL", Frequency = "Yearly", DefaultAnnualDays = 18, CarryForward = true },
                    new LeaveType { Name = "Casual Leave", Alias = "CL", Frequency = "Monthly", DefaultAnnualDays = 12 },
                    new LeaveType { Name = "Sick Leave", Alias = "SL", Frequency = "Yearly", DefaultAnnualDays = 12 },
                    new LeaveType { Name = "Comp Off", Alias = "CO", Frequency = "On Request", IsCompOff = true },
                    new LeaveType { Name = "Leave Without Pay", Alias = "LWP", Frequency = "On Request" },
                    new LeaveType { Name = "Bereavement Leave", Alias = "BL", Frequency = "On Request", DefaultAnnualDays = 3 },
                    new LeaveType { Name = "Short Leave", Alias = "SHL", Frequency = "Monthly" },
                    new LeaveType { Name = "Paternity Leave", Alias = "PL", Gender = "Male", Frequency = "Yearly", DefaultAnnualDays = 15 },
                    new LeaveType { Name = "Maternity Leave", Alias = "ML", Gender = "Female", Frequency = "Yearly", DefaultAnnualDays = 182 }
                );
                db.SaveChanges();
                Console.WriteLine("✅ Leave types seeded");
            }

            // ═══════════════════════════════════════════
            // SALARY COMPONENTS — a sensible starting structure (Basic +
            // HRA + Special Allowance as earnings, PF + Professional Tax as
            // deductions). Admin can rename/add/deactivate any of these or
            // build entirely different ones from Salary > Components.
            // ═══════════════════════════════════════════
            if (!db.SalaryComponents.Any())
            {
                db.SalaryComponents.AddRange(
                    new SalaryComponent { Name = "Basic", Code = "BASIC", ComponentType = "Earning", CalculationType = "PercentOfCTC", DefaultValue = 40, IsBasic = true, IsTaxable = true, DisplayOrder = 1 },
                    new SalaryComponent { Name = "House Rent Allowance", Code = "HRA", ComponentType = "Earning", CalculationType = "PercentOfBasic", DefaultValue = 50, IsHRA = true, IsTaxable = true, DisplayOrder = 2 },
                    new SalaryComponent { Name = "Special Allowance", Code = "SPL", ComponentType = "Earning", CalculationType = "PercentOfCTC", DefaultValue = 10, IsTaxable = true, DisplayOrder = 3 },
                    new SalaryComponent { Name = "Conveyance Allowance", Code = "CONV", ComponentType = "Earning", CalculationType = "Fixed", DefaultValue = 1600, IsTaxable = true, DisplayOrder = 4 },
                    new SalaryComponent { Name = "Provident Fund (Employee)", Code = "PF", ComponentType = "Deduction", CalculationType = "PercentOfBasic", DefaultValue = 12, IsTaxable = false, DisplayOrder = 5 },
                    new SalaryComponent { Name = "Professional Tax", Code = "PT", ComponentType = "Deduction", CalculationType = "Fixed", DefaultValue = 200, IsTaxable = false, DisplayOrder = 6 }
                );
                db.SaveChanges();
                Console.WriteLine("✅ Salary components seeded");
            }

            // ═══════════════════════════════════════════
            // TAX SECTION MASTER — common Old-Regime deduction sections.
            // Limits are editable from Tax > Sections; this is only a
            // sensible starting point, not a hardcoded law.
            // ═══════════════════════════════════════════
            if (!db.TaxSectionMasters.Any())
            {
                db.TaxSectionMasters.AddRange(
                    new TaxSectionMaster { Code = "80C", Name = "Section 80C (PPF, ELSS, Life Insurance, Tuition Fees, etc.)", MaxLimit = 150000, ApplicableRegime = "Old", RequiresDocument = true, DisplayOrder = 1 },
                    new TaxSectionMaster { Code = "80CCD1B", Name = "Section 80CCD(1B) — additional NPS contribution", MaxLimit = 50000, ApplicableRegime = "Old", RequiresDocument = true, DisplayOrder = 2 },
                    new TaxSectionMaster { Code = "80D-SELF", Name = "Section 80D — Medical Insurance (Self & Family)", MaxLimit = 25000, ApplicableRegime = "Old", RequiresDocument = true, DisplayOrder = 3 },
                    new TaxSectionMaster { Code = "80D-PARENT", Name = "Section 80D — Medical Insurance (Parents)", MaxLimit = 50000, ApplicableRegime = "Old", RequiresDocument = true, DisplayOrder = 4 },
                    new TaxSectionMaster { Code = "80E", Name = "Section 80E — Education Loan Interest", MaxLimit = null, ApplicableRegime = "Old", RequiresDocument = true, DisplayOrder = 5 },
                    new TaxSectionMaster { Code = "80G", Name = "Section 80G — Donations to Approved Funds/Charities", MaxLimit = null, ApplicableRegime = "Old", RequiresDocument = true, DisplayOrder = 6 },
                    new TaxSectionMaster { Code = "80TTA", Name = "Section 80TTA — Savings Account Interest", MaxLimit = 10000, ApplicableRegime = "Old", RequiresDocument = false, DisplayOrder = 7 },
                    new TaxSectionMaster { Code = "24B", Name = "Section 24(b) — Home Loan Interest (Self-Occupied)", MaxLimit = 200000, ApplicableRegime = "Old", RequiresDocument = true, DisplayOrder = 8 },
                    new TaxSectionMaster { Code = "80CCD2", Name = "Section 80CCD(2) — Employer NPS Contribution", MaxLimit = null, ApplicableRegime = "Both", RequiresDocument = false, DisplayOrder = 9 }
                );
                db.SaveChanges();
                Console.WriteLine("✅ Tax sections seeded");
            }

            // ═══════════════════════════════════════════
            // TAX SLAB SETTINGS — FY 2026-27 (AY 2027-28), Old + New Regime.
            // Figures verified via web search on 2026-08-26 (ClearTax,
            // BankBazaar — Budget 2026 made no change to FY 2025-26 rates
            // for FY 2026-27) rather than hardcoded from training memory,
            // since this drives real payroll TDS. Fully editable afterwards
            // from Tax > Slab Settings, and PayrollTaxEngine falls back to
            // whichever FinancialYear row is newest if a later year is
            // never seeded — see PayrollTaxEngine.ResolveSlabSettings.
            // ═══════════════════════════════════════════
            if (!db.TaxSlabSettingsList.Any())
            {
                var newRegime = new TaxSlabSettings
                {
                    FinancialYear = "2026-27",
                    Regime = "New",
                    StandardDeduction = 75000,
                    Rebate87AIncomeLimit = 1200000,
                    Rebate87AMaxAmount = 60000,
                    CessPercent = 4,
                    Slabs = new List<TaxSlab>
                    {
                        new() { FromAmount = 0,        ToAmount = 400000,   RatePercent = 0,  DisplayOrder = 1 },
                        new() { FromAmount = 400000,   ToAmount = 800000,   RatePercent = 5,  DisplayOrder = 2 },
                        new() { FromAmount = 800000,   ToAmount = 1200000,  RatePercent = 10, DisplayOrder = 3 },
                        new() { FromAmount = 1200000,  ToAmount = 1600000,  RatePercent = 15, DisplayOrder = 4 },
                        new() { FromAmount = 1600000,  ToAmount = 2000000,  RatePercent = 20, DisplayOrder = 5 },
                        new() { FromAmount = 2000000,  ToAmount = 2400000,  RatePercent = 25, DisplayOrder = 6 },
                        new() { FromAmount = 2400000,  ToAmount = null,     RatePercent = 30, DisplayOrder = 7 },
                    },
                    SurchargeSlabs = new List<TaxSurchargeSlab>
                    {
                        new() { FromAmount = 5000000,  ToAmount = 10000000, RatePercent = 10, DisplayOrder = 1 },
                        new() { FromAmount = 10000000, ToAmount = 20000000, RatePercent = 15, DisplayOrder = 2 },
                        new() { FromAmount = 20000000, ToAmount = null,     RatePercent = 25, DisplayOrder = 3 }, // New Regime caps surcharge at 25% — no 37% top tier
                    }
                };
                var oldRegime = new TaxSlabSettings
                {
                    FinancialYear = "2026-27",
                    Regime = "Old",
                    StandardDeduction = 50000,
                    Rebate87AIncomeLimit = 500000,
                    Rebate87AMaxAmount = 12500,
                    CessPercent = 4,
                    Slabs = new List<TaxSlab>
                    {
                        new() { FromAmount = 0,       ToAmount = 250000,  RatePercent = 0,  DisplayOrder = 1 },
                        new() { FromAmount = 250000,  ToAmount = 500000,  RatePercent = 5,  DisplayOrder = 2 },
                        new() { FromAmount = 500000,  ToAmount = 1000000, RatePercent = 20, DisplayOrder = 3 },
                        new() { FromAmount = 1000000, ToAmount = null,    RatePercent = 30, DisplayOrder = 4 },
                    },
                    SurchargeSlabs = new List<TaxSurchargeSlab>
                    {
                        new() { FromAmount = 5000000,  ToAmount = 10000000, RatePercent = 10, DisplayOrder = 1 },
                        new() { FromAmount = 10000000, ToAmount = 20000000, RatePercent = 15, DisplayOrder = 2 },
                        new() { FromAmount = 20000000, ToAmount = 50000000, RatePercent = 25, DisplayOrder = 3 },
                        new() { FromAmount = 50000000, ToAmount = null,     RatePercent = 37, DisplayOrder = 4 },
                    }
                };
                db.TaxSlabSettingsList.AddRange(newRegime, oldRegime);
                db.SaveChanges();
                Console.WriteLine("✅ Tax slab settings seeded (FY 2026-27, Old + New Regime)");
            }

            if (!db.Employees.Any(e => e.Role == "admin"))
            {
                var admin = cfg["AdminDepartmentCode"]; // reserved for future use
                var adminDept = db.Departments.FirstOrDefault(d => d.Name == "ADMINISTRATION");
                db.Employees.Add(new Employee
                {
                    EmpCode      = "ADMIN001",
                    Name         = "HR Admin",
                    Email        = cfg["AdminEmail"] ?? "admin@ampmfashions.com",
                    PasswordHash = BCrypt.Net.BCrypt.HashPassword(cfg["AdminPassword"] ?? "AMPM@Admin123"),
                    Role         = "admin",
                    DepartmentId = adminDept?.Id,
                    Status       = "Active",
                    IsActive     = true
                });
                db.SaveChanges();
                Console.WriteLine("✅ Admin seeded: ADMIN001");
            }
        }
    }
}
