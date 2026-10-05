// Services/IndiaTime.cs
// The server (Render) runs on UTC, but every HR schedule ("send at 7:30 AM")
// is meant in India time. Schedulers use IndiaTime.Now / IndiaTime.Today.
namespace AmpmHrmsPro.Services
{
    public static class IndiaTime
    {
        private static readonly TimeZoneInfo Zone = FindZone();

        private static TimeZoneInfo FindZone()
        {
            foreach (var id in new[] { "Asia/Kolkata", "India Standard Time" })
            {
                try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch { /* try next */ }
            }
            // No tz database in the container — IST has no daylight saving, so a fixed offset is exact.
            return TimeZoneInfo.CreateCustomTimeZone("IST", TimeSpan.FromHours(5.5), "India Standard Time", "IST");
        }

        public static DateTime Now   => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Zone);
        public static DateTime Today => Now.Date;
    }
}
