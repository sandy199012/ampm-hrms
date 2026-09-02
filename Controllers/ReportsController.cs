using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AmpmHrmsPro.Data;
using AmpmHrmsPro.Models;
using AmpmHrmsPro.Services;

namespace AmpmHrmsPro.Controllers
{
    // ═══════════════════════════════════════════
    // REPORTS — the on-screen equivalent of every sheet in the target
    // "AMPM <Month> <Year> Final Report.xlsx" workbook, all for a
    // selectable month. "Export Excel" on every page calls
    // ExcelReportBuilder to generate the exact same 11-sheet workbook —
    // that Excel file is the pixel/color-exact deliverable; these pages
    // are the fast on-screen equivalent for day-to-day use.
    // ═══════════════════════════════════════════
    [Authorize(Roles = "admin,hr")]
    public class ReportsController : Controller
    {
        readonly AppDbContext _db;
        public ReportsController(AppDbContext db) => _db = db;

        (List<Employee> Employees, ILookup<int, AttendanceDaily> DailyByEmp, List<AttendanceDaily> DailyAll, List<Application> Applications, int Year, int Month, int DaysInMonth) LoadMonth(int? year, int? month)
        {
            int y = year ?? DateTime.Today.Year, m = month ?? DateTime.Today.Month;
            int daysInMonth = DateTime.DaysInMonth(y, m);
            var monthStartStr = new DateTime(y, m, 1).ToString("yyyy-MM-dd");
            var monthEndStr = new DateTime(y, m, daysInMonth).ToString("yyyy-MM-dd");

            var employees = _db.Employees.Include(e => e.Department).Include(e => e.Designation).Include(e => e.Shift)
                .Where(e => e.IsActive).OrderBy(e => e.Name).ToList();
            var dailyAll = _db.AttendanceDailies
                .Where(d => string.Compare(d.Date, monthStartStr) >= 0 && string.Compare(d.Date, monthEndStr) <= 0).ToList();
            var dailyByEmp = dailyAll.ToLookup(d => d.EmployeeId);
            var applications = _db.Applications.Include(a => a.Employee).ThenInclude(e => e!.Department)
                .Include(a => a.LeaveType).Include(a => a.Approver)
                .Where(a => string.Compare(a.FromDate, monthEndStr) <= 0 && string.Compare(a.ToDate, monthStartStr) >= 0)
                .OrderBy(a => a.FromDate).ToList();

            SetMonthViewBag(y, m);
            return (employees, dailyByEmp, dailyAll, applications, y, m, daysInMonth);
        }

        void SetMonthViewBag(int year, int month)
        {
            ViewBag.Year = year; ViewBag.Month = month;
            ViewBag.MonthLabel = new DateTime(year, month, 1).ToString("MMMM yyyy");
        }

        public IActionResult ExportExcel(int? year, int? month)
        {
            int y = year ?? DateTime.Today.Year, m = month ?? DateTime.Today.Month;
            var bytes = ExcelReportBuilder.BuildMonthlyReport(_db, y, m);
            var name = $"AMPM_{new DateTime(y, m, 1):MMMM_yyyy}_Report.xlsx";
            return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", name);
        }

        // ═══ DASHBOARD ═══
        public IActionResult Dashboard(int? year, int? month)
        {
            var (employees, dailyByEmp, _, _, y, m, _) = LoadMonth(year, month);

            int present = 0, absent = 0, leave = 0, weekOff = 0;
            foreach (var e in employees)
                foreach (var d in dailyByEmp[e.Id])
                {
                    if (ExcelReportBuilder.IsPresentFamily(d.EffectiveStatus)) present++;
                    else if (d.EffectiveStatus == "A") absent++;
                    if (d.EffectiveStatus.StartsWith("L (")) leave++;
                    if (d.WasWeekOff) weekOff++;
                }
            ViewBag.Present = present; ViewBag.Absent = absent; ViewBag.Leave = leave; ViewBag.WeekOff = weekOff;
            ViewBag.TotalEmployees = employees.Count;

            var probation = employees.Where(e => !string.IsNullOrWhiteSpace(e.DOJ))
                .Select(e => (Emp: e, ProbEnd: DateTime.Parse(e.DOJ!).AddMonths(6)))
                .OrderBy(x => x.ProbEnd).ToList();
            ViewBag.ProbationPendingCount = probation.Count(x => x.ProbEnd >= DateTime.Today);
            ViewBag.ProbationCompleteCount = probation.Count(x => x.ProbEnd < DateTime.Today);
            return View(probation);
        }

        // ═══ ATTENDANCE REGISTER ═══
        public IActionResult AttendanceRegister(int? year, int? month)
        {
            var (employees, dailyByEmp, _, applications, y, m, daysInMonth) = LoadMonth(year, month);
            ViewBag.DaysInMonth = daysInMonth;

            var rows = new List<AttendanceRegisterRow>();
            foreach (var emp in employees)
            {
                var row = new AttendanceRegisterRow { Employee = emp };
                var empApps = applications.Where(a => a.EmployeeId == emp.Id).ToList();
                foreach (var d in dailyByEmp[emp.Id])
                {
                    int dayNum = int.Parse(d.Date.Substring(8, 2));
                    row.Days[dayNum] = d;
                    if (ExcelReportBuilder.IsPresentFamily(d.EffectiveStatus)) row.Present++;
                    else if (d.EffectiveStatus == "A") row.Absent++;
                    if (d.EffectiveStatus.StartsWith("L (")) row.LeaveDays++;
                    if (d.WasWeekOff) row.WeekOff++;
                }
                row.LeaveAppr = empApps.Count(a => a.Type == "Leave" && a.Status == "Approved");
                row.LeavePend = empApps.Count(a => a.Type == "Leave" && a.Status == "Pending");
                row.RegAppr = empApps.Count(a => a.Type == "Regularisation" && a.Status == "Approved");
                row.RegPend = empApps.Count(a => a.Type == "Regularisation" && a.Status == "Pending");
                row.WfhAppr = empApps.Count(a => a.Type == "WFH" && a.Status == "Approved");
                row.WfhPend = empApps.Count(a => a.Type == "WFH" && a.Status == "Pending");
                row.OdAppr = empApps.Count(a => a.Type == "OD" && a.Status == "Approved");
                row.OdPend = empApps.Count(a => a.Type == "OD" && a.Status == "Pending");
                rows.Add(row);
            }
            return View(rows);
        }

        // ═══ APPLICATION TRACKER ═══
        public IActionResult ApplicationTracker(int? year, int? month, string? status, string? type)
        {
            var (_, _, _, applications, y, m, _) = LoadMonth(year, month);
            var q = applications.AsEnumerable();
            if (!string.IsNullOrWhiteSpace(status)) q = q.Where(a => a.Status == status);
            if (!string.IsNullOrWhiteSpace(type)) q = q.Where(a => a.Type == type);
            ViewBag.Status = status; ViewBag.Type = type;
            return View(q.OrderByDescending(a => a.AppliedOn).ToList());
        }

        // ═══ EMPLOYEE SUMMARY ═══
        public IActionResult EmployeeSummary(int? year, int? month)
        {
            var (employees, dailyByEmp, _, applications, y, m, _) = LoadMonth(year, month);
            var rows = new List<EmployeeSummaryRow>();
            foreach (var emp in employees)
            {
                var recs = dailyByEmp[emp.Id].ToList();
                var apps = applications.Where(a => a.EmployeeId == emp.Id).ToList();
                var leaveApps = apps.Where(a => a.Type == "Leave").ToList();
                var regApps = apps.Where(a => a.Type == "Regularisation").ToList();
                var wfhApps = apps.Where(a => a.Type == "WFH").ToList();
                var odApps = apps.Where(a => a.Type == "OD").ToList();
                rows.Add(new EmployeeSummaryRow
                {
                    Employee = emp,
                    Present = recs.Count(r => ExcelReportBuilder.IsPresentFamily(r.EffectiveStatus)),
                    Absent = recs.Count(r => r.EffectiveStatus == "A"),
                    LeaveDays = recs.Count(r => r.EffectiveStatus.StartsWith("L (")),
                    WeekOff = recs.Count(r => r.WasWeekOff),
                    LeaveTotal = leaveApps.Count,
                    LeaveApproved = leaveApps.Count(a => a.Status == "Approved"),
                    LeavePending = leaveApps.Count(a => a.Status == "Pending"),
                    LeaveOther = leaveApps.Count(a => a.Status is "Revoked" or "Rejected"),
                    RegTotal = regApps.Count,
                    RegApproved = regApps.Count(a => a.Status == "Approved"),
                    RegPending = regApps.Count(a => a.Status == "Pending"),
                    WfhTotal = wfhApps.Count,
                    WfhApproved = wfhApps.Count(a => a.Status == "Approved"),
                    WfhPending = wfhApps.Count(a => a.Status == "Pending"),
                    OdTotal = odApps.Count,
                    OdApproved = odApps.Count(a => a.Status == "Approved"),
                });
            }
            return View(rows);
        }

        // ═══ DATE-WISE APPLICATIONS ═══
        public IActionResult DateWiseApplications(int? year, int? month)
        {
            var (_, _, dailyAll, applications, y, m, _) = LoadMonth(year, month);
            var dailyByKey = dailyAll.ToDictionary(d => (d.EmployeeId, d.Date));
            var rows = new List<DateWiseApplicationRow>();
            foreach (var a in applications)
            {
                var from = DateTime.Parse(a.FromDate); var to = DateTime.Parse(a.ToDate);
                for (var d = from; d <= to; d = d.AddDays(1))
                {
                    if (d.Year != y || d.Month != m) continue;
                    dailyByKey.TryGetValue((a.EmployeeId, d.ToString("yyyy-MM-dd")), out var daily);
                    rows.Add(new DateWiseApplicationRow { Date = d, App = a, AttendanceStatus = daily?.EffectiveStatus });
                }
            }
            return View(rows.OrderBy(r => r.Date).ToList());
        }

        // ═══ LEGEND & KEY (static) ═══
        public IActionResult LegendKey() => View();

        // ═══ OT - ATTENDANCE REPORT ═══
        public IActionResult OTAttendanceReport(int? year, int? month)
        {
            var (employees, dailyByEmp, _, _, y, m, daysInMonth) = LoadMonth(year, month);
            var rows = new List<OTAttendanceRow>();
            foreach (var emp in employees)
            {
                var recs = dailyByEmp[emp.Id].ToList();
                int weekOff = recs.Count(r => r.WasWeekOff);
                decimal totalWorkedHrs = recs.Where(r => r.WorkedMinutes.HasValue).Sum(r => r.WorkedMinutes!.Value) / 60m;
                int present = recs.Count(r => ExcelReportBuilder.IsPresentFamily(r.EffectiveStatus));
                rows.Add(new OTAttendanceRow
                {
                    Department = emp.Department?.Name ?? "(No Department)",
                    Employee = emp,
                    WorkingDays = daysInMonth - weekOff,
                    Present = present,
                    Absent = recs.Count(r => r.EffectiveStatus == "A"),
                    MissPunch = recs.Count(r => ExcelReportBuilder.IsMispunch(r.EffectiveStatus)),
                    Leave = recs.Count(r => r.EffectiveStatus.StartsWith("L (")),
                    AvgHrsPerDay = present > 0 ? Math.Round(totalWorkedHrs / present, 2) : 0,
                    OTHours = recs.Where(r => r.OTHours.HasValue).Sum(r => r.OTHours!.Value),
                    IsRetail = (emp.Category ?? "").Contains("Retail", StringComparison.OrdinalIgnoreCase),
                    IsStaff = (emp.Category ?? "").Contains("Staff", StringComparison.OrdinalIgnoreCase),
                });
            }
            return View(rows.OrderBy(r => r.Department).ThenBy(r => r.Employee.Name).ToList());
        }

        // ═══ OT - DEPARTMENT SUMMARY ═══
        public IActionResult OTDepartmentSummary(int? year, int? month)
        {
            var (employees, dailyByEmp, _, _, y, m, daysInMonth) = LoadMonth(year, month);
            var rows = new List<OTDepartmentSummaryRow>();
            foreach (var deptGroup in employees.GroupBy(e => e.Department?.Name ?? "(No Department)").OrderBy(g => g.Key))
            {
                int empCount = deptGroup.Count();
                int present = 0, absent = 0, missPunch = 0, leave = 0, workingDaysTotal = 0;
                decimal workerOT = 0;
                foreach (var emp in deptGroup)
                {
                    var recs = dailyByEmp[emp.Id].ToList();
                    present += recs.Count(r => ExcelReportBuilder.IsPresentFamily(r.EffectiveStatus));
                    absent += recs.Count(r => r.EffectiveStatus == "A");
                    missPunch += recs.Count(r => ExcelReportBuilder.IsMispunch(r.EffectiveStatus));
                    leave += recs.Count(r => r.EffectiveStatus.StartsWith("L ("));
                    int weekOff = recs.Count(r => r.WasWeekOff);
                    workingDaysTotal += daysInMonth - weekOff;
                    if (!(emp.Category ?? "").Contains("Staff", StringComparison.OrdinalIgnoreCase))
                        workerOT += recs.Where(r => r.OTHours.HasValue).Sum(r => r.OTHours!.Value);
                }
                rows.Add(new OTDepartmentSummaryRow
                {
                    Department = deptGroup.Key, Employees = empCount, Present = present, Absent = absent, MissPunch = missPunch, Leave = leave,
                    AvgAbsentPerEmp = empCount > 0 ? Math.Round((decimal)absent / empCount, 2) : 0,
                    AvgMissPerEmp = empCount > 0 ? Math.Round((decimal)missPunch / empCount, 2) : 0,
                    WorkerOTHrs = workerOT,
                    AttendancePercent = workingDaysTotal > 0 ? Math.Round(present * 100m / workingDaysTotal, 1) : 0,
                });
            }
            return View(rows);
        }

        // ═══ OT - WORKER OT DETAILS ═══
        public IActionResult OTWorkerDetails(int? year, int? month)
        {
            var (employees, dailyByEmp, _, _, y, m, _) = LoadMonth(year, month);
            var rows = new List<OTWorkerDetailRow>();
            foreach (var emp in employees)
            {
                if (string.IsNullOrWhiteSpace(emp.Category) || emp.Category.Contains("Staff", StringComparison.OrdinalIgnoreCase)) continue;
                var otRecs = dailyByEmp[emp.Id].Where(r => r.OTHours.HasValue && r.OTHours.Value > 0).ToList();
                if (!otRecs.Any()) continue;
                var total = otRecs.Sum(r => r.OTHours!.Value);
                rows.Add(new OTWorkerDetailRow { Employee = emp, OtDays = otRecs.Count, TotalOT = total, AvgOTPerDay = Math.Round(total / otRecs.Count, 2) });
            }
            return View(rows.OrderByDescending(r => r.TotalOT).ToList());
        }

        // ═══ OT - DAILY OT REGISTER ═══
        public IActionResult OTDailyRegister(int? year, int? month)
        {
            var (employees, dailyByEmp, _, _, y, m, _) = LoadMonth(year, month);
            var rows = new List<OTDailyRegisterRow>();
            foreach (var emp in employees)
            {
                foreach (var rec in dailyByEmp[emp.Id].Where(r => r.OTHours.HasValue && r.OTHours.Value > 0))
                {
                    var date = DateTime.Parse(rec.Date);
                    string boundary = rec.IsRetailOT && rec.InTime.HasValue
                        ? (date + rec.InTime.Value).AddHours(9).ToString("hh:mm:ss")
                        : (emp.Shift?.EndTime ?? new TimeSpan(18, 30, 0)).ToString(@"hh\:mm\:ss");
                    rows.Add(new OTDailyRegisterRow { Employee = emp, Daily = rec, Boundary = boundary });
                }
            }
            return View(rows.OrderBy(r => r.Daily.Date).ThenBy(r => r.Employee.Name).ToList());
        }

        // ═══ OT RULES & LEGEND (static) ═══
        public IActionResult OTRulesLegend() => View();

        // ═══════════════════════════════════════════
        // ATTENDANCE REPORTS — one flexible screen instead of 14 separate
        // pages: "period" picks Daily (a single date) or Monthly (a whole
        // month); "groupBy" picks whether each row is one Employee or an
        // aggregated Department/Location/Shift; the four filters narrow
        // which employees are included either way. Late/Early/LOP are
        // computed here rather than stored, since they depend on the
        // employee's CURRENT shift assignment and leave-type paid flag —
        // both can change after the fact, so computing at report-time (not
        // baking a flag into AttendanceDaily) keeps the numbers current.
        // ═══════════════════════════════════════════
        public IActionResult AttendanceReports(string? period, string? date, int? year, int? month,
            string? groupBy, int? departmentId, int? locationId, int? shiftId, int? employeeId)
        {
            period = period == "Daily" ? "Daily" : "Monthly";
            groupBy = groupBy is "Department" or "Location" or "Shift" ? groupBy : "Employee";

            DateTime fromDate, toDate;
            if (period == "Daily")
            {
                fromDate = toDate = DateTime.TryParse(date, out var dd) ? dd.Date : DateTime.Today;
            }
            else
            {
                int y = year ?? DateTime.Today.Year, m = month ?? DateTime.Today.Month;
                fromDate = new DateTime(y, m, 1);
                toDate = new DateTime(y, m, DateTime.DaysInMonth(y, m));
            }
            string fromStr = fromDate.ToString("yyyy-MM-dd"), toStr = toDate.ToString("yyyy-MM-dd");

            ViewBag.Period = period;
            ViewBag.Date = fromDate.ToString("yyyy-MM-dd");
            ViewBag.Year = fromDate.Year;
            ViewBag.Month = fromDate.Month;
            ViewBag.GroupBy = groupBy;
            ViewBag.DepartmentId = departmentId;
            ViewBag.LocationId = locationId;
            ViewBag.ShiftId = shiftId;
            ViewBag.EmployeeId = employeeId;
            ViewBag.PeriodLabel = period == "Daily" ? fromDate.ToString("dd MMM yyyy") : fromDate.ToString("MMMM yyyy");

            ViewBag.DepartmentList = _db.Departments.Where(d => d.IsActive).OrderBy(d => d.Name).ToList();
            ViewBag.LocationList = _db.Locations.Where(l => l.IsActive).OrderBy(l => l.Name).ToList();
            ViewBag.ShiftList = _db.Shifts.Where(s => s.IsActive).OrderBy(s => s.Name).ToList();
            ViewBag.EmployeeList = _db.Employees.Where(e => e.IsActive).OrderBy(e => e.Name).ToList();

            var empQuery = _db.Employees.Include(e => e.Department).Include(e => e.Location).Include(e => e.Shift)
                .Where(e => e.IsActive).AsQueryable();
            if (departmentId.HasValue) empQuery = empQuery.Where(e => e.DepartmentId == departmentId);
            if (locationId.HasValue) empQuery = empQuery.Where(e => e.LocationId == locationId);
            if (shiftId.HasValue) empQuery = empQuery.Where(e => e.ShiftId == shiftId);
            if (employeeId.HasValue) empQuery = empQuery.Where(e => e.Id == employeeId);
            var employees = empQuery.OrderBy(e => e.Name).ToList();

            var empIds = employees.Select(e => e.Id).ToHashSet();
            var dailyByEmp = _db.AttendanceDailies
                .Where(d => empIds.Contains(d.EmployeeId) && string.Compare(d.Date, fromStr) >= 0 && string.Compare(d.Date, toStr) <= 0)
                .ToList().ToLookup(d => d.EmployeeId);

            var leaveAliasPaid = _db.LeaveTypes.ToDictionary(t => t.Alias, t => t.IsPaid);

            var rows = new List<AttendanceReportRow>();
            foreach (var emp in employees)
            {
                var recs = dailyByEmp[emp.Id].ToList();
                var workedHours = Math.Round(recs.Where(r => r.WorkedMinutes.HasValue).Sum(r => r.WorkedMinutes!.Value) / 60m, 2);
                var workedDaysCount = recs.Count(r => r.WorkedMinutes.HasValue);
                rows.Add(new AttendanceReportRow
                {
                    GroupName = $"{emp.EmpCode} — {emp.Name}",
                    SubLabel = $"{emp.Department?.Name ?? "—"} / {emp.Location?.Name ?? "—"} / {emp.Shift?.Name ?? "—"}",
                    EmployeeCount = 1,
                    WorkingDays = recs.Count(r => !r.WasWeekOff && !r.WasHoliday),
                    Present = recs.Count(r => ExcelReportBuilder.IsPresentFamily(r.EffectiveStatus)),
                    Absent = recs.Count(r => r.EffectiveStatus == "A"),
                    HalfDay = recs.Count(r => r.EffectiveStatus.StartsWith("HD")),
                    Leave = recs.Count(r => r.EffectiveStatus.StartsWith("L (")),
                    MissingPunch = recs.Count(r => ExcelReportBuilder.IsMispunch(r.EffectiveStatus)),
                    Late = recs.Count(r => ExcelReportBuilder.IsLate(r, emp.Shift)),
                    Early = recs.Count(r => ExcelReportBuilder.IsEarlyGoing(r, emp.Shift)),
                    WorkedHours = workedHours,
                    WorkedDaysCount = workedDaysCount,
                    AvgHrsPerDay = workedDaysCount > 0 ? Math.Round(workedHours / workedDaysCount, 2) : 0,
                    OTHours = recs.Where(r => r.OTHours.HasValue).Sum(r => r.OTHours!.Value),
                    LOPDays = recs.Sum(r => ExcelReportBuilder.LopDays(r, leaveAliasPaid)),
                });
            }

            if (groupBy == "Employee") return View(rows);

            Func<Employee, string> keySelector = groupBy switch
            {
                "Department" => e => e.Department?.Name ?? "(No Department)",
                "Location" => e => e.Location?.Name ?? "(No Location)",
                _ => e => e.Shift?.Name ?? "(No Shift)", // "Shift"
            };

            var grouped = employees.Zip(rows, (e, r) => (Emp: e, Row: r))
                .GroupBy(x => keySelector(x.Emp))
                .OrderBy(g => g.Key)
                .Select(g =>
                {
                    var totalWorkedHours = Math.Round(g.Sum(x => x.Row.WorkedHours), 2);
                    var totalWorkedDays = g.Sum(x => x.Row.WorkedDaysCount);
                    return new AttendanceReportRow
                    {
                        GroupName = g.Key,
                        SubLabel = null,
                        EmployeeCount = g.Count(),
                        WorkingDays = g.Sum(x => x.Row.WorkingDays),
                        Present = g.Sum(x => x.Row.Present),
                        Absent = g.Sum(x => x.Row.Absent),
                        HalfDay = g.Sum(x => x.Row.HalfDay),
                        Leave = g.Sum(x => x.Row.Leave),
                        MissingPunch = g.Sum(x => x.Row.MissingPunch),
                        Late = g.Sum(x => x.Row.Late),
                        Early = g.Sum(x => x.Row.Early),
                        WorkedHours = totalWorkedHours,
                        WorkedDaysCount = totalWorkedDays,
                        AvgHrsPerDay = totalWorkedDays > 0 ? Math.Round(totalWorkedHours / totalWorkedDays, 2) : 0,
                        OTHours = g.Sum(x => x.Row.OTHours),
                        LOPDays = g.Sum(x => x.Row.LOPDays),
                    };
                }).ToList();

            return View(grouped);
        }

        // ═══════════════════════════════════════════
        // LEAVE REPORTS — Balance / Applications / Summary in one screen.
        // "year" identifies which leave CYCLE to compute (each policy rule
        // can start its own cycle on any month via CycleStartMonth — the
        // cycle used is CycleStartMonth of the selected year through the
        // same month next year), not a plain calendar year.
        //
        // Balance has no persisted year-to-year ledger (no "opening
        // balance" table exists), so AccruedSoFar is always computed fresh
        // for the selected cycle from scratch — proportional accrual from
        // the later of cycle-start/DOJ up to today (or cycle-end, if the
        // cycle has already closed), capped at the annual entitlement. A
        // prior cycle's leftover carry-forward is NOT reflected — this is
        // a known simplification, flagged here since it affects payroll
        // decisions if relied on directly.
        // ═══════════════════════════════════════════
        public IActionResult LeaveReports(string? view, int? year, int? departmentId, int? leaveTypeId, int? employeeId, string? status, string? groupBy)
        {
            view = view is "Applications" or "Summary" ? view : "Balance";
            groupBy = groupBy == "Department" ? "Department" : "LeaveType";
            int y = year ?? DateTime.Today.Year;

            ViewBag.View = view; ViewBag.Year = y; ViewBag.Status = status; ViewBag.GroupBy = groupBy;
            ViewBag.DepartmentId = departmentId; ViewBag.LeaveTypeId = leaveTypeId; ViewBag.EmployeeId = employeeId;
            ViewBag.DepartmentList = _db.Departments.Where(d => d.IsActive).OrderBy(d => d.Name).ToList();
            ViewBag.LeaveTypeList = _db.LeaveTypes.Where(t => t.IsActive).OrderBy(t => t.Name).ToList();
            ViewBag.EmployeeList = _db.Employees.Where(e => e.IsActive).OrderBy(e => e.Name).ToList();

            var vm = new LeaveReportsViewModel { View = view };
            string yearFromStr = $"{y - 1}-01-01", yearToStr = $"{y + 1}-12-31"; // generous overlap window — exact cycle windows are computed per-rule below

            if (view == "Balance")
            {
                var empQuery = _db.Employees.Include(e => e.Department)
                    .Include(e => e.LeavePolicy).ThenInclude(p => p!.Rules).ThenInclude(r => r.LeaveType)
                    .Where(e => e.IsActive && e.LeavePolicyId != null).AsQueryable();
                if (departmentId.HasValue) empQuery = empQuery.Where(e => e.DepartmentId == departmentId);
                if (employeeId.HasValue) empQuery = empQuery.Where(e => e.Id == employeeId);
                var employees = empQuery.ToList();
                var empIds = employees.Select(e => e.Id).ToList();

                var apps = _db.Applications
                    .Where(a => a.Type == "Leave" && empIds.Contains(a.EmployeeId) && (a.Status == "Approved" || a.Status == "Pending")
                        && string.Compare(a.FromDate, yearToStr) <= 0 && string.Compare(a.ToDate, yearFromStr) >= 0)
                    .ToList();

                var rows = new List<LeaveBalanceRow>();
                foreach (var emp in employees)
                {
                    if (emp.LeavePolicy == null) continue;
                    foreach (var rule in emp.LeavePolicy.Rules)
                    {
                        if (leaveTypeId.HasValue && rule.LeaveTypeId != leaveTypeId) continue;
                        if (rule.LeaveType == null) continue;

                        var cycleStart = new DateTime(y, rule.CycleStartMonth, 1);
                        var cycleEnd = cycleStart.AddYears(1).AddDays(-1);

                        var effStart = cycleStart;
                        if (!string.IsNullOrWhiteSpace(emp.DOJ) && DateTime.TryParse(emp.DOJ, out var doj) && doj > effStart) effStart = doj;

                        var asOf = DateTime.Today < cycleEnd ? DateTime.Today : cycleEnd;

                        decimal accrued = 0;
                        if (asOf >= effStart)
                        {
                            if (rule.AccrualMethod == "Monthly")
                            {
                                int monthsElapsed = (asOf.Year - effStart.Year) * 12 + (asOf.Month - effStart.Month) + 1;
                                monthsElapsed = Math.Max(0, Math.Min(12, monthsElapsed));
                                accrued = Math.Min(monthsElapsed * (rule.MonthlyAccrualDays ?? 0), rule.AnnualEntitlementDays);
                            }
                            else // Yearly, OneTime — full entitlement available as soon as the cycle applies to this employee
                            {
                                accrued = rule.AnnualEntitlementDays;
                            }
                        }

                        var cycleStartStr = cycleStart.ToString("yyyy-MM-dd");
                        var cycleEndStr = cycleEnd.ToString("yyyy-MM-dd");
                        var empApps = apps.Where(a => a.EmployeeId == emp.Id && a.LeaveTypeId == rule.LeaveTypeId
                            && string.Compare(a.FromDate, cycleEndStr) <= 0 && string.Compare(a.ToDate, cycleStartStr) >= 0).ToList();
                        decimal taken = empApps.Where(a => a.Status == "Approved").Sum(a => a.DurationDays);
                        decimal pending = empApps.Where(a => a.Status == "Pending").Sum(a => a.DurationDays);

                        rows.Add(new LeaveBalanceRow
                        {
                            Employee = emp,
                            LeaveTypeName = rule.LeaveType.Name,
                            LeaveTypeAlias = rule.LeaveType.Alias,
                            Entitlement = rule.AnnualEntitlementDays,
                            AccruedSoFar = Math.Round(accrued, 2),
                            Taken = taken,
                            Pending = pending,
                            Balance = Math.Round(accrued - taken, 2),
                        });
                    }
                }
                vm.BalanceRows = rows.OrderBy(r => r.Employee.Name).ThenBy(r => r.LeaveTypeName).ToList();
            }
            else if (view == "Applications")
            {
                var q = _db.Applications.Include(a => a.Employee).ThenInclude(e => e!.Department)
                    .Include(a => a.LeaveType).Include(a => a.Approver)
                    .Where(a => a.Type == "Leave" && string.Compare(a.FromDate, $"{y}-12-31") <= 0 && string.Compare(a.ToDate, $"{y}-01-01") >= 0)
                    .AsQueryable();
                if (departmentId.HasValue) q = q.Where(a => a.Employee!.DepartmentId == departmentId);
                if (leaveTypeId.HasValue) q = q.Where(a => a.LeaveTypeId == leaveTypeId);
                if (employeeId.HasValue) q = q.Where(a => a.EmployeeId == employeeId);
                if (!string.IsNullOrWhiteSpace(status)) q = q.Where(a => a.Status == status);
                vm.ApplicationRows = q.OrderByDescending(a => a.FromDate).ToList();
            }
            else // Summary
            {
                var q = _db.Applications.Include(a => a.Employee).ThenInclude(e => e!.Department)
                    .Include(a => a.LeaveType)
                    .Where(a => a.Type == "Leave" && string.Compare(a.FromDate, $"{y}-12-31") <= 0 && string.Compare(a.ToDate, $"{y}-01-01") >= 0)
                    .AsQueryable();
                if (departmentId.HasValue) q = q.Where(a => a.Employee!.DepartmentId == departmentId);
                if (leaveTypeId.HasValue) q = q.Where(a => a.LeaveTypeId == leaveTypeId);
                if (employeeId.HasValue) q = q.Where(a => a.EmployeeId == employeeId);
                var apps = q.ToList();

                Func<Application, string> keySel = groupBy == "Department"
                    ? (a => a.Employee?.Department?.Name ?? "(No Department)")
                    : (a => a.LeaveType?.Name ?? "(Unknown)");

                vm.SummaryRows = apps.GroupBy(keySel).OrderBy(g => g.Key).Select(g => new LeaveGroupRow
                {
                    GroupName = g.Key,
                    Applications = g.Count(),
                    TotalDays = g.Sum(a => a.DurationDays),
                    Approved = g.Count(a => a.Status == "Approved"),
                    Pending = g.Count(a => a.Status == "Pending"),
                    Rejected = g.Count(a => a.Status == "Rejected"),
                    Other = g.Count(a => a.Status is "Revoked" or "Cancelled"),
                }).ToList();
            }

            return View(vm);
        }

        // ═══════════════════════════════════════════
        // COMPLIANCE REPORTS — five views in one screen (Attendance
        // Register and the OT reports already exist as their own pages —
        // linked from this screen rather than rebuilt here).
        // ═══════════════════════════════════════════
        public IActionResult ComplianceReports(string? view, int? year, int? month, int? departmentId, int? employeeId)
        {
            view = view is "WorkingHours" or "HolidayWorking" or "WeekOffWorking" or "EmployeeMovement" ? view : "MusterRoll";
            int y = year ?? DateTime.Today.Year, m = month ?? DateTime.Today.Month;
            int daysInMonth = DateTime.DaysInMonth(y, m);
            var fromStr = new DateTime(y, m, 1).ToString("yyyy-MM-dd");
            var toStr = new DateTime(y, m, daysInMonth).ToString("yyyy-MM-dd");

            ViewBag.View = view; ViewBag.Year = y; ViewBag.Month = m; ViewBag.DaysInMonth = daysInMonth;
            ViewBag.MonthLabel = new DateTime(y, m, 1).ToString("MMMM yyyy");
            ViewBag.DepartmentId = departmentId; ViewBag.EmployeeId = employeeId;
            ViewBag.DepartmentList = _db.Departments.Where(d => d.IsActive).OrderBy(d => d.Name).ToList();
            ViewBag.EmployeeList = _db.Employees.Where(e => e.IsActive).OrderBy(e => e.Name).ToList();

            var vm = new ComplianceReportsViewModel { View = view };

            if (view == "EmployeeMovement")
            {
                // Includes inactive employees too, deliberately — an exit
                // that happened this period is exactly the case where
                // IsActive has already flipped to false.
                var allQuery = _db.Employees.Include(e => e.Department).Include(e => e.Designation).AsQueryable();
                if (departmentId.HasValue) allQuery = allQuery.Where(e => e.DepartmentId == departmentId);
                if (employeeId.HasValue) allQuery = allQuery.Where(e => e.Id == employeeId);
                var allEmp = allQuery.ToList();
                vm.Joiners = allEmp.Where(e => !string.IsNullOrWhiteSpace(e.DOJ) && DateTime.TryParse(e.DOJ, out var doj) && doj.Year == y && doj.Month == m)
                    .OrderBy(e => e.DOJ).ToList();
                vm.Exits = allEmp.Where(e => !string.IsNullOrWhiteSpace(e.DateOfLeaving) && DateTime.TryParse(e.DateOfLeaving, out var dol) && dol.Year == y && dol.Month == m)
                    .OrderBy(e => e.DateOfLeaving).ToList();
                return View(vm);
            }

            var empQuery = _db.Employees.Include(e => e.Department).Include(e => e.Designation).Include(e => e.Shift)
                .Where(e => e.IsActive).AsQueryable();
            if (departmentId.HasValue) empQuery = empQuery.Where(e => e.DepartmentId == departmentId);
            if (employeeId.HasValue) empQuery = empQuery.Where(e => e.Id == employeeId);
            var employees = empQuery.OrderBy(e => e.Name).ToList();
            var empIds = employees.Select(e => e.Id).ToHashSet();

            var dailyByEmp = _db.AttendanceDailies
                .Where(d => empIds.Contains(d.EmployeeId) && string.Compare(d.Date, fromStr) >= 0 && string.Compare(d.Date, toStr) <= 0)
                .ToList().ToLookup(d => d.EmployeeId);

            if (view == "MusterRoll")
            {
                var rows = new List<AttendanceRegisterRow>();
                foreach (var emp in employees)
                {
                    var row = new AttendanceRegisterRow { Employee = emp };
                    foreach (var d in dailyByEmp[emp.Id])
                    {
                        int dayNum = int.Parse(d.Date.Substring(8, 2));
                        row.Days[dayNum] = d;
                        if (ExcelReportBuilder.IsPresentFamily(d.EffectiveStatus)) row.Present++;
                        else if (d.EffectiveStatus == "A") row.Absent++;
                        if (d.EffectiveStatus.StartsWith("L (")) row.LeaveDays++;
                        if (d.WasWeekOff) row.WeekOff++;
                    }
                    rows.Add(row);
                }
                vm.MusterRows = rows;
            }
            else if (view == "WorkingHours")
            {
                var rows = new List<WorkingHoursRow>();
                foreach (var emp in employees)
                    foreach (var d in dailyByEmp[emp.Id].Where(d => d.WorkedMinutes.HasValue))
                        rows.Add(new WorkingHoursRow { Employee = emp, Daily = d, Hours = Math.Round(d.WorkedMinutes!.Value / 60m, 2) });
                vm.WorkingHoursRows = rows.OrderBy(r => r.Daily.Date).ThenBy(r => r.Employee.Name).ToList();
            }
            else if (view == "HolidayWorking")
            {
                var holidayNames = _db.Holidays.Where(h => h.IsActive && string.Compare(h.Date, fromStr) >= 0 && string.Compare(h.Date, toStr) <= 0)
                    .ToDictionary(h => h.Date, h => h.Name);
                var rows = new List<HolidayWeekOffWorkRow>();
                foreach (var emp in employees)
                    foreach (var d in dailyByEmp[emp.Id].Where(d => d.WasHoliday && ExcelReportBuilder.IsPresentFamily(d.EffectiveStatus)))
                        rows.Add(new HolidayWeekOffWorkRow { Employee = emp, Daily = d, Label = holidayNames.TryGetValue(d.Date, out var hn) ? hn : "Holiday" });
                vm.HolidayWeekOffRows = rows.OrderBy(r => r.Daily.Date).ThenBy(r => r.Employee.Name).ToList();
            }
            else // WeekOffWorking — a holiday that also happens to be a
                 // week-off day is reported once, under Holiday Working, not
                 // duplicated here.
            {
                var rows = new List<HolidayWeekOffWorkRow>();
                foreach (var emp in employees)
                    foreach (var d in dailyByEmp[emp.Id].Where(d => d.WasWeekOff && !d.WasHoliday && ExcelReportBuilder.IsPresentFamily(d.EffectiveStatus)))
                        rows.Add(new HolidayWeekOffWorkRow { Employee = emp, Daily = d, Label = "Week Off" });
                vm.HolidayWeekOffRows = rows.OrderBy(r => r.Daily.Date).ThenBy(r => r.Employee.Name).ToList();
            }

            return View(vm);
        }
    }
}
