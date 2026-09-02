using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using AmpmHrmsPro.Data;
using AmpmHrmsPro.Models;

namespace AmpmHrmsPro.Services
{
    // ═══════════════════════════════════════════
    // EXCEL REPORT BUILDER — reproduces the company's "AMPM <Month> <Year>
    // Final Report.xlsx" workbook: same 11 sheets, same columns, same
    // color-coding, generated fresh from live AttendanceDaily/Application
    // data instead of a static export. The source file had zero live
    // formulas (it was itself a static export), so there was nothing to
    // port — every value and color rule here was reverse-engineered from a
    // full structural analysis of a real July-2026 copy of that report
    // (see the color tables below, each commented with what they matched).
    // ═══════════════════════════════════════════
    public static class ExcelReportBuilder
    {
        // Colors, verbatim from the analysed source workbook.
        const string ClrGreen = "E2EFDA";      // Present-family (P, POW, HD, approved Leave/Reg)
        const string ClrRed = "FFC7CE";        // Absent
        const string ClrGrey = "D6DCE4";        // Week Off
        const string ClrYellow = "FFF2CC";      // Mispunch / no-data
        const string ClrBorderApproved = "70AD47";
        const string ClrBorderPending = "FF8000";
        const string ClrBorderRejected = "FF0000";
        const string ClrBorderRevoked = "808080";
        const string ClrBorderDefault = "B8CCE4";
        const string ClrStatusApproved = "E2EFDA";
        const string ClrStatusPending = "FFF2CC";
        const string ClrStatusRejected = "FFC7CE";
        const string ClrStatusRevoked = "D9D9D9";
        const string ClrNavy = "1F3864";
        const string ClrGreenDark = "375623";
        const string ClrAmber = "9C6500";
        const string ClrNavyLight = "2E4A7A";
        const string ClrRedDark = "9C0006";
        const string ClrDeptBanner = "4472C4";
        const string ClrRetailGreen = "D6E4BC";
        const string ClrWorkerPurple = "E2CFED";
        const string ClrOTOrange = "FFE2CC";

        public static byte[] BuildMonthlyReport(AppDbContext db, int year, int month)
        {
            using var wb = new XLWorkbook();

            var employees = db.Employees.Include(e => e.Department).Include(e => e.Designation).Include(e => e.Shift)
                .Where(e => e.IsActive).OrderBy(e => e.Name).ToList();

            int daysInMonth = DateTime.DaysInMonth(year, month);
            var monthStartStr = new DateTime(year, month, 1).ToString("yyyy-MM-dd");
            var monthEndStr = new DateTime(year, month, daysInMonth).ToString("yyyy-MM-dd");

            var dailyAll = db.AttendanceDailies
                .Where(d => string.Compare(d.Date, monthStartStr) >= 0 && string.Compare(d.Date, monthEndStr) <= 0)
                .ToList();
            var dailyByEmp = dailyAll.ToLookup(d => d.EmployeeId);

            var applications = db.Applications.Include(a => a.Employee).ThenInclude(e => e!.Department)
                .Include(a => a.LeaveType).Include(a => a.Approver)
                .Where(a => string.Compare(a.FromDate, monthEndStr) <= 0 && string.Compare(a.ToDate, monthStartStr) >= 0)
                .OrderBy(a => a.FromDate).ToList();

            BuildDashboard(wb, employees, dailyByEmp, year, month);
            BuildAttendanceRegister(wb, employees, dailyByEmp, applications, year, month, daysInMonth);
            BuildApplicationTracker(wb, applications);
            BuildEmployeeSummary(wb, employees, dailyByEmp, applications);
            BuildDateWiseApplications(wb, applications, dailyAll, year, month);
            BuildLegendKey(wb);
            BuildOTAttendanceReport(wb, employees, dailyByEmp, daysInMonth);
            BuildOTDepartmentSummary(wb, employees, dailyByEmp, daysInMonth);
            BuildOTWorkerDetails(wb, employees, dailyByEmp);
            BuildOTDailyRegister(wb, employees, dailyByEmp);
            BuildOTRulesLegend(wb);

            using var stream = new MemoryStream();
            wb.SaveAs(stream);
            return stream.ToArray();
        }

        static void Fill(IXLCell cell, string rgbHex) => cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#" + rgbHex);

        // Shared with ReportsController's on-screen views, so the on-screen
        // grids and the Excel export always classify a status the same way.
        public static bool IsPresentFamily(string s) => s == "P" || s == "POW" || s.StartsWith("HD") || s.StartsWith("P (") || s.StartsWith("L (");
        public static bool IsMispunch(string s) => s.Contains("MIS");
        public static bool IsAbsentFamily(string s) => s == "A";

        // ── Late / Early / LOP — used by ReportsController's Attendance
        // Reports screen. A Late/Early flag only means something relative
        // to an assigned shift on a day that was actually scheduled to be
        // worked, so both return false with no shift, and both skip a
        // week-off/holiday (there's no "on time" to measure against).
        public static bool IsLate(AttendanceDaily d, Shift? shift)
            => shift != null && !d.WasWeekOff && !d.WasHoliday && d.InTime.HasValue
               && d.InTime.Value > shift.StartTime + TimeSpan.FromMinutes(shift.GraceMinutes);

        public static bool IsEarlyGoing(AttendanceDaily d, Shift? shift)
            => shift != null && !d.WasWeekOff && !d.WasHoliday && d.OutTime.HasValue
               && d.OutTime.Value < shift.EndTime - TimeSpan.FromMinutes(shift.GraceMinutes);

        // LOP (Loss of Pay) days contributed by one AttendanceDaily row.
        // Plain Absent, and a Mispunch that's still unresolved by the end
        // of the period (no approved Regularisation, or EffectiveStatus
        // would already show something else), each cost a full day. A
        // Leave day costs a day only if its LeaveType is marked Unpaid —
        // half a day for a half-day leave. leaveAliasPaid maps each
        // LeaveType's Alias (as embedded in the "L (CL)" / "HD (L-CL)"
        // status text) to whether it's paid; an alias not found (e.g. the
        // leave type was later deleted) defaults to paid, so a data gap
        // never silently docks pay.
        public static decimal LopDays(AttendanceDaily d, Dictionary<string, bool> leaveAliasPaid)
        {
            var s = d.EffectiveStatus;
            if (s == "A" || IsMispunch(s)) return 1m;
            if (s.StartsWith("L (") && s.EndsWith(")"))
            {
                var alias = s.Substring(3, s.Length - 4);
                return leaveAliasPaid.TryGetValue(alias, out var paid) && !paid ? 1m : 0m;
            }
            if (s.StartsWith("HD (L-") && s.EndsWith(")"))
            {
                var alias = s.Substring(6, s.Length - 7);
                return leaveAliasPaid.TryGetValue(alias, out var paid) && !paid ? 0.5m : 0m;
            }
            return 0m;
        }

        // ═══ SHEET 1 — DASHBOARD ═══
        static void BuildDashboard(XLWorkbook wb, List<Employee> employees, ILookup<int, AttendanceDaily> dailyByEmp, int year, int month)
        {
            var ws = wb.Worksheets.Add("📊 Dashboard");
            ws.Cell(1, 1).Value = "AMPM Fashions Pvt. Ltd — HR Dashboard";
            ws.Range(1, 1, 1, 10).Merge(); Fill(ws.Cell(1, 1), ClrNavy);
            ws.Cell(1, 1).Style.Font.FontColor = XLColor.White; ws.Cell(1, 1).Style.Font.Bold = true; ws.Cell(1, 1).Style.Font.FontSize = 14;
            ws.Cell(2, 1).Value = $"{new DateTime(year, month, 1):MMMM yyyy} — Generated {DateTime.Now:dd-MMM-yyyy}";

            int present = 0, absent = 0, leave = 0, weekOff = 0;
            foreach (var e in employees)
                foreach (var d in dailyByEmp[e.Id])
                {
                    if (IsPresentFamily(d.EffectiveStatus)) present++;
                    else if (d.EffectiveStatus == "A") absent++;
                    if (d.EffectiveStatus.StartsWith("L (")) leave++;
                    if (d.WasWeekOff) weekOff++;
                }

            var probEnd = employees.Where(e => !string.IsNullOrWhiteSpace(e.DOJ))
                .Select(e => (Emp: e, ProbEnd: DateTime.Parse(e.DOJ!).AddMonths(6))).ToList();
            int pending = probEnd.Count(x => x.ProbEnd >= DateTime.Today);
            int complete = probEnd.Count(x => x.ProbEnd < DateTime.Today);

            var tiles = new (string Label, string Value, string Color)[]
            {
                ("Total Employees", employees.Count.ToString(), ClrNavy),
                ("Probation Complete", complete.ToString(), ClrGreenDark),
                ("Probation Pending", pending.ToString(), ClrAmber),
                ("Present (month)", present.ToString(), ClrGreenDark),
                ("Absent (month)", absent.ToString(), ClrRedDark),
                ("On Leave (month)", leave.ToString(), ClrNavyLight),
                ("Week Off (month)", weekOff.ToString(), ClrGrey),
            };
            int col = 1;
            foreach (var t in tiles)
            {
                ws.Cell(4, col).Value = t.Label; Fill(ws.Cell(4, col), t.Color);
                ws.Cell(4, col).Style.Font.FontColor = XLColor.White; ws.Cell(4, col).Style.Font.FontSize = 9;
                ws.Cell(5, col).Value = t.Value; Fill(ws.Cell(5, col), t.Color);
                ws.Cell(5, col).Style.Font.FontColor = XLColor.White; ws.Cell(5, col).Style.Font.Bold = true; ws.Cell(5, col).Style.Font.FontSize = 16;
                col++;
            }

            int row = 8;
            ws.Cell(row, 1).Value = "🎓 PROBATION STATUS TRACKER — 6 Months from Date of Joining";
            ws.Range(row, 1, row, 10).Merge(); ws.Cell(row, 1).Style.Font.Bold = true;
            row++;
            string[] headers = { "#", "Employee Name", "Code", "Department", "Designation", "Date of Joining", "Prob. End Date", "Days Remaining", "Status" };
            for (int c = 0; c < headers.Length; c++) { ws.Cell(row, c + 1).Value = headers[c]; ws.Cell(row, c + 1).Style.Font.Bold = true; }
            row++;
            int sno = 1;
            foreach (var (emp, probE) in probEnd.OrderBy(x => x.ProbEnd))
            {
                int daysRemaining = (probE - DateTime.Today).Days;
                bool isComplete = daysRemaining < 0;
                ws.Cell(row, 1).Value = sno++;
                ws.Cell(row, 2).Value = emp.Name;
                ws.Cell(row, 3).Value = emp.EmpCode;
                ws.Cell(row, 4).Value = emp.Department?.Name ?? "";
                ws.Cell(row, 5).Value = emp.Designation?.Name ?? "";
                ws.Cell(row, 6).Value = emp.DOJ;
                ws.Cell(row, 7).Value = probE.ToString("dd-MMM-yyyy");
                ws.Cell(row, 8).Value = isComplete ? "" : daysRemaining.ToString();
                ws.Cell(row, 9).Value = isComplete ? "✅ COMPLETE" : (daysRemaining <= 3 ? $"🔴 {daysRemaining} days" : daysRemaining <= 14 ? $"🟡 {daysRemaining} days" : $"🟢 {daysRemaining} days");
                Fill(ws.Cell(row, 1), isComplete ? ClrGreen : ClrYellow);
                row++;
            }
            ws.SheetView.FreezeRows(9);
            ws.Columns().AdjustToContents();
        }

        // ═══ SHEET 2 — ATTENDANCE REGISTER ═══
        static void BuildAttendanceRegister(XLWorkbook wb, List<Employee> employees, ILookup<int, AttendanceDaily> dailyByEmp,
            List<Application> applications, int year, int month, int daysInMonth)
        {
            var ws = wb.Worksheets.Add("📋 Attendance Register");
            int headerRow = 3;
            ws.Cell(1, 1).Value = $"Attendance Register — {new DateTime(year, month, 1):MMMM yyyy}";
            ws.Cell(2, 1).Value = "🔑 Color Key — 🟢 P/HD=Present  🔴 A=Absent  🟡 A(MIS)=Mispunch  🔵 WO=Week Off | Border: 🟠 Pending 🟢 Approved 🔴 Rejected ⚫ Revoked | 🏖 L=Leave";

            ws.Cell(headerRow, 1).Value = "S.No"; ws.Cell(headerRow, 2).Value = "Employee Name"; ws.Cell(headerRow, 3).Value = "Emp Code";
            ws.Cell(headerRow, 4).Value = "Department"; ws.Cell(headerRow, 5).Value = "Designation";
            int dayStartCol = 6;
            for (int d = 1; d <= daysInMonth; d++)
            {
                var date = new DateTime(year, month, d);
                ws.Cell(headerRow, dayStartCol + d - 1).Value = $"{d:00}\n{date:ddd}";
                ws.Cell(headerRow, dayStartCol + d - 1).Style.Alignment.WrapText = true;
            }
            int sumStartCol = dayStartCol + daysInMonth;
            string[] sumHeaders = { "Total Present", "Total Absent", "Leave Days", "Week Off", "Leave Appr", "Leave Pend", "REG Appr", "REG Pend", "WFH Appr", "WFH Pend", "OD Appr", "OD Pend" };
            for (int i = 0; i < sumHeaders.Length; i++) ws.Cell(headerRow, sumStartCol + i).Value = sumHeaders[i];
            ws.Row(headerRow).Style.Font.Bold = true;

            int row = headerRow + 1, sno = 1;
            foreach (var emp in employees)
            {
                var dayRecords = dailyByEmp[emp.Id].ToDictionary(d => int.Parse(d.Date.Substring(8, 2)));
                var empApps = applications.Where(a => a.EmployeeId == emp.Id).ToList();

                ws.Cell(row, 1).Value = sno++; ws.Cell(row, 2).Value = emp.Name; ws.Cell(row, 3).Value = emp.EmpCode;
                ws.Cell(row, 4).Value = emp.Department?.Name ?? ""; ws.Cell(row, 5).Value = emp.Designation?.Name ?? "";

                int present = 0, absent = 0, leaveDays = 0, weekOff = 0;
                for (int d = 1; d <= daysInMonth; d++)
                {
                    var cell = ws.Cell(row, dayStartCol + d - 1);
                    if (dayRecords.TryGetValue(d, out var rec))
                    {
                        cell.Value = rec.EffectiveStatus;
                        if (IsPresentFamily(rec.EffectiveStatus)) { present++; Fill(cell, ClrGreen); }
                        else if (IsMispunch(rec.EffectiveStatus)) Fill(cell, ClrYellow);
                        else if (rec.EffectiveStatus == "WO" || rec.EffectiveStatus == "POW") { if (rec.EffectiveStatus == "WO") weekOff++; Fill(cell, rec.EffectiveStatus == "WO" ? ClrGrey : ClrGreen); }
                        else if (IsAbsentFamily(rec.EffectiveStatus)) { absent++; Fill(cell, ClrRed); }
                        if (rec.EffectiveStatus.StartsWith("L (")) leaveDays++;

                        var dateStr = new DateTime(year, month, d).ToString("yyyy-MM-dd");
                        var overlappingApp = empApps.FirstOrDefault(a => string.Compare(a.FromDate, dateStr) <= 0 && string.Compare(a.ToDate, dateStr) >= 0);
                        string borderColor = overlappingApp?.Status switch
                        {
                            "Approved" => ClrBorderApproved, "Pending" => ClrBorderPending, "Rejected" => ClrBorderRejected, "Revoked" => ClrBorderRevoked, _ => ClrBorderDefault
                        };
                        cell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                        cell.Style.Border.OutsideBorderColor = XLColor.FromHtml("#" + borderColor);
                    }
                    else cell.Value = "—";
                }

                ws.Cell(row, sumStartCol).Value = present;
                ws.Cell(row, sumStartCol + 1).Value = absent;
                ws.Cell(row, sumStartCol + 2).Value = leaveDays;
                ws.Cell(row, sumStartCol + 3).Value = weekOff;
                ws.Cell(row, sumStartCol + 4).Value = empApps.Count(a => a.Type == "Leave" && a.Status == "Approved");
                ws.Cell(row, sumStartCol + 5).Value = empApps.Count(a => a.Type == "Leave" && a.Status == "Pending");
                ws.Cell(row, sumStartCol + 6).Value = empApps.Count(a => a.Type == "Regularisation" && a.Status == "Approved");
                ws.Cell(row, sumStartCol + 7).Value = empApps.Count(a => a.Type == "Regularisation" && a.Status == "Pending");
                ws.Cell(row, sumStartCol + 8).Value = empApps.Count(a => a.Type == "WFH" && a.Status == "Approved");
                ws.Cell(row, sumStartCol + 9).Value = empApps.Count(a => a.Type == "WFH" && a.Status == "Pending");
                ws.Cell(row, sumStartCol + 10).Value = empApps.Count(a => a.Type == "OD" && a.Status == "Approved");
                ws.Cell(row, sumStartCol + 11).Value = empApps.Count(a => a.Type == "OD" && a.Status == "Pending");
                row++;
            }
            ws.SheetView.FreezeRows(headerRow);
            ws.SheetView.FreezeColumns(5);
            ws.Column(2).Width = 24; ws.Column(4).Width = 20; ws.Column(5).Width = 18;
        }

        // ═══ SHEET 3 — APPLICATION TRACKER ═══
        static void BuildApplicationTracker(XLWorkbook wb, List<Application> applications)
        {
            var ws = wb.Worksheets.Add("📝 Application Tracker");
            ws.Cell(2, 1).Value = $"Generated: {DateTime.Now:dd-MMM-yyyy}";
            int headerRow = 3;
            string[] headers = { "S.No", "Employee Name", "Emp Code", "Department", "Application Type", "From Date", "To Date", "Duration/Days", "Reason/Detail", "Manager/Approver", "Applied On", "Status", "Pending At", "Remarks" };
            for (int i = 0; i < headers.Length; i++) ws.Cell(headerRow, i + 1).Value = headers[i];
            ws.Row(headerRow).Style.Font.Bold = true;

            int row = headerRow + 1, sno = 1;
            foreach (var a in applications)
            {
                string typeLabel = a.Type switch
                {
                    "Regularisation" => "🔄 Regularisation",
                    "WFH" => "🏠 Work From Home",
                    "OD" => "🚗 On Duty",
                    _ => $"🏖 {a.LeaveType?.Name ?? "Leave"}"
                };
                ws.Cell(row, 1).Value = sno++;
                ws.Cell(row, 2).Value = a.Employee?.Name;
                ws.Cell(row, 3).Value = a.Employee?.EmpCode;
                ws.Cell(row, 4).Value = a.Employee?.Department?.Name ?? "";
                ws.Cell(row, 5).Value = typeLabel;
                ws.Cell(row, 6).Value = a.FromDate;
                ws.Cell(row, 7).Value = a.ToDate;
                ws.Cell(row, 8).Value = a.DayPart == "Single" ? $"{a.DurationDays} day(s)" : $"{a.DurationDays} day(s) [{(a.DayPart == "FirstHalf" ? "First Half" : "Second Half")}]";
                ws.Cell(row, 9).Value = a.Reason;
                ws.Cell(row, 10).Value = a.Approver?.Name;
                ws.Cell(row, 11).Value = a.AppliedOn.ToString("dd-MMM-yyyy");
                ws.Cell(row, 12).Value = a.Status;
                ws.Cell(row, 13).Value = a.PendingAt;
                ws.Cell(row, 14).Value = a.Remarks;

                string fill = a.Status switch { "Approved" => ClrStatusApproved, "Pending" => ClrStatusPending, "Rejected" => ClrStatusRejected, _ => ClrStatusRevoked };
                Fill(ws.Cell(row, 12), fill);
                row++;
            }
            ws.SheetView.FreezeRows(headerRow);
            ws.Columns().AdjustToContents();
        }

        // ═══ SHEET 4 — EMPLOYEE SUMMARY ═══
        static void BuildEmployeeSummary(XLWorkbook wb, List<Employee> employees, ILookup<int, AttendanceDaily> dailyByEmp, List<Application> applications)
        {
            var ws = wb.Worksheets.Add("📊 Employee Summary");
            int groupRow = 3, headerRow = 4;
            ws.Range(groupRow, 5, groupRow, 8).Merge(); ws.Cell(groupRow, 5).Value = "ATTENDANCE";
            ws.Range(groupRow, 9, groupRow, 12).Merge(); ws.Cell(groupRow, 9).Value = "LEAVE";
            ws.Range(groupRow, 13, groupRow, 15).Merge(); ws.Cell(groupRow, 13).Value = "REGULARISATION";
            ws.Range(groupRow, 16, groupRow, 18).Merge(); ws.Cell(groupRow, 16).Value = "WFH";
            ws.Range(groupRow, 19, groupRow, 20).Merge(); ws.Cell(groupRow, 19).Value = "OD";
            ws.Row(groupRow).Style.Font.Bold = true;

            string[] headers = { "S.No", "Name", "Code", "Dept", "Present", "Absent", "Leave Days", "WO", "Leave Total", "L ✅", "L ⏳", "L ↩/❌", "REG Total", "REG ✅", "REG ⏳", "WFH Total", "WFH ✅", "WFH ⏳", "OD Total", "OD ✅" };
            for (int i = 0; i < headers.Length; i++) ws.Cell(headerRow, i + 1).Value = headers[i];
            ws.Row(headerRow).Style.Font.Bold = true;

            int row = headerRow + 1, sno = 1;
            foreach (var emp in employees)
            {
                var recs = dailyByEmp[emp.Id].ToList();
                var apps = applications.Where(a => a.EmployeeId == emp.Id).ToList();
                var leaveApps = apps.Where(a => a.Type == "Leave").ToList();
                var regApps = apps.Where(a => a.Type == "Regularisation").ToList();
                var wfhApps = apps.Where(a => a.Type == "WFH").ToList();
                var odApps = apps.Where(a => a.Type == "OD").ToList();

                ws.Cell(row, 1).Value = sno++;
                ws.Cell(row, 2).Value = emp.Name; ws.Cell(row, 3).Value = emp.EmpCode; ws.Cell(row, 4).Value = emp.Department?.Name ?? "";
                ws.Cell(row, 5).Value = recs.Count(r => IsPresentFamily(r.EffectiveStatus));
                ws.Cell(row, 6).Value = recs.Count(r => r.EffectiveStatus == "A");
                ws.Cell(row, 7).Value = recs.Count(r => r.EffectiveStatus.StartsWith("L ("));
                ws.Cell(row, 8).Value = recs.Count(r => r.WasWeekOff);
                ws.Cell(row, 9).Value = leaveApps.Count;
                ws.Cell(row, 10).Value = leaveApps.Count(a => a.Status == "Approved");
                ws.Cell(row, 11).Value = leaveApps.Count(a => a.Status == "Pending");
                ws.Cell(row, 12).Value = leaveApps.Count(a => a.Status is "Revoked" or "Rejected");
                ws.Cell(row, 13).Value = regApps.Count;
                ws.Cell(row, 14).Value = regApps.Count(a => a.Status == "Approved");
                ws.Cell(row, 15).Value = regApps.Count(a => a.Status == "Pending");
                ws.Cell(row, 16).Value = wfhApps.Count;
                ws.Cell(row, 17).Value = wfhApps.Count(a => a.Status == "Approved");
                ws.Cell(row, 18).Value = wfhApps.Count(a => a.Status == "Pending");
                ws.Cell(row, 19).Value = odApps.Count;
                ws.Cell(row, 20).Value = odApps.Count(a => a.Status == "Approved");
                row++;
            }
            ws.SheetView.FreezeRows(headerRow);
            ws.Columns().AdjustToContents();
        }

        // ═══ SHEET 5 — DATE-WISE APPLICATIONS ═══
        static void BuildDateWiseApplications(XLWorkbook wb, List<Application> applications, List<AttendanceDaily> dailyAll, int year, int month)
        {
            var ws = wb.Worksheets.Add("📅 Date-wise Applications");
            int headerRow = 3;
            string[] headers = { "Date", "Day", "Employee Name", "Emp Code", "Department", "Attendance Status", "Application Type", "Status", "Approver", "Reason" };
            for (int i = 0; i < headers.Length; i++) ws.Cell(headerRow, i + 1).Value = headers[i];
            ws.Row(headerRow).Style.Font.Bold = true;

            var dailyByKey = dailyAll.ToDictionary(d => (d.EmployeeId, d.Date));
            int daysInMonth = DateTime.DaysInMonth(year, month);
            var rows = new List<(DateTime Date, Application App)>();
            foreach (var a in applications)
            {
                var from = DateTime.Parse(a.FromDate); var to = DateTime.Parse(a.ToDate);
                for (var d = from; d <= to; d = d.AddDays(1))
                {
                    if (d.Year == year && d.Month == month) rows.Add((d, a));
                }
            }

            int row = headerRow + 1;
            foreach (var (date, a) in rows.OrderBy(r => r.Date))
            {
                var dateStr = date.ToString("yyyy-MM-dd");
                dailyByKey.TryGetValue((a.EmployeeId, dateStr), out var daily);
                ws.Cell(row, 1).Value = date.ToString("dd-MMM-yyyy");
                ws.Cell(row, 2).Value = date.DayOfWeek.ToString();
                ws.Cell(row, 3).Value = a.Employee?.Name;
                ws.Cell(row, 4).Value = a.Employee?.EmpCode;
                ws.Cell(row, 5).Value = a.Employee?.Department?.Name ?? "";
                ws.Cell(row, 6).Value = daily?.EffectiveStatus ?? "";
                ws.Cell(row, 7).Value = a.Type == "Leave" ? (a.LeaveType?.Name ?? "Leave") : a.Type;
                ws.Cell(row, 8).Value = a.Status;
                ws.Cell(row, 9).Value = a.Approver?.Name;
                ws.Cell(row, 10).Value = a.Reason;

                string fill = a.Status switch { "Approved" => ClrStatusApproved, "Pending" => ClrStatusPending, "Rejected" => ClrStatusRejected, _ => ClrStatusRevoked };
                Fill(ws.Cell(row, 8), fill);
                row++;
            }
            ws.SheetView.FreezeRows(headerRow);
            ws.Columns().AdjustToContents();
        }

        // ═══ SHEET 6 — LEGEND & KEY (static content) ═══
        static void BuildLegendKey(XLWorkbook wb)
        {
            var ws = wb.Worksheets.Add("📖 Legend & Key");
            ws.Cell(1, 1).Value = "LEGEND & COLOR KEY";
            ws.Cell(1, 1).Style.Font.Bold = true; ws.Cell(1, 1).Style.Font.FontSize = 13;

            ws.Cell(3, 1).Value = "ATTENDANCE STATUS CODES"; ws.Cell(3, 1).Style.Font.Bold = true;
            ws.Cell(4, 1).Value = "Code"; ws.Cell(4, 2).Value = "Meaning"; ws.Cell(4, 3).Value = "Color";
            ws.Row(4).Style.Font.Bold = true;
            var codes = new (string Code, string Meaning, string Color)[]
            {
                ("P", "Present (Full Day)", ClrGreen),
                ("A", "Absent", ClrRed),
                ("A (MIS-Morning)", "Absent - Missed Morning (In) Punch", ClrYellow),
                ("A (MIS-Evening)", "Absent - Missed Evening (Out) Punch", ClrYellow),
                ("HD", "Half Day", ClrYellow),
                ("L (CL)", "Leave - Casual", ClrGreen),
                ("L (SL)", "Leave - Sick", ClrGreen),
                ("L (EL)", "Leave - Earned", ClrGreen),
                ("WO", "Week Off", ClrGrey),
                ("POW", "Present on Week Off", ClrNavyLight),
            };
            int row = 5;
            foreach (var c in codes)
            {
                ws.Cell(row, 1).Value = c.Code; ws.Cell(row, 2).Value = c.Meaning; Fill(ws.Cell(row, 3), c.Color);
                if (c.Code == "POW") { Fill(ws.Cell(row, 1), ClrNavyLight); Fill(ws.Cell(row, 2), ClrNavyLight); ws.Cell(row, 1).Style.Font.FontColor = XLColor.White; ws.Cell(row, 2).Style.Font.FontColor = XLColor.White; ws.Cell(row, 1).Style.Font.Bold = true; }
                row++;
            }

            row += 1;
            ws.Cell(row, 1).Value = "APPLICATION TYPE CODES (in brackets on Attendance Register)"; ws.Cell(row, 1).Style.Font.Bold = true; row++;
            ws.Cell(row, 1).Value = "Code"; ws.Cell(row, 2).Value = "Application Type"; ws.Cell(row, 3).Value = "Color"; ws.Row(row).Style.Font.Bold = true; row++;
            var appCodes = new (string Code, string Type, string Color)[]
            {
                ("[L]", "Leave Application", "B2EBF2"),
                ("[O]", "On Duty Application", "E4DFEC"),
                ("[R]", "Regularisation Request", ClrYellow),
                ("[W]", "Work From Home Request", ClrNavyLight),
            };
            foreach (var c in appCodes)
            {
                ws.Cell(row, 1).Value = c.Code; ws.Cell(row, 2).Value = c.Type; Fill(ws.Cell(row, 3), c.Color);
                row++;
            }

            row += 1;
            ws.Cell(row, 1).Value = "APPROVAL STATUS COLORS"; ws.Cell(row, 1).Style.Font.Bold = true; row++;
            ws.Cell(row, 2).Value = "Color"; ws.Cell(row, 3).Value = "Description"; ws.Row(row).Style.Font.Bold = true; row++;
            var statusCodes = new (string Status, string Desc, string Color)[]
            {
                ("Approved", "Application approved by manager", ClrStatusApproved),
                ("Pending", "Awaiting manager approval", ClrStatusPending),
                ("Rejected", "Application rejected", ClrStatusRejected),
                ("Revoked", "Application revoked/cancelled", ClrStatusRevoked),
            };
            foreach (var s in statusCodes)
            {
                ws.Cell(row, 1).Value = s.Status; Fill(ws.Cell(row, 2), s.Color); ws.Cell(row, 3).Value = s.Desc;
                row++;
            }
            ws.Columns().AdjustToContents();
        }

        // ═══ SHEET 7 — OT ATTENDANCE REPORT ═══
        static void BuildOTAttendanceReport(XLWorkbook wb, List<Employee> employees, ILookup<int, AttendanceDaily> dailyByEmp, int daysInMonth)
        {
            var ws = wb.Worksheets.Add("📈 OT - Attendance Report");
            ws.Cell(3, 1).Value = "Retail Workers: OT after 9h (In+9h=shift end) | Non-Retail Workers: OT after 18:00 + Sunday≥7h=8h | All workers only";
            int headerRow = 4;
            string[] headers = { "Dept.", "Employee Name", "Emp. Code", "Designation", "Category", "Working Days", "Present", "Absent", "Miss Punch", "Leave", "Avg Hrs/Day", "OT Hours" };
            for (int i = 0; i < headers.Length; i++) ws.Cell(headerRow, i + 1).Value = headers[i];
            ws.Row(headerRow).Style.Font.Bold = true;

            int row = headerRow + 1;
            foreach (var deptGroup in employees.GroupBy(e => e.Department?.Name ?? "(No Department)").OrderBy(g => g.Key))
            {
                ws.Cell(row, 1).Value = $"▶  {deptGroup.Key}";
                ws.Range(row, 1, row, 12).Merge(); Fill(ws.Cell(row, 1), ClrDeptBanner);
                ws.Cell(row, 1).Style.Font.FontColor = XLColor.White; ws.Cell(row, 1).Style.Font.Bold = true;
                row++;

                foreach (var emp in deptGroup)
                {
                    var recs = dailyByEmp[emp.Id].ToList();
                    int present = recs.Count(r => IsPresentFamily(r.EffectiveStatus));
                    int absent = recs.Count(r => r.EffectiveStatus == "A");
                    int missPunch = recs.Count(r => IsMispunch(r.EffectiveStatus));
                    int leave = recs.Count(r => r.EffectiveStatus.StartsWith("L ("));
                    int weekOff = recs.Count(r => r.WasWeekOff);
                    int workingDays = daysInMonth - weekOff;
                    decimal totalWorkedHrs = recs.Where(r => r.WorkedMinutes.HasValue).Sum(r => r.WorkedMinutes!.Value) / 60m;
                    decimal avgHrs = present > 0 ? Math.Round(totalWorkedHrs / present, 2) : 0;
                    decimal otHours = recs.Where(r => r.OTHours.HasValue).Sum(r => r.OTHours!.Value);

                    ws.Cell(row, 1).Value = deptGroup.Key; ws.Cell(row, 2).Value = emp.Name; ws.Cell(row, 3).Value = emp.EmpCode;
                    ws.Cell(row, 4).Value = emp.Designation?.Name ?? ""; ws.Cell(row, 5).Value = emp.Category ?? "";
                    ws.Cell(row, 6).Value = workingDays; ws.Cell(row, 7).Value = present; ws.Cell(row, 8).Value = absent;
                    ws.Cell(row, 9).Value = missPunch; ws.Cell(row, 10).Value = leave; ws.Cell(row, 11).Value = avgHrs;

                    bool isStaff = (emp.Category ?? "").Contains("Staff", StringComparison.OrdinalIgnoreCase);
                    bool isRetail = (emp.Category ?? "").Contains("Retail", StringComparison.OrdinalIgnoreCase);
                    if (!isStaff)
                    {
                        ws.Cell(row, 12).Value = otHours;
                        if (isRetail) Fill(ws.Cell(row, 5), ClrRetailGreen);
                        else if (otHours > 0) { Fill(ws.Cell(row, 5), ClrWorkerPurple); Fill(ws.Cell(row, 12), ClrWorkerPurple); }
                    }
                    row++;
                }
            }
            ws.SheetView.FreezeRows(headerRow);
            ws.Columns().AdjustToContents();
        }

        // ═══ SHEET 8 — OT DEPARTMENT SUMMARY ═══
        static void BuildOTDepartmentSummary(XLWorkbook wb, List<Employee> employees, ILookup<int, AttendanceDaily> dailyByEmp, int daysInMonth)
        {
            var ws = wb.Worksheets.Add("📈 OT - Department Summary");
            string[] headers = { "Department", "Employees", "Present", "Absent", "Miss Punch", "Leave", "Avg Absent/Emp", "Avg Miss/Emp", "Worker OT Hrs", "Attendance %" };
            for (int i = 0; i < headers.Length; i++) ws.Cell(1, i + 1).Value = headers[i];
            ws.Row(1).Style.Font.Bold = true;

            int row = 2;
            foreach (var deptGroup in employees.GroupBy(e => e.Department?.Name ?? "(No Department)").OrderBy(g => g.Key))
            {
                int empCount = deptGroup.Count();
                int present = 0, absent = 0, missPunch = 0, leave = 0, workingDaysTotal = 0;
                decimal workerOT = 0;
                foreach (var emp in deptGroup)
                {
                    var recs = dailyByEmp[emp.Id].ToList();
                    present += recs.Count(r => IsPresentFamily(r.EffectiveStatus));
                    absent += recs.Count(r => r.EffectiveStatus == "A");
                    missPunch += recs.Count(r => IsMispunch(r.EffectiveStatus));
                    leave += recs.Count(r => r.EffectiveStatus.StartsWith("L ("));
                    int weekOff = recs.Count(r => r.WasWeekOff);
                    workingDaysTotal += daysInMonth - weekOff;
                    bool eligible = !(emp.Category ?? "").Contains("Staff", StringComparison.OrdinalIgnoreCase);
                    if (eligible) workerOT += recs.Where(r => r.OTHours.HasValue).Sum(r => r.OTHours!.Value);
                }
                ws.Cell(row, 1).Value = deptGroup.Key; ws.Cell(row, 2).Value = empCount; ws.Cell(row, 3).Value = present;
                ws.Cell(row, 4).Value = absent; ws.Cell(row, 5).Value = missPunch; ws.Cell(row, 6).Value = leave;
                ws.Cell(row, 7).Value = empCount > 0 ? Math.Round((decimal)absent / empCount, 2) : 0;
                ws.Cell(row, 8).Value = empCount > 0 ? Math.Round((decimal)missPunch / empCount, 2) : 0;
                if (workerOT > 0) ws.Cell(row, 9).Value = workerOT;
                ws.Cell(row, 10).Value = workingDaysTotal > 0 ? $"{Math.Round(present * 100m / workingDaysTotal, 1)}%" : "";
                row++;
            }
            ws.SheetView.FreezeRows(1);
            ws.Columns().AdjustToContents();
        }

        // ═══ SHEET 9 — OT WORKER DETAILS ═══
        static void BuildOTWorkerDetails(XLWorkbook wb, List<Employee> employees, ILookup<int, AttendanceDaily> dailyByEmp)
        {
            var ws = wb.Worksheets.Add("📈 OT - Worker OT Details");
            ws.Cell(2, 1).Value = "Non-Retail Workers: Eve OT after 18:00 (nearest 30min) + Morning≥60min=+1h + Sunday≥7h=8h";
            ws.Cell(3, 1).Value = "Retail Workers: OT after In+9h (nearest 30min) | Sunday = normal day";
            int headerRow = 4;
            string[] headers = { "Department", "Employee Name", "Emp. Code", "Designation", "Category", "OT Days", "Total OT Hrs", "Avg OT/Day" };
            for (int i = 0; i < headers.Length; i++) ws.Cell(headerRow, i + 1).Value = headers[i];
            ws.Row(headerRow).Style.Font.Bold = true;

            var rows = new List<(Employee Emp, int OtDays, decimal TotalOT)>();
            foreach (var emp in employees)
            {
                bool eligible = !(emp.Category ?? "").Contains("Staff", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(emp.Category);
                if (!eligible) continue;
                var otRecs = dailyByEmp[emp.Id].Where(r => r.OTHours.HasValue && r.OTHours.Value > 0).ToList();
                if (!otRecs.Any()) continue;
                rows.Add((emp, otRecs.Count, otRecs.Sum(r => r.OTHours!.Value)));
            }

            int row = headerRow + 1;
            foreach (var r in rows.OrderByDescending(x => x.TotalOT))
            {
                ws.Cell(row, 1).Value = r.Emp.Department?.Name ?? ""; ws.Cell(row, 2).Value = r.Emp.Name; ws.Cell(row, 3).Value = r.Emp.EmpCode;
                ws.Cell(row, 4).Value = r.Emp.Designation?.Name ?? ""; ws.Cell(row, 5).Value = r.Emp.Category;
                ws.Cell(row, 6).Value = r.OtDays; ws.Cell(row, 7).Value = r.TotalOT; ws.Cell(row, 8).Value = Math.Round(r.TotalOT / r.OtDays, 2);
                row++;
            }
            ws.SheetView.FreezeRows(headerRow);
            ws.Columns().AdjustToContents();
        }

        // ═══ SHEET 10 — OT DAILY REGISTER ═══
        static void BuildOTDailyRegister(XLWorkbook wb, List<Employee> employees, ILookup<int, AttendanceDaily> dailyByEmp)
        {
            var ws = wb.Worksheets.Add("📈 OT - Daily OT Register");
            ws.Cell(2, 1).Value = "🟢 Retail Workers (OT after In+9h | Sunday=normal) | 🟠 Non-Retail Sunday (8h if ≥7h) | 🟣 OT Hours";
            int headerRow = 3;
            string[] headers = { "Date", "Day", "Dept", "Employee Name", "Emp. Code", "Designation", "In Time", "Out Time", "9h End / Shift End", "Extra Mins", "OT Rule", "OT Hours" };
            for (int i = 0; i < headers.Length; i++) ws.Cell(headerRow, i + 1).Value = headers[i];
            ws.Row(headerRow).Style.Font.Bold = true;

            var empById = employees.ToDictionary(e => e.Id);
            var otRows = employees.SelectMany(e => dailyByEmp[e.Id].Where(r => r.OTHours.HasValue && r.OTHours.Value > 0).Select(r => (Emp: e, Rec: r)))
                .OrderBy(x => x.Rec.Date).ThenBy(x => x.Emp.Name).ToList();

            int row = headerRow + 1;
            foreach (var (emp, rec) in otRows)
            {
                var date = DateTime.Parse(rec.Date);
                string boundary = rec.IsRetailOT && rec.InTime.HasValue
                    ? (date + rec.InTime.Value).AddHours(9).ToString("HH:mm:ss")
                    : (emp.Shift?.EndTime ?? new TimeSpan(18, 30, 0)).ToString(@"hh\:mm\:ss");

                ws.Cell(row, 1).Value = date.ToString("dd-MMM-yyyy"); ws.Cell(row, 2).Value = date.DayOfWeek.ToString().Substring(0, 3);
                ws.Cell(row, 3).Value = emp.Department?.Name ?? ""; ws.Cell(row, 4).Value = emp.Name; ws.Cell(row, 5).Value = emp.EmpCode;
                ws.Cell(row, 6).Value = emp.Designation?.Name ?? "";
                ws.Cell(row, 7).Value = rec.InTime?.ToString(@"hh\:mm\:ss") ?? "";
                ws.Cell(row, 8).Value = rec.OutTime?.ToString(@"hh\:mm\:ss") ?? "";
                ws.Cell(row, 9).Value = boundary;
                ws.Cell(row, 10).Value = rec.ExtraMinutes?.ToString() ?? "—";
                ws.Cell(row, 11).Value = rec.OTRule;
                ws.Cell(row, 12).Value = rec.OTHours;

                bool sundayHolidayFlat = (rec.OTRule ?? "").Contains("Sunday") || (rec.OTRule ?? "").Contains("Holiday") || (rec.OTRule ?? "").Contains("Night");
                if (rec.IsRetailOT) { foreach (var c in ws.Range(row, 1, row, 11).Cells()) Fill(c, ClrRetailGreen); }
                else if (sundayHolidayFlat) { foreach (var c in ws.Range(row, 1, row, 11).Cells()) Fill(c, ClrOTOrange); }
                Fill(ws.Cell(row, 12), ClrWorkerPurple);
                row++;
            }
            ws.SheetView.FreezeRows(headerRow);
            ws.Columns().AdjustToContents();
        }

        // ═══ SHEET 11 — OT RULES & LEGEND (static content) ═══
        static void BuildOTRulesLegend(XLWorkbook wb)
        {
            var ws = wb.Worksheets.Add("📋 OT Rules & Legend");
            ws.Cell(1, 1).Value = "AMPM Fashions Pvt. Ltd — OT Policy & Attendance Legend";
            ws.Cell(1, 1).Style.Font.Bold = true; ws.Cell(1, 1).Style.Font.FontSize = 13;
            ws.Cell(1, 1).Style.Font.FontColor = XLColor.FromHtml("#1565C0");

            string[] ruleHeaders = { "#", "Rule", "Extra Time / Condition", "OT Eligible?", "OT Credited", "Applies To", "Notes" };
            for (int i = 0; i < ruleHeaders.Length; i++) ws.Cell(2, i + 1).Value = ruleHeaders[i];
            ws.Row(2).Style.Font.Bold = true;

            var rules = new (string Rule, string Cond, string Elig, string Credit, string Applies, string Notes)[]
            {
                ("Normal Shift (9:30–18:00)", "Within shift hours", "No OT", "0 Min", "Workers", "Regular hours"),
                ("Extra ≤ 30 minutes after shift", "≤ 30 mins", "No OT*", "0 Min*", "Workers", "*Rounds to 30min slab in practice — see rounding note below"),
                ("Extra 31–45 minutes", "31–45 mins", "✔ Yes", "30 Min", "Workers", "Rounded to 30 min"),
                ("Extra 46–60 minutes", "46–60 mins", "✔ Yes", "60 Min", "Workers", "Rounded to 1 hour"),
                ("Night Work till 00:45 AM or beyond", "Works past 12:45 AM (00:45+)", "✔ Yes", "8 Hours", "Workers", "Night OT benefit"),
                ("Sunday Work", "≥ 7 Hours worked", "✔ Yes", "8 Hours", "Workers", "Sunday = full OT benefit (non-retail)"),
                ("Holiday / Weekly-Off Work", "≥ 7 Hours", "✔ Yes", "8 Hours", "Workers", "Same as Sunday rule"),
                ("Morning OT", "Min. 60 mins before shift start", "✔ Yes (≥60 min)", "60 Min", "Workers", "No partial OT"),
                ("Retail (Store Opening)", "9h shift from In-time", "Same slabs apply", "Per slabs", "Retail Workers", "Sunday = normal day for retail"),
                ("Maximum OT Cap", "Any combination", "Hard Cap", "16 Hours", "All Workers", "Compliance limit"),
            };
            int row = 3;
            foreach (var (rule, cond, elig, credit, applies, notes) in rules)
            {
                ws.Cell(row, 1).Value = row - 2; ws.Cell(row, 2).Value = rule; ws.Cell(row, 3).Value = cond;
                ws.Cell(row, 4).Value = elig; ws.Cell(row, 5).Value = credit; ws.Cell(row, 6).Value = applies; ws.Cell(row, 7).Value = notes;
                row++;
            }

            row += 2;
            ws.Cell(row, 1).Value = "Attendance Status Codes — Legend";
            ws.Range(row, 1, row, 7).Merge(); Fill(ws.Cell(row, 1), "DDEEFF"); ws.Cell(row, 1).Style.Font.Bold = true;
            row++;
            var codes = new (string Code, string Meaning)[]
            {
                ("P", "Full Day Present"), ("HD", "Half Day Present"), ("A", "Absent"),
                ("A (MIS-Morning)", "Miss Punch — In-punch missing"), ("A (MIS-Evening)", "Miss Punch — Out-punch missing"),
                ("L (CL)", "Casual Leave"), ("L (EL)", "Earned Leave"), ("L (SL)", "Sick Leave"), ("L (WOL)", "Leave Without Pay"),
                ("L (COMPO-OFF)", "Compensatory Off"), ("POW", "Present on Holiday / Weekly Off"), ("WO", "Weekly Off — excluded from counts"),
            };
            foreach (var (code, meaning) in codes) { ws.Cell(row, 1).Value = code; ws.Cell(row, 2).Value = meaning; row++; }
            ws.Columns().AdjustToContents();
        }
    }
}
