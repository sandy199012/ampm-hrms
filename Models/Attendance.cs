using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AmpmHrmsPro.Models
{
    // ═══════════════════════════════════════════
    // ATTENDANCE PUNCH — the raw ledger. One row per in/out event captured
    // by the biometric device (via API sync) or entered through the manual
    // Excel import. Nothing here is ever recomputed or overwritten — it is
    // the source of truth that AttendanceDaily is derived from, so a wrong
    // daily status can always be re-derived by recomputing from these.
    // ═══════════════════════════════════════════
    public class AttendancePunch
    {
        [Key] public int Id { get; set; }

        public int EmployeeId { get; set; }
        [ForeignKey("EmployeeId")] public Employee? Employee { get; set; }

        public DateTime PunchDateTime { get; set; }

        // In, Out, or Unknown — many biometric APIs/machines don't send an
        // explicit direction, only a raw timestamp; when Unknown, the daily
        // computation treats the earliest punch of the day as In and the
        // latest as Out (which is what the source report's data implies).
        [MaxLength(10)] public string Direction { get; set; } = "Unknown";

        [MaxLength(30)] public string? DeviceId { get; set; }
        [MaxLength(20)] public string Source { get; set; } = "Manual"; // BiometricApi, Manual, ExcelImport, MobileApp
        public DateTime SyncedAt { get; set; } = DateTime.Now;

        // ── Mobile App punch metadata — set only when Source = MobileApp ──
        public double? Latitude { get; set; }
        public double? Longitude { get; set; }
        [MaxLength(300)] public string? LocationAddress { get; set; } // reverse-geocoded label, if the app resolves one — optional, never required
        [MaxLength(300)] public string? PhotoPath { get; set; }        // the live selfie captured at punch time, stored under wwwroot/uploads/punches
        public bool? FaceMatched { get; set; }                         // result of FaceMatchService.VerifyAsync at punch time — null when face matching wasn't run (e.g. disabled, or a non-mobile punch)
        public decimal? FaceMatchConfidence { get; set; }              // 0–100, whatever FaceMatchService returned
    }

    // ═══════════════════════════════════════════
    // ATTENDANCE DAILY — one computed row per employee per calendar date.
    // This single table is what drives both the Attendance Register (status
    // codes) and the OT Daily Register (in/out + OT calc) sheets in the
    // target report, so both are just different views/filters over it.
    //
    // RawStatus = what the punches alone say happened (before any
    // application is applied) — Employee applied for Regularisation but
    // it's still Pending? RawStatus stays "A (MIS)". Once approved,
    // EffectiveStatus flips to "P" while RawStatus still remembers the
    // original mispunch — this is exactly the two-field behavior the
    // uploaded report's color/text mismatch on ~9 cells revealed.
    // ═══════════════════════════════════════════
    public class AttendanceDaily
    {
        [Key] public int Id { get; set; }

        public int EmployeeId { get; set; }
        [ForeignKey("EmployeeId")] public Employee? Employee { get; set; }

        [Required, MaxLength(10)] public string Date { get; set; } = ""; // YYYY-MM-DD, matches the rest of the app's date convention

        public TimeSpan? InTime { get; set; }
        public TimeSpan? OutTime { get; set; }

        // P, A, HD, A (MIS), WO, POW, POW (MIS) — the base code before any
        // application changes it (see class remarks above).
        [Required, MaxLength(20)] public string RawStatus { get; set; } = "A";

        // Same code set as RawStatus, but reflects the day AFTER any
        // approved Leave/Regularisation/WFH/OD application is applied —
        // this is what actually counts toward Present/Absent/Leave totals
        // and what the Attendance Register cell TEXT shows.
        [Required, MaxLength(20)] public string EffectiveStatus { get; set; } = "A";

        public bool WasHoliday { get; set; } = false;
        public bool WasWeekOff { get; set; } = false;

        public int? WorkedMinutes { get; set; }

        // ── OT calculation results (populated by OTEngine; null when no OT was credited that day) ──
        public int? ExtraMinutes { get; set; }
        [MaxLength(200)] public string? OTRule { get; set; }   // human-readable rule text, e.g. "Eve 126min → 120min"
        public decimal? OTHours { get; set; }
        public bool IsRetailOT { get; set; } = false;          // true = retail (In+9h) slab was used, false = non-retail (after shift end) slab

        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime UpdatedAt { get; set; } = DateTime.Now;
    }

    // ═══════════════════════════════════════════
    // BIOMETRIC API SETTINGS — a single configurable row (Admin > Attendance
    // > API Settings). Deliberately vendor-agnostic: every biometric/eSSL/
    // ZKTeco/Matrix/Realtime/Suprema-style "pull punches" API returns some
    // flavor of a JSON list of punch records, but the exact envelope shape,
    // field names, date/time format, and auth header differ per vendor —
    // every one of those is configurable here so the SAME sync code maps
    // to whichever machine/API the company is actually using, without a
    // rebuild. No vendor-specific code should ever be hardcoded against
    // this — see Services/BiometricSyncService.cs.
    // ═══════════════════════════════════════════
    public class BiometricApiSettings
    {
        [Key] public int Id { get; set; }
        public bool IsEnabled { get; set; } = false;

        [MaxLength(300)] public string? BaseUrl { get; set; }       // e.g. https://device.vendor.com/api/punches?from={from}&to={to} — {from}/{to} tokens are substituted with yyyy-MM-dd
        [MaxLength(20)] public string HttpMethod { get; set; } = "GET"; // GET or POST
        [MaxLength(2000)] public string? RequestBodyTemplate { get; set; } // used when HttpMethod = POST; supports {from}/{to} tokens
        [MaxLength(300)] public string? ApiKey { get; set; }
        [MaxLength(60)] public string? AuthHeaderName { get; set; } = "Authorization"; // e.g. "Authorization" (sent as "Bearer <key>") or a custom header some vendors use, e.g. "X-Api-Key" (sent as the raw key)
        [MaxLength(20)] public string AuthScheme { get; set; } = "Bearer"; // Bearer, Raw (no scheme prefix), Basic

        // Where the punch array lives inside the JSON response — dot path,
        // e.g. "" (response IS the array), "data", "result.records". Lets
        // this handle both a bare array and a wrapped envelope.
        [MaxLength(120)] public string? ResponseArrayPath { get; set; }

        // JSON field-name mapping (dot paths supported for nested fields,
        // e.g. "employee.code") — lets the same generic sync code handle
        // completely different vendors' payload shapes.
        [MaxLength(60)] public string EmployeeCodeField { get; set; } = "EmployeeCode";

        // Either one combined datetime field, OR a separate date + time
        // field pair — set whichever the vendor actually sends and leave
        // the other blank.
        [MaxLength(60)] public string? PunchDateTimeField { get; set; } = "PunchDateTime";
        [MaxLength(60)] public string? PunchDateField { get; set; }
        [MaxLength(60)] public string? PunchTimeField { get; set; }
        [MaxLength(60)] public string? DateTimeFormat { get; set; } // .NET format string, e.g. "dd-MM-yyyy HH:mm:ss" — blank = auto-detect common formats

        [MaxLength(60)] public string? DirectionField { get; set; } = "Direction"; // optional — leave blank if the vendor doesn't send one; when blank, earliest punch of the day = In, latest = Out
        [MaxLength(60)] public string? InDirectionValue { get; set; } = "In";   // what value in DirectionField means "in" (e.g. "0", "IN", "CheckIn")
        [MaxLength(60)] public string? OutDirectionValue { get; set; } = "Out"; // what value means "out" (e.g. "1", "OUT", "CheckOut")
        [MaxLength(60)] public string? DeviceIdField { get; set; } = "DeviceId";

        public int SyncIntervalMinutes { get; set; } = 15;

        public DateTime? LastSyncAt { get; set; }
        [MaxLength(20)] public string? LastSyncStatus { get; set; } // Success, Failed, Never
        [MaxLength(1000)] public string? LastSyncMessage { get; set; }
        [MaxLength(4000)] public string? LastSampleResponse { get; set; } // raw snippet of the last response received — helps Admin verify/fix the field mapping without needing server log access
    }

    // ═══════════════════════════════════════════
    // ATTENDANCE IMPORT PROFILE — the file-based equivalent of the API
    // field-mapping above, for when attendance arrives as a machine-
    // exported Excel/CSV instead of (or in addition to) a live API. Every
    // biometric vendor's export layout is different — some give one row
    // per punch, some give one row per employee-per-day with several punch-
    // time columns side by side — so instead of hardcoding one file
    // format, the Admin maps their file's actual column headers to our
    // fields ONCE (via BulkAttendanceImport's mapping step) and the
    // mapping is saved here for every future month's import to reuse.
    // ═══════════════════════════════════════════
    public class AttendanceImportProfile
    {
        [Key] public int Id { get; set; }
        [Required, MaxLength(80)] public string Name { get; set; } = ""; // e.g. "eSSL Monthly Export"

        // PerDayInOut = each row is one employee's whole day with EXPLICIT
        // Punch-In Date/Time and Punch-Out Date/Time columns — the most
        // common real biometric export shape, and the only one that can
        // tell a missing-in-punch apart from a missing-out-punch directly
        // from the source data (no guessing from ambiguous ordering).
        // PerPunchRow = each row is one punch (EmployeeCode + DateTime [+ Direction]).
        // PerDayMultiPunch = each row is one employee's whole day, with
        // several UNLABELED punch-time columns side by side (first
        // populated = In, last populated = Out) — the fallback for files
        // that don't separate In/Out into their own columns.
        [Required, MaxLength(20)] public string Format { get; set; } = "PerDayInOut";

        [Required, MaxLength(80)] public string EmployeeCodeColumn { get; set; } = "";
        [MaxLength(80)] public string? DateColumn { get; set; }          // PerDayInOut: the In Date column. PerPunchRow: the date column when Date/Time are separate.
        [MaxLength(80)] public string? TimeColumn { get; set; }          // PerDayInOut: the In Time column. PerPunchRow: the time column when Date/Time are separate.
        [MaxLength(80)] public string? OutDateColumn { get; set; }       // PerDayInOut only — the Out Date column; blank = same date as DateColumn (same-day out)
        [MaxLength(80)] public string? OutTimeColumn { get; set; }       // PerDayInOut only — the Out Time column
        [MaxLength(80)] public string? DateTimeColumn { get; set; }      // PerPunchRow only, when one combined column
        [MaxLength(80)] public string? DirectionColumn { get; set; }     // PerPunchRow only, optional
        [MaxLength(400)] public string? PunchColumnsCsv { get; set; }    // PerDayMultiPunch only — ordered, comma-separated column headers holding punch times

        [MaxLength(40)] public string? DateFormat { get; set; }  // e.g. "dd-MM-yyyy" — blank = auto-detect
        [MaxLength(40)] public string? TimeFormat { get; set; }  // e.g. "HH:mm:ss" — blank = auto-detect

        public bool IsDefault { get; set; } = false;
        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }
}
