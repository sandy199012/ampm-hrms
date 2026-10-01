// Controllers/AttendanceReviewController.cs
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
        private readonly ILogger<AttendanceReviewController> _log;

        public AttendanceReviewController(
            IAttendanceReviewService svc,
            ILogger<AttendanceReviewController> log)
        {
            _svc = svc;
            _log = log;
        }

        // GET /AttendanceReview
        public async Task<IActionResult> Index()
        {
            ViewData["Title"]    = "Attendance Review";
            ViewData["Subtitle"] = "Upload daily Presence 360 report and review attendance gaps";
            var imports = await _svc.GetImportsAsync();
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

                TempData["Success"] = "CSV imported successfully. Gaps have been detected.";
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
                TempData["Success"] = $"Emails sent to {sent} manager(s). Failed: {failed}.";
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "AttendanceReview SendEmails error for import {Id}", id);
                TempData["Error"] = $"Error sending emails: {ex.Message}";
            }
            return RedirectToAction(nameof(GapReport), new { id });
        }

        // GET /AttendanceReview/DownloadExcel/5  [?managerEmail=...]
        // ?hod=Manish Rana → only that HoD's department; no filter → all HoDs
        // (Summary sheet + one sheet per HoD).
        public async Task<IActionResult> DownloadExcel(int id, string? managerEmail = null, string? hod = null)
        {
            var gaps = await _svc.GetGapsAsync(id);
            var filtered = gaps;
            if (!string.IsNullOrEmpty(hod))
                filtered = gaps.Where(g => string.Equals(g.HodName ?? g.ManagerName, hod,
                                           StringComparison.OrdinalIgnoreCase)).ToList();
            else if (!string.IsNullOrEmpty(managerEmail))
                filtered = gaps.Where(g => g.ManagerEmail == managerEmail).ToList();

            var bytes = await _svc.GenerateExcelAsync(managerEmail ?? "all", filtered);
            var label = string.IsNullOrEmpty(hod) ? "AllHoDs" : hod.Replace(" ", "_");
            var name  = $"AttendanceGaps_{label}_{id}_{DateTime.Today:yyyyMMdd}.xlsx";
            return File(bytes,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", name);
        }
    }
}
