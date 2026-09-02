namespace AmpmHrmsPro.Models
{
    // ═══════════════════════════════════════════
    // REPORT VIEW MODELS — plain display DTOs for ReportsController's
    // on-screen sheets (the Excel export in Services/ExcelReportBuilder.cs
    // computes its own copies of these same aggregates independently, so a
    // report page and its "Export Excel" button always agree).
    // ═══════════════════════════════════════════
    public class AttendanceRegisterRow
    {
        public Employee Employee { get; set; } = null!;
        public Dictionary<int, AttendanceDaily> Days { get; set; } = new(); // keyed by day-of-month
        public int Present, Absent, LeaveDays, WeekOff;
        public int LeaveAppr, LeavePend, RegAppr, RegPend, WfhAppr, WfhPend, OdAppr, OdPend;
    }

    public class EmployeeSummaryRow
    {
        public Employee Employee { get; set; } = null!;
        public int Present, Absent, LeaveDays, WeekOff;
        public int LeaveTotal, LeaveApproved, LeavePending, LeaveOther;
        public int RegTotal, RegApproved, RegPending;
        public int WfhTotal, WfhApproved, WfhPending;
        public int OdTotal, OdApproved;
    }

    public class OTAttendanceRow
    {
        public string Department { get; set; } = "";
        public Employee Employee { get; set; } = null!;
        public int WorkingDays, Present, Absent, MissPunch, Leave;
        public decimal AvgHrsPerDay, OTHours;
        public bool IsRetail, IsStaff;
    }

    public class OTDepartmentSummaryRow
    {
        public string Department { get; set; } = "";
        public int Employees, Present, Absent, MissPunch, Leave;
        public decimal AvgAbsentPerEmp, AvgMissPerEmp, WorkerOTHrs, AttendancePercent;
    }

    public class OTWorkerDetailRow
    {
        public Employee Employee { get; set; } = null!;
        public int OtDays;
        public decimal TotalOT, AvgOTPerDay;
    }

    public class OTDailyRegisterRow
    {
        public Employee Employee { get; set; } = null!;
        public AttendanceDaily Daily { get; set; } = null!;
        public string Boundary { get; set; } = "";
    }

    public class DateWiseApplicationRow
    {
        public DateTime Date { get; set; }
        public Application App { get; set; } = null!;
        public string? AttendanceStatus { get; set; }
    }

    // ═══════════════════════════════════════════
    // ATTENDANCE REPORTS — the flexible, filterable report (Daily/Monthly
    // period × Employee/Department/Location/Shift grouping). One row shape
    // serves both modes: GroupName/SubLabel/EmployeeCount describe WHAT the
    // row is (one employee, or a group of them); everything else is always
    // a total for whatever that row represents, except AvgHrsPerDay which
    // is a true average (WorkedHours / WorkedDaysCount) at either level.
    // ═══════════════════════════════════════════
    public class AttendanceReportRow
    {
        public string GroupName { get; set; } = "";  // Employee-wise: "Code — Name". Grouped: the Department/Location/Shift name.
        public string? SubLabel { get; set; }         // Employee-wise: "Dept / Location / Shift". Grouped: null (EmployeeCount is shown instead).
        public int EmployeeCount { get; set; } = 1;    // 1 for an employee-wise row, N for a grouped row.
        public int WorkingDays, Present, Absent, HalfDay, Leave, MissingPunch, Late, Early;
        public int WorkedDaysCount;                    // days with an actual WorkedMinutes value — the denominator for AvgHrsPerDay
        public decimal WorkedHours, AvgHrsPerDay, OTHours, LOPDays;
    }

    // ═══════════════════════════════════════════
    // LEAVE REPORTS — one screen, three views (Balance / Applications /
    // Summary) selected by "View"; only the matching list is populated so
    // the Razor view just checks View and renders the right table.
    // ═══════════════════════════════════════════
    public class LeaveReportsViewModel
    {
        public string View { get; set; } = "Balance";
        public List<LeaveBalanceRow> BalanceRows { get; set; } = new();
        public List<Application> ApplicationRows { get; set; } = new();   // Applications view — covers Taken/Pending/Rejected/History via the Status filter
        public List<LeaveGroupRow> SummaryRows { get; set; } = new();     // Summary view — Leave Type-wise or Department-wise breakdown
    }

    // One employee × one Leave Type within their policy, for one leave
    // cycle (see ReportsController.LeaveReports for how "as of" accrual is
    // computed — there is no persisted year-to-year balance ledger, so
    // AccruedSoFar is always computed fresh for the selected cycle only,
    // never carried in from a prior cycle).
    public class LeaveBalanceRow
    {
        public Employee Employee { get; set; } = null!;
        public string LeaveTypeName { get; set; } = "";
        public string LeaveTypeAlias { get; set; } = "";
        public decimal Entitlement, AccruedSoFar, Taken, Pending, Balance;
    }

    public class LeaveGroupRow
    {
        public string GroupName { get; set; } = "";
        public int Applications, Approved, Pending, Rejected, Other;
        public decimal TotalDays;
    }

    // ═══════════════════════════════════════════
    // COMPLIANCE REPORTS — one screen, five views selected by "View";
    // Attendance Register and the OT reports already exist as their own
    // pages (linked from here) rather than being rebuilt.
    // ═══════════════════════════════════════════
    public class ComplianceReportsViewModel
    {
        public string View { get; set; } = "MusterRoll";
        public List<AttendanceRegisterRow> MusterRows { get; set; } = new();     // same shape as the Attendance Register — Muster Roll adds identity columns in the view
        public List<WorkingHoursRow> WorkingHoursRows { get; set; } = new();
        public List<HolidayWeekOffWorkRow> HolidayWeekOffRows { get; set; } = new();
        public List<Employee> Joiners { get; set; } = new();
        public List<Employee> Exits { get; set; } = new();
    }

    public class WorkingHoursRow
    {
        public Employee Employee { get; set; } = null!;
        public AttendanceDaily Daily { get; set; } = null!;
        public decimal Hours;
    }

    public class HolidayWeekOffWorkRow
    {
        public Employee Employee { get; set; } = null!;
        public AttendanceDaily Daily { get; set; } = null!;
        public string Label { get; set; } = "";
    }
}
