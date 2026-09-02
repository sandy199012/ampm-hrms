using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using AmpmHrmsPro.Data;
using AmpmHrmsPro.Models;

namespace AmpmHrmsPro.Services
{
    public interface IBiometricSyncService
    {
        Task<(bool Success, string Message)> SyncAsync(AppDbContext db, DateTime from, DateTime to);
    }

    // ═══════════════════════════════════════════
    // BIOMETRIC SYNC SERVICE — deliberately vendor-agnostic. It calls
    // whatever URL is configured in BiometricApiSettings, reads the JSON
    // response using the configured field-name mapping (dot-path aware, so
    // nested envelopes like {"data":{"records":[...]}} work too), and
    // upserts AttendancePunch rows. NOTHING vendor-specific is hardcoded
    // here — to point this at a real machine/API, only the
    // BiometricApiSettings row needs to change (Admin > Attendance > API
    // Settings), never this file. See Models/Attendance.cs for what every
    // setting controls.
    // ═══════════════════════════════════════════
    public class BiometricSyncService : IBiometricSyncService
    {
        readonly IHttpClientFactory _httpFactory;
        public BiometricSyncService(IHttpClientFactory httpFactory) => _httpFactory = httpFactory;

        public async Task<(bool, string)> SyncAsync(AppDbContext db, DateTime from, DateTime to)
        {
            var settings = await db.BiometricApiSettingsList.FirstOrDefaultAsync();
            if (settings == null || !settings.IsEnabled || string.IsNullOrWhiteSpace(settings.BaseUrl))
                return (false, "Biometric API is not configured/enabled.");

            try
            {
                var client = _httpFactory.CreateClient();
                var url = settings.BaseUrl!.Replace("{from}", from.ToString("yyyy-MM-dd")).Replace("{to}", to.ToString("yyyy-MM-dd"));

                var req = new HttpRequestMessage(settings.HttpMethod == "POST" ? HttpMethod.Post : HttpMethod.Get, url);
                if (!string.IsNullOrWhiteSpace(settings.ApiKey))
                {
                    var headerName = string.IsNullOrWhiteSpace(settings.AuthHeaderName) ? "Authorization" : settings.AuthHeaderName!;
                    var value = settings.AuthScheme switch
                    {
                        "Bearer" => $"Bearer {settings.ApiKey}",
                        "Basic" => $"Basic {settings.ApiKey}",
                        _ => settings.ApiKey! // Raw — sent as-is, no scheme prefix
                    };
                    req.Headers.TryAddWithoutValidation(headerName, value);
                }
                if (settings.HttpMethod == "POST" && !string.IsNullOrWhiteSpace(settings.RequestBodyTemplate))
                {
                    var body = settings.RequestBodyTemplate!.Replace("{from}", from.ToString("yyyy-MM-dd")).Replace("{to}", to.ToString("yyyy-MM-dd"));
                    req.Content = new StringContent(body, Encoding.UTF8, "application/json");
                }

                var resp = await client.SendAsync(req);
                var raw = await resp.Content.ReadAsStringAsync();
                settings.LastSampleResponse = raw.Length > 4000 ? raw.Substring(0, 4000) : raw;

                if (!resp.IsSuccessStatusCode)
                    return await Fail(db, settings, $"HTTP {(int)resp.StatusCode}: {resp.ReasonPhrase}");

                using var doc = JsonDocument.Parse(raw);
                var arrayElement = NavigatePath(doc.RootElement, settings.ResponseArrayPath);
                if (arrayElement.ValueKind != JsonValueKind.Array)
                    return await Fail(db, settings, "Response Array Path didn't point to a JSON array — check Admin > Attendance > API Settings against the Last Sample Response shown there.");

                var empByCode = await db.Employees.Where(e => e.IsActive)
                    .ToDictionaryAsync(e => e.EmpCode, e => e.Id, StringComparer.OrdinalIgnoreCase);
                var existingKeys = new HashSet<(int, DateTime)>(
                    (await db.AttendancePunches.Where(p => p.PunchDateTime >= from.Date && p.PunchDateTime < to.Date.AddDays(1))
                        .Select(p => new { p.EmployeeId, p.PunchDateTime }).ToListAsync())
                        .Select(p => (p.EmployeeId, p.PunchDateTime)));

                int imported = 0, skippedNoEmployee = 0, skippedDup = 0, skippedBadDate = 0;
                var affected = new HashSet<(int EmployeeId, DateTime Date)>();

                foreach (var item in arrayElement.EnumerateArray())
                {
                    var empCode = GetString(item, settings.EmployeeCodeField);
                    if (string.IsNullOrWhiteSpace(empCode) || !empByCode.TryGetValue(empCode, out var empId))
                    { skippedNoEmployee++; continue; }

                    var punchDt = ParsePunchDateTime(item, settings);
                    if (punchDt == null) { skippedBadDate++; continue; }

                    if (existingKeys.Contains((empId, punchDt.Value))) { skippedDup++; continue; }

                    string direction = "Unknown";
                    if (!string.IsNullOrWhiteSpace(settings.DirectionField))
                    {
                        var dv = GetString(item, settings.DirectionField!);
                        if (dv != null)
                        {
                            if (string.Equals(dv, settings.InDirectionValue, StringComparison.OrdinalIgnoreCase)) direction = "In";
                            else if (string.Equals(dv, settings.OutDirectionValue, StringComparison.OrdinalIgnoreCase)) direction = "Out";
                        }
                    }
                    string? deviceId = !string.IsNullOrWhiteSpace(settings.DeviceIdField) ? GetString(item, settings.DeviceIdField!) : null;

                    db.AttendancePunches.Add(new AttendancePunch
                    {
                        EmployeeId = empId,
                        PunchDateTime = punchDt.Value,
                        Direction = direction,
                        DeviceId = deviceId,
                        Source = "BiometricApi"
                    });
                    existingKeys.Add((empId, punchDt.Value));
                    affected.Add((empId, punchDt.Value.Date));
                    imported++;
                }

                await db.SaveChangesAsync();

                foreach (var (empId, date) in affected)
                    await AttendanceEngine.RecomputeDayAsync(db, empId, date);

                string msg = $"{imported} punch(es) imported, {skippedDup} duplicate(s) skipped, {skippedNoEmployee} unmatched employee code(s), {skippedBadDate} unparsable date(s).";
                settings.LastSyncAt = DateTime.Now;
                settings.LastSyncStatus = "Success";
                settings.LastSyncMessage = msg;
                await db.SaveChangesAsync();
                return (true, msg);
            }
            catch (Exception ex)
            {
                return await Fail(db, settings, ex.Message);
            }
        }

        static async Task<(bool, string)> Fail(AppDbContext db, BiometricApiSettings settings, string message)
        {
            settings.LastSyncAt = DateTime.Now;
            settings.LastSyncStatus = "Failed";
            settings.LastSyncMessage = message;
            await db.SaveChangesAsync();
            return (false, message);
        }

        // Dot-path navigation so both a bare array response and a wrapped
        // envelope (e.g. "data" or "result.records") work from the same
        // config field.
        static JsonElement NavigatePath(JsonElement root, string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return root;
            var current = root;
            foreach (var seg in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(seg, out var next))
                    return default;
                current = next;
            }
            return current;
        }

        static string? GetString(JsonElement item, string fieldPath)
        {
            var el = NavigatePath(item, fieldPath);
            return el.ValueKind switch
            {
                JsonValueKind.Undefined => null,
                JsonValueKind.Null => null,
                JsonValueKind.String => el.GetString(),
                _ => el.ToString()
            };
        }

        static DateTime? ParsePunchDateTime(JsonElement item, BiometricApiSettings settings)
        {
            if (!string.IsNullOrWhiteSpace(settings.PunchDateTimeField))
            {
                var raw = GetString(item, settings.PunchDateTimeField!);
                return raw == null ? null : TryParseDateTime(raw, settings.DateTimeFormat);
            }
            if (!string.IsNullOrWhiteSpace(settings.PunchDateField))
            {
                var dRaw = GetString(item, settings.PunchDateField!);
                if (dRaw == null) return null;
                var tRaw = !string.IsNullOrWhiteSpace(settings.PunchTimeField) ? GetString(item, settings.PunchTimeField!) : "00:00:00";
                return TryParseDateTime($"{dRaw} {tRaw}", settings.DateTimeFormat);
            }
            return null;
        }

        static readonly string[] CommonFormats =
        {
            "dd-MM-yyyy HH:mm:ss", "yyyy-MM-dd HH:mm:ss", "MM/dd/yyyy HH:mm:ss", "dd/MM/yyyy HH:mm:ss",
            "yyyy-MM-ddTHH:mm:ss", "dd-MM-yyyy hh:mm:ss tt", "M/d/yyyy h:mm:ss tt"
        };

        static DateTime? TryParseDateTime(string raw, string? fmt)
        {
            if (!string.IsNullOrWhiteSpace(fmt) && DateTime.TryParseExact(raw, fmt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact))
                return exact;
            if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                return parsed;
            foreach (var f in CommonFormats)
                if (DateTime.TryParseExact(raw, f, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                    return d;
            return null;
        }
    }

    // Polls the configured API on a timer when enabled — pulls the last 2
    // days each cycle (not just "today") so punches that arrive late or
    // out of order from the device are still caught on the next run.
    public class BiometricSyncHostedService : BackgroundService
    {
        readonly IServiceProvider _services;
        public BiometricSyncHostedService(IServiceProvider services) => _services = services;

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                int intervalMin = 15;
                try
                {
                    using var scope = _services.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var sync = scope.ServiceProvider.GetRequiredService<IBiometricSyncService>();
                    var settings = await db.BiometricApiSettingsList.FirstOrDefaultAsync(stoppingToken);
                    if (settings != null)
                    {
                        intervalMin = Math.Max(5, settings.SyncIntervalMinutes);
                        if (settings.IsEnabled)
                        {
                            var today = DateTime.Today;
                            await sync.SyncAsync(db, today.AddDays(-1), today);
                        }
                    }
                }
                catch (OperationCanceledException) { break; }
                catch { /* swallow — this is a background poller; a bad cycle just retries next interval, LastSyncStatus already records the failure for Admin to see */ }

                try { await Task.Delay(TimeSpan.FromMinutes(intervalMin), stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }
}
