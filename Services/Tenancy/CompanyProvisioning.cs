using System.Text.RegularExpressions;
using AmpmHrmsPro.Data;
using AmpmHrmsPro.Models;
using Microsoft.EntityFrameworkCore;

namespace AmpmHrmsPro.Services.Tenancy
{
    public record NewCompanyRequest(
        string Code, string Name, string? ContactName, string? ContactEmail, string? ContactPhone,
        string Plan, int? MaxEmployees, string? Modules, int? TrialDays, DateTime? SubscriptionEndsAt,
        string AdminEmpCode, string AdminName, string AdminEmail, string? AdminPassword);

    public record NewCompanyResult(Company Company, string AdminEmpCode, string AdminPassword);

    public interface ICompanyProvisioning
    {
        Task<NewCompanyResult> CreateAsync(NewCompanyRequest req, CancellationToken ct = default);
        Task<int> ActiveEmployeeCountAsync(int companyId, CancellationToken ct = default);
        /// <summary>Null = OK to add <paramref name="adding"/> more active employees; otherwise the reason.</summary>
        Task<string?> CheckEmployeeLimitAsync(int adding = 1, CancellationToken ct = default);
    }

    public class CompanyProvisioning : ICompanyProvisioning
    {
        private readonly IServiceScopeFactory _scopes;
        private readonly AppDbContext _db;
        private readonly ITenantContext _tenant;
        private readonly IConfiguration _cfg;

        public CompanyProvisioning(IServiceScopeFactory scopes, AppDbContext db, ITenantContext tenant, IConfiguration cfg)
        { _scopes = scopes; _db = db; _tenant = tenant; _cfg = cfg; }

        public static string NormalizeCode(string? code) =>
            Regex.Replace((code ?? "").Trim().ToUpperInvariant(), "[^A-Z0-9]", "");

        public async Task<NewCompanyResult> CreateAsync(NewCompanyRequest req, CancellationToken ct = default)
        {
            var code = NormalizeCode(req.Code);
            if (code.Length < 2 || code.Length > 20) throw new InvalidOperationException("Company code must be 2–20 letters/numbers.");
            if (string.IsNullOrWhiteSpace(req.Name)) throw new InvalidOperationException("Company name is required.");
            if (string.IsNullOrWhiteSpace(req.AdminEmpCode) || string.IsNullOrWhiteSpace(req.AdminEmail))
                throw new InvalidOperationException("Admin login code and email are required.");
            if (await _db.Companies.AnyAsync(c => c.Code == code, ct))
                throw new InvalidOperationException($"Company code {code} is already used.");

            var preset = Plans.Find(req.Plan) ?? Plans.All[0];
            var company = new Company
            {
                Code = code,
                Name = req.Name.Trim(),
                ContactName = req.ContactName?.Trim(),
                ContactEmail = req.ContactEmail?.Trim(),
                ContactPhone = req.ContactPhone?.Trim(),
                Plan = preset.Name,
                MaxEmployees = req.MaxEmployees ?? preset.MaxEmployees,
                Modules = string.IsNullOrWhiteSpace(req.Modules) ? preset.Modules : req.Modules!,
                TrialEndsAt = preset.Name == "Trial" ? DateTime.UtcNow.AddDays(req.TrialDays ?? preset.TrialDays ?? 14) : null,
                SubscriptionEndsAt = req.SubscriptionEndsAt,
                EmailSenderName = req.Name.Trim(),
                Status = "Active",
            };
            _db.Companies.Add(company);
            await _db.SaveChangesAsync(ct);

            var password = string.IsNullOrWhiteSpace(req.AdminPassword) ? PasswordPolicy.Generate() : req.AdminPassword!;

            // Seed masters + the first admin inside the new company.
            using (var scope = _scopes.CreateScopeFor(company.Id))
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                SeedData.Run(db, _cfg, genericCompany: true, seedAdmin: false);
                var dept = await db.Departments.FirstOrDefaultAsync(d => d.Name == "ADMINISTRATION", ct);
                db.Employees.Add(new Employee
                {
                    EmpCode = req.AdminEmpCode.Trim(),
                    Name = string.IsNullOrWhiteSpace(req.AdminName) ? "Company Admin" : req.AdminName.Trim(),
                    Email = req.AdminEmail.Trim(),
                    PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
                    Role = "admin",
                    DepartmentId = dept?.Id,
                    Status = "Active",
                    IsActive = true,
                    MustChangePassword = true,
                    PasswordChangedAt = DateTime.UtcNow,
                    CreatedAt = DateTime.UtcNow,
                });
                db.Audit("CompanyCreated", $"Company {company.Code} created with plan {company.Plan}", "Company", company.Id.ToString(), company.Id);
                await db.SaveChangesAsync(ct);
            }
            return new NewCompanyResult(company, req.AdminEmpCode.Trim(), password);
        }

        public Task<int> ActiveEmployeeCountAsync(int companyId, CancellationToken ct = default) =>
            _db.Employees.IgnoreQueryFilters()
               .Where(e => EF.Property<int>(e, AppDbContext.TenantColumn) == companyId && e.IsActive && e.Role != "admin")
               .CountAsync(ct);

        public async Task<string?> CheckEmployeeLimitAsync(int adding = 1, CancellationToken ct = default)
        {
            var cid = _tenant.CompanyId;
            var max = await _db.Companies.Where(c => c.Id == cid).Select(c => c.MaxEmployees).FirstOrDefaultAsync(ct);
            if (max <= 0) return null;
            var current = await ActiveEmployeeCountAsync(cid, ct);
            return current + adding > max
                ? $"Your plan allows {max} active employees (currently {current}). Please contact your HRMS provider to upgrade."
                : null;
        }
    }
}
