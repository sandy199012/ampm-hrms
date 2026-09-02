using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AmpmHrmsPro.Models
{
    // ═══════════════════════════════════════════
    // FACE PROFILE — one enrolled reference photo per employee, captured
    // once (Profile screen in the mobile app, or an Admin-side enrollment
    // screen), used by FaceMatchService to verify every later punch. Kept
    // as its own table rather than an Employee column so re-enrollment
    // keeps history (IsActive flips the old one off) instead of losing it.
    // ═══════════════════════════════════════════
    public class FaceProfile
    {
        [Key] public int Id { get; set; }

        public int EmployeeId { get; set; }
        [ForeignKey("EmployeeId")] public Employee? Employee { get; set; }

        [Required, MaxLength(300)] public string PhotoPath { get; set; } = ""; // under wwwroot/uploads/faces
        public bool IsActive { get; set; } = true;
        public DateTime EnrolledAt { get; set; } = DateTime.Now;
    }

    // ═══════════════════════════════════════════
    // FACE MATCH API SETTINGS — deliberately vendor-agnostic, same
    // philosophy as BiometricApiSettings (see Attendance.cs): whichever
    // face-recognition vendor is actually used (Azure Face API, AWS
    // Rekognition, a self-hosted model behind a REST endpoint, ...) is
    // configured here rather than hardcoded, so FaceMatchService's code
    // never changes when the vendor does. A single row, same pattern as
    // BiometricApiSettings.
    // ═══════════════════════════════════════════
    public class FaceMatchApiSettings
    {
        [Key] public int Id { get; set; }
        public bool IsEnabled { get; set; } = false;

        [MaxLength(300)] public string? VerifyUrl { get; set; }        // POST endpoint that compares two face images and returns a similarity score
        [MaxLength(300)] public string? ApiKey { get; set; }
        [MaxLength(60)] public string? AuthHeaderName { get; set; } = "Ocp-Apim-Subscription-Key"; // Azure Face API's header name by default — change per vendor
        [MaxLength(20)] public string AuthScheme { get; set; } = "Raw"; // Raw (no "Bearer " prefix), Bearer

        // Field names in the vendor's JSON response — dot paths supported.
        [MaxLength(120)] public string? ConfidenceField { get; set; } = "confidence"; // 0–1 or 0–100 depending on vendor — see ConfidenceIsFraction
        public bool ConfidenceIsFraction { get; set; } = true; // true: vendor returns 0.0–1.0 (multiply by 100); false: vendor already returns 0–100
        [MaxLength(120)] public string? IsIdenticalField { get; set; } // optional — some vendors return a bool "isIdentical" alongside/instead of a score

        public decimal MinConfidencePercent { get; set; } = 80m; // below this, VerifyAsync reports no match regardless of what the vendor says

        public DateTime? LastTestAt { get; set; }
        [MaxLength(20)] public string? LastTestStatus { get; set; } // Success, Failed, Never
        [MaxLength(1000)] public string? LastTestMessage { get; set; }
    }

    // ═══════════════════════════════════════════
    // NOTIFICATION — in-app only for now (no push-service credentials are
    // configured yet — see FaceMatchApiSettings' sibling reasoning; when
    // the company sets up Firebase or another push service, sending a
    // push alongside every row created here is a small addition to
    // NotificationService, not a redesign). Raised whenever something an
    // employee or their manager should know about happens: an
    // application's status changes, a reminder, a general announcement.
    // ═══════════════════════════════════════════
    public class Notification
    {
        [Key] public int Id { get; set; }

        public int EmployeeId { get; set; } // who sees this notification
        [ForeignKey("EmployeeId")] public Employee? Employee { get; set; }

        [Required, MaxLength(120)] public string Title { get; set; } = "";
        [MaxLength(500)] public string? Message { get; set; }
        [MaxLength(30)] public string Type { get; set; } = "General"; // General, Leave, Regularisation, WFH, OD, Approval, Attendance

        // Optional deep-link target — lets the mobile app jump straight to
        // the relevant Application when the employee taps the notification.
        public int? RelatedApplicationId { get; set; }
        [ForeignKey("RelatedApplicationId")] public Application? RelatedApplication { get; set; }

        public bool IsRead { get; set; } = false;
        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }
}
