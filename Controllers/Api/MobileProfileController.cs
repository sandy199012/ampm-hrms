using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using AmpmHrmsPro.Data;
using AmpmHrmsPro.Models;

namespace AmpmHrmsPro.Controllers.Api
{
    // ═══════════════════════════════════════════
    // MOBILE PROFILE — read the employee's own record, and update only the
    // handful of fields it's safe for an employee to self-edit (contact
    // info, emergency contact, profile photo). Deliberately does NOT allow
    // editing Department/Designation/Shift/Role/salary-adjacent fields —
    // those stay Admin/HR-only, changed from the existing web Edit
    // Employee screen.
    // ═══════════════════════════════════════════
    [ApiController]
    [Route("api/mobile/profile")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public class MobileProfileController : ControllerBase
    {
        readonly AppDbContext _db;
        public MobileProfileController(AppDbContext db) => _db = db;

        int CurrentEmpId => int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);

        [HttpGet]
        public async Task<IActionResult> Get()
        {
            var e = await _db.Employees
                .Include(e => e.Department).Include(e => e.Designation)
                .Include(e => e.Location).Include(e => e.Shift).Include(e => e.ReportingManager)
                .Include(e => e.CompOffRule).Include(e => e.OTRule)
                .FirstOrDefaultAsync(e => e.Id == CurrentEmpId);
            if (e == null) return NotFound();

            // Feature flags — category-driven:
            //   Worker employees → OT tab only (if OTRuleId assigned)
            //   Staff employees  → CompOff tab only (if CompOffRuleId assigned)
            bool isWorker   = (e.Category ?? "").Equals("Worker", StringComparison.OrdinalIgnoreCase);
            bool hasCompOff = !isWorker && e.CompOffRuleId.HasValue;
            bool hasOT      = isWorker && e.OTRuleId.HasValue;

            // ── Leave balances ──────────────────────────────────────────────
            // PRIMARY SOURCE: LeaveBalance table (flat rows uploaded from Excel
            // or written by LeaveBalanceEngine when a leave is approved).
            // FALLBACK: if no rows exist for this employee in the current year,
            // compute dynamically from the employee's LeavePolicy so that the
            // mobile home screen is never blank even before any Excel import.
            int year = DateTime.Now.Year;
            string empGender = (e.Gender ?? "").Trim(); // "Male" / "Female" / ""

            // Gender-filtered leave type aliases
            var allowedAliases = await _db.LeaveTypes
                .Where(lt => lt.IsActive &&
                             (lt.Gender == "All" ||
                              lt.Gender == empGender ||
                              string.IsNullOrEmpty(lt.Gender)))
                .Select(lt => lt.Alias)
                .ToListAsync();

            var leaveBalanceRows = await _db.LeaveBalances
                .Where(b => b.EmployeeId == CurrentEmpId && b.Year == year
                            && allowedAliases.Contains(b.LeaveTypeCode))
                .ToListAsync();

            var leaveTypeMaster = await _db.LeaveTypes
                .Where(lt => lt.IsActive)
                .ToDictionaryAsync(lt => lt.Alias, lt => lt.Name);

            List<object> leaveBalances;

            if (leaveBalanceRows.Any())
            {
                // Use the authoritative stored balances
                leaveBalances = leaveBalanceRows.Select(b => (object)new
                {
                    leaveType     = b.LeaveTypeCode,
                    leaveTypeName = leaveTypeMaster.TryGetValue(b.LeaveTypeCode, out var n) ? n : b.LeaveTypeCode,
                    year          = b.Year,
                    carryForward  = b.CarryForward,
                    totalEarned   = b.TotalEarned,
                    totalConsumed = b.TotalConsumed,
                    balance       = b.Balance,
                }).ToList();
            }
            else
            {
                // Fallback: compute from leave policy (same logic as /api/mobile/leave/balance)
                leaveBalances = new List<object>();
                var emp = await _db.Employees
                    .Include(x => x.LeavePolicy).ThenInclude(p => p!.Rules).ThenInclude(r => r.LeaveType)
                    .FirstOrDefaultAsync(x => x.Id == CurrentEmpId);

                if (emp?.LeavePolicy != null)
                {
                    var approvedApps = await _db.Applications
                        .Where(a => a.EmployeeId == CurrentEmpId && a.Type == "Leave" && a.Status == "Approved")
                        .ToListAsync();

                    foreach (var rule in emp.LeavePolicy.Rules)
                    {
                        if (rule.LeaveType == null) continue;
                        // Skip leave types that don't apply to this gender
                        if (!allowedAliases.Contains(rule.LeaveType.Alias)) continue;

                        var cycleStart = new DateTime(year, rule.CycleStartMonth, 1);
                        var cycleEnd   = cycleStart.AddYears(1).AddDays(-1);
                        var effStart   = cycleStart;
                        if (!string.IsNullOrWhiteSpace(emp.DOJ) &&
                            DateTime.TryParse(emp.DOJ, out var doj) && doj > effStart) effStart = doj;
                        var asOf = DateTime.Today < cycleEnd ? DateTime.Today : cycleEnd;

                        decimal accrued = 0;
                        if (asOf >= effStart)
                        {
                            if (rule.AccrualMethod == "Monthly")
                            {
                                int months = Math.Max(0, Math.Min(12,
                                    (asOf.Year - effStart.Year) * 12 + (asOf.Month - effStart.Month) + 1));
                                accrued = Math.Min(months * (rule.MonthlyAccrualDays ?? 0), rule.AnnualEntitlementDays);
                            }
                            else accrued = rule.AnnualEntitlementDays;
                        }

                        var cycleStartStr = cycleStart.ToString("yyyy-MM-dd");
                        var cycleEndStr   = cycleEnd.ToString("yyyy-MM-dd");
                        decimal taken = approvedApps
                            .Where(a => a.LeaveTypeId == rule.LeaveTypeId
                                && string.Compare(a.FromDate, cycleEndStr) <= 0
                                && string.Compare(a.ToDate, cycleStartStr) >= 0)
                            .Sum(a => a.DurationDays);

                        leaveBalances.Add(new
                        {
                            leaveType     = rule.LeaveType.Alias,
                            leaveTypeName = rule.LeaveType.Name,
                            year,
                            carryForward  = 0m,
                            totalEarned   = Math.Round(accrued, 2),
                            totalConsumed = taken,
                            balance       = Math.Round(accrued - taken, 2),
                        });
                    }
                }
            }

            return Ok(new
            {
                empCode = e.EmpCode,
                name = e.Name,
                email = e.Email,
                mobile = e.Mobile,
                gender = e.Gender,
                dob = e.DOB,
                address = e.Address,
                photoUrl = e.PhotoUrl,
                doj = e.DOJ,
                category = e.Category,
                department = e.Department?.Name,
                designation = e.Designation?.Name,
                location = e.Location?.Name,
                shift = e.Shift?.Name,
                reportingManager = e.ReportingManager?.Name,
                emergencyContactName = e.EmergencyContactName,
                emergencyContactMobile = e.EmergencyContactMobile,
                emergencyContactRelation = e.EmergencyContactRelation,
                // Leave balances (EL / CL / SL) for current year
                leaveBalances,
                // Mobile-app feature flags
                features = new { hasCompOff, hasOT },
            });
        }

        public record UpdateProfileRequest(string? Mobile, string? Address, string? EmergencyContactName,
            string? EmergencyContactMobile, string? EmergencyContactRelation);

        [HttpPut]
        public async Task<IActionResult> Update(UpdateProfileRequest req)
        {
            var e = await _db.Employees.FindAsync(CurrentEmpId);
            if (e == null) return NotFound();

            if (req.Mobile != null) e.Mobile = req.Mobile;
            if (req.Address != null) e.Address = req.Address;
            if (req.EmergencyContactName != null) e.EmergencyContactName = req.EmergencyContactName;
            if (req.EmergencyContactMobile != null) e.EmergencyContactMobile = req.EmergencyContactMobile;
            if (req.EmergencyContactRelation != null) e.EmergencyContactRelation = req.EmergencyContactRelation;

            await _db.SaveChangesAsync();
            return Ok(new { success = true });
        }

        // ── CompOff balance — available credits for Staff employees ─────────
        [HttpGet("compoff-balance")]
        public async Task<IActionResult> CompOffBalance()
        {
            var entries = await _db.CompOffLedgers
                .Where(l => l.EmployeeId == CurrentEmpId)
                .OrderBy(l => l.ExpiryDate)
                .ToListAsync();

            var available = entries.Where(l => l.Status == "Available").ToList();
            decimal balance     = available.Sum(l => l.EarnedDays - l.UsedDays);
            decimal totalEarned = entries.Sum(l => l.EarnedDays);
            decimal totalUsed   = entries.Sum(l => l.UsedDays);

            return Ok(new
            {
                balance,
                totalEarned,
                totalUsed,
                entries = available.Select(l => new
                {
                    id            = l.Id,
                    earnedDate    = l.EarnedDate,
                    earnedDays    = l.EarnedDays,
                    usedDays      = l.UsedDays,
                    availableDays = l.EarnedDays - l.UsedDays,
                    expiryDate    = l.ExpiryDate,
                    source        = l.Source,
                    remarks       = l.Remarks,
                }),
            });
        }

        [HttpPost("photo")]
        public async Task<IActionResult> UploadPhoto(IFormFile photo)
        {
            if (photo == null || photo.Length == 0) return BadRequest(new { message = "Photo is required." });
            var e = await _db.Employees.FindAsync(CurrentEmpId);
            if (e == null) return NotFound();

            e.PhotoUrl = await Services.FileStorageHelper.SavePhotoAsync(photo, "profiles");
            await _db.SaveChangesAsync();
            return Ok(new { success = true, photoUrl = e.PhotoUrl });
        }
    }
}
