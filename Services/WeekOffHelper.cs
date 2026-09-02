using AmpmHrmsPro.Models;

namespace AmpmHrmsPro.Services
{
    // Evaluates a WeekOffPolicy's rules against a real calendar date. Used
    // by the Masters screen to preview a policy before saving it, and later
    // by the attendance engine to decide whether a given day is a
    // scheduled week-off for an employee.
    public static class WeekOffHelper
    {
        public static bool IsWeekOff(DateTime date, WeekOffPolicy policy)
        {
            foreach (var rule in policy.Rules)
                if (RuleMatches(date, rule)) return true;
            return false;
        }

        public static bool RuleMatches(DateTime date, WeekOffRule rule)
        {
            if (!Enum.TryParse<DayOfWeek>(rule.DayOfWeek, true, out var dow)) return false;
            if (date.DayOfWeek != dow) return false;

            if (rule.RuleType == "Weekly") return true;

            if (rule.RuleType == "NthOccurrence" && !string.IsNullOrWhiteSpace(rule.Occurrences))
            {
                int occurrence = (date.Day - 1) / 7 + 1; // which Nth <DayOfWeek> of the month this date is
                bool isLastOccurrence = date.AddDays(7).Month != date.Month;

                foreach (var token in rule.Occurrences.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (token.Equals("L", StringComparison.OrdinalIgnoreCase) && isLastOccurrence) return true;
                    if (int.TryParse(token, out var n) && n == occurrence) return true;
                }
            }

            return false;
        }

        // Returns every week-off date for the given month under this policy
        // — used for the "Preview" panel on the Week-Off Policies screen.
        public static List<DateTime> PreviewMonth(WeekOffPolicy policy, int year, int month)
        {
            var days = new List<DateTime>();
            int daysInMonth = DateTime.DaysInMonth(year, month);
            for (int d = 1; d <= daysInMonth; d++)
            {
                var date = new DateTime(year, month, d);
                if (IsWeekOff(date, policy)) days.Add(date);
            }
            return days;
        }
    }
}
