namespace AmpmHrmsPro.Models
{
    // ═══════════════════════════════════════════
    // DASHBOARD WIDGETS — plain classes (not C# tuples) on purpose: these
    // are handed to the view via ViewBag, which the view reads through
    // Razor's `dynamic` binding. A ValueTuple's named elements ("Name",
    // "DaysAway", etc.) are compile-time-only sugar — at runtime a boxed
    // tuple only exposes Item1/Item2/... via reflection, so `dynamic`
    // access to a named element would throw. A real class's properties are
    // genuinely reflectable, so they work correctly through ViewBag/dynamic.
    // ═══════════════════════════════════════════
    public class UpcomingHolidayVm
    {
        public string Name { get; set; } = "";
        public string Type { get; set; } = "";
        public DateTime Date { get; set; }
        public int DaysAway { get; set; }
    }

    public class UpcomingBirthdayVm
    {
        public string Name { get; set; } = "";
        public string EmpCode { get; set; } = "";
        public string? DeptName { get; set; }
        public DateTime NextBirthday { get; set; }
        public int DaysAway { get; set; }
    }

    public class RecentJoiningVm
    {
        public string Name { get; set; } = "";
        public string EmpCode { get; set; } = "";
        public string? DeptName { get; set; }
        public DateTime DOJ { get; set; }
        public int DaysAgo { get; set; }
    }
}
