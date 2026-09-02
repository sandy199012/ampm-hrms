using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using AmpmHrmsPro.Data;
using AmpmHrmsPro.Models;
using AmpmHrmsPro.Services;

namespace AmpmHrmsPro.Controllers
{
    // ═══════════════════════════════════════════
    // ATTENDANCE — everything about getting raw punch data INTO the system:
    // the biometric API settings + manual sync trigger, and the file-based
    // import with its column-mapping wizard (so "any machine's export"
    // works, per the requirement — see Models/Attendance.cs for why the
    // mapping is fully configurable rather than hardcoded to one vendor).
    // Reports that READ this data live in ReportsController instead.
    // ═══════════════════════════════════════════
    [Authorize(Roles = "admin,hr")]
    public class AttendanceController : Controller
    {
        readonly AppDbContext _db;
        readonly IBiometricSyncService _sync;
        readonly IEmailSender _emailSender;
        readonly IHrEmailNotificationService _notifier;
        public AttendanceController(AppDbContext db, IBiometricSyncService sync, IEmailSender emailSender, IHrEmailNotificationService notifier)
        {
            _db = db; _sync = sync; _emailSender = emailSender; _notifier = notifier;
        }

        static string TempDir => Path.Combine(Directory.GetCurrentDirectory(), "App_Data", "temp");

        // ═══════════════════════════════════════════
        // BIOMETRIC API SETTINGS
        // ═══════════════════════════════════════════
        public IActionResult ApiSettings()
        {
            var settings = _db.BiometricApiSettingsList.FirstOrDefault();
            if (settings == null)
            {
                settings = new BiometricApiSettings();
                _db.BiometricApiSettingsList.Add(settings);
                _db.SaveChanges();
            }
            return View(settings);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult SaveApiSettings(BiometricApiSettings model)
        {
            var existing = _db.BiometricApiSettingsList.Find(model.Id);
            if (existing == null) { existing = new BiometricApiSettings(); _db.BiometricApiSettingsList.Add(existing); }

            existing.IsEnabled = model.IsEnabled;
            existing.BaseUrl = model.BaseUrl;
            existing.HttpMethod = string.IsNullOrWhiteSpace(model.HttpMethod) ? "GET" : model.HttpMethod;
            existing.RequestBodyTemplate = model.RequestBodyTemplate;
            existing.ApiKey = model.ApiKey;
            existing.AuthHeaderName = string.IsNullOrWhiteSpace(model.AuthHeaderName) ? "Authorization" : model.AuthHeaderName;
            existing.AuthScheme = string.IsNullOrWhiteSpace(model.AuthScheme) ? "Bearer" : model.AuthScheme;
            existing.ResponseArrayPath = model.ResponseArrayPath;
            existing.EmployeeCodeField = string.IsNullOrWhiteSpace(model.EmployeeCodeField) ? "EmployeeCode" : model.EmployeeCodeField;
            existing.PunchDateTimeField = model.PunchDateTimeField;
            existing.PunchDateField = model.PunchDateField;
            existing.PunchTimeField = model.PunchTimeField;
            existing.DateTimeFormat = model.DateTimeFormat;
            existing.DirectionField = model.DirectionField;
            existing.InDirectionValue = model.InDirectionValue;
            existing.OutDirectionValue = model.OutDirectionValue;
            existing.DeviceIdField = model.DeviceIdField;
            existing.SyncIntervalMinutes = model.SyncIntervalMinutes < 5 ? 15 : model.SyncIntervalMinutes;

            _db.SaveChanges();
            TempData["Success"] = "API settings saved.";
            return RedirectToAction("ApiSettings");
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> SyncNow(DateTime? from, DateTime? to)
        {
            var f = from ?? DateTime.Today.AddDays(-1);
            var t = to ?? DateTime.Today;
            var (success, message) = await _sync.SyncAsync(_db, f, t);
            TempData[success ? "Success" : "Error"] = message;
            return RedirectToAction("ApiSettings");
        }

        // ═══════════════════════════════════════════
        // FACE MATCH API SETTINGS — same single-row, vendor-agnostic pattern
        // as Biometric API Settings above, but for the mobile app's Face
        // Attendance feature (see FaceMatchService.cs). Left disabled by
        // default: until a real vendor is configured here, mobile punches
        // are accepted without face verification rather than blocked.
        // ═══════════════════════════════════════════
        public IActionResult FaceMatchSettings()
        {
            var settings = _db.FaceMatchApiSettingsList.FirstOrDefault();
            if (settings == null)
            {
                settings = new FaceMatchApiSettings();
                _db.FaceMatchApiSettingsList.Add(settings);
                _db.SaveChanges();
            }
            return View(settings);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult SaveFaceMatchSettings(FaceMatchApiSettings model)
        {
            var existing = _db.FaceMatchApiSettingsList.Find(model.Id);
            if (existing == null) { existing = new FaceMatchApiSettings(); _db.FaceMatchApiSettingsList.Add(existing); }

            existing.IsEnabled = model.IsEnabled;
            existing.VerifyUrl = model.VerifyUrl;
            existing.ApiKey = model.ApiKey;
            existing.AuthHeaderName = string.IsNullOrWhiteSpace(model.AuthHeaderName) ? "Ocp-Apim-Subscription-Key" : model.AuthHeaderName;
            existing.AuthScheme = string.IsNullOrWhiteSpace(model.AuthScheme) ? "Raw" : model.AuthScheme;
            existing.ConfidenceField = string.IsNullOrWhiteSpace(model.ConfidenceField) ? "confidence" : model.ConfidenceField;
            existing.ConfidenceIsFraction = model.ConfidenceIsFraction;
            existing.IsIdenticalField = model.IsIdenticalField;
            existing.MinConfidencePercent = model.MinConfidencePercent <= 0 ? 80m : model.MinConfidencePercent;

            _db.SaveChanges();
            TempData["Success"] = "Face Match settings saved.";
            return RedirectToAction("FaceMatchSettings");
        }

        // ═══════════════════════════════════════════
        // KIOSK DEVICES — the shared Android tablets/phones set up as
        // walk-up "Attendance Machine" kiosks (separate standalone app,
        // NOT the regular employee mobile app — see KioskAttendanceController
        // for how a device authenticates with its ApiKey instead of a
        // per-employee login). Same simple "list + modal add + toggle +
        // delete-or-deactivate-if-in-use" pattern as Locations etc. in
        // AdminController, kept here instead since kiosks are an Attendance
        // concern.
        // ═══════════════════════════════════════════
        public IActionResult KioskDevices()
        {
            return View(_db.KioskDevices.OrderByDescending(k => k.CreatedAt).ToList());
        }

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult SaveKioskDevice(int id, string deviceName, string? locationLabel)
        {
            if (string.IsNullOrWhiteSpace(deviceName))
            {
                TempData["Error"] = "Device name is required.";
                return RedirectToAction("KioskDevices");
            }

            if (id > 0)
            {
                var existing = _db.KioskDevices.Find(id);
                if (existing == null) return NotFound();
                existing.DeviceName = deviceName.Trim();
                existing.LocationLabel = string.IsNullOrWhiteSpace(locationLabel) ? null : locationLabel.Trim();
                _db.SaveChanges();
                TempData["Success"] = "Kiosk device updated.";
            }
            else
            {
                var device = new KioskDevice
                {
                    DeviceName = deviceName.Trim(),
                    LocationLabel = string.IsNullOrWhiteSpace(locationLabel) ? null : locationLabel.Trim(),
                    ApiKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)), // 48 hex chars — entered once into the kiosk app's setup screen
                };
                _db.KioskDevices.Add(device);
                _db.SaveChanges();
                TempData["Success"] = $"Kiosk device \"{device.DeviceName}\" created. Copy its key now — enter it in the kiosk app's Setup screen.";
                TempData["NewKioskKey"] = device.ApiKey;
            }
            return RedirectToAction("KioskDevices");
        }

        [HttpPost]
        public IActionResult ToggleKioskDevice(int id)
        {
            var device = _db.KioskDevices.Find(id);
            if (device == null) return Json(new { success = false });
            device.IsActive = !device.IsActive;
            _db.SaveChanges();
            return Json(new { success = true, isActive = device.IsActive });
        }

        // Issues a fresh key for a device that's lost/being redeployed —
        // the old key stops working the moment this saves, so the kiosk
        // app on the physical device needs the new key re-entered.
        [HttpPost]
        public IActionResult RegenerateKioskKey(int id)
        {
            var device = _db.KioskDevices.Find(id);
            if (device == null) return Json(new { success = false });
            device.ApiKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            _db.SaveChanges();
            return Json(new { success = true, apiKey = device.ApiKey });
        }

        [HttpPost]
        public IActionResult DeleteKioskDevice(int id)
        {
            var device = _db.KioskDevices.Find(id);
            if (device == null) return Json(new { success = false });
            _db.KioskDevices.Remove(device);
            _db.SaveChanges();
            return Json(new { success = true, message = "Kiosk device deleted." });
        }

        // ═══════════════════════════════════════════
        // BULK ATTENDANCE IMPORT (Excel/CSV) — a two-step flow so a file
        // from ANY biometric machine's export works: step 1 reads just the
        // header row and lets Admin map columns to our fields (or reuse a
        // saved profile from a previous import); step 2 actually imports
        // using that mapping. The mapping is saved as an AttendanceImportProfile
        // so next month's export from the same machine imports in one step.
        // ═══════════════════════════════════════════
        public IActionResult BulkAttendanceImport()
        {
            ViewBag.Profiles = _db.AttendanceImportProfiles.OrderBy(p => p.Name).ToList();
            return View();
        }

        // Step 1 — read the header row only, show the mapping screen.
        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult UploadForMapping(IFormFile file)
        {
            if (file == null || file.Length == 0)
            { TempData["Error"] = "Please choose a file to upload."; return RedirectToAction("BulkAttendanceImport"); }

            Directory.CreateDirectory(TempDir);
            var token = Guid.NewGuid().ToString("N");
            var tempPath = Path.Combine(TempDir, token + Path.GetExtension(file.FileName is { Length: > 0 } fn ? fn : ".xlsx"));
            using (var fs = System.IO.File.Create(tempPath))
                file.CopyTo(fs);

            List<string> headers;
            try
            {
                using var wb = new ClosedXML.Excel.XLWorkbook(tempPath);
                var ws = wb.Worksheet(1);
                var used = ws.RangeUsed();
                if (used == null) throw new Exception("That file appears to be empty.");
                int lastCol = used.LastColumn().ColumnNumber();
                headers = new List<string>();
                for (int c = 1; c <= lastCol; c++)
                {
                    var h = ws.Cell(1, c).GetString().Trim();
                    if (!string.IsNullOrEmpty(h)) headers.Add(h);
                }
            }
            catch (Exception ex)
            {
                System.IO.File.Delete(tempPath);
                TempData["Error"] = $"Couldn't read that file: {ex.Message}";
                return RedirectToAction("BulkAttendanceImport");
            }

            ViewBag.Token = token;
            ViewBag.FileName = file.FileName;
            // Best-effort auto-mapping so the screen opens pre-filled —
            // Admin still sees and confirms every field before importing.
            ViewBag.Detected = AttendanceColumnDetector.Detect(headers);
            return View("MapAttendanceColumns", headers);
        }

        // Step 2 — parse the temp file using the submitted (or reused) column mapping.
        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult ImportWithMapping(string token, string? saveAsProfileName, AttendanceImportProfile mapping)
        {
            var tempFile = Directory.Exists(TempDir) ? Directory.GetFiles(TempDir, token + ".*").FirstOrDefault() : null;
            if (tempFile == null)
            { TempData["Error"] = "That upload has expired — please upload the file again."; return RedirectToAction("BulkAttendanceImport"); }

            var result = RunImport(tempFile, mapping);
            System.IO.File.Delete(tempFile);

            if (!string.IsNullOrWhiteSpace(saveAsProfileName))
            {
                mapping.Id = 0;
                mapping.Name = saveAsProfileName.Trim();
                mapping.CreatedAt = DateTime.Now;
                _db.AttendanceImportProfiles.Add(mapping);
                _db.SaveChanges();
            }

            return View("AttendanceImportResult", result);
        }

        // Repeat import using an already-saved profile — one step.
        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult ImportWithProfile(int profileId, IFormFile file)
        {
            var profile = _db.AttendanceImportProfiles.Find(profileId);
            if (profile == null) { TempData["Error"] = "That mapping profile no longer exists."; return RedirectToAction("BulkAttendanceImport"); }
            if (file == null || file.Length == 0) { TempData["Error"] = "Please choose a file to upload."; return RedirectToAction("BulkAttendanceImport"); }

            Directory.CreateDirectory(TempDir);
            var tempPath = Path.Combine(TempDir, Guid.NewGuid().ToString("N") + ".xlsx");
            using (var fs = System.IO.File.Create(tempPath)) file.CopyTo(fs);

            var result = RunImport(tempPath, profile);
            System.IO.File.Delete(tempPath);
            return View("AttendanceImportResult", result);
        }

        [HttpPost]
        public IActionResult DeleteImportProfile(int id)
        {
            var p = _db.AttendanceImportProfiles.Find(id);
            if (p == null) return Json(new { success = false });
            _db.AttendanceImportProfiles.Remove(p);
            _db.SaveChanges();
            return Json(new { success = true });
        }

        // Shared parser — works for both a one-off mapping and a saved
        // profile, since AttendanceImportProfile IS the mapping shape.
        BulkImportResult RunImport(string filePath, AttendanceImportProfile map)
        {
            var result = new BulkImportResult();
            try
            {
                using var wb = new ClosedXML.Excel.XLWorkbook(filePath);
                var ws = wb.Worksheet(1);
                var used = ws.RangeUsed();
                if (used == null) throw new Exception("That file appears to be empty.");
                int lastRow = used.LastRow().RowNumber();
                int lastCol = used.LastColumn().ColumnNumber();

                var col = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (int c = 1; c <= lastCol; c++)
                {
                    var h = ws.Cell(1, c).GetString().Trim();
                    if (!string.IsNullOrEmpty(h) && !col.ContainsKey(h)) col[h] = c;
                }

                string? S(int r, string? header)
                {
                    if (string.IsNullOrWhiteSpace(header) || !col.TryGetValue(header, out var c)) return null;
                    var cell = ws.Cell(r, c);
                    if (cell.IsEmpty()) return null;
                    if (cell.DataType == ClosedXML.Excel.XLDataType.DateTime)
                        return cell.GetDateTime().ToString("yyyy-MM-dd HH:mm:ss");
                    var v = cell.GetString().Trim();
                    return v.Length == 0 ? null : v;
                }

                DateTime? ParseDate(string? raw, string? fmt)
                {
                    if (raw == null) return null;
                    if (!string.IsNullOrWhiteSpace(fmt) && DateTime.TryParseExact(raw, fmt, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var exact)) return exact;
                    if (DateTime.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsed)) return parsed;
                    return null;
                }

                // A time cell may hold just a time-of-day, or (ClosedXML
                // sometimes returns this for time-formatted cells) a full
                // datetime string — try both, same fallback S() already
                // relies on for date cells.
                bool TryTime(string? raw, out TimeSpan ts)
                {
                    ts = default;
                    if (raw == null) return false;
                    if (TimeSpan.TryParse(raw, out ts)) return true;
                    if (DateTime.TryParse(raw, out var full)) { ts = full.TimeOfDay; return true; }
                    return false;
                }

                var empByCode = _db.Employees.Where(e => e.IsActive).ToDictionary(e => e.EmpCode, e => e.Id, StringComparer.OrdinalIgnoreCase);
                var existingKeys = new HashSet<(int, DateTime)>(_db.AttendancePunches.Select(p => new { p.EmployeeId, p.PunchDateTime }).ToList().Select(p => (p.EmployeeId, p.PunchDateTime)));
                var affected = new HashSet<(int EmployeeId, DateTime Date)>();
                int imported = 0, skippedNoEmployee = 0, skippedDup = 0, skippedBadDate = 0;

                for (int r = 2; r <= lastRow; r++)
                {
                    var empCode = S(r, map.EmployeeCodeColumn);
                    if (string.IsNullOrWhiteSpace(empCode)) continue; // blank/trailing row
                    if (!empByCode.TryGetValue(empCode, out var empId)) { skippedNoEmployee++; continue; }

                    var punchTimes = new List<(DateTime dt, string direction)>();

                    if (map.Format == "PerDayInOut")
                    {
                        // Explicit In Date/Time + Out Date/Time columns — the
                        // gold-standard shape. Critically, a punch is only
                        // added for the side that actually HAS data: a row
                        // with an In but no Out (or vice versa) correctly
                        // produces zero punches for the missing side, which
                        // is exactly what lets AttendanceEngine tell a
                        // Morning mispunch (missing In) apart from an
                        // Evening mispunch (missing Out) downstream.
                        var inDateRaw = S(r, map.DateColumn);
                        var inDate = ParseDate(inDateRaw, map.DateFormat);
                        var inTimeRaw = S(r, map.TimeColumn);

                        // Blank/unmapped Out Date column = same calendar day as the In punch.
                        var outDateRaw = S(r, map.OutDateColumn);
                        var outDate = outDateRaw != null ? ParseDate(outDateRaw, map.DateFormat) : inDate;
                        var outTimeRaw = S(r, map.OutTimeColumn);

                        if (inDate != null && TryTime(inTimeRaw, out var inTs))
                            punchTimes.Add((inDate.Value.Date + inTs, "In"));
                        if (outDate != null && TryTime(outTimeRaw, out var outTs))
                            punchTimes.Add((outDate.Value.Date + outTs, "Out"));

                        // Nothing usable anywhere on this row (not even a
                        // date) — a genuinely blank trailing row, not a
                        // mispunch to report on.
                        if (punchTimes.Count == 0 && inDate == null && outDate == null) continue;
                    }
                    else if (map.Format == "PerDayMultiPunch")
                    {
                        var dateRaw = S(r, map.DateColumn);
                        var date = ParseDate(dateRaw, map.DateFormat);
                        if (date == null) { skippedBadDate++; continue; }

                        var punchCols = (map.PunchColumnsCsv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        foreach (var pc in punchCols)
                        {
                            var timeRaw = S(r, pc);
                            if (timeRaw == null) continue;
                            // the cell may hold just a time, or a full datetime — try both
                            DateTime punchDt;
                            if (TimeSpan.TryParse(timeRaw, out var ts)) punchDt = date.Value.Date + ts;
                            else if (DateTime.TryParse(timeRaw, out var full)) punchDt = date.Value.Date + full.TimeOfDay;
                            else continue;
                            punchTimes.Add((punchDt, "Unknown"));
                        }
                        if (punchTimes.Count > 0)
                        {
                            punchTimes = punchTimes.OrderBy(p => p.dt).ToList();
                            punchTimes[0] = (punchTimes[0].dt, "In");
                            if (punchTimes.Count > 1) punchTimes[^1] = (punchTimes[^1].dt, "Out");
                        }
                    }
                    else // PerPunchRow
                    {
                        DateTime? punchDt = null;
                        if (!string.IsNullOrWhiteSpace(map.DateTimeColumn))
                            punchDt = ParseDate(S(r, map.DateTimeColumn), map.DateFormat);
                        else if (!string.IsNullOrWhiteSpace(map.DateColumn))
                        {
                            var d = ParseDate(S(r, map.DateColumn), map.DateFormat);
                            var tRaw = S(r, map.TimeColumn);
                            if (d != null)
                                punchDt = tRaw != null && TimeSpan.TryParse(tRaw, out var ts) ? d.Value.Date + ts : d;
                        }
                        if (punchDt == null) { skippedBadDate++; continue; }

                        string direction = "Unknown";
                        var dirRaw = S(r, map.DirectionColumn);
                        if (dirRaw != null)
                        {
                            if (dirRaw.Equals("In", StringComparison.OrdinalIgnoreCase) || dirRaw.Equals("IN", StringComparison.OrdinalIgnoreCase) || dirRaw == "0") direction = "In";
                            else if (dirRaw.Equals("Out", StringComparison.OrdinalIgnoreCase) || dirRaw.Equals("OUT", StringComparison.OrdinalIgnoreCase) || dirRaw == "1") direction = "Out";
                        }
                        punchTimes.Add((punchDt.Value, direction));
                    }

                    foreach (var (dt, direction) in punchTimes)
                    {
                        if (existingKeys.Contains((empId, dt))) { skippedDup++; continue; }
                        _db.AttendancePunches.Add(new AttendancePunch { EmployeeId = empId, PunchDateTime = dt, Direction = direction, Source = "ExcelImport" });
                        existingKeys.Add((empId, dt));
                        affected.Add((empId, dt.Date));
                        imported++;
                    }
                }

                _db.SaveChanges();

                foreach (var (empId, date) in affected)
                    AttendanceEngine.RecomputeDayAsync(_db, empId, date).GetAwaiter().GetResult();

                result.Success = true;
                result.Created = imported; // reusing BulkImportResult's fields — Created = punches imported here
                result.Updated = skippedDup;
                if (skippedNoEmployee > 0) result.ManagerNotFound.Add($"{skippedNoEmployee} row(s) had an Employee Code not found in Employee Master.");
                if (skippedBadDate > 0) result.ManagerNotFound.Add($"{skippedBadDate} row(s) had a date/time that couldn't be parsed.");
                result.MastersCreated.Add($"{affected.Count} employee-day(s) recomputed.");
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Error = ex.Message;
            }
            return result;
        }

        public IActionResult DownloadAttendanceTemplate()
        {
            using var wb = new ClosedXML.Excel.XLWorkbook();
            var ws = wb.Worksheets.Add("Punches");
            ws.Cell(1, 1).Value = "Employee Code";
            ws.Cell(1, 2).Value = "Punch In Date";
            ws.Cell(1, 3).Value = "Punch In Time";
            ws.Cell(1, 4).Value = "Punch Out Date";
            ws.Cell(1, 5).Value = "Punch Out Time";
            ws.Row(1).Style.Font.Bold = true;
            ws.Cell(2, 1).Value = "00001"; ws.Cell(2, 2).Value = "01-07-2026"; ws.Cell(2, 3).Value = "09:58:00"; ws.Cell(2, 4).Value = "01-07-2026"; ws.Cell(2, 5).Value = "20:06:00";
            // Example: a missing Out punch — leave Punch Out Date/Time blank and it correctly shows as an Evening mispunch, not a guess.
            ws.Cell(3, 1).Value = "00002"; ws.Cell(3, 2).Value = "01-07-2026"; ws.Cell(3, 3).Value = "09:52:00"; ws.Cell(3, 4).Value = ""; ws.Cell(3, 5).Value = "";
            ws.Columns().AdjustToContents();
            using var stream = new MemoryStream();
            wb.SaveAs(stream);
            return File(stream.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "AMPM_Attendance_Import_Template.xlsx");
        }

        // ═══════════════════════════════════════════
        // MANUAL DAILY CORRECTION — a light-touch single-day editor for
        // Admin/HR to fix or backfill one employee's punches without a full
        // file import; also usable for pure manual-entry attendance if the
        // company doesn't have a biometric feed for some staff.
        // ═══════════════════════════════════════════
        public IActionResult ManualEntry(int? employeeId, string? date)
        {
            ViewBag.EmployeeList = _db.Employees.Where(e => e.IsActive).OrderBy(e => e.Name).ToList();
            ViewBag.SelectedEmployeeId = employeeId;
            ViewBag.SelectedDate = date ?? DateTime.Today.ToString("yyyy-MM-dd");
            if (employeeId.HasValue && !string.IsNullOrWhiteSpace(date))
            {
                var punches = _db.AttendancePunches
                    .Where(p => p.EmployeeId == employeeId && p.PunchDateTime.Date == DateTime.Parse(date).Date)
                    .OrderBy(p => p.PunchDateTime).ToList();
                return View(punches);
            }
            return View(new List<AttendancePunch>());
        }

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult SaveManualPunch(int employeeId, string date, string time, string direction)
        {
            if (!DateTime.TryParse($"{date} {time}", out var dt))
            { TempData["Error"] = "Invalid date/time."; return RedirectToAction("ManualEntry", new { employeeId, date }); }

            _db.AttendancePunches.Add(new AttendancePunch { EmployeeId = employeeId, PunchDateTime = dt, Direction = direction, Source = "Manual" });
            _db.SaveChanges();
            AttendanceEngine.RecomputeDayAsync(_db, employeeId, dt.Date).GetAwaiter().GetResult();
            TempData["Success"] = "Punch saved and day recomputed.";
            return RedirectToAction("ManualEntry", new { employeeId, date });
        }

        [HttpPost]
        public IActionResult DeleteManualPunch(int id, int employeeId, string date)
        {
            var p = _db.AttendancePunches.Find(id);
            if (p != null) { _db.AttendancePunches.Remove(p); _db.SaveChanges(); AttendanceEngine.RecomputeDayAsync(_db, employeeId, DateTime.Parse(date)).GetAwaiter().GetResult(); }
            return RedirectToAction("ManualEntry", new { employeeId, date });
        }

        // ═══════════════════════════════════════════
        // EMAIL NOTIFICATIONS — SMTP settings plus the three scheduled HR
        // jobs' own enable-toggle/send-time (Daily Attendance Alert,
        // Birthday wishes, Weekly Attendance Report). Same single-row
        // settings pattern as Biometric API / Face Match API above; the
        // actual report-building and sending logic lives in
        // HrEmailNotificationService.cs, fired automatically by
        // HrNotificationHostedService on the schedule configured here, or
        // right now via the three "Run Now" buttons for testing.
        // ═══════════════════════════════════════════
        public IActionResult EmailSettings()
        {
            var settings = _db.EmailSettingsList.FirstOrDefault();
            if (settings == null)
            {
                settings = new EmailSettings();
                _db.EmailSettingsList.Add(settings);
                _db.SaveChanges();
            }
            return View(settings);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult SaveEmailSettings(EmailSettings model)
        {
            var existing = _db.EmailSettingsList.Find(model.Id);
            if (existing == null) { existing = new EmailSettings(); _db.EmailSettingsList.Add(existing); }

            existing.IsEnabled = model.IsEnabled;
            existing.SmtpHost = model.SmtpHost;
            existing.SmtpPort = model.SmtpPort <= 0 ? 587 : model.SmtpPort;
            existing.SmtpUsername = model.SmtpUsername;
            // Blank password on save = "keep the existing one" (the settings
            // page never re-displays a stored password, so a blank submit
            // isn't a deliberate clear — same convention as the mobile app's
            // own password-change screens elsewhere in this app).
            if (!string.IsNullOrEmpty(model.SmtpPassword)) existing.SmtpPassword = model.SmtpPassword;
            existing.SmtpUseSsl = model.SmtpUseSsl;
            existing.FromEmail = model.FromEmail;
            existing.FromName = string.IsNullOrWhiteSpace(model.FromName) ? "AMPM HRMS" : model.FromName;

            existing.DailyAttendanceAlertEnabled = model.DailyAttendanceAlertEnabled;
            existing.DailyAttendanceAlertTime = model.DailyAttendanceAlertTime;

            existing.BirthdayEnabled = model.BirthdayEnabled;
            existing.BirthdayTime = model.BirthdayTime;

            existing.WeeklyReportEnabled = model.WeeklyReportEnabled;
            existing.WeeklyReportDay = model.WeeklyReportDay;
            existing.WeeklyReportTime = model.WeeklyReportTime;

            _db.SaveChanges();
            TempData["Success"] = "Email settings saved.";
            return RedirectToAction("EmailSettings");
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> SendTestEmail(string to)
        {
            var settings = _db.EmailSettingsList.FirstOrDefault();
            if (settings == null) { TempData["Error"] = "Save email settings first."; return RedirectToAction("EmailSettings"); }
            if (string.IsNullOrWhiteSpace(to)) { TempData["Error"] = "Enter an address to send the test to."; return RedirectToAction("EmailSettings"); }

            var (ok, msg) = await _emailSender.SendAsync(settings, new[] { to.Trim() }, "AMPM HRMS — Test Email",
                "<p>This is a test email from AMPM HRMS. If you're reading this, your SMTP settings are working correctly.</p>");
            TempData[ok ? "Success" : "Error"] = ok ? $"Test email sent to {to}." : $"Test email failed: {msg}";
            return RedirectToAction("EmailSettings");
        }

        // Runs each job immediately, ignoring its schedule and the
        // once-per-day guard, so Admin can verify content/recipients
        // without waiting for the actual scheduled time. Does NOT update
        // LastXRunDate, so the normal scheduled run for today still fires
        // on time afterward (this is a preview send, not a replacement).
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> RunDailyAlertNow()
        {
            var settings = _db.EmailSettingsList.FirstOrDefault();
            if (settings == null) { TempData["Error"] = "Save email settings first."; return RedirectToAction("EmailSettings"); }
            var msg = await _notifier.SendDailyAttendanceAlertsAsync(_db, settings, DateTime.Today.AddDays(-1));
            TempData["Success"] = msg;
            return RedirectToAction("EmailSettings");
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> RunBirthdayNow()
        {
            var settings = _db.EmailSettingsList.FirstOrDefault();
            if (settings == null) { TempData["Error"] = "Save email settings first."; return RedirectToAction("EmailSettings"); }
            var msg = await _notifier.SendBirthdayEmailsAsync(_db, settings, DateTime.Today);
            TempData["Success"] = msg;
            return RedirectToAction("EmailSettings");
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> RunWeeklyReportNow()
        {
            var settings = _db.EmailSettingsList.FirstOrDefault();
            if (settings == null) { TempData["Error"] = "Save email settings first."; return RedirectToAction("EmailSettings"); }
            var weekEnd = DateTime.Today.AddDays(-1);
            var weekStart = weekEnd.AddDays(-6);
            var msg = await _notifier.SendWeeklyAttendanceReportsAsync(_db, settings, weekStart, weekEnd);
            TempData["Success"] = msg;
            return RedirectToAction("EmailSettings");
        }

        // ═══════════════════════════════════════════
        // RECOMPUTE — lets Admin/HR rerun AttendanceEngine (and the
        // CompOff/OT auto-credit hooks inside it) for any date range,
        // for all employees or one specific employee. Useful after:
        //   • fixing biometric punches after the fact
        //   • approving old regularisations (so comp-off auto-credits fire)
        //   • updating a shift / week-off policy retroactively
        // ═══════════════════════════════════════════
        public IActionResult Recompute()
        {
            ViewBag.EmployeeList = _db.Employees.Where(e => e.IsActive).OrderBy(e => e.Name).ToList();
            return View();
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Recompute(string fromDate, string toDate, int? employeeId)
        {
            ViewBag.EmployeeList = _db.Employees.Where(e => e.IsActive).OrderBy(e => e.Name).ToList();

            if (string.IsNullOrWhiteSpace(fromDate) || string.IsNullOrWhiteSpace(toDate))
            {
                TempData["Error"] = "Please select both From and To dates.";
                return View();
            }

            if (!DateTime.TryParse(fromDate, out var from) || !DateTime.TryParse(toDate, out var to))
            {
                TempData["Error"] = "Invalid date format.";
                return View();
            }

            if (from > to) { TempData["Error"] = "From Date cannot be after To Date."; return View(); }
            if ((to - from).TotalDays > 366) { TempData["Error"] = "Date range cannot exceed 366 days."; return View(); }

            int days = (int)(to - from).TotalDays + 1;

            if (employeeId.HasValue && employeeId > 0)
            {
                await AttendanceEngine.RecomputeRangeAsync(_db, employeeId.Value, from, to);
                var emp = await _db.Employees.FindAsync(employeeId.Value);
                TempData["Success"] = $"Recomputed {days} day(s) for {emp?.Name ?? "employee"}.";
            }
            else
            {
                await AttendanceEngine.RecomputeAllAsync(_db, from, to);
                int empCount = await _db.Employees.CountAsync(e => e.IsActive);
                TempData["Success"] = $"Recomputed {days} day(s) for all {empCount} active employees.";
            }

            return View();
        }
    }
}
