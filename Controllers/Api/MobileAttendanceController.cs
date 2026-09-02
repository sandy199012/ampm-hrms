using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using AmpmHrmsPro.Data;
using AmpmHrmsPro.Models;
using AmpmHrmsPro.Services;

namespace AmpmHrmsPro.Controllers.Api
{
    // ═══════════════════════════════════════════
    // MOBILE ATTENDANCE — Punch In/Out (with GPS + a live selfie verified
    // against the employee's enrolled FaceProfile), today's status,
    // history, face enrollment, and the employee's own OT figures. Every
    // punch this creates lands in the SAME AttendancePunch table the
    // biometric-machine sync and the Excel import use (Source =
    // "MobileApp" instead of "BiometricApi"/"ExcelImport") — so it flows
    // through the exact same AttendanceEngine recompute and shows up in
    // every existing report without any special-casing there.
    // ═══════════════════════════════════════════
    [ApiController]
    [Route("api/mobile/attendance")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public class MobileAttendanceController : ControllerBase
    {
        readonly AppDbContext _db;
        readonly IFaceMatchService _faceMatch;
        public MobileAttendanceController(AppDbContext db, IFaceMatchService faceMatch) { _db = db; _faceMatch = faceMatch; }

        int CurrentEmpId => int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);

        [HttpGet("today")]
        public async Task<IActionResult> Today()
        {
            var today = DateTime.Today.ToString("yyyy-MM-dd");
            var daily = await _db.AttendanceDailies.FirstOrDefaultAsync(d => d.EmployeeId == CurrentEmpId && d.Date == today);
            var lastPunch = await _db.AttendancePunches.Where(p => p.EmployeeId == CurrentEmpId && p.PunchDateTime.Date == DateTime.Today)
                .OrderByDescending(p => p.PunchDateTime).FirstOrDefaultAsync();

            return Ok(new
            {
                date = today,
                inTime = daily?.InTime?.ToString(@"hh\:mm"),
                outTime = daily?.OutTime?.ToString(@"hh\:mm"),
                status = daily?.EffectiveStatus,
                workedMinutes = daily?.WorkedMinutes,
                // "In" if the last punch today was an Out (or there's none
                // yet) — i.e. what the NEXT punch button should say.
                nextDirection = lastPunch == null || lastPunch.Direction == "Out" ? "In" : "Out",
            });
        }

        // multipart/form-data: direction ("In"/"Out"), latitude, longitude, photo (file)
        [HttpPost("punch")]
        public async Task<IActionResult> Punch([FromForm] string direction, [FromForm] double? latitude, [FromForm] double? longitude, IFormFile? photo)
        {
            if (direction != "In" && direction != "Out")
                return BadRequest(new { message = "direction must be \"In\" or \"Out\"." });

            string? photoPath = null;
            bool? faceMatched = null;
            decimal? faceConfidence = null;
            string? faceMessage = null;

            if (photo != null && photo.Length > 0)
            {
                photoPath = await FileStorageHelper.SavePhotoAsync(photo, "punches");

                var faceProfile = await _db.FaceProfiles.FirstOrDefaultAsync(f => f.EmployeeId == CurrentEmpId && f.IsActive);
                if (faceProfile != null)
                {
                    var enrolledBytes = FileStorageHelper.ReadBytes(faceProfile.PhotoPath);
                    var liveBytes = FileStorageHelper.ReadBytes(photoPath);
                    if (enrolledBytes.Length > 0 && liveBytes.Length > 0)
                    {
                        var (matched, confidence, message) = await _faceMatch.VerifyAsync(enrolledBytes, liveBytes);
                        faceMatched = matched; faceConfidence = confidence; faceMessage = message;
                    }
                }
                else
                {
                    faceMessage = "No enrolled face on file yet — punch accepted without face verification. Enroll your face from the Profile screen.";
                }
            }

            var punch = new AttendancePunch
            {
                EmployeeId = CurrentEmpId,
                PunchDateTime = DateTime.Now,
                Direction = direction,
                Source = "MobileApp",
                Latitude = latitude,
                Longitude = longitude,
                PhotoPath = photoPath,
                FaceMatched = faceMatched,
                FaceMatchConfidence = faceConfidence,
            };
            _db.AttendancePunches.Add(punch);
            await _db.SaveChangesAsync();

            await AttendanceEngine.RecomputeDayAsync(_db, CurrentEmpId, DateTime.Today);
            var daily = await _db.AttendanceDailies.FirstOrDefaultAsync(d => d.EmployeeId == CurrentEmpId && d.Date == DateTime.Today.ToString("yyyy-MM-dd"));

            return Ok(new
            {
                success = true,
                punchTime = punch.PunchDateTime.ToString("hh:mm tt"),
                faceMatched,
                faceConfidence,
                faceMessage,
                status = daily?.EffectiveStatus,
            });
        }

        [HttpPost("enroll-face")]
        public async Task<IActionResult> EnrollFace(IFormFile photo)
        {
            if (photo == null || photo.Length == 0) return BadRequest(new { message = "Photo is required." });

            var existing = await _db.FaceProfiles.Where(f => f.EmployeeId == CurrentEmpId && f.IsActive).ToListAsync();
            foreach (var f in existing) f.IsActive = false; // keep history, just retire it — see FaceProfile's class remarks

            var path = await FileStorageHelper.SavePhotoAsync(photo, "faces");
            _db.FaceProfiles.Add(new FaceProfile { EmployeeId = CurrentEmpId, PhotoPath = path });
            await _db.SaveChangesAsync();

            return Ok(new { success = true, message = "Face enrolled." });
        }

        [HttpGet("history")]
        public async Task<IActionResult> History(int? year, int? month)
        {
            int y = year ?? DateTime.Today.Year, m = month ?? DateTime.Today.Month;
            var fromStr = new DateTime(y, m, 1).ToString("yyyy-MM-dd");
            var toStr = new DateTime(y, m, DateTime.DaysInMonth(y, m)).ToString("yyyy-MM-dd");

            var rows = await _db.AttendanceDailies
                .Where(d => d.EmployeeId == CurrentEmpId && string.Compare(d.Date, fromStr) >= 0 && string.Compare(d.Date, toStr) <= 0)
                .OrderBy(d => d.Date).ToListAsync();

            return Ok(rows.Select(d => new
            {
                date = d.Date,
                inTime = d.InTime?.ToString(@"hh\:mm"),
                outTime = d.OutTime?.ToString(@"hh\:mm"),
                status = d.EffectiveStatus,
                workedHours = d.WorkedMinutes.HasValue ? Math.Round(d.WorkedMinutes.Value / 60m, 2) : (decimal?)null,
                otHours = d.OTHours,
            }));
        }

        [HttpGet("ot")]
        public async Task<IActionResult> Ot(int? year, int? month)
        {
            int y = year ?? DateTime.Today.Year, m = month ?? DateTime.Today.Month;
            var fromStr = new DateTime(y, m, 1).ToString("yyyy-MM-dd");
            var toStr = new DateTime(y, m, DateTime.DaysInMonth(y, m)).ToString("yyyy-MM-dd");

            var rows = await _db.AttendanceDailies
                .Where(d => d.EmployeeId == CurrentEmpId && string.Compare(d.Date, fromStr) >= 0 && string.Compare(d.Date, toStr) <= 0
                    && d.OTHours.HasValue && d.OTHours.Value > 0)
                .OrderBy(d => d.Date).ToListAsync();

            return Ok(new
            {
                totalOTHours = rows.Sum(d => d.OTHours ?? 0),
                days = rows.Select(d => new { date = d.Date, otHours = d.OTHours, otRule = d.OTRule }),
            });
        }
    }
}
