using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using AmpmHrmsPro.Data;
using AmpmHrmsPro.Models;

namespace AmpmHrmsPro.Controllers
{
    // ── Employee self-service dashboard — all authenticated users
    // (role "employee", "manager", etc.) land here after login.
    // Admin/HR are redirected to Admin/Index by HomeController instead.
    [Authorize]
    public class EmployeeDashboardController : Controller
    {
        readonly AppDbContext _db;
        public EmployeeDashboardController(AppDbContext db) => _db = db;

        int CurrentEmpId => int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);

        public IActionResult Index()
        {
            var empId = CurrentEmpId;
            var emp = _db.Employees
                .Include(e => e.Department)
                .Include(e => e.Designation)
                .Include(e => e.Shift)
                .Include(e => e.ReportingManager)
                .FirstOrDefault(e => e.Id == empId);

            if (emp == null) return RedirectToAction("Logout", "Account");

            ViewBag.Employee = emp;

            var today      = DateTime.Today;
            var monthStart = new DateTime(today.Year, today.Month, 1);
            var monthEnd   = monthStart.AddMonths(1).AddDays(-1);

            var monthStartStr = monthStart.ToString("yyyy-MM-dd");
            var monthEndStr   = monthEnd.ToString("yyyy-MM-dd");
            var todayStr      = today.ToString("yyyy-MM-dd");

            // ── Today's punch ──────────────────────────────────────────────
            var todayRecord = _db.AttendanceDailies
                .FirstOrDefault(a => a.EmployeeId == empId && a.Date == todayStr);
            ViewBag.TodayRecord = todayRecord;

            // Raw punches for today (to show exact in/out timestamps)
            var todayPunches = _db.AttendancePunches
                .Where(p => p.EmployeeId == empId
                         && p.PunchDateTime.Date == today)
                .OrderBy(p => p.PunchDateTime)
                .ToList();
            ViewBag.TodayPunches = todayPunches;

            // ── This month attendance summary ──────────────────────────────
            var monthRecords = _db.AttendanceDailies
                .Where(a => a.EmployeeId == empId
                         && string.Compare(a.Date, monthStartStr) >= 0
                         && string.Compare(a.Date, monthEndStr)   <= 0)
                .ToList();

            ViewBag.PresentDays  = monthRecords.Count(r => r.EffectiveStatus.StartsWith("P"));
            ViewBag.AbsentDays   = monthRecords.Count(r => r.EffectiveStatus.StartsWith("A") && !r.WasWeekOff && !r.WasHoliday);
            ViewBag.WeekOffDays  = monthRecords.Count(r => r.WasWeekOff);
            ViewBag.HolidayDays  = monthRecords.Count(r => r.WasHoliday);
            ViewBag.WorkingDays  = monthRecords.Count(r => !r.WasWeekOff && !r.WasHoliday);

            // ── Recent 10 attendance days ──────────────────────────────────
            ViewBag.RecentAttendance = _db.AttendanceDailies
                .Where(a => a.EmployeeId == empId)
                .OrderByDescending(a => a.Date)
                .Take(10)
                .ToList();

            // ── Leave balances ─────────────────────────────────────────────
            ViewBag.LeaveBalances = _db.LeaveBalances
                .Where(b => b.EmployeeId == empId && b.Year == today.Year)
                .OrderBy(b => b.LeaveTypeCode)
                .ToList();

            // ── Pending applications ───────────────────────────────────────
            ViewBag.PendingApplications = _db.Applications
                .Where(a => a.EmployeeId == empId && a.Status == "Pending")
                .OrderByDescending(a => a.AppliedOn)
                .Take(5)
                .ToList();

            // ── Upcoming holidays ─────────────────────────────────────────
            var holidays = _db.Holidays.Where(h => h.IsActive).ToList();
            var upcoming = new List<(string Name, string Type, DateTime Date, int DaysAway)>();
            foreach (var h in holidays)
            {
                if (!DateTime.TryParse(h.Date, out var d)) continue;
                if (d.Date < today) continue;
                upcoming.Add((h.Name, h.Type ?? "", d, (d.Date - today).Days));
            }
            ViewBag.UpcomingHolidays = upcoming.OrderBy(x => x.Date).Take(5).ToList();

            ViewData["Title"]    = $"My Dashboard";
            ViewData["Subtitle"] = $"Welcome, {emp.Name}";
            return View();
        }
    }
}
