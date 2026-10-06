// ─────────────────────────────────────────────────────────────────────────────
// Services/GapAnalysisEmail.cs
//
// The Gap Analysis email body: a one-screen dashboard of the HoD's team for
// the month to date. Email clients ignore scripts and most CSS, so this is
// plain tables + inline styles (works in Gmail, Outlook, phone mail apps).
//
//   ┌ header ─ HoD · period · team size ─────────────────────────────────┐
//   │ [Late] [Hrs Not Completed] [Extra Time] [Absent / Unplanned]  tiles │
//   │ Yesterday's gaps (report date)                                      │
//   │ By reporting manager                                                │
//   │ Employees with most gaps this month (top 10)                        │
//   └ footer ─ "full detail in the attached Excel" ───────────────────────┘
//
// Tile / cell tints are the same as the attached Excel (Late #FCE4D6,
// Short #FFF2CC, Extra #E2EFDA, Absent #F8CBAD) and every tint sits next to
// its label, so colour is never the only cue.
// ─────────────────────────────────────────────────────────────────────────────
using System.Globalization;
using System.Net;
using System.Text;

namespace AmpmHrmsPro.Services
{
    public static class GapAnalysisEmail
    {
        private const string Ink      = "#1f2937";
        private const string Muted    = "#6b7280";
        private const string Rule     = "#e5e7eb";
        private const string Navy     = "#1F4E78";   // Excel header colour
        private const string LateBg   = "#FCE4D6";
        private const string ShortBg  = "#FFF2CC";
        private const string ExtraBg  = "#E2EFDA";
        private const string AbsentBg = "#F8CBAD";

        private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
        private static string Hm(int m) => (m < 0 ? "-" : "") + $"{Math.Abs(m) / 60}:{Math.Abs(m) % 60:00}";
        private static string D(DateOnly d, string f) => d.ToString(f, CultureInfo.InvariantCulture);

        public static string BuildHtml(string hodName, List<MtdRow> rows, DateOnly upto)
        {
            var from = new DateOnly(upto.Year, upto.Month, 1);
            var period = from == upto ? D(upto, "dd MMM yyyy") : $"{D(from, "dd MMM")} – {D(upto, "dd MMM yyyy")}";
            var people = rows.GroupBy(r => r.Code).ToList();

            int lateDays  = rows.Count(r => r.Late),  lateMin  = rows.Where(r => r.Late).Sum(r => r.LateMin ?? 0);
            int shortDays = rows.Count(r => r.Short), shortMin = rows.Sum(r => r.ShortMin);
            int extraDays = rows.Count(r => r.Extra), extraMin = rows.Sum(r => r.ExtraMin);
            int absDays   = rows.Count(r => r.Absent);
            int lateEmp   = people.Count(g => g.Any(r => r.Late));
            int shortEmp  = people.Count(g => g.Any(r => r.Short));
            int extraEmp  = people.Count(g => g.Any(r => r.Extra));
            int absEmp    = people.Count(g => g.Any(r => r.Absent));

            var sb = new StringBuilder();
            sb.Append($@"<!DOCTYPE html><html><head><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'></head>
<body style='margin:0;padding:0;background:#f3f4f6;'>
<table role='presentation' width='100%' cellpadding='0' cellspacing='0' style='background:#f3f4f6;'><tr><td align='center' style='padding:20px 10px;'>
<table role='presentation' width='680' cellpadding='0' cellspacing='0' style='width:100%;max-width:680px;background:#ffffff;border-radius:8px;overflow:hidden;font-family:Segoe UI,Arial,sans-serif;color:{Ink};'>");

            // Header
            sb.Append($@"
<tr><td style='background:{Navy};padding:18px 24px;color:#ffffff;'>
  <div style='font-size:12px;letter-spacing:.5px;text-transform:uppercase;opacity:.85;'>Gap Analysis · Month to date</div>
  <div style='font-size:20px;font-weight:600;margin-top:4px;'>{E(hodName)}</div>
  <div style='font-size:13px;margin-top:4px;opacity:.9;'>{period} &nbsp;·&nbsp; {people.Count} team member{(people.Count == 1 ? "" : "s")}</div>
</td></tr>
<tr><td style='padding:18px 24px 4px;font-size:14px;line-height:1.5;'>
  Dear {E(hodName)}, here is your team's attendance gap summary. The complete day-by-day detail is in the attached Excel.
</td></tr>");

            // KPI tiles
            // 2 × 2 so it reads the same on a phone and a desktop
            sb.Append("<tr><td style='padding:12px 18px 4px;'><table role='presentation' width='100%' cellpadding='0' cellspacing='0'><tr>");
            sb.Append(Tile("Late coming", lateDays, "days", $"{lateMin:N0} min in total · {lateEmp} people", LateBg));
            sb.Append(Tile("Hrs not completed", shortDays, "days", $"{Hm(shortMin)} hrs short · {shortEmp} people", ShortBg));
            sb.Append("</tr><tr>");
            sb.Append(Tile("Extra time", extraDays, "days", $"{Hm(extraMin)} hrs extra · {extraEmp} people", ExtraBg));
            sb.Append(Tile("Absent / unplanned", absDays, "days", $"{absEmp} people", AbsentBg));
            sb.Append("</tr></table></td></tr>");

            // Yesterday's gaps
            var yesterday = rows.Where(r => r.Date == upto && r.Gaps != null)
                                .OrderBy(r => r.Name, StringComparer.Ordinal).ToList();
            sb.Append(SectionTitle($"Gaps on {D(upto, "ddd, dd MMM")}", yesterday.Count == 0 ? "No gaps 🎉" : $"{yesterday.Count} employee{(yesterday.Count == 1 ? "" : "s")}"));
            if (yesterday.Count > 0)
            {
                sb.Append(TableStart(("Employee", "left"), ("Reports to", "left"), ("Gap", "left")));
                foreach (var r in yesterday)
                {
                    var bg = r.Absent ? AbsentBg : r.Short ? ShortBg : r.Late ? LateBg : ExtraBg;
                    sb.Append($@"<tr>
  <td style='{Cell()}'><b>{E(r.Name)}</b><div style='color:{Muted};font-size:11px;'>{E(r.Code)}</div></td>
  <td style='{Cell()}color:{Muted};'>{E(r.ReportsTo)}</td>
  <td style='{Cell()}'><span style='display:inline-block;background:{bg};padding:2px 6px;border-radius:4px;'>{E(r.Gaps)}</span></td>
</tr>");
                }
                sb.Append("</table></td></tr>");
            }

            // By reporting manager
            var byMgr = rows.GroupBy(r => string.IsNullOrWhiteSpace(r.ReportsTo) ? "—" : r.ReportsTo!.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(g => new
                {
                    Mgr = g.Key,
                    Emp = g.Select(x => x.Code).Distinct().Count(),
                    Late = g.Count(x => x.Late), Short = g.Count(x => x.Short),
                    Extra = g.Count(x => x.Extra), Abs = g.Count(x => x.Absent),
                })
                .OrderByDescending(x => x.Late + x.Short + x.Abs).ThenBy(x => x.Mgr, StringComparer.Ordinal).ToList();
            if (byMgr.Count > 1)
            {
                sb.Append(SectionTitle("By reporting manager", "days with each gap, month to date"));
                sb.Append(TableStart(("Reporting manager", "left"), ("Team", "center"), ("Late", "center"), ("Hrs not completed", "center"), ("Extra", "center"), ("Absent", "center")));
                foreach (var m in byMgr)
                    sb.Append($@"<tr>
  <td style='{Cell()}'><b>{E(m.Mgr)}</b></td>
  <td style='{Cell()}text-align:center;color:{Muted};'>{m.Emp}</td>
  {Num(m.Late, LateBg)}{Num(m.Short, ShortBg)}{Num(m.Extra, ExtraBg)}{Num(m.Abs, AbsentBg)}
</tr>");
                sb.Append("</table></td></tr>");
            }

            // Top employees
            var top = people
                .Select(g => new
                {
                    Name = g.First().Name, Code = g.First().Code, Mgr = g.First().ReportsTo,
                    Late = g.Count(x => x.Late), LateMin = g.Where(x => x.Late).Sum(x => x.LateMin ?? 0),
                    Short = g.Count(x => x.Short), Extra = g.Count(x => x.Extra), Abs = g.Count(x => x.Absent),
                })
                .Where(x => x.Late + x.Short + x.Abs > 0)
                .OrderByDescending(x => x.Late + x.Short + x.Abs).ThenByDescending(x => x.LateMin)
                .ThenBy(x => x.Name, StringComparer.Ordinal)
                .Take(10).ToList();
            if (top.Count > 0)
            {
                sb.Append(SectionTitle("Most gaps this month", "late + hrs not completed + absent days"));
                sb.Append(TableStart(("Employee", "left"), ("Late", "center"), ("Hrs not completed", "center"), ("Extra", "center"), ("Absent", "center")));
                foreach (var t in top)
                    sb.Append($@"<tr>
  <td style='{Cell()}'><b>{E(t.Name)}</b><div style='color:{Muted};font-size:11px;'>{E(t.Code)} · {E(t.Mgr)}</div></td>
  {Num(t.Late, LateBg, t.Late > 0 ? $"{t.LateMin} min" : null)}{Num(t.Short, ShortBg)}{Num(t.Extra, ExtraBg)}{Num(t.Abs, AbsentBg)}
</tr>");
                sb.Append("</table></td></tr>");
            }

            // Footer
            sb.Append($@"
<tr><td style='padding:18px 24px 8px;font-size:13px;'>
  📎 <b>Attached Excel</b> — <i>Summary</i> (one row per employee), <i>Daily Detail (MTD)</i> (every day: planned vs actual in/out and hours), <i>Gaps Only</i>.
</td></tr>
<tr><td style='padding:8px 24px 20px;font-size:11px;color:{Muted};line-height:1.5;'>
  Late = in after planned in-time + buffer · Hrs not completed = worked less than planned hours (incl. missed punch) ·
  Extra = 30+ min over planned hours, or work on a week off / holiday · Absent = rostered working day with no punch and no planned leave.<br>
  Automated email from AMPM HRMS — please do not reply.
</td></tr>
</table></td></tr></table></body></html>");
            return sb.ToString();
        }

        private static string Tile(string label, int value, string unit, string sub, string bg) => $@"
<td width='50%' valign='top' style='padding:6px;'>
  <table role='presentation' width='100%' cellpadding='0' cellspacing='0' style='background:{bg};border-radius:6px;'>
    <tr><td style='padding:12px 12px 10px;'>
      <div style='font-size:11px;font-weight:600;color:{Ink};text-transform:uppercase;letter-spacing:.3px;'>{E(label)}</div>
      <div style='font-size:28px;font-weight:700;color:{Ink};line-height:1.2;margin-top:4px;'>{value}<span style='font-size:12px;font-weight:400;color:{Muted};'> {unit}</span></div>
      <div style='font-size:11px;color:#374151;margin-top:2px;'>{E(sub)}</div>
    </td></tr>
  </table>
</td>";

        private static string SectionTitle(string title, string sub) => $@"
<tr><td style='padding:18px 24px 6px;'>
  <span style='font-size:15px;font-weight:600;color:{Ink};'>{E(title)}</span>
  <span style='font-size:12px;color:{Muted};'>&nbsp;·&nbsp;{E(sub)}</span>
</td></tr>";

        private static string TableStart(params (string Text, string Align)[] cols)
        {
            var sb = new StringBuilder($"<tr><td style='padding:0 24px;'><table role='presentation' width='100%' cellpadding='0' cellspacing='0' style='border-collapse:collapse;font-size:12.5px;'><tr>");
            foreach (var (text, align) in cols)
                sb.Append($"<th style='text-align:{align};padding:7px 8px;background:#f3f4f6;color:{Muted};font-weight:600;font-size:11px;text-transform:uppercase;border-bottom:1px solid {Rule};'>{E(text)}</th>");
            sb.Append("</tr>");
            return sb.ToString();
        }

        private static string Cell() => $"padding:7px 8px;border-bottom:1px solid {Rule};vertical-align:top;";

        private static string Num(int n, string bg, string? sub = null) =>
            n == 0
                ? $"<td style='{Cell()}text-align:center;color:#9ca3af;'>–</td>"
                : $"<td style='{Cell()}text-align:center;'><span style='display:inline-block;min-width:22px;background:{bg};padding:2px 6px;border-radius:4px;font-weight:600;'>{n}</span>"
                  + (sub != null ? $"<div style='color:{Muted};font-size:10.5px;margin-top:2px;'>{E(sub)}</div>" : "") + "</td>";
    }
}
