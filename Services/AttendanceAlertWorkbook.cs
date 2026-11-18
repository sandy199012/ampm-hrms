using System.Globalization;
using AmpmHrmsPro.Models;
using ClosedXML.Excel;

namespace AmpmHrmsPro.Services
{
    // ═══════════════════════════════════════════════════════════════════
    // Excel attachments for the Daily Attendance Alert and the Weekly
    // Attendance Report — same look as the Gap Analysis workbook: navy
    // header row, thin borders, frozen header + name columns, filters,
    // and the same colours (late = peach, early going / short = yellow,
    // absent = salmon, miss punch = lavender, week off / holiday = grey).
    // ═══════════════════════════════════════════════════════════════════
    public static class AttendanceAlertWorkbook
    {
        static readonly XLColor HeaderFill = XLColor.FromHtml("#1F4E78");
        static readonly XLColor GreyFill   = XLColor.FromHtml("#E7E6E6");
        static readonly XLColor LateFill   = XLColor.FromHtml("#FCE4D6");
        static readonly XLColor EarlyFill  = XLColor.FromHtml("#FFF2CC");
        static readonly XLColor AbsentFill = XLColor.FromHtml("#F8CBAD");
        static readonly XLColor MissFill   = XLColor.FromHtml("#E4DFEC");
        static readonly XLColor LeaveFill  = XLColor.FromHtml("#DDEBF7");
        static readonly XLColor GoodFill   = XLColor.FromHtml("#E2EFDA");

        public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

        /// <summary>One employee-day, with late / early-going / miss-punch worked out against the shift.</summary>
        public class DayRow
        {
            public string Name = "", Code = "", Designation = "";
            public DateTime Date;
            public string? ShiftName;
            public TimeSpan? ShiftIn, ShiftOut, In, Out;
            public int? WorkedMin, LateMin, EarlyMin;
            public string Status = "";
            public bool Late, Early, Miss, Absent, Leave, HalfDay, Present, RestDay, Regularized;
            public bool HasIssue => Late || Early || Miss || Absent;
        }

        public static DayRow Compute(AttendanceDaily r)
        {
            var e = r.Employee!;
            var shift = e.Shift;
            var st = (r.EffectiveStatus ?? "").Trim();
            var su = st.ToUpperInvariant();
            var x = new DayRow
            {
                Name = e.Name, Code = e.EmpCode, Designation = e.Designation?.Name ?? "",
                Date = DateTime.TryParse(r.Date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : DateTime.MinValue,
                ShiftName = shift?.Name, ShiftIn = shift?.StartTime, ShiftOut = shift?.EndTime,
                In = r.InTime, Out = r.OutTime, WorkedMin = r.WorkedMinutes, Status = st,
                Regularized = r.RawStatus != r.EffectiveStatus,
            };
            x.Miss    = su.Contains("MIS");
            x.Absent  = !x.Miss && (su == "A" || su.StartsWith("A ("));
            x.Leave   = su.StartsWith("L (") || su.StartsWith("L(") || su == "L";
            x.HalfDay = su == "HD" || su.StartsWith("HD (");
            x.Present = su == "P" || su.StartsWith("P (") || su.StartsWith("POW") || su.StartsWith("POH");
            x.RestDay = !x.Present && (su.StartsWith("WO") || su == "H" || r.WasWeekOff || r.WasHoliday) && !x.Absent && !x.Leave;
            if (!x.WorkedMin.HasValue && r.InTime.HasValue && r.OutTime.HasValue && r.OutTime > r.InTime)
                x.WorkedMin = (int)(r.OutTime.Value - r.InTime.Value).TotalMinutes;

            // Same rules as the Daily Attendance Alert email: only plain P / HD days,
            // and not days a regularisation already corrected.
            bool normalDay = st == "P" || st == "HD";
            if (shift != null && normalDay && !x.Regularized)
            {
                if (r.InTime.HasValue && r.InTime.Value > shift.StartTime + TimeSpan.FromMinutes(shift.GraceMinutes))
                { x.Late = true; x.LateMin = (int)(r.InTime.Value - shift.StartTime).TotalMinutes; }
                if (r.OutTime.HasValue && r.OutTime.Value < shift.EndTime)
                { x.Early = true; x.EarlyMin = (int)(shift.EndTime - r.OutTime.Value).TotalMinutes; }
            }
            return x;
        }

        // ── Daily ────────────────────────────────────────────────────────────
        public static byte[] BuildDaily(string deptName, string? headName, DateTime forDate, List<DayRow> rows)
        {
            using var wb = new XLWorkbook();
            wb.Style.Font.FontName = "Arial";
            var sum = wb.Worksheets.Add("Summary");
            WriteSummaryBlock(sum, "Daily Attendance Alert", deptName, headName, forDate.ToString("dd-MMM-yyyy (dddd)", CultureInfo.InvariantCulture), new (string, int, XLColor?)[]
            {
                ("Employees with attendance", rows.Count, null),
                ("Present", rows.Count(r => r.Present), GoodFill),
                ("Half Day", rows.Count(r => r.HalfDay), EarlyFill),
                ("Absent", rows.Count(r => r.Absent), AbsentFill),
                ("On Leave", rows.Count(r => r.Leave), LeaveFill),
                ("Week Off / Holiday", rows.Count(r => r.RestDay), GreyFill),
                ("Late Coming", rows.Count(r => r.Late), LateFill),
                ("Early Going", rows.Count(r => r.Early), EarlyFill),
                ("Miss Punch", rows.Count(r => r.Miss), MissFill),
            });

            var ordered = rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
            DetailSheet(wb.Worksheets.Add("Exceptions"), ordered.Where(r => r.HasIssue).ToList(), withDate: false);
            DetailSheet(wb.Worksheets.Add("All Employees"), ordered, withDate: false);
            return Save(wb);
        }

        // ── Weekly ───────────────────────────────────────────────────────────
        static readonly string[] WeeklyHeaders =
        {
            "Employee", "Code", "Designation", "Present", "Half Day", "Absent", "Leave", "Week Off", "Holiday",
            "Miss Punch", "Late (days)", "Late (total min)", "Early Going (days)", "Early (total min)", "Hours Worked", "Attendance %"
        };
        static readonly double[] WeeklyWidths = { 28, 8, 20, 9, 9, 9, 9, 9, 9, 10, 10, 11, 11, 11, 11, 11 };

        public static byte[] BuildWeekly(string deptName, string? headName, DateTime weekStart, DateTime weekEnd,
            List<AttendanceDaily> source, List<DayRow> rows)
        {
            using var wb = new XLWorkbook();
            wb.Style.Font.FontName = "Arial";
            var ws = wb.Worksheets.Add("Summary");
            Header(ws, WeeklyHeaders, WeeklyWidths);
            int r = 2;
            var wasWo = source.GroupBy(s => s.EmployeeId).ToDictionary(g => g.First().Employee!.EmpCode, g => g.Count(s => s.WasWeekOff));
            var wasH  = source.GroupBy(s => s.EmployeeId).ToDictionary(g => g.First().Employee!.EmpCode, g => g.Count(s => s.WasHoliday));
            foreach (var g in rows.GroupBy(x => x.Code).OrderBy(g => g.First().Name, StringComparer.OrdinalIgnoreCase))
            {
                var l = g.ToList(); var f = l[0];
                int present = l.Count(x => x.Present), half = l.Count(x => x.HalfDay), absent = l.Count(x => x.Absent);
                int leave = l.Count(x => x.Leave), miss = l.Count(x => x.Miss);
                int lateD = l.Count(x => x.Late), lateM = l.Sum(x => x.LateMin ?? 0);
                int earlyD = l.Count(x => x.Early), earlyM = l.Sum(x => x.EarlyMin ?? 0);
                int worked = l.Sum(x => x.WorkedMin ?? 0);
                int working = l.Count(x => !x.RestDay);
                double pct = working == 0 ? 0 : (present + half * 0.5) / working;

                ws.Cell(r, 1).Value = f.Name;
                SetCode(ws.Cell(r, 2), f.Code);
                ws.Cell(r, 3).Value = f.Designation;
                ws.Cell(r, 4).Value = present; ws.Cell(r, 5).Value = half; ws.Cell(r, 6).Value = absent;
                ws.Cell(r, 7).Value = leave;
                ws.Cell(r, 8).Value = wasWo.GetValueOrDefault(f.Code); ws.Cell(r, 9).Value = wasH.GetValueOrDefault(f.Code);
                ws.Cell(r, 10).Value = miss; ws.Cell(r, 11).Value = lateD; ws.Cell(r, 12).Value = lateM;
                ws.Cell(r, 13).Value = earlyD; ws.Cell(r, 14).Value = earlyM;
                ws.Cell(r, 15).Value = Hm(worked);
                ws.Cell(r, 16).Value = pct; ws.Cell(r, 16).Style.NumberFormat.Format = "0%";
                ws.Range(r, 4, r, 16).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

                if (half > 0)   ws.Cell(r, 5).Style.Fill.BackgroundColor = EarlyFill;
                if (absent > 0) ws.Cell(r, 6).Style.Fill.BackgroundColor = AbsentFill;
                if (leave > 0)  ws.Cell(r, 7).Style.Fill.BackgroundColor = LeaveFill;
                if (miss > 0)   ws.Cell(r, 10).Style.Fill.BackgroundColor = MissFill;
                if (lateD > 0)  ws.Range(r, 11, r, 12).Style.Fill.BackgroundColor = LateFill;
                if (earlyD > 0) ws.Range(r, 13, r, 14).Style.Fill.BackgroundColor = EarlyFill;
                ws.Cell(r, 16).Style.Fill.BackgroundColor = pct >= 0.9 ? GoodFill : pct >= 0.75 ? EarlyFill : AbsentFill;
                r++;
            }
            Finish(ws, r - 1, WeeklyHeaders.Length, freezeCols: 2);
            TitleNote(ws, r + 1, $"Weekly Attendance Report — {deptName} — {weekStart:dd MMM} to {weekEnd:dd MMM yyyy}", headName);

            var ordered = rows.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Date).ToList();
            DetailSheet(wb.Worksheets.Add("Daily Detail"), ordered, withDate: true);
            DetailSheet(wb.Worksheets.Add("Exceptions Only"), ordered.Where(x => x.HasIssue).ToList(), withDate: true);
            return Save(wb);
        }

        // ── shared pieces ────────────────────────────────────────────────────
        static readonly string[] DetailHeadersDated =
        {
            "Employee", "Code", "Designation", "Date", "Day", "Shift", "Shift In", "Shift Out", "Actual In", "Actual Out",
            "Hours Worked", "HRMS Status", "Late (min)", "Early Going (min)", "Late", "Early Going", "Miss Punch", "Absent", "Remarks"
        };
        static readonly double[] DetailWidthsDated = { 26, 8, 20, 11, 6, 16, 9, 9, 9, 9, 10, 12, 9, 11, 7, 9, 9, 8, 30 };

        static void DetailSheet(IXLWorksheet ws, List<DayRow> rows, bool withDate)
        {
            // Without a date column (daily file) the Date/Day columns are dropped.
            var headers = withDate ? DetailHeadersDated : DetailHeadersDated.Where((_, i) => i != 3 && i != 4).ToArray();
            var widths  = withDate ? DetailWidthsDated  : DetailWidthsDated.Where((_, i) => i != 3 && i != 4).ToArray();
            Header(ws, headers, widths);
            int r = 2;
            foreach (var x in rows)
            {
                int c = 1;
                ws.Cell(r, c++).Value = x.Name;
                SetCode(ws.Cell(r, c++), x.Code);
                ws.Cell(r, c++).Value = x.Designation;
                if (withDate)
                {
                    ws.Cell(r, c).Value = x.Date; ws.Cell(r, c++).Style.DateFormat.Format = "dd-mmm-yy";
                    ws.Cell(r, c++).Value = x.Date.ToString("ddd", CultureInfo.InvariantCulture);
                }
                int first = c;
                ws.Cell(r, c++).Value = x.ShiftName ?? "";
                ws.Cell(r, c++).Value = Tm(x.ShiftIn);
                ws.Cell(r, c++).Value = Tm(x.ShiftOut);
                ws.Cell(r, c++).Value = Tm(x.In);
                ws.Cell(r, c++).Value = Tm(x.Out);
                ws.Cell(r, c++).Value = x.WorkedMin.HasValue ? Hm(x.WorkedMin.Value) : "";
                ws.Cell(r, c++).Value = x.Status;
                int cStatus = c - 1;
                int cLate = c; if (x.LateMin.HasValue) ws.Cell(r, c).Value = x.LateMin.Value; c++;
                int cEarly = c; if (x.EarlyMin.HasValue) ws.Cell(r, c).Value = x.EarlyMin.Value; c++;
                int cL = c++, cE = c++, cM = c++, cA = c++;
                if (x.Late) ws.Cell(r, cL).Value = "Yes";
                if (x.Early) ws.Cell(r, cE).Value = "Yes";
                if (x.Miss) ws.Cell(r, cM).Value = "Yes";
                if (x.Absent) ws.Cell(r, cA).Value = "Yes";
                var remarks = new List<string>();
                if (x.Late) remarks.Add($"Late by {Hm(x.LateMin ?? 0)}");
                if (x.Early) remarks.Add($"Left {Hm(x.EarlyMin ?? 0)} early");
                if (x.Miss) remarks.Add("Miss punch");
                if (x.Absent) remarks.Add("Absent");
                if (x.Regularized) remarks.Add("Regularised");
                ws.Cell(r, c).Value = string.Join("; ", remarks);
                ws.Range(r, first, r, c - 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

                if (x.RestDay) ws.Range(r, 1, r, headers.Length).Style.Fill.BackgroundColor = GreyFill;
                else
                {
                    if (x.Late)   { ws.Cell(r, cLate).Style.Fill.BackgroundColor = LateFill; ws.Cell(r, cL).Style.Fill.BackgroundColor = LateFill; }
                    if (x.Early)  { ws.Cell(r, cEarly).Style.Fill.BackgroundColor = EarlyFill; ws.Cell(r, cE).Style.Fill.BackgroundColor = EarlyFill; }
                    if (x.Miss)   ws.Cell(r, cM).Style.Fill.BackgroundColor = MissFill;
                    if (x.Absent) ws.Cell(r, cA).Style.Fill.BackgroundColor = AbsentFill;
                    if (x.Leave)  ws.Cell(r, cStatus).Style.Fill.BackgroundColor = LeaveFill;
                    var rf = x.Absent ? AbsentFill : x.Miss ? MissFill : x.Late ? LateFill : x.Early ? EarlyFill : null;
                    if (rf != null) ws.Cell(r, c).Style.Fill.BackgroundColor = rf;
                }
                r++;
            }
            if (rows.Count == 0) { ws.Cell(2, 1).Value = "Nothing to report"; ws.Cell(2, 1).Style.Font.Italic = true; r = 3; }
            Finish(ws, r - 1, headers.Length, freezeCols: 2);
        }

        static void WriteSummaryBlock(IXLWorksheet ws, string title, string dept, string? head, string period, (string Label, int Count, XLColor? Fill)[] items)
        {
            ws.Cell(1, 1).Value = title;
            ws.Range(1, 1, 1, 2).Merge();
            ws.Cell(1, 1).Style.Font.Bold = true; ws.Cell(1, 1).Style.Font.FontSize = 14;
            ws.Cell(1, 1).Style.Font.FontColor = XLColor.White; ws.Cell(1, 1).Style.Fill.BackgroundColor = HeaderFill;
            ws.Row(1).Height = 26;
            var info = new (string, string)[] { ("Department", dept), ("Department Head", head ?? ""), ("Date", period) };
            int r = 2;
            foreach (var (k, v) in info)
            {
                ws.Cell(r, 1).Value = k; ws.Cell(r, 1).Style.Font.Bold = true; ws.Cell(r, 1).Style.Fill.BackgroundColor = GreyFill;
                ws.Cell(r, 2).Value = v; r++;
            }
            r++;
            ws.Cell(r, 1).Value = "Item"; ws.Cell(r, 2).Value = "Employees";
            var hr = ws.Range(r, 1, r, 2);
            hr.Style.Font.Bold = true; hr.Style.Font.FontColor = XLColor.White; hr.Style.Fill.BackgroundColor = HeaderFill;
            int top = r; r++;
            foreach (var (label, count, fill) in items)
            {
                ws.Cell(r, 1).Value = label;
                ws.Cell(r, 2).Value = count;
                ws.Cell(r, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                if (fill != null && count > 0) ws.Range(r, 1, r, 2).Style.Fill.BackgroundColor = fill;
                r++;
            }
            var all = ws.Range(2, 1, r - 1, 2);
            all.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            all.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
            ws.Column(1).Width = 28; ws.Column(2).Width = 34;
            ws.Cell(r + 1, 1).Value = "Details are on the 'Exceptions' and 'All Employees' sheets.";
            ws.Cell(r + 1, 1).Style.Font.Italic = true; ws.Cell(r + 1, 1).Style.Font.FontColor = XLColor.Gray;
        }

        static void TitleNote(IXLWorksheet ws, int row, string title, string? head)
        {
            ws.Cell(row, 1).Value = title + (string.IsNullOrEmpty(head) ? "" : $" — Head: {head}");
            ws.Cell(row, 1).Style.Font.Italic = true; ws.Cell(row, 1).Style.Font.FontColor = XLColor.Gray;
        }

        static void Header(IXLWorksheet ws, string[] headers, double[] widths)
        {
            for (int c = 0; c < headers.Length; c++)
            {
                var cell = ws.Cell(1, c + 1);
                cell.Value = headers[c];
                cell.Style.Font.Bold = true;
                cell.Style.Font.FontColor = XLColor.White;
                cell.Style.Fill.BackgroundColor = HeaderFill;
                cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
                cell.Style.Alignment.WrapText = true;
                ws.Column(c + 1).Width = widths[c];
            }
            ws.Row(1).Height = 32;
        }

        static void Finish(IXLWorksheet ws, int lastRow, int cols, int freezeCols)
        {
            var rng = ws.Range(1, 1, Math.Max(lastRow, 1), cols);
            rng.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            rng.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
            ws.SheetView.Freeze(1, freezeCols);
            rng.SetAutoFilter();
            ws.PageSetup.PageOrientation = XLPageOrientation.Landscape;
            ws.PageSetup.FitToPages(1, 0);
            ws.PageSetup.SetRowsToRepeatAtTop(1, 1);
        }

        static void SetCode(IXLCell cell, string code) => cell.Value = code;   // keep leading zeros (00126)
        static string Tm(TimeSpan? t) => t.HasValue ? $"{(int)t.Value.TotalHours % 24:00}:{t.Value.Minutes:00}" : "";
        static string Hm(int m) => $"{m / 60}:{Math.Abs(m % 60):00}";

        static byte[] Save(XLWorkbook wb)
        {
            using var ms = new MemoryStream();
            wb.SaveAs(ms);
            return ms.ToArray();
        }
    }
}
