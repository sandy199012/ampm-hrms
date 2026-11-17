using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using AmpmHrmsPro.Data;
using AmpmHrmsPro.Models;
using AmpmHrmsPro.Services.Tenancy;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace AmpmHrmsPro.Services
{
    // ═══════════════════════════════════════════════════════════════════
    // PRESENCE 360 API (web.thepresence360.com/api, "API Reference Guide v1.0")
    //
    //   POST v1/admin-login          → bearer token (valid ~1 year, renewed on 400/401)
    //   GET  v1/attendance-records   → in/out per employee-day → AttendancePunches (Source "P360Api")
    //   GET  v1/leave-records        → Leave requests          ┐ same upsert as the
    //   GET  v1/requests-list?type=  → On Duty / Regularization ┘ Excel report import
    //
    // Every run is idempotent: the API punches of the window are replaced,
    // requests are matched on employee + type + start date + submission time.
    // Attendance is then recomputed with the bulk engine.
    // ═══════════════════════════════════════════════════════════════════
    public interface IPresence360ApiService
    {
        Task<(bool Ok, string Message)> TestAsync(AppDbContext db, CancellationToken ct = default);
        Task<(bool Ok, string Message)> SyncAsync(AppDbContext db, DateTime from, DateTime to, CancellationToken ct = default);
        string? Protect(string? plain);
    }

    public class Presence360ApiService : IPresence360ApiService
    {
        public const string PunchSource = "P360Api";
        public const string AppSource = "P360Api";

        readonly IHttpClientFactory _http;
        readonly IDataProtector _protector;
        readonly ILogger<Presence360ApiService> _log;

        public Presence360ApiService(IHttpClientFactory http, IDataProtectionProvider dp, ILogger<Presence360ApiService> log)
        {
            _http = http;
            _protector = dp.CreateProtector("AmpmHrmsPro.P360ApiSettings.v1");
            _log = log;
        }

        public string? Protect(string? plain) => string.IsNullOrEmpty(plain) ? null : _protector.Protect(plain);
        string? Unprotect(string? enc)
        {
            if (string.IsNullOrEmpty(enc)) return null;
            try { return _protector.Unprotect(enc); } catch { return null; }
        }

        sealed class P360Exception : Exception { public P360Exception(string m) : base(m) { } }

        // ── HTTP ─────────────────────────────────────────────────────────
        HttpClient Client()
        {
            var c = _http.CreateClient("presence360");
            c.Timeout = TimeSpan.FromSeconds(90);
            return c;
        }

        static string Url(P360ApiSettings s, string path) => s.BaseUrl.TrimEnd('/') + "/" + path.TrimStart('/');

        static string Short(string raw, int n = 300)
        {
            raw = (raw ?? "").Trim();
            return raw.Length > n ? raw[..n] + "…" : raw;
        }

        static string? MessageOf(string raw)
        {
            try
            {
                using var d = JsonDocument.Parse(raw);
                if (d.RootElement.ValueKind == JsonValueKind.Object && d.RootElement.TryGetProperty("message", out var m))
                    return m.ToString();
            }
            catch { }
            return null;
        }

        async Task<string> LoginAsync(AppDbContext db, P360ApiSettings s, CancellationToken ct)
        {
            var password = Unprotect(s.PasswordEnc);
            var secret = Unprotect(s.ClientSecretEnc);
            if (string.IsNullOrWhiteSpace(s.Username) || string.IsNullOrEmpty(password) || string.IsNullOrWhiteSpace(s.ClientId) || string.IsNullOrEmpty(secret))
                throw new P360Exception("Username, Password, Client ID and Client Secret are all required — fill them in Presence 360 API settings.");

            object clientId = int.TryParse(s.ClientId.Trim(), out var cid) ? cid : s.ClientId.Trim();
            var body = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["username"] = s.Username.Trim(),
                ["password"] = password,
                ["grant_type"] = "admin",
                ["client_id"] = clientId,
                ["client_secret"] = secret,
            });
            using var req = new HttpRequestMessage(HttpMethod.Post, Url(s, "v1/admin-login"))
            { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            req.Headers.Accept.ParseAdd("application/json");
            using var resp = await Client().SendAsync(req, ct);
            var raw = await resp.Content.ReadAsStringAsync(ct);

            string? token = null; long expiresIn = 0;
            try
            {
                using var doc = JsonDocument.Parse(raw);
                if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
                {
                    if (data.TryGetProperty("token", out var t) && t.ValueKind == JsonValueKind.String) token = t.GetString();
                    if (data.TryGetProperty("detail_response", out var dr) && dr.ValueKind == JsonValueKind.Object)
                    {
                        if (token == null && dr.TryGetProperty("access_token", out var at) && at.ValueKind == JsonValueKind.String) token = at.GetString();
                        if (dr.TryGetProperty("expires_in", out var ei) && ei.TryGetInt64(out var e)) expiresIn = e;
                    }
                }
            }
            catch (JsonException) { }

            if (!resp.IsSuccessStatusCode || string.IsNullOrEmpty(token))
                throw new P360Exception($"Presence 360 login failed (HTTP {(int)resp.StatusCode}): {MessageOf(raw) ?? Short(raw)}");

            s.TokenEnc = Protect(token);
            s.TokenExpiresAt = DateTime.UtcNow.AddSeconds(expiresIn > 0 ? Math.Min(expiresIn, 31_622_400) : 86_400).AddMinutes(-10);
            await db.SaveChangesAsync(ct);
            return token;
        }

        async Task<string> TokenAsync(AppDbContext db, P360ApiSettings s, CancellationToken ct)
        {
            var t = Unprotect(s.TokenEnc);
            if (!string.IsNullOrEmpty(t) && s.TokenExpiresAt > DateTime.UtcNow) return t;
            return await LoginAsync(db, s, ct);
        }

        /// <summary>GET every page of a list endpoint; returns the records (cloned) and the first raw response.</summary>
        async Task<(List<JsonElement> Items, string FirstRaw)> GetAllAsync(AppDbContext db, P360ApiSettings s, string path,
            Dictionary<string, string> query, CancellationToken ct, int maxPages = 300)
        {
            var items = new List<JsonElement>();
            string firstRaw = "";
            var token = await TokenAsync(db, s, ct);
            bool relogged = false;

            for (int page = 1; page <= maxPages; page++)
            {
                var q = new Dictionary<string, string>(query) { ["page"] = page.ToString(), ["limit"] = "200" };
                if (!string.IsNullOrWhiteSpace(s.Organization)) q["organization"] = s.Organization.Trim();
                var url = Url(s, path) + "?" + string.Join("&", q.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

                string raw; HttpStatusCode code;
                int attempt = 0;
                while (true)
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    req.Headers.Accept.ParseAdd("application/json");
                    req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
                    using var resp = await Client().SendAsync(req, ct);
                    raw = await resp.Content.ReadAsStringAsync(ct);
                    code = resp.StatusCode;

                    if ((int)code == 429 && attempt++ < 4) { await Task.Delay(TimeSpan.FromSeconds(5 * attempt), ct); continue; }
                    bool unauthorized = code == HttpStatusCode.Unauthorized
                        || (code == HttpStatusCode.BadRequest && raw.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase));
                    if (unauthorized && !relogged)
                    {
                        relogged = true;
                        s.TokenEnc = null;
                        token = await LoginAsync(db, s, ct);
                        continue;
                    }
                    break;
                }
                if (page == 1) firstRaw = raw;
                if ((int)code < 200 || (int)code >= 300)
                    throw new P360Exception($"{path}: HTTP {(int)code} — {MessageOf(raw) ?? Short(raw)}");

                JsonElement root;
                try { using var doc = JsonDocument.Parse(raw); root = doc.RootElement.Clone(); }
                catch (JsonException) { throw new P360Exception($"{path}: response is not JSON — {Short(raw)}"); }

                // data: [...]  |  data: { data: [...], next_page_url }  |  [...]
                JsonElement arr = default; string? next = null; int? lastPage = null;
                if (root.ValueKind == JsonValueKind.Array) arr = root;
                else if (root.ValueKind == JsonValueKind.Object)
                {
                    next = Str(root, "next_page_url");
                    if (root.TryGetProperty("last_page", out var lp) && lp.TryGetInt32(out var l1)) lastPage = l1;
                    if (root.TryGetProperty("data", out var d))
                    {
                        if (d.ValueKind == JsonValueKind.Array) arr = d;
                        else if (d.ValueKind == JsonValueKind.Object)
                        {
                            if (d.TryGetProperty("data", out var dd) && dd.ValueKind == JsonValueKind.Array) arr = dd;
                            next ??= Str(d, "next_page_url");
                            if (d.TryGetProperty("last_page", out var lp2) && lp2.TryGetInt32(out var l2)) lastPage = l2;
                        }
                    }
                    if (root.TryGetProperty("meta", out var meta) && meta.ValueKind == JsonValueKind.Object
                        && meta.TryGetProperty("last_page", out var lp3) && lp3.TryGetInt32(out var l3)) lastPage = l3;
                    if (next == null && root.TryGetProperty("links", out var links) && links.ValueKind == JsonValueKind.Object) next = Str(links, "next");
                }
                if (arr.ValueKind != JsonValueKind.Array) break;

                int count = 0;
                foreach (var it in arr.EnumerateArray()) { items.Add(it.Clone()); count++; }
                if (count == 0) break;
                if (lastPage.HasValue) { if (page >= lastPage.Value) break; }
                else if (string.IsNullOrEmpty(next)) break;
            }
            return (items, firstRaw);
        }

        // ── JSON helpers ─────────────────────────────────────────────────
        static string? Str(JsonElement e, params string[] names)
        {
            if (e.ValueKind != JsonValueKind.Object) return null;
            foreach (var n in names)
            {
                JsonElement cur = e; bool ok = true;
                foreach (var seg in n.Split('.'))
                {
                    if (cur.ValueKind != JsonValueKind.Object) { ok = false; break; }
                    // case-insensitive property lookup ("Attendance_date" vs "attendance_date")
                    bool found = false;
                    foreach (var p in cur.EnumerateObject())
                        if (string.Equals(p.Name, seg, StringComparison.OrdinalIgnoreCase)) { cur = p.Value; found = true; break; }
                    if (!found) { ok = false; break; }
                }
                if (!ok) continue;
                var v = cur.ValueKind switch
                {
                    JsonValueKind.String => cur.GetString(),
                    JsonValueKind.Number => cur.GetRawText(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    _ => null,
                };
                if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
            }
            return null;
        }

        static readonly string[] DateFmts = { "yyyy-MM-dd", "dd-MM-yyyy", "yyyy/MM/dd", "dd/MM/yyyy", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss" };
        static DateTime? ParseDay(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim();
            if (s.Length > 10 && (s[10] == ' ' || s[10] == 'T') && char.IsDigit(s[0]) && s[4] == '-') s = s[..10];
            return DateTime.TryParseExact(s, DateFmts, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.Date : null;
        }
        static TimeSpan? ParseTime(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim();
            if (s.Length > 8 && s.Contains(' ')) s = s[(s.LastIndexOf(' ') + 1)..];   // "2026-11-16 09:12:00" → time part
            if (TimeSpan.TryParseExact(s, new[] { @"hh\:mm\:ss", @"h\:mm\:ss", @"hh\:mm", @"h\:mm" }, CultureInfo.InvariantCulture, out var t)
                && t > TimeSpan.Zero && t < TimeSpan.FromDays(1)) return t;
            return null;
        }
        static string CodeKey(string? c) { var t = (c ?? "").Trim(); if (t.EndsWith(".0")) t = t[..^2]; t = t.TrimStart('0'); return t.Length == 0 ? "0" : t.ToUpperInvariant(); }

        static async Task<P360ApiSettings> SettingsAsync(AppDbContext db, CancellationToken ct) =>
            await db.P360ApiSettingsList.FirstOrDefaultAsync(ct)
            ?? throw new P360Exception("Presence 360 API is not configured yet.");

        async Task Finish(AppDbContext db, P360ApiSettings s, bool ok, string msg, string? sample, CancellationToken ct)
        {
            s.LastSyncAt = DateTime.UtcNow;
            s.LastSyncStatus = ok ? "Success" : "Failed";
            s.LastSyncMessage = msg.Length > 2000 ? msg[..2000] : msg;
            if (sample != null) s.LastSampleResponse = sample.Length > 8000 ? sample[..8000] : sample;
            try { await db.SaveChangesAsync(ct); } catch (Exception ex) { _log.LogWarning(ex, "P360 API: could not save sync status"); db.ChangeTracker.Clear(); }
        }

        // ── Test ─────────────────────────────────────────────────────────
        public async Task<(bool Ok, string Message)> TestAsync(AppDbContext db, CancellationToken ct = default)
        {
            P360ApiSettings? s = null;
            try
            {
                s = await SettingsAsync(db, ct);
                s.TokenEnc = null;
                await LoginAsync(db, s, ct);
                var today = IndiaTime.Today;
                var (items, raw) = await GetAllAsync(db, s, "v1/attendance-records",
                    new() { ["from_date"] = today.AddDays(-1).ToString("yyyy-MM-dd"), ["to_date"] = today.ToString("yyyy-MM-dd") }, ct, maxPages: 1);
                var msg = $"Connected — login OK, {items.Count} attendance record(s) on the first page for yesterday/today.";
                await Finish(db, s, true, msg, raw, ct);
                return (true, msg);
            }
            catch (Exception ex) when (ex is P360Exception or HttpRequestException or TaskCanceledException)
            {
                var msg = ex is TaskCanceledException ? "Presence 360 did not answer in time (timeout)." : ex.Message;
                if (s != null) await Finish(db, s, false, msg, null, ct);
                return (false, msg);
            }
        }

        // ── Sync ─────────────────────────────────────────────────────────
        public async Task<(bool Ok, string Message)> SyncAsync(AppDbContext db, DateTime from, DateTime to, CancellationToken ct = default)
        {
            from = from.Date; to = to.Date;
            if (to > IndiaTime.Today) to = IndiaTime.Today;
            if (from > to) from = to;

            P360ApiSettings? s = null;
            var parts = new List<string>();
            string? sample = null;
            try
            {
                s = await SettingsAsync(db, ct);
                if (string.IsNullOrWhiteSpace(s.Username)) throw new P360Exception("Presence 360 API is not configured yet.");

                var emps = await db.Employees.AsNoTracking().Select(e => new { e.Id, e.EmpCode, e.IsActive }).ToListAsync(ct);
                var byCode = new Dictionary<string, int>();
                foreach (var e in emps.OrderByDescending(e => e.IsActive)) byCode.TryAdd(CodeKey(e.EmpCode), e.Id);

                var recompute = new Dictionary<int, (DateTime From, DateTime To)>();
                void Mark(int empId, DateTime a, DateTime b)
                {
                    if (b > to) b = to;
                    if (a > b) return;
                    recompute[empId] = recompute.TryGetValue(empId, out var r) ? (a < r.From ? a : r.From, b > r.To ? b : r.To) : (a, b);
                }

                var fromS = from.ToString("yyyy-MM-dd"); var toS = to.ToString("yyyy-MM-dd");

                // 1. Attendance (in/out per day) ──────────────────────────
                if (s.SyncAttendance)
                {
                    var (items, raw) = await GetAllAsync(db, s, "v1/attendance-records", new() { ["from_date"] = fromS, ["to_date"] = toS }, ct);
                    sample = raw;
                    var punches = new List<AttendancePunch>();
                    var seen = new HashSet<(int, DateTime)>();
                    int unmatched = 0, noIn = 0, overnight = 0;
                    var unmatchedCodes = new SortedSet<string>();
                    foreach (var it in items)
                    {
                        var code = Str(it, "employee.employee_code", "employee_code", "emp_code");
                        if (code == null || !byCode.TryGetValue(CodeKey(code), out var empId))
                        { unmatched++; if (code != null && unmatchedCodes.Count < 15) unmatchedCodes.Add(code); continue; }

                        var day = ParseDay(Str(it, "attendance_date", "in_date"));
                        if (day == null || day < from || day > to) continue;
                        var inT = ParseTime(Str(it, "in_time"));
                        if (inT == null) { noIn++; continue; }
                        var inDay = ParseDay(Str(it, "in_date")) ?? day.Value;
                        var inDt = inDay + inT.Value;
                        if (seen.Add((empId, inDt)))
                            punches.Add(new AttendancePunch { EmployeeId = empId, PunchDateTime = inDt, Direction = "In", Source = PunchSource, SyncedAt = DateTime.Now });

                        var outT = ParseTime(Str(it, "out_time"));
                        if (outT != null)
                        {
                            var outDay = ParseDay(Str(it, "out_date")) ?? day.Value;
                            var outDt = outDay + outT.Value;
                            // An out on the next day (night shift) would land on the wrong
                            // attendance day — same rule as the P360 CSV upload: skip it.
                            if (outDay.Date != inDt.Date) overnight++;
                            else if (outDt > inDt && seen.Add((empId, outDt)))
                                punches.Add(new AttendancePunch { EmployeeId = empId, PunchDateTime = outDt, Direction = "Out", Source = PunchSource, SyncedAt = DateTime.Now });
                        }
                        Mark(empId, day.Value, day.Value);
                    }

                    // Replace this window's API punches; never duplicate a punch another source already has.
                    var winEnd = to.AddDays(1);
                    var ids = punches.Select(p => p.EmployeeId).Distinct().ToList();
                    var removed = await db.AttendancePunches
                        .Where(p => p.Source == PunchSource && p.PunchDateTime >= from && p.PunchDateTime < winEnd)
                        .Select(p => new { p.EmployeeId, Day = p.PunchDateTime.Date }).Distinct().ToListAsync(ct);
                    foreach (var r in removed) Mark(r.EmployeeId, r.Day, r.Day);   // days that may lose punches get recomputed too
                    await db.AttendancePunches
                        .Where(p => p.Source == PunchSource && p.PunchDateTime >= from && p.PunchDateTime < winEnd)
                        .ExecuteDeleteAsync(ct);
                    var other = (await db.AttendancePunches.AsNoTracking()
                            .Where(p => ids.Contains(p.EmployeeId) && p.PunchDateTime >= from && p.PunchDateTime < winEnd)
                            .Select(p => new { p.EmployeeId, p.PunchDateTime }).ToListAsync(ct))
                        .Select(p => (p.EmployeeId, p.PunchDateTime)).ToHashSet();
                    var fresh = punches.Where(p => !other.Contains((p.EmployeeId, p.PunchDateTime))).ToList();
                    db.AttendancePunches.AddRange(fresh);
                    await db.SaveChangesAsync(ct);
                    db.ChangeTracker.Clear();

                    var part = $"Attendance: {items.Count} record(s), {fresh.Count} punch(es) for {ids.Count} employee(s)";
                    if (noIn > 0) part += $", {noIn} day(s) without punch-in";
                    if (overnight > 0) part += $", {overnight} next-day out(s) skipped";
                    if (unmatched > 0) part += $", {unmatched} record(s) with codes not in HRMS ({string.Join(", ", unmatchedCodes)}{(unmatched > unmatchedCodes.Count ? ", …" : "")})";
                    parts.Add(part);
                    s = await SettingsAsync(db, ct);   // re-attach after ChangeTracker.Clear
                }

                // 2. Leave + On Duty + Regularization ─────────────────────
                var rows = new List<(string Type, ApplicationReports.IReportRow Row)>();
                if (s.SyncLeaves)
                {
                    var (items, raw) = await GetAllAsync(db, s, "v1/leave-records", new() { ["from_date"] = fromS, ["to_date"] = toS }, ct);
                    sample ??= raw;
                    foreach (var it in items)
                        rows.Add(("Leave", new ApplicationReports.DictRow(new()
                        {
                            ["Employee Code"] = Str(it, "employee_code", "employee.employee_code"),
                            ["Employee Name"] = Str(it, "employee_name", "employee.name"),
                            ["Leave Type"] = Str(it, "leave_type"),
                            ["leave Duration"] = Str(it, "leave_duration"),
                            ["From date"] = Str(it, "from_date"),
                            ["To Date"] = Str(it, "to_date"),
                            ["No Of Leave"] = Str(it, "no_of_leave"),
                            ["Pending At"] = Str(it, "pending_at"),
                            ["Approval Level"] = Str(it, "approval_level"),
                            ["Status"] = Str(it, "final_status", "status", "my_status"),
                            ["Apply Date and Time"] = Str(it, "apply_datetime", "applied_on", "created_at"),
                            ["Applied By"] = Str(it, "applied_by"),
                        })));
                    parts.Add($"Leave: {items.Count}");
                }
                if (s.SyncRequests)
                {
                    foreach (var (apiType, type) in new[] { ("od", "OD"), ("regularize", "Regularisation") })
                    {
                        var (items, raw) = await GetAllAsync(db, s, "v1/requests-list", new() { ["type"] = apiType, ["from_date"] = fromS, ["to_date"] = toS }, ct);
                        sample ??= raw;
                        foreach (var it in items)
                        {
                            var d = new Dictionary<string, string?>
                            {
                                ["Employee Code"] = Str(it, "employee_code", "employee.employee_code"),
                                ["Employee Name"] = Str(it, "employee_name", "employee.name"),
                                ["Requested Punch In Date/Time"] = Str(it, "punch_in"),
                                ["Requested Punch Out Date/Time"] = Str(it, "punch_out"),
                                ["Pending At"] = Str(it, "pending_at"),
                                ["Approval Level"] = Str(it, "approval_level"),
                                ["Request Reason"] = Str(it, "reason"),
                                ["Status"] = Str(it, "status", "final_status"),
                                ["Submission Date"] = Str(it, "submitted_on", "created_at"),
                                ["Applied By"] = Str(it, "applied_by"),
                            };
                            if (type == "OD")
                            {
                                d["From Location"] = Str(it, "from_location");
                                d["To Location"] = Str(it, "to_location");
                                d["Duration"] = Str(it, "duration");
                                d["Compensate As"] = Str(it, "compensate_as");
                            }
                            else d["Attachment"] = Str(it, "attachment");
                            rows.Add((type, new ApplicationReports.DictRow(d)));
                        }
                        parts.Add($"{(type == "OD" ? "On Duty" : "Regularization")}: {items.Count}");
                    }
                }
                if (rows.Count > 0)
                {
                    var res = await ApplicationReports.ImportRowsAsync(db, rows, AppSource, "Presence 360 API");
                    foreach (var (empId, a, b) in res.ToRecompute) Mark(empId, a, b);
                    var created = res.Created.Values.Sum(); var updated = res.Updated.Values.Sum();
                    parts.Add($"requests {created} new / {updated} updated" + (res.NotFound.Count > 0 ? $", {res.NotFound.Count} employee(s) not in HRMS" : ""));
                    if (res.LeaveTypesCreated.Count > 0) parts.Add("new leave types: " + string.Join(", ", res.LeaveTypesCreated));
                    db.ChangeTracker.Clear();
                    s = await SettingsAsync(db, ct);
                }

                // 3. Recompute the touched employee-days ──────────────────
                int recomputed = 0;
                foreach (var (empId, (a, b)) in recompute)
                {
                    ct.ThrowIfCancellationRequested();
                    recomputed += await AttendanceEngine.RecomputeBulkAsync(db, a, b, new[] { empId }, ct);
                }
                db.ChangeTracker.Clear();
                s = await SettingsAsync(db, ct);
                parts.Add($"{recomputed} attendance day(s) recalculated");

                var msg = $"{from:dd-MMM} to {to:dd-MMM}: " + string.Join(" · ", parts);
                await Finish(db, s, true, msg, sample, ct);
                _log.LogInformation("P360 API sync (company {Company}): {Msg}", db.CurrentCompanyId, msg);
                return (true, msg);
            }
            catch (Exception ex) when (ex is P360Exception or HttpRequestException or TaskCanceledException or JsonException)
            {
                if (ex is TaskCanceledException && ct.IsCancellationRequested) throw;
                var msg = ex is TaskCanceledException ? "Presence 360 did not answer in time (timeout)." : ex.Message;
                if (parts.Count > 0) msg += " (done before the error: " + string.Join(" · ", parts) + ")";
                db.ChangeTracker.Clear();
                s = await db.P360ApiSettingsList.FirstOrDefaultAsync(ct);
                if (s != null) await Finish(db, s, false, msg, sample, ct);
                _log.LogWarning("P360 API sync failed (company {Company}): {Msg}", db.CurrentCompanyId, msg);
                return (false, msg);
            }
        }
    }

    // Runs every 5 minutes; per company, syncs when its interval is due.
    public class Presence360SyncHostedService : BackgroundService
    {
        readonly IServiceProvider _services;
        readonly ILogger<Presence360SyncHostedService> _log;
        public Presence360SyncHostedService(IServiceProvider services, ILogger<Presence360SyncHostedService> log) { _services = services; _log = log; }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); } catch (OperationCanceledException) { return; }
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await TenantJobs.ForEachActiveCompanyAsync(_services, async (sp, companyId) =>
                    {
                        var db = sp.GetRequiredService<AppDbContext>();
                        db.AuditEnabled = false;
                        var s = await db.P360ApiSettingsList.AsNoTracking().FirstOrDefaultAsync(stoppingToken);
                        if (s == null || !s.IsEnabled) return;
                        // Due? (LastSyncAt is kept in the DB, so a restart doesn't reset the clock.)
                        var interval = TimeSpan.FromMinutes(Math.Max(10, s.SyncIntervalMinutes));
                        if (s.LastSyncAt.HasValue && DateTime.UtcNow - s.LastSyncAt.Value < interval - TimeSpan.FromSeconds(30)) return;
                        var today = IndiaTime.Today;
                        var api = sp.GetRequiredService<IPresence360ApiService>();
                        await api.SyncAsync(db, today.AddDays(-Math.Clamp(s.DaysBack, 0, 31)), today, stoppingToken);
                    }, _log, stoppingToken);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { _log.LogError(ex, "Presence 360 API sync loop failed"); }

                try { await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }
}
