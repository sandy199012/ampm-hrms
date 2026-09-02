using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AmpmHrmsPro.Data;
using AmpmHrmsPro.Models;
using AmpmHrmsPro.Services;

namespace AmpmHrmsPro.Controllers.Api
{
    // ═══════════════════════════════════════════
    // KIOSK ATTENDANCE — the walk-up "Attendance Machine" API. A kiosk
    // tablet is not logged in as any one employee, so it can't use the
    // per-employee JWT the regular mobile app uses (MobileAttendanceController).
    // Instead every call carries the device's own long-lived key in an
    // "X-Kiosk-Key" header, checked against KioskDevices below — deliberately
    // a plain inline check rather than a new [Authorize] scheme, matching
    // this codebase's preference for straightforward hand-rolled code over
    // extra framework machinery for a single, simple credential type.
    //
    // Punch() (below) does 1:N face IDENTIFICATION server-side by looping
    // the 1:1-only IFaceMatchService.VerifyAsync once per enrolled employee
    // — see its perf note. That path depends on Admin > Attendance > Face
    // Match API being configured with a working vendor (self-hosted or
    // cloud), which turned out unreliable enough on this deployment that
    // the kiosk app was switched to identify faces ON THE DEVICE instead
    // (Employees()/Enroll()/PunchIdentified() below) — the kiosk downloads
    // the employee roster, enrolls faces locally, matches locally, and
    // only tells the server WHO it already decided this is. Punch() is
    // kept as-is (unused by the current kiosk app, but harmless) in case
    // server-side matching is revisited later. Either way, every punch
    // created lands in the SAME AttendancePunch table the regular mobile
    // app and biometric-machine sync use (Source = "Kiosk"), so it flows
    // through the exact same AttendanceEngine.RecomputeDayAsync — the
    // "however many times a face gets punched, only the first In and the
    // last Out that day count" behavior the kiosk needs already lives there
    // (see AttendanceEngine.ResolveInOut) and needed no changes here.
    // ═══════════════════════════════════════════
    [ApiController]
    [Route("api/kiosk")]
    public class KioskAttendanceController : ControllerBase
    {
        readonly AppDbContext _db;
        readonly IFaceMatchService _faceMatch;
        public KioskAttendanceController(AppDbContext db, IFaceMatchService faceMatch) { _db = db; _faceMatch = faceMatch; }

        async Task<KioskDevice?> AuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-Kiosk-Key", out var key) || string.IsNullOrWhiteSpace(key))
                return null;
            var device = await _db.KioskDevices.FirstOrDefaultAsync(d => d.ApiKey == key.ToString() && d.IsActive);
            return device;
        }

        // Called by the kiosk app's setup screen to confirm the server URL
        // and device key are both correct before the device is put into use.
        [HttpGet("ping")]
        public async Task<IActionResult> Ping()
        {
            var device = await AuthenticateAsync();
            if (device == null) return Unauthorized(new { message = "Invalid or inactive kiosk key." });

            device.LastSeenAt = DateTime.Now;
            await _db.SaveChangesAsync();

            return Ok(new { deviceName = device.DeviceName, locationLabel = device.LocationLabel });
        }

        // ═══════════════════════════════════════════
        // ROSTER SYNC — the kiosk app calls this to build/refresh its local
        // "pick an employee to enroll" list. Deliberately does NOT include
        // any photo here: enrollment always uses a FRESH photo captured at
        // the kiosk itself (see Enroll() below), not whatever's already on
        // file, so this stays a light, fast call even at a few hundred
        // employees.
        // ═══════════════════════════════════════════
        [HttpGet("employees")]
        public async Task<IActionResult> Employees()
        {
            var device = await AuthenticateAsync();
            if (device == null) return Unauthorized(new { message = "Invalid or inactive kiosk key." });

            var employees = await _db.Employees
                .Where(e => e.IsActive)
                .Include(e => e.Department)
                .Include(e => e.Designation)
                .OrderBy(e => e.Name)
                .Select(e => new
                {
                    id = e.Id,
                    empCode = e.EmpCode,
                    name = e.Name,
                    department = e.Department != null ? e.Department.Name : null,
                    designation = e.Designation != null ? e.Designation.Name : null,
                })
                .ToListAsync();

            return Ok(employees);
        }

        // ═══════════════════════════════════════════
        // ON-DEVICE ENROLLMENT — the kiosk computes and stores the actual
        // face embedding itself (see the kiosk app's face_ml_service.dart);
        // this endpoint just keeps a server-side copy of the enrollment
        // photo for HR's own reference/audit (Employee Master's Face
        // Recognition tab) and as a backup if the kiosk is ever replaced.
        // Writes to the SAME FaceProfile table Employee Master's own
        // upload and the mobile app's self-enrollment use (same
        // retire-old-add-new pattern) — none of those three paths conflict.
        // ═══════════════════════════════════════════
        [HttpPost("enroll")]
        public async Task<IActionResult> Enroll([FromForm] int employeeId, IFormFile? photo)
        {
            var device = await AuthenticateAsync();
            if (device == null) return Unauthorized(new { message = "Invalid or inactive kiosk key." });
            if (photo == null || photo.Length == 0) return BadRequest(new { message = "A photo is required." });

            var emp = await _db.Employees.FindAsync(employeeId);
            if (emp == null) return NotFound(new { message = "Employee not found." });

            var existing = await _db.FaceProfiles.Where(f => f.EmployeeId == employeeId && f.IsActive).ToListAsync();
            foreach (var f in existing) f.IsActive = false; // keep history, just retire it — same pattern as EnrollFace/UploadEmployeeFace

            var path = await FileStorageHelper.SavePhotoAsync(photo, "faces");
            _db.FaceProfiles.Add(new FaceProfile { EmployeeId = employeeId, PhotoPath = path });

            device.LastSeenAt = DateTime.Now;
            await _db.SaveChangesAsync();

            return Ok(new { success = true, employeeName = emp.Name });
        }

        // ═══════════════════════════════════════════
        // IDENTIFIED PUNCH — the kiosk already decided WHO this is on-device
        // (local face match against its locally-enrolled set); this just
        // records the punch, using the exact same direction-lookup +
        // AttendancePunch + AttendanceEngine.RecomputeDayAsync flow as
        // Punch() above, minus the whole server-side 1:N search loop since
        // identification already happened. [confidence] is whatever
        // similarity score the kiosk's own on-device match produced —
        // stored purely for visibility on the Attendance Register, it does
        // not get re-checked here.
        // ═══════════════════════════════════════════
        [HttpPost("punch-identified")]
        public async Task<IActionResult> PunchIdentified([FromForm] int employeeId, [FromForm] double? confidence, IFormFile? photo)
        {
            var device = await AuthenticateAsync();
            if (device == null) return Unauthorized(new { message = "Invalid or inactive kiosk key." });

            var employee = await _db.Employees.FindAsync(employeeId);
            if (employee == null || !employee.IsActive) return NotFound(new { message = "Employee not found or inactive." });

            device.LastSeenAt = DateTime.Now;

            string? photoPath = null;
            if (photo != null && photo.Length > 0)
                photoPath = await FileStorageHelper.SavePhotoAsync(photo, "kiosk-punches");

            var lastPunch = await _db.AttendancePunches
                .Where(p => p.EmployeeId == employee.Id && p.PunchDateTime.Date == DateTime.Today)
                .OrderByDescending(p => p.PunchDateTime)
                .FirstOrDefaultAsync();
            var direction = lastPunch == null || lastPunch.Direction == "Out" ? "In" : "Out";

            var punch = new AttendancePunch
            {
                EmployeeId = employee.Id,
                PunchDateTime = DateTime.Now,
                Direction = direction,
                Source = "Kiosk",
                DeviceId = device.DeviceName.Length > 30 ? device.DeviceName.Substring(0, 30) : device.DeviceName,
                PhotoPath = photoPath,
                FaceMatched = true,
                FaceMatchConfidence = confidence.HasValue ? (decimal?)confidence.Value : null,
            };
            _db.AttendancePunches.Add(punch);
            await _db.SaveChangesAsync();

            await AttendanceEngine.RecomputeDayAsync(_db, employee.Id, DateTime.Today);

            return Ok(new
            {
                recognized = true,
                employeeName = employee.Name,
                empCode = employee.EmpCode,
                direction,
                punchTime = punch.PunchDateTime.ToString("hh:mm tt"),
            });
        }

        // multipart/form-data: photo (file, required), latitude/longitude (optional)
        [HttpPost("punch")]
        public async Task<IActionResult> Punch([FromForm] double? latitude, [FromForm] double? longitude, IFormFile? photo)
        {
            var device = await AuthenticateAsync();
            if (device == null) return Unauthorized(new { message = "Invalid or inactive kiosk key." });
            if (photo == null || photo.Length == 0) return BadRequest(new { message = "A photo is required." });

            device.LastSeenAt = DateTime.Now;
            await _db.SaveChangesAsync();

            var livePath = await FileStorageHelper.SavePhotoAsync(photo, "kiosk-punches");
            var liveBytes = FileStorageHelper.ReadBytes(livePath);
            if (liveBytes.Length == 0)
                return BadRequest(new { message = "Could not read the captured photo — please try again." });

            // ── 1:N identification ──────────────────────────────────────
            // PERFORMANCE NOTE: the configured Face Match API is strictly
            // 1:1 (see FaceMatchService — it compares exactly two photos and
            // returns a similarity score). There is no true 1:N "who is
            // this" search available, so this loops one external API call
            // per active enrolled employee and keeps whichever comparison
            // both matched and scored highest. That's fine at the headcount
            // this app is built for, but it does mean punch time grows
            // roughly linearly with headcount — if that ever becomes
            // noticeable, the fix is a vendor with real 1:N face search
            // (or a local embedding cache), not a change to this loop's
            // logic.
            var profiles = await _db.FaceProfiles
                .Where(f => f.IsActive)
                .Include(f => f.Employee)
                .ToListAsync();

            FaceProfile? bestMatch = null;
            decimal bestConfidence = 0;
            foreach (var profile in profiles)
            {
                if (profile.Employee == null || !profile.Employee.IsActive) continue;
                var enrolledBytes = FileStorageHelper.ReadBytes(profile.PhotoPath);
                if (enrolledBytes.Length == 0) continue;

                var (matched, confidence, _) = await _faceMatch.VerifyAsync(enrolledBytes, liveBytes);
                if (matched && confidence > bestConfidence)
                {
                    bestMatch = profile;
                    bestConfidence = confidence;
                }
            }

            if (bestMatch?.Employee == null)
            {
                return Ok(new
                {
                    recognized = false,
                    message = "Face not recognized. Please try again, or contact HR if your face hasn't been enrolled yet.",
                });
            }

            var employee = bestMatch.Employee;

            var lastPunch = await _db.AttendancePunches
                .Where(p => p.EmployeeId == employee.Id && p.PunchDateTime.Date == DateTime.Today)
                .OrderByDescending(p => p.PunchDateTime)
                .FirstOrDefaultAsync();
            var direction = lastPunch == null || lastPunch.Direction == "Out" ? "In" : "Out";

            var punch = new AttendancePunch
            {
                EmployeeId = employee.Id,
                PunchDateTime = DateTime.Now,
                Direction = direction,
                Source = "Kiosk",
                // AttendancePunch.DeviceId is MaxLength(30) — DeviceName can be
                // up to 80, so clip it rather than risk a SQL truncation error.
                DeviceId = device.DeviceName.Length > 30 ? device.DeviceName.Substring(0, 30) : device.DeviceName,
                Latitude = latitude,
                Longitude = longitude,
                PhotoPath = livePath,
                FaceMatched = true,
                FaceMatchConfidence = bestConfidence,
            };
            _db.AttendancePunches.Add(punch);
            await _db.SaveChangesAsync();

            await AttendanceEngine.RecomputeDayAsync(_db, employee.Id, DateTime.Today);

            return Ok(new
            {
                recognized = true,
                employeeName = employee.Name,
                empCode = employee.EmpCode,
                direction,
                confidence = bestConfidence,
                punchTime = punch.PunchDateTime.ToString("hh:mm tt"),
            });
        }
    }
}
