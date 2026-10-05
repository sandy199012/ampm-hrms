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

    public record BulkDeletePreview(
        List<(int Id, string Name, string EmpCode)> Employees,
        List<(string Label, int Count)> Owned,
        List<(string Label, int Count)> Unlinked);

    public record BulkDeleteResult(int Deleted, List<string> Failures);

    public interface IEmployeeDeletionService
    {
        Task<EmployeeDeletePreview?> PreviewAsync(int employeeId, CancellationToken ct = default);
        Task<(bool Success, string Message)> DeleteAsync(int employeeId, CancellationToken ct = default);

        // Bulk: one combined preview, and one transaction PER employee — a
        // failure on one person doesn't undo the others; it's reported back.
        Task<BulkDeletePreview> PreviewManyAsync(IReadOnlyCollection<int> employeeIds, CancellationToken ct = default);
        Task<BulkDeleteResult> DeleteManyAsync(IReadOnlyCollection<int> employeeIds, CancellationToken ct = default);
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

        public async Task<BulkDeletePreview> PreviewManyAsync(IReadOnlyCollection<int> ids, CancellationToken ct = default)
        {
            var idList = ids.Distinct().ToList();
            var emps = await _db.Employees.AsNoTracking()
                .Where(e => idList.Contains(e.Id))
                .OrderBy(e => e.EmpCode)
                .Select(e => new { e.Id, e.Name, e.EmpCode })
                .ToListAsync(ct);
            idList = emps.Select(e => e.Id).ToList();
            var codes = emps.Select(e => e.EmpCode).Where(c => !string.IsNullOrWhiteSpace(c)).ToList();

            var owned = new List<(string, int)>
            {
                ("Employee records",           idList.Count),
                ("Attendance days",            await _db.AttendanceDailies.CountAsync(x => idList.Contains(x.EmployeeId), ct)),
                ("Attendance punches",         await _db.AttendancePunches.CountAsync(x => idList.Contains(x.EmployeeId), ct)),
                ("Leave / OD / other applications", await _db.Applications.CountAsync(x => idList.Contains(x.EmployeeId), ct)),
                ("Leave balances",             await _db.LeaveBalances.CountAsync(x => idList.Contains(x.EmployeeId), ct)),
                ("Comp-off ledger entries",    await _db.CompOffLedgers.CountAsync(x => idList.Contains(x.EmployeeId), ct)),
                ("OT ledger entries",          await _db.OTLedgers.CountAsync(x => idList.Contains(x.EmployeeId), ct)),
                ("Salary structures",          await _db.EmployeeSalaryStructures.CountAsync(x => idList.Contains(x.EmployeeId), ct)),
                ("Tax declarations",           await _db.TaxDeclarationHeaders.CountAsync(x => idList.Contains(x.EmployeeId), ct)),
                ("Face profiles",              await _db.FaceProfiles.CountAsync(x => idList.Contains(x.EmployeeId), ct)),
                ("Notifications",              await _db.Notifications.CountAsync(x => idList.Contains(x.EmployeeId), ct)),
                ("Roster entries",             await _db.RosterEntries.CountAsync(x => codes.Contains(x.EmployeeCode), ct)),
                ("Attendance Review gaps",     await _db.AttendanceGapLogs.CountAsync(x => codes.Contains(x.EmployeeCode), ct)),
            };

            // Only count links from people who are NOT also being deleted.
            var unlinked = new List<(string, int)>
            {
                ("Employees reporting to them",
                    await _db.Employees.CountAsync(x => x.ReportingManagerId != null &&
                        idList.Contains(x.ReportingManagerId.Value) && !idList.Contains(x.Id), ct)),
                ("Departments they head",
                    await _db.Departments.CountAsync(x => x.HeadEmployeeId != null && idList.Contains(x.HeadEmployeeId.Value), ct)),
                ("Applications they approved / decided",
                    await _db.Applications.CountAsync(x => !idList.Contains(x.EmployeeId) &&
                        ((x.ApproverEmployeeId != null && idList.Contains(x.ApproverEmployeeId.Value)) ||
                         (x.DecisionByEmployeeId != null && idList.Contains(x.DecisionByEmployeeId.Value))), ct)),
            };

            return new BulkDeletePreview(
                emps.Select(e => (e.Id, e.Name, e.EmpCode)).ToList(),
                owned.Where(o => o.Item2 > 0).ToList(),
                unlinked.Where(u => u.Item2 > 0).ToList());
        }

        public async Task<BulkDeleteResult> DeleteManyAsync(IReadOnlyCollection<int> ids, CancellationToken ct = default)
        {
            int deleted = 0;
            var failures = new List<string>();
            var idList = ids.Distinct().ToList();
            var labels = await _db.Employees.AsNoTracking()
                .Where(e => idList.Contains(e.Id))
                .ToDictionaryAsync(e => e.Id, e => $"{e.Name} ({e.EmpCode})", ct);

            foreach (var id in idList)
            {
                var (ok, msg) = await DeleteAsync(id, ct);
                if (ok) deleted++;
                else failures.Add($"{(labels.TryGetValue(id, out var l) ? l : $"#{id}")}: {msg}");
            }
            return new BulkDeleteResult(deleted, failures);
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
