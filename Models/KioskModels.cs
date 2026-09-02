using System.ComponentModel.DataAnnotations;

namespace AmpmHrmsPro.Models
{
    // ═══════════════════════════════════════════
    // KIOSK DEVICE — a shared Android tablet/phone set up as a walk-up
    // "Attendance Machine" (Admin > Attendance > Kiosk Devices). It is not
    // logged in as any one employee, so it can't use the per-employee JWT
    // the regular mobile app uses (see MobileAttendanceController). Instead
    // each device is issued its own long-lived ApiKey here, which the kiosk
    // app sends on every call in an "X-Kiosk-Key" header — see
    // KioskAttendanceController for how that's checked. Revoking access is
    // just flipping IsActive off, same pattern as every other "in use" flag
    // in this app, rather than deleting the row (keeps punch history's
    // DeviceId label meaningful).
    // ═══════════════════════════════════════════
    public class KioskDevice
    {
        [Key] public int Id { get; set; }

        [Required, MaxLength(80)] public string DeviceName { get; set; } = ""; // e.g. "Main Gate", "Floor 3 Entrance"
        [MaxLength(120)] public string? LocationLabel { get; set; }

        [Required, MaxLength(64)] public string ApiKey { get; set; } = "";
        public bool IsActive { get; set; } = true;

        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime? LastSeenAt { get; set; } // updated on every successful call — lets Admin see which kiosks are actually in use
    }
}
