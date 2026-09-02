using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using AmpmHrmsPro.Data;
using AmpmHrmsPro.Models;

namespace AmpmHrmsPro.Controllers
{
    [Authorize]
    public class ApplicationsController : Controller
    {
        readonly AppDbContext _db;
        public ApplicationsController(AppDbContext db) => _db = db;

        int CurrentEmpId => int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);

        // ── My Applications list ──────────────────────────────────────────
        public IActionResult Index()
        {
            var empId = CurrentEmpId;
            var apps = _db.Applications
                .Include(a => a.LeaveType)
                .Where(a => a.EmployeeId == empId)
                .OrderByDescending(a => a.AppliedOn)
                .ToList();
            ViewData["Title"]    = "My Applications";
            ViewData["Subtitle"] = "Leave, Regularisation, WFH & OD";
            return View(apps);
        }

        // ── Apply — GET ───────────────────────────────────────────────────
        public IActionResult Apply(string type = "Leave")
        {
            var empId = CurrentEmpId;
            ViewBag.Type       = type;
            ViewBag.LeaveTypes = _db.LeaveTypes.OrderBy(lt => lt.Name).ToList();
            ViewBag.Employee   = _db.Employees
                                    .Include(e => e.Shift)
                                    .FirstOrDefault(e => e.Id == empId);
            ViewData["Title"]    = $"Apply {type}";
            ViewData["Subtitle"] = "";
            return View();
        }

        // ── Apply — POST ──────────────────────────────────────────────────
        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult Apply(
            string  type,
            int?    leaveTypeId,
            string  fromDate,
            string  toDate,
            string  dayPart            = "Single",
            string? requestedInTime    = null,
            string? requestedOutTime   = null,
            string? reason             = null)
        {
            var empId = CurrentEmpId;
            var emp   = _db.Employees
                           .Include(e => e.ReportingManager)
                           .FirstOrDefault(e => e.Id == empId);

            if (emp == null) return RedirectToAction("Logout", "Account");

            // Validate dates
            if (!DateTime.TryParse(fromDate, out var fd) || !DateTime.TryParse(toDate, out var td))
            {
                TempData["Error"] = "Invalid date.";
                return RedirectToAction("Apply", new { type });
            }
            if (fd > td)
            {
                TempData["Error"] = "From date cannot be after To date.";
                return RedirectToAction("Apply", new { type });
            }

            // Duration
            decimal duration = (dayPart == "FirstHalf" || dayPart == "SecondHalf") ? 0.5m
                             : (td - fd).Days + 1;

            // Times for Regularisation
            TimeSpan? inTs = null, outTs = null;
            if (type == "Regularisation")
            {
                if (!string.IsNullOrWhiteSpace(requestedInTime)  && TimeSpan.TryParse(requestedInTime,  out var it)) inTs  = it;
                if (!string.IsNullOrWhiteSpace(requestedOutTime) && TimeSpan.TryParse(requestedOutTime, out var ot)) outTs = ot;
            }

            // Validate Leave type selected
            if (type == "Leave" && leaveTypeId == null)
            {
                TempData["Error"] = "Please select a leave type.";
                return RedirectToAction("Apply", new { type });
            }

            var app = new Application
            {
                EmployeeId         = empId,
                Type               = type,
                LeaveTypeId        = type == "Leave" ? leaveTypeId : null,
                FromDate           = fd.ToString("yyyy-MM-dd"),
                ToDate             = td.ToString("yyyy-MM-dd"),
                DurationDays       = duration,
                DayPart            = dayPart,
                RequestedInTime    = inTs,
                RequestedOutTime   = outTs,
                Reason             = reason,
                AppliedOn          = DateTime.Now,
                Status             = "Pending",
                ApproverEmployeeId = emp.ReportingManagerId,
                PendingAt          = emp.ReportingManager?.Name ?? "Manager",
                CreatedAt          = DateTime.Now
            };

            _db.Applications.Add(app);
            _db.SaveChanges();

            TempData["Success"] = $"{type} application submitted successfully.";
            return RedirectToAction("Index");
        }

        // ── Cancel ────────────────────────────────────────────────────────
        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult Cancel(int id, string? remarks = null)
        {
            var empId = CurrentEmpId;
            var app   = _db.Applications.FirstOrDefault(a => a.Id == id && a.EmployeeId == empId);

            if (app == null || app.Status != "Pending")
            {
                TempData["Error"] = "Application not found or cannot be cancelled.";
                return RedirectToAction("Index");
            }

            app.Status     = "Cancelled";
            app.Remarks    = remarks;
            app.DecisionAt = DateTime.Now;
            _db.SaveChanges();

            TempData["Success"] = "Application cancelled.";
            return RedirectToAction("Index");
        }
    }
}
