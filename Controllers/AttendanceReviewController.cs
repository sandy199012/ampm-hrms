// Controllers/AttendanceReviewController.cs
using System.Globalization;
using AmpmHrmsPro.Models;
using AmpmHrmsPro.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AmpmHrmsPro.Controllers
{
    [Authorize(Roles = "admin,hr")]
    public class AttendanceReviewController : Controller
    {
        private readonly IAttendanceReviewService _svc;
        private readonly IGapAnalysisReport _report;
        private readonly AmpmHrmsPro.Data.AppDbContext _db;
        private readonly ILogger<AttendanceReviewController> _log;

        public AttendanceReviewController(
            IAttendanceReviewService svc,
            IGapAnalysisReport report,
            AmpmHrmsPro.Data.AppDbContext db,
            ILogger<AttendanceReviewController> log)
        {
            _svc = svc;
            _report = report;
            _db = db;
            _log = log;
        }

        // GET /AttendanceReview
        public async Task<IActionResult> Index()
        {
            ViewData["Title"]    = "Attendance Review";
            ViewData["Subtitle"] = "Upload daily Presence 360 report and review attendance gaps";
            var imports = await _svc.GetImportsAsync();
            ViewBag.DataStatus = await _report.GetStatusAsync();
            return View(imports);
        }

        // POST /AttendanceReview/Upload
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Upload(IFormFile csvFile)
        {
            if (csvFile == null || csvFile.Length == 0)
            {
                TempData["Error"] = "Please select a CSV file to upload.";
                return RedirectToAction(nameof(Index));
            }

            if (!csvFile.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            {
                TempData["Error"] = "Only .csv files are supported.";
                return RedirectToAction(nameof(Index));
            }

            try
            {
                using var stream  = csvFile.OpenReadStream();
                var importedBy   = User.Identity?.Name ?? "Admin";
                var importId     = await _svc.ImportCsvAsync(stream, csvFile.FileName, importedBy);

                TempData["Success"] = "CSV imported — gaps detected. The same punches are also being added to HRMS attendance in the background (Daily Alert, Weekly Report and attendance reports use them); a month-long file can take a few minutes.";
                return RedirectToAction(nameof(GapReport), new { id = importId });
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "AttendanceReview upload error");
                TempData["Error"] = $"Import failed: {ex.Message}";
                return RedirectToAction(nameof(Index));
            }
        }

        // GET /AttendanceReview/GapReport/5
        public async Task<IActionResult> GapReport(int id)
        {
            ViewData["Title"]    = "Gap Report";
            ViewData["Subtitle"] = "Attendance gaps detected in this import";
            var gaps = await _svc.GetGapsAsync(id);
            ViewBag.ImportId = id;
            ViewBag.RecipientMap = await _svc.GetRecipientMapAsync();
            ViewBag.HodNames = (await _svc.GetRecipientRowsAsync())
                .Select(r => r.HodName).Where(n => !string.Equals(n, "Unassigned", StringComparison.OrdinalIgnoreCase)).ToList();
            ViewBag.TestEmail = TempData["TestEmail"] as string
                ?? (await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstOrDefaultAsync(_db.MailAccounts))?.Email;

            // Group summary for display
            // Group by HoD (falls back to direct manager for older imports
            // created before HoD roll-up existed).
            ViewBag.ByManager = gaps
                .GroupBy(g => g.HodName ?? g.ManagerName ?? "Unassigned")
                .OrderBy(g => g.Key)
                .ToList();

            return View(gaps);
        }

        // POST /AttendanceReview/SendEmails/5
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> SendEmails(int id)
        {
            try
            {
                var (sent, failed) = await _svc.SendEmailsForImportAsync(id);
                if (failed > 0)
                    TempData["Error"] = $"Emails sent: {sent}, failed: {failed}. Reason: {_svc.LastSendError}";
                else if (sent == 0)
                    TempData["Error"] = "No emails sent — either they were already sent, or none of these HoDs has an email added on the HoD Emails page.";
                else
                    TempData["Success"] = $"Gap Analysis emailed to {sent} HoD(s).";
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "AttendanceReview SendEmails error for import {Id}", id);
                TempData["Error"] = $"Error sending emails: {ex.Message}";
            }
            return RedirectToAction(nameof(GapReport), new { id });
        }

        // GET /AttendanceReview/Recipients — who receives the Gap Analysis email
        public async Task<IActionResult> Recipients()
        {
            ViewData["Title"]    = "HoD Emails";
            ViewData["Subtitle"] = "Who receives the Gap Analysis email";
            return View(await _svc.GetRecipientRowsAsync());
        }

        // POST /AttendanceReview/Recipients — hodNames[i] ↔ emails[i]
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Recipients(List<string> hodNames, List<string?> emails)
        {
            try
            {
                var rows = hodNames.Select((n, i) => (n, i < emails.Count ? emails[i] : null));
                await _svc.SaveRecipientsAsync(rows);
                var count = (await _svc.GetRecipientMapAsync()).Count;
                TempData["Success"] = $"Saved. Gap Analysis emails will go to {count} HoD(s) — everyone else gets nothing.";
            }
            catch (InvalidOperationException ex)
            {
                TempData["Error"] = ex.Message + " — nothing was saved.";
            }
            return RedirectToAction(nameof(Recipients));
        }

        // POST /AttendanceReview/SendTest — one HoD's Gap Analysis to a test address only
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> SendTest(int id, string? hod, string? testEmail)
        {
            var (ok, msg) = await _svc.SendTestEmailAsync(id, hod ?? "", testEmail ?? "");
            TempData[ok ? "Success" : "Error"] = msg;
            TempData["TestEmail"] = testEmail;
            TempData["TestHod"] = hod;
            return RedirectToAction(nameof(GapReport), new { id });
        }

        // POST /AttendanceReview/UploadRoster — Attendance Management.xlsx (all month sheets)
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> UploadRoster(IFormFile rosterFile)
        {
            if (rosterFile == null || rosterFile.Length == 0)
            { TempData["Error"] = "Please select the roster Excel file."; return RedirectToAction(nameof(Index)); }
            try
            {
                using var stream = rosterFile.OpenReadStream();
                var (rows, months, warnings) = await _report.ImportRosterAsync(stream);
                TempData["Success"] = $"Roster uploaded — {rows:N0} rows ({string.Join(", ", months)}).";
                if (warnings.Count > 0)
                    TempData["Warning"] = "Please check the roster: " + string.Join(" · ", warnings.Take(15))
                                        + (warnings.Count > 15 ? $" · …and {warnings.Count - 15} more." : "");
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Roster upload failed");
                TempData["Error"] = $"Roster upload failed: {ex.Message}";
            }
            return RedirectToAction(nameof(Index));
        }

        // GET /AttendanceReview/DownloadRoster?month=2026-11 — roster Excel for every staff member & worker,
        // pre-filled, in the Attendance Management.xlsx format. Fill it and upload it back above.
        [HttpGet]
        public async Task<IActionResult> DownloadRoster(string? month)
        {
            var today = IndiaTime.Today;
            var m = new DateTime(today.Year, today.Month, 1);
            if (!string.IsNullOrWhiteSpace(month) &&
                DateTime.TryParseExact(month, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var picked))
                m = picked;
            try
            {
                var bytes = await _report.BuildRosterTemplateAsync(m.Year, m.Month);
                return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                    $"Roster_{m.ToString("MMM-yyyy", CultureInfo.InvariantCulture)}.xlsx");
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Roster template failed");
                TempData["Error"] = $"Couldn't build the roster file: {ex.Message}";
                return RedirectToAction(nameof(Index));
            }
        }

        // POST /AttendanceReview/UploadMaster — Master Sheet (Employee Master: who reports to whom)
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> UploadMaster(IFormFile masterFile)
        {
            if (masterFile == null || masterFile.Length == 0)
            { TempData["Error"] = "Please select the Master Sheet Excel file."; return RedirectToAction(nameof(Index)); }
            try
            {
                using var stream = masterFile.OpenReadStream();
                var n = await _report.ImportMasterAsync(stream);
                TempData["Success"] = $"Master Sheet uploaded — {n} employees with their reporting managers.";
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Master upload failed");
                TempData["Error"] = $"Master Sheet upload failed: {ex.Message}";
            }
            return RedirectToAction(nameof(Index));
        }

        // GET /AttendanceReview/DownloadExcel/5[?hod=Manish Rana]
        // Month-to-Date Gap Analysis up to this import's last date —
        // everyone, or one HoD's whole team.
        public async Task<IActionResult> DownloadExcel(int id, string? hod = null)
        {
            var import = await _db.AttendanceImports.FindAsync(id);
            if (import == null) return NotFound();
            var upto = import.ToDate;

            var scope = await _report.ResolveScopeAsync(string.IsNullOrWhiteSpace(hod) ? null : hod, upto);
            var rows  = await _report.BuildRowsAsync(scope, upto);
            var bytes = _report.BuildWorkbook(rows, upto);

            var date = upto.ToString("dd-MMM-yyyy", System.Globalization.CultureInfo.InvariantCulture);
            string name;
            if (string.IsNullOrWhiteSpace(hod))
                name = $"ALL_Employees_Attendance_MTD_{date}.xlsx";
            else
            {
                var display = await _report.MasterNameForAsync(hod) ?? hod;
                name = $"Attendance_MTD_{display.Trim().Replace(' ', '_')}_{date}.xlsx";
            }
            return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", name);
        }
    }
}
