using AmpmHrmsPro.Models;

namespace AmpmHrmsPro.Services
{
    // ═══════════════════════════════════════════
    // ATTENDANCE COLUMN DETECTOR — best-effort auto-mapping so a common
    // biometric export "just works" without the Admin manually picking
    // every column every time. Runs once, right after a file is uploaded,
    // to PRE-FILL the mapping screen (never skips the screen — Admin
    // always sees and can correct the guess before importing).
    //
    // Whatever the exact header wording a given machine uses (Punch In
    // Date / In Date / Date In / PunchInDate ...), matching is done on a
    // normalized (lowercased, punctuation-stripped) form so spacing,
    // underscores, and casing differences don't matter.
    // ═══════════════════════════════════════════
    public static class AttendanceColumnDetector
    {
        static string Norm(string h) => new string(h.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

        public static AttendanceImportProfile Detect(List<string> headers)
        {
            var normed = headers.ToDictionary(h => h, h => Norm(h));
            string? Find(Func<string, bool> pred) => headers.FirstOrDefault(h => pred(normed[h]));

            var map = new AttendanceImportProfile
            {
                EmployeeCodeColumn =
                    Find(n => n.Contains("empcode") || n.Contains("employeecode"))
                    ?? Find(n => n.Contains("empid") || n.Contains("employeeid"))
                    ?? Find(n => (n.Contains("emp") || n.Contains("employee")) && n.Contains("code"))
                    ?? Find(n => n == "code" || n == "id" || n == "empno" || n == "employeeno")
                    ?? ""
            };

            // Explicit In/Out Date + Time columns — the shape most real biometric exports use.
            var inDate = Find(n => n.Contains("indate") && !n.Contains("out"))
                ?? Find(n => n.Contains("in") && n.Contains("date") && !n.Contains("out"));
            var inTime = Find(n => n.Contains("intime") && !n.Contains("out"))
                ?? Find(n => n.Contains("firstpunch") || n.Contains("punchin"))
                ?? Find(n => n.Contains("in") && n.Contains("time") && !n.Contains("out"));
            var outDate = Find(n => n.Contains("outdate"))
                ?? Find(n => n.Contains("out") && n.Contains("date"));
            var outTime = Find(n => n.Contains("outtime"))
                ?? Find(n => n.Contains("lastpunch") || n.Contains("punchout"))
                ?? Find(n => n.Contains("out") && n.Contains("time"));

            // One combined date+time column per punch row.
            var combinedDateTime = Find(n => n.Contains("punchdatetime") || n.Contains("datetime") || n.Contains("timestamp"));

            // A single plain date column (used with either combined or in/out time columns).
            var plainDate = Find(n => n == "date" || n.Contains("attendancedate") || n.Contains("punchdate"));

            var direction = Find(n => n.Contains("direction") || n == "inout" || n.Contains("punchtype") || n == "type");

            if (inTime != null && outTime != null)
            {
                // The gold-standard shape: explicit in-punch and out-punch
                // columns, so a missing side is known directly rather than guessed.
                map.Format = "PerDayInOut";
                map.DateColumn = inDate ?? plainDate;
                map.TimeColumn = inTime;
                map.OutDateColumn = outDate; // left blank if not found — RunImport falls back to the In Date for a same-day out-punch
                map.OutTimeColumn = outTime;
            }
            else if (combinedDateTime != null)
            {
                map.Format = "PerPunchRow";
                map.DateTimeColumn = combinedDateTime;
                map.DirectionColumn = direction;
            }
            else if (plainDate != null && (inTime != null || outTime != null))
            {
                map.Format = "PerPunchRow";
                map.DateColumn = plainDate;
                map.TimeColumn = inTime ?? outTime;
                map.DirectionColumn = direction;
            }
            else
            {
                // Nothing recognizable enough to guess confidently — leave
                // Format at its default (PerDayInOut) with fields blank;
                // Admin maps manually from the full column list shown.
                map.Format = "PerDayInOut";
            }

            return map;
        }
    }
}
