using Microsoft.EntityFrameworkCore;
using AmpmHrmsPro.Models;

namespace AmpmHrmsPro.Data
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        public DbSet<Employee>       Employees       { get; set; }
        public DbSet<Department>     Departments     { get; set; }
        public DbSet<Designation>    Designations    { get; set; }
        public DbSet<Grade>          Grades          { get; set; }
        public DbSet<EmploymentType> EmploymentTypes { get; set; }
        public DbSet<Location>       Locations       { get; set; }
        public DbSet<WeekOffPolicy>  WeekOffPolicies { get; set; }
        public DbSet<WeekOffRule>    WeekOffRules    { get; set; }
        public DbSet<Shift>          Shifts          { get; set; }
        public DbSet<Holiday>        Holidays        { get; set; }
        public DbSet<LeaveType>      LeaveTypes      { get; set; }
        public DbSet<LeavePolicy>     LeavePolicies     { get; set; }
        public DbSet<LeavePolicyRule> LeavePolicyRules  { get; set; }

        public DbSet<AttendancePunch>         AttendancePunches         { get; set; }
        public DbSet<AttendanceDaily>         AttendanceDailies         { get; set; }
        public DbSet<BiometricApiSettings>    BiometricApiSettingsList  { get; set; }
        public DbSet<AttendanceImportProfile> AttendanceImportProfiles  { get; set; }
        public DbSet<Application>             Applications              { get; set; }

        // ── Mobile app ──
        public DbSet<FaceProfile>          FaceProfiles          { get; set; }
        public DbSet<FaceMatchApiSettings> FaceMatchApiSettingsList { get; set; }
        public DbSet<Notification>         Notifications         { get; set; }
        public DbSet<KioskDevice>          KioskDevices          { get; set; }
        public DbSet<EmailSettings>        EmailSettingsList     { get; set; }

        // ── Salary Structure / TDS / Income Tax ──
        public DbSet<SalaryComponent>              SalaryComponents             { get; set; }
        public DbSet<SalaryStructureTemplate>      SalaryStructureTemplates     { get; set; }
        public DbSet<SalaryStructureTemplateItem>  SalaryStructureTemplateItems { get; set; }
        public DbSet<EmployeeSalaryStructure>      EmployeeSalaryStructures     { get; set; }
        public DbSet<EmployeeSalaryComponent>      EmployeeSalaryComponents     { get; set; }
        public DbSet<TaxSlabSettings>              TaxSlabSettingsList          { get; set; }
        public DbSet<TaxSlab>                      TaxSlabs                     { get; set; }
        public DbSet<TaxSurchargeSlab>             TaxSurchargeSlabs            { get; set; }
        public DbSet<TaxSectionMaster>             TaxSectionMasters            { get; set; }
        public DbSet<TaxDeclarationHeader>         TaxDeclarationHeaders        { get; set; }
        public DbSet<TaxDeclarationItem>           TaxDeclarationItems          { get; set; }

        // ── Comp-Off Rule ──
        public DbSet<CompOffRule>        CompOffRules        { get; set; }
        public DbSet<CompOffLedger>      CompOffLedgers      { get; set; }
        public DbSet<CompOffConsumption> CompOffConsumptions { get; set; }

        // ── OT (Overtime) Rule ──
        public DbSet<OTRule>   OTRules   { get; set; }
        public DbSet<OTLedger> OTLedgers { get; set; }

        // ── Leave Balance (EL / CL) ──
        public DbSet<LeaveBalance> LeaveBalances { get; set; }

        protected override void OnModelCreating(ModelBuilder mb)
        {
            // Unique indexes
            mb.Entity<Employee>().HasIndex(e => e.EmpCode).IsUnique();
            mb.Entity<Employee>().HasIndex(e => e.Email).IsUnique();
            mb.Entity<Department>().HasIndex(d => d.Name).IsUnique();
            mb.Entity<Designation>().HasIndex(d => d.Name).IsUnique();
            mb.Entity<Grade>().HasIndex(g => g.Name).IsUnique();
            mb.Entity<EmploymentType>().HasIndex(t => t.Name).IsUnique();
            mb.Entity<Location>().HasIndex(l => l.Name).IsUnique();
            mb.Entity<WeekOffPolicy>().HasIndex(w => w.Name).IsUnique();
            mb.Entity<LeaveType>().HasIndex(t => t.Alias).IsUnique();
            mb.Entity<Holiday>().HasIndex(h => h.Date);
            mb.Entity<LeavePolicy>().HasIndex(p => p.Name).IsUnique();

            // Decimal precision
            mb.Entity<Shift>().Property(s => s.HalfDayThresholdHours).HasPrecision(4, 1);
            mb.Entity<Shift>().Property(s => s.FullDayThresholdHours).HasPrecision(4, 1);
            mb.Entity<LeaveType>().Property(t => t.DefaultAnnualDays).HasPrecision(5, 1);
            mb.Entity<LeavePolicyRule>().Property(r => r.MonthlyAccrualDays).HasPrecision(5, 2);
            mb.Entity<LeavePolicyRule>().Property(r => r.AnnualEntitlementDays).HasPrecision(5, 2);
            mb.Entity<LeavePolicyRule>().Property(r => r.CarryForwardLimit).HasPrecision(5, 2);

            // Every Employee FK is optional — SetNull on the plain master-data
            // lookups (Department, Designation, etc.) is safe on its own.
            mb.Entity<Employee>().HasOne(e => e.Department).WithMany().HasForeignKey(e => e.DepartmentId).OnDelete(DeleteBehavior.SetNull);
            mb.Entity<Employee>().HasOne(e => e.Designation).WithMany().HasForeignKey(e => e.DesignationId).OnDelete(DeleteBehavior.SetNull);
            mb.Entity<Employee>().HasOne(e => e.Location).WithMany().HasForeignKey(e => e.LocationId).OnDelete(DeleteBehavior.SetNull);
            mb.Entity<Employee>().HasOne(e => e.Grade).WithMany().HasForeignKey(e => e.GradeId).OnDelete(DeleteBehavior.SetNull);
            mb.Entity<Employee>().HasOne(e => e.EmploymentType).WithMany().HasForeignKey(e => e.EmploymentTypeId).OnDelete(DeleteBehavior.SetNull);
            mb.Entity<Employee>().HasOne(e => e.Shift).WithMany().HasForeignKey(e => e.ShiftId).OnDelete(DeleteBehavior.SetNull);
            mb.Entity<Employee>().HasOne(e => e.WeekOffPolicy).WithMany().HasForeignKey(e => e.WeekOffPolicyId).OnDelete(DeleteBehavior.SetNull);
            mb.Entity<Employee>().HasOne(e => e.LeavePolicy).WithMany().HasForeignKey(e => e.LeavePolicyId).OnDelete(DeleteBehavior.SetNull);
            mb.Entity<Employee>().HasOne(e => e.CompOffRule).WithMany().HasForeignKey(e => e.CompOffRuleId).OnDelete(DeleteBehavior.SetNull);

            // Employee.ReportingManagerId (self-referencing) and
            // Department.HeadEmployeeId (points back at Employee) together
            // form a cycle back into the Employees table — SQL Server refuses
            // to create ANY cascading action (even SET NULL) on a
            // self-referencing FK when a second path back to the same table
            // exists, with "may cause cycles or multiple cascade paths".
            // NoAction on the self-reference breaks that cycle; deleting an
            // employee who is someone's manager will need to be handled
            // explicitly in code rather than silently cascading — which is
            // the right behavior for org-chart data anyway.
            mb.Entity<Employee>().HasOne(e => e.ReportingManager).WithMany().HasForeignKey(e => e.ReportingManagerId).OnDelete(DeleteBehavior.NoAction);
            mb.Entity<Department>().HasOne(d => d.Head).WithMany().HasForeignKey(d => d.HeadEmployeeId).OnDelete(DeleteBehavior.SetNull);

            // A policy's rules are owned by it — delete the policy, its
            // rules go with it.
            mb.Entity<WeekOffRule>().HasOne(r => r.WeekOffPolicy).WithMany(p => p.Rules)
                .HasForeignKey(r => r.WeekOffPolicyId).OnDelete(DeleteBehavior.Cascade);

            // Same ownership pattern for Leave Policy rules — delete the
            // policy, its rules go with it. A LeavePolicyRule referencing a
            // LeaveType should NOT cascade-delete the rule if the LeaveType
            // is removed — Restrict forces the leave-type delete path to
            // check for usage first (see DeleteLeaveType), same as every
            // other "in use" master-data guard in this app.
            mb.Entity<LeavePolicyRule>().HasOne(r => r.LeavePolicy).WithMany(p => p.Rules)
                .HasForeignKey(r => r.LeavePolicyId).OnDelete(DeleteBehavior.Cascade);
            mb.Entity<LeavePolicyRule>().HasOne(r => r.LeaveType).WithMany()
                .HasForeignKey(r => r.LeaveTypeId).OnDelete(DeleteBehavior.Restrict);

            // ── Attendance / OT ──
            mb.Entity<AttendancePunch>().HasIndex(p => new { p.EmployeeId, p.PunchDateTime });
            mb.Entity<AttendancePunch>().HasOne(p => p.Employee).WithMany()
                .HasForeignKey(p => p.EmployeeId).OnDelete(DeleteBehavior.NoAction); // NoAction — same cascade-cycle reasoning as ReportingManager above; Employees already has enough inbound FK chains that any cascading delete here risks the same SQL Server "multiple cascade paths" error, and attendance history should never silently vanish on an employee delete anyway.

            mb.Entity<AttendanceDaily>().HasIndex(d => new { d.EmployeeId, d.Date }).IsUnique(); // one computed row per employee per date — recompute overwrites, never duplicates
            mb.Entity<AttendanceDaily>().HasOne(d => d.Employee).WithMany()
                .HasForeignKey(d => d.EmployeeId).OnDelete(DeleteBehavior.NoAction);
            mb.Entity<AttendanceDaily>().Property(d => d.OTHours).HasPrecision(5, 2);

            // ── Applications (Leave / Regularisation / WFH / OD) ──
            // Three separate FKs into Employees (EmployeeId, Approver,
            // DecisionBy) — NoAction on all three so SQL Server never sees
            // more than one cascading path back into Employees from this
            // table (see the ReportingManager comment above for why that
            // matters).
            mb.Entity<Application>().HasOne(a => a.Employee).WithMany()
                .HasForeignKey(a => a.EmployeeId).OnDelete(DeleteBehavior.NoAction);
            mb.Entity<Application>().HasOne(a => a.Approver).WithMany()
                .HasForeignKey(a => a.ApproverEmployeeId).OnDelete(DeleteBehavior.NoAction);
            mb.Entity<Application>().HasOne(a => a.DecisionBy).WithMany()
                .HasForeignKey(a => a.DecisionByEmployeeId).OnDelete(DeleteBehavior.NoAction);
            mb.Entity<Application>().HasOne(a => a.LeaveType).WithMany()
                .HasForeignKey(a => a.LeaveTypeId).OnDelete(DeleteBehavior.NoAction);
            mb.Entity<Application>().Property(a => a.DurationDays).HasPrecision(5, 2);
            mb.Entity<Application>().HasIndex(a => new { a.EmployeeId, a.FromDate, a.ToDate });

            // ── Mobile app ──
            mb.Entity<AttendancePunch>().Property(p => p.FaceMatchConfidence).HasPrecision(5, 2);
            mb.Entity<FaceProfile>().HasOne(f => f.Employee).WithMany()
                .HasForeignKey(f => f.EmployeeId).OnDelete(DeleteBehavior.NoAction); // same multiple-cascade-paths reasoning as every other Employees FK above
            mb.Entity<FaceProfile>().HasIndex(f => f.EmployeeId);

            mb.Entity<Notification>().HasOne(n => n.Employee).WithMany()
                .HasForeignKey(n => n.EmployeeId).OnDelete(DeleteBehavior.NoAction);
            mb.Entity<Notification>().HasOne(n => n.RelatedApplication).WithMany()
                .HasForeignKey(n => n.RelatedApplicationId).OnDelete(DeleteBehavior.NoAction);
            mb.Entity<Notification>().HasIndex(n => new { n.EmployeeId, n.IsRead, n.CreatedAt });

            mb.Entity<FaceMatchApiSettings>().Property(s => s.MinConfidencePercent).HasPrecision(5, 2);

            // ── Kiosk devices ── one row per physical Attendance Machine;
            // the ApiKey is how KioskAttendanceController authenticates a
            // punch instead of a per-employee JWT (see KioskDevice's class
            // remarks). Unique so two devices can never collide on one key.
            mb.Entity<KioskDevice>().HasIndex(k => k.ApiKey).IsUnique();

            // ── Salary Structure / TDS / Income Tax ──
            // Decimal precision — every money/percent field gets an explicit
            // precision so EF Core doesn't fall back to SQL Server's default
            // (18,0), which would silently truncate fractional rupees/percent.
            mb.Entity<SalaryComponent>().Property(c => c.DefaultValue).HasPrecision(12, 2);
            mb.Entity<SalaryStructureTemplateItem>().Property(i => i.Value).HasPrecision(12, 2);
            mb.Entity<EmployeeSalaryStructure>().Property(s => s.AnnualCTC).HasPrecision(14, 2);
            mb.Entity<EmployeeSalaryComponent>().Property(c => c.Value).HasPrecision(12, 2);
            mb.Entity<EmployeeSalaryComponent>().Property(c => c.MonthlyAmount).HasPrecision(12, 2);
            mb.Entity<TaxSlabSettings>().Property(s => s.StandardDeduction).HasPrecision(12, 2);
            mb.Entity<TaxSlabSettings>().Property(s => s.Rebate87AIncomeLimit).HasPrecision(12, 2);
            mb.Entity<TaxSlabSettings>().Property(s => s.Rebate87AMaxAmount).HasPrecision(12, 2);
            mb.Entity<TaxSlabSettings>().Property(s => s.CessPercent).HasPrecision(5, 2);
            mb.Entity<TaxSlab>().Property(s => s.FromAmount).HasPrecision(14, 2);
            mb.Entity<TaxSlab>().Property(s => s.ToAmount).HasPrecision(14, 2);
            mb.Entity<TaxSlab>().Property(s => s.RatePercent).HasPrecision(5, 2);
            mb.Entity<TaxSurchargeSlab>().Property(s => s.FromAmount).HasPrecision(14, 2);
            mb.Entity<TaxSurchargeSlab>().Property(s => s.ToAmount).HasPrecision(14, 2);
            mb.Entity<TaxSurchargeSlab>().Property(s => s.RatePercent).HasPrecision(5, 2);
            mb.Entity<TaxSectionMaster>().Property(s => s.MaxLimit).HasPrecision(12, 2);
            mb.Entity<TaxDeclarationHeader>().Property(h => h.AnnualRentPaid).HasPrecision(12, 2);
            mb.Entity<TaxDeclarationItem>().Property(i => i.DeclaredAmount).HasPrecision(12, 2);
            mb.Entity<TaxDeclarationItem>().Property(i => i.ApprovedAmount).HasPrecision(12, 2);

            // Uniqueness
            mb.Entity<SalaryStructureTemplate>().HasIndex(t => t.Name).IsUnique();
            mb.Entity<TaxSectionMaster>().HasIndex(s => s.Code).IsUnique();
            mb.Entity<TaxSlabSettings>().HasIndex(s => new { s.FinancialYear, s.Regime }).IsUnique();
            mb.Entity<TaxDeclarationHeader>().HasIndex(h => new { h.EmployeeId, h.FinancialYear }).IsUnique(); // one declaration header per employee per FY

            // Optional lookup — same SetNull-on-delete convention as every
            // other Employee/Grade-style master lookup in this app.
            mb.Entity<SalaryStructureTemplate>().HasOne(t => t.Grade).WithMany()
                .HasForeignKey(t => t.GradeId).OnDelete(DeleteBehavior.SetNull);

            // Owned-child cascade — deleting the parent removes its rows,
            // same "owned rows" pattern as WeekOffRule/LeavePolicyRule above.
            mb.Entity<SalaryStructureTemplateItem>().HasOne(i => i.Template).WithMany(t => t.Items)
                .HasForeignKey(i => i.SalaryStructureTemplateId).OnDelete(DeleteBehavior.Cascade);
            mb.Entity<EmployeeSalaryComponent>().HasOne(c => c.EmployeeSalaryStructure).WithMany(s => s.Components)
                .HasForeignKey(c => c.EmployeeSalaryStructureId).OnDelete(DeleteBehavior.Cascade);
            mb.Entity<TaxSlab>().HasOne(s => s.TaxSlabSettings).WithMany(x => x.Slabs)
                .HasForeignKey(s => s.TaxSlabSettingsId).OnDelete(DeleteBehavior.Cascade);
            mb.Entity<TaxSurchargeSlab>().HasOne(s => s.TaxSlabSettings).WithMany(x => x.SurchargeSlabs)
                .HasForeignKey(s => s.TaxSlabSettingsId).OnDelete(DeleteBehavior.Cascade);
            mb.Entity<TaxDeclarationItem>().HasOne(i => i.Header).WithMany(h => h.Items)
                .HasForeignKey(i => i.TaxDeclarationHeaderId).OnDelete(DeleteBehavior.Cascade);

            // Restrict — a SalaryComponent / TaxSectionMaster in use must be
            // deactivated, not deleted out from under real assignments/data
            // (same "in use" guard as every other master in this app).
            mb.Entity<SalaryStructureTemplateItem>().HasOne(i => i.SalaryComponent).WithMany()
                .HasForeignKey(i => i.SalaryComponentId).OnDelete(DeleteBehavior.Restrict);
            mb.Entity<EmployeeSalaryComponent>().HasOne(c => c.SalaryComponent).WithMany()
                .HasForeignKey(c => c.SalaryComponentId).OnDelete(DeleteBehavior.Restrict);
            mb.Entity<TaxDeclarationItem>().HasOne(i => i.Section).WithMany()
                .HasForeignKey(i => i.TaxSectionMasterId).OnDelete(DeleteBehavior.Restrict);

            // Employees FK — NoAction, same multiple-cascade-paths reasoning
            // as every other Employees FK above (Employees already has too
            // many inbound chains for SQL Server to allow more cascades).
            mb.Entity<EmployeeSalaryStructure>().HasOne(s => s.Employee).WithMany()
                .HasForeignKey(s => s.EmployeeId).OnDelete(DeleteBehavior.NoAction);
            mb.Entity<EmployeeSalaryStructure>().HasOne(s => s.CreatedByEmployee).WithMany()
                .HasForeignKey(s => s.CreatedByEmployeeId).OnDelete(DeleteBehavior.NoAction);
            mb.Entity<TaxDeclarationHeader>().HasOne(h => h.Employee).WithMany()
                .HasForeignKey(h => h.EmployeeId).OnDelete(DeleteBehavior.NoAction);
            mb.Entity<TaxDeclarationItem>().HasOne(i => i.ReviewedByEmployee).WithMany()
                .HasForeignKey(i => i.ReviewedByEmployeeId).OnDelete(DeleteBehavior.NoAction);

            mb.Entity<EmployeeSalaryStructure>().HasIndex(s => new { s.EmployeeId, s.EffectiveTo }); // fast "current structure" lookup

            // ── Comp-Off Rule ──
            mb.Entity<CompOffRule>().HasIndex(r => r.Name).IsUnique();
            mb.Entity<CompOffRule>().Property(r => r.MinHoursForFullDay).HasPrecision(4, 1);
            mb.Entity<CompOffRule>().Property(r => r.MinHoursForHalfDay).HasPrecision(4, 1);
            mb.Entity<CompOffRule>().Property(r => r.MaxOpenBalance).HasPrecision(5, 1);

            mb.Entity<CompOffLedger>().Property(l => l.EarnedDays).HasPrecision(5, 1);
            mb.Entity<CompOffLedger>().Property(l => l.UsedDays).HasPrecision(5, 1);
            // One Auto-sourced credit per employee per worked off-day — the
            // engine's own idempotency check already guards this in code
            // (see CompOffEngine.TryAutoCreditAsync), but the index makes it
            // impossible to double-credit even under a race (e.g. two
            // near-simultaneous biometric syncs recomputing the same day).
            // Manual entries are exempt (an admin may legitimately log more
            // than one manual credit for the same date, e.g. a correction).
            mb.Entity<CompOffLedger>().HasIndex(l => new { l.EmployeeId, l.EarnedDate, l.Source })
                .IsUnique().HasFilter("[Source] = 'Auto'");
            mb.Entity<CompOffLedger>().HasOne(l => l.Employee).WithMany()
                .HasForeignKey(l => l.EmployeeId).OnDelete(DeleteBehavior.NoAction); // same multiple-cascade-paths reasoning as every other Employees FK above
            mb.Entity<CompOffLedger>().HasOne(l => l.CreatedByEmployee).WithMany()
                .HasForeignKey(l => l.CreatedByEmployeeId).OnDelete(DeleteBehavior.NoAction);
            // A rule in use by past credits must be deactivated, not deleted
            // — Restrict, same "in use" guard as every other master here.
            mb.Entity<CompOffLedger>().HasOne(l => l.CompOffRule).WithMany()
                .HasForeignKey(l => l.CompOffRuleId).OnDelete(DeleteBehavior.Restrict);

            mb.Entity<CompOffConsumption>().Property(c => c.DaysConsumed).HasPrecision(5, 1);
            mb.Entity<CompOffConsumption>().HasOne(c => c.Application).WithMany()
                .HasForeignKey(c => c.ApplicationId).OnDelete(DeleteBehavior.Cascade); // consumption rows are owned by the application that made them
            mb.Entity<CompOffConsumption>().HasOne(c => c.CompOffLedger).WithMany()
                .HasForeignKey(c => c.CompOffLedgerId).OnDelete(DeleteBehavior.Cascade); // and by the ledger row they drew from
            mb.Entity<CompOffConsumption>().HasIndex(c => c.ApplicationId);

            // ── OT Rule ──────────────────────────────────────────────────────
            mb.Entity<Employee>().HasOne(e => e.OTRule).WithMany()
                .HasForeignKey(e => e.OTRuleId).OnDelete(DeleteBehavior.SetNull);

            mb.Entity<OTLedger>().HasOne(l => l.Employee).WithMany()
                .HasForeignKey(l => l.EmployeeId).OnDelete(DeleteBehavior.NoAction);
            mb.Entity<OTLedger>().HasOne(l => l.CreatedByEmployee).WithMany()
                .HasForeignKey(l => l.CreatedByEmployeeId).OnDelete(DeleteBehavior.NoAction);
            mb.Entity<OTLedger>().HasOne(l => l.OTRule).WithMany()
                .HasForeignKey(l => l.OTRuleId).OnDelete(DeleteBehavior.Restrict);

            // One Auto OT row per employee per date (same idempotency guard as CompOff).
            mb.Entity<OTLedger>().HasIndex(l => new { l.EmployeeId, l.Date, l.Source })
                .IsUnique().HasFilter("[Source] = 'Auto'");

            // ── Leave Balance ────────────────────────────────────────────────
            // One row per employee per leave-type per year — upsert pattern
            // (bulk upload overwrites an existing row rather than duplicating).
            mb.Entity<LeaveBalance>().HasIndex(b => new { b.EmployeeId, b.LeaveTypeCode, b.Year })
                .IsUnique();
            mb.Entity<LeaveBalance>().HasOne(b => b.Employee).WithMany()
                .HasForeignKey(b => b.EmployeeId).OnDelete(DeleteBehavior.NoAction);
        }
    }
}
