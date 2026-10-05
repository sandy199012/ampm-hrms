// ─────────────────────────────────────────────────────────────────────────────
// Services/EmployeeDeletionService.cs
//
// Permanently deletes an employee AND every record that belongs to them,
// in one transaction (all-or-nothing).
//
// Most Employee foreign keys are configured NoAction (see AppDbContext —
// avoids multiple-cascade-path errors), so the database will refuse a plain
// "DELETE FROM Employees" while any child row exists. This service removes
// children first, in dependency order, and un-links (sets NULL) the places
// where OTHER employees' records merely point at this person (reporting
// manager, department head, approver, "created by", "reviewed by").
// ─────────────────────────────────────────────────────────────────────────────
using AmpmHrmsPro.Data;
using AmpmHrmsPro.Models;
using Microsoft.EntityFrameworkCore;

namespace AmpmHrmsPro.Services
{
    public record EmployeeDeletePreview(
        int EmployeeId, string Name, string EmpCode,
        List<(string Label, int Count)> Owned,     // deleted with the employee
        List<(string Label, int Count)> Unlinked); // other people's rows → set to blank

    public interface IEmployeeDeletionService
    {
        Task<EmployeeDeletePreview?> PreviewAsync(int employeeId, CancellationToken ct = default);
        Task<(bool Success, string Message)> DeleteAsync(int employeeId, CancellationToken ct = default);
    }

    public class EmployeeDeletionService : IEmployeeDeletionService
    {
        private readonly AppDbContext _db;
        private readonly ILogger<EmployeeDeletionService> _log;

        public EmployeeDeletionService(AppDbContext db, ILogger<EmployeeDeletionService> log)
        {
            _db  = db;
            _log = log;
        }

        public async Task<EmployeeDeletePreview?> PreviewAsync(int id, CancellationToken ct = default)
        {
            var emp = await _db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct);
            if (emp == null) return null;
            var code = emp.EmpCode;

            var owned = new List<(string, int)>
            {
                ("Attendance days",            await _db.AttendanceDailies.CountAsync(x => x.EmployeeId == id, ct)),
                ("Attendance punches",         await _db.AttendancePunches.CountAsync(x => x.EmployeeId == id, ct)),
                ("Leave / OD / other applications", await _db.Applications.CountAsync(x => x.EmployeeId == id, ct)),
                ("Leave balances",             await _db.LeaveBalances.CountAsync(x => x.EmployeeId == id, ct)),
                ("Comp-off ledger entries",    await _db.CompOffLedgers.CountAsync(x => x.EmployeeId == id, ct)),
                ("OT ledger entries",          await _db.OTLedgers.CountAsync(x => x.EmployeeId == id, ct)),
                ("Salary structures",          await _db.EmployeeSalaryStructures.CountAsync(x => x.EmployeeId == id, ct)),
                ("Tax declarations",           await _db.TaxDeclarationHeaders.CountAsync(x => x.EmployeeId == id, ct)),
                ("Face profiles",              await _db.FaceProfiles.CountAsync(x => x.EmployeeId == id, ct)),
                ("Notifications",              await _db.Notifications.CountAsync(x => x.EmployeeId == id, ct)),
                ("Roster entries",             await _db.RosterEntries.CountAsync(x => x.EmployeeCode == code, ct)),
                ("Attendance Review gaps",     await _db.AttendanceGapLogs.CountAsync(x => x.EmployeeCode == code, ct)),
            };

            var unlinked = new List<(string, int)>
            {
                ("Employees reporting to them",  await _db.Employees.CountAsync(x => x.ReportingManagerId == id, ct)),
                ("Departments they head",        await _db.Departments.CountAsync(x => x.HeadEmployeeId == id, ct)),
                ("Applications they approved / decided",
                    await _db.Applications.CountAsync(x => x.EmployeeId != id &&
                        (x.ApproverEmployeeId == id || x.DecisionByEmployeeId == id), ct)),
            };

            return new EmployeeDeletePreview(id, emp.Name, emp.EmpCode,
                owned.Where(o => o.Item2 > 0).ToList(),
                unlinked.Where(u => u.Item2 > 0).ToList());
        }

        public async Task<(bool Success, string Message)> DeleteAsync(int id, CancellationToken ct = default)
        {
            var emp = await _db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct);
            if (emp == null) return (false, "Employee not found.");
            var code = emp.EmpCode;

            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            try
            {
                // 1. Comp-off consumptions hang off this employee's applications
                //    and comp-off ledger rows — remove them before their parents.
                await _db.CompOffConsumptions
                    .Where(c => _db.Applications.Any(a => a.Id == c.ApplicationId && a.EmployeeId == id)
                             || _db.CompOffLedgers.Any(l => l.Id == c.CompOffLedgerId && l.EmployeeId == id))
                    .ExecuteDeleteAsync(ct);

                // 2. Other people's notifications that point at this employee's
                //    applications → keep the notification, drop the link.
                await _db.Notifications
                    .Where(n => n.EmployeeId != id && n.RelatedApplicationId != null &&
                                _db.Applications.Any(a => a.Id == n.RelatedApplicationId && a.EmployeeId == id))
                    .ExecuteUpdateAsync(s => s.SetProperty(n => n.RelatedApplicationId, (int?)null), ct);

                // 3. Records owned by the employee.
                await _db.Notifications     .Where(x => x.EmployeeId == id).ExecuteDeleteAsync(ct);
                await _db.Applications      .Where(x => x.EmployeeId == id).ExecuteDeleteAsync(ct);
                await _db.CompOffLedgers    .Where(x => x.EmployeeId == id).ExecuteDeleteAsync(ct);
                await _db.OTLedgers         .Where(x => x.EmployeeId == id).ExecuteDeleteAsync(ct);
                await _db.LeaveBalances     .Where(x => x.EmployeeId == id).ExecuteDeleteAsync(ct);
                await _db.FaceProfiles      .Where(x => x.EmployeeId == id).ExecuteDeleteAsync(ct);
                await _db.AttendancePunches .Where(x => x.EmployeeId == id).ExecuteDeleteAsync(ct);
                await _db.AttendanceDailies .Where(x => x.EmployeeId == id).ExecuteDeleteAsync(ct);

                await _db.EmployeeSalaryComponents
                    .Where(c => _db.EmployeeSalaryStructures.Any(s => s.Id == c.EmployeeSalaryStructureId && s.EmployeeId == id))
                    .ExecuteDeleteAsync(ct);
                await _db.EmployeeSalaryStructures.Where(x => x.EmployeeId == id).ExecuteDeleteAsync(ct);

                await _db.TaxDeclarationItems
                    .Where(i => _db.TaxDeclarationHeaders.Any(h => h.Id == i.TaxDeclarationHeaderId && h.EmployeeId == id))
                    .ExecuteDeleteAsync(ct);
                await _db.TaxDeclarationHeaders.Where(x => x.EmployeeId == id).ExecuteDeleteAsync(ct);

                // Matched by employee code (these come from Presence 360 / roster files).
                if (!string.IsNullOrWhiteSpace(code))
                {
                    await _db.RosterEntries    .Where(x => x.EmployeeCode == code).ExecuteDeleteAsync(ct);
                    await _db.AttendanceGapLogs.Where(x => x.EmployeeCode == code).ExecuteDeleteAsync(ct);
                }

                // 4. Other people's records that merely reference this employee → NULL.
                await _db.Employees.Where(x => x.ReportingManagerId == id)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.ReportingManagerId, (int?)null), ct);
                await _db.Departments.Where(x => x.HeadEmployeeId == id)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.HeadEmployeeId, (int?)null), ct);
                await _db.Applications.Where(x => x.ApproverEmployeeId == id)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.ApproverEmployeeId, (int?)null), ct);
                await _db.Applications.Where(x => x.DecisionByEmployeeId == id)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.DecisionByEmployeeId, (int?)null), ct);
                await _db.CompOffLedgers.Where(x => x.CreatedByEmployeeId == id)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedByEmployeeId, (int?)null), ct);
                await _db.OTLedgers.Where(x => x.CreatedByEmployeeId == id)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedByEmployeeId, (int?)null), ct);
                await _db.EmployeeSalaryStructures.Where(x => x.CreatedByEmployeeId == id)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedByEmployeeId, (int?)null), ct);
                await _db.TaxDeclarationItems.Where(x => x.ReviewedByEmployeeId == id)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.ReviewedByEmployeeId, (int?)null), ct);

                // 5. Finally the employee.
                await _db.Employees.Where(x => x.Id == id).ExecuteDeleteAsync(ct);

                await tx.CommitAsync(ct);
                _log.LogWarning("Employee {Code} ({Name}) and all related data permanently deleted.", emp.EmpCode, emp.Name);
                return (true, $"{emp.Name} ({emp.EmpCode}) and all their data were permanently deleted.");
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync(ct);
                _log.LogError(ex, "Employee delete failed for {Id}", id);
                var msg = ex.InnerException?.Message ?? ex.Message;
                return (false, $"Delete failed — nothing was removed. {msg.Split('\n')[0]}");
            }
        }
    }
}
