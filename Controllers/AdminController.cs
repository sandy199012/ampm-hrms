using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using AmpmHrmsPro.Data;
using AmpmHrmsPro.Models;
using AmpmHrmsPro.Services;

namespace AmpmHrmsPro.Controllers
{
    [Authorize(Roles = "admin,hr")]
    public class AdminController : Controller
    {
        private readonly AppDbContext _db;
        public AdminController(AppDbContext db) => _db = db;

        int CurrentEmpId => int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);

        // ── DASHBOARD ──
        public IActionResult Index()
        {
            ViewBag.TotalEmployees = _db.Employees.Count(e => e.IsActive);
            ViewBag.Departments    = _db.Departments.Count(d => d.IsActive);
            ViewBag.Designations   = _db.Designations.Count(d => d.IsActive);
            ViewBag.Locations      = _db.Locations.Count(l => l.IsActive);
            ViewBag.Shifts         = _db.Shifts.Count(s => s.IsActive);
            ViewBag.LeaveTypes     = _db.LeaveTypes.Count(t => t.IsActive);
            ViewBag.Holidays       = _db.Holidays.Count(h => h.IsActive && h.Date.StartsWith(DateTime.Now.Year.ToString()));

            var today = DateTime.Today;

            // ── Upcoming Holidays — next 5 from today onward. Date is
            // stored as a plain string (yyyy-MM-dd convention, same as
            // everywhere else in this app), so it's parsed in memory rather
            // than compared as a string — a string compare works fine
            // within one year but silently breaks across a year boundary
            // (e.g. "2027-01-01" sorting before "2026-12-25"). ──
            var holidayRows = _db.Holidays.Where(h => h.IsActive).ToList();
            var upcomingHolidays = new List<UpcomingHolidayVm>();
            foreach (var h in holidayRows)
            {
                if (!HrEmailNotificationService.TryParseFlexibleDate(h.Date, out var date)) continue;
                if (date.Date < today) continue;
                upcomingHolidays.Add(new UpcomingHolidayVm { Name = h.Name, Type = h.Type, Date = date, DaysAway = (date.Date - today).Days });
            }
            ViewBag.UpcomingHolidays = upcomingHolidays.OrderBy(x => x.Date).Take(5).ToList();

            // ── Upcoming Birthdays — next 30 days, wrapping across the
            // turn of the year (e.g. a Dec-20 today should still surface a
            // Jan-5 birthday). Same flexible-DOB-format parser as the
            // scheduled birthday email, so a dashboard widget and the
            // actual email never quietly disagree about whose DOB parses. ──
            var employeesWithDob = _db.Employees.Where(e => e.IsActive && e.DOB != null && e.DOB != "")
                .Select(e => new { e.Name, e.EmpCode, e.DOB, DeptName = e.Department != null ? e.Department.Name : null })
                .ToList();
            var upcomingBirthdays = new List<UpcomingBirthdayVm>();
            foreach (var e in employeesWithDob)
            {
                if (!HrEmailNotificationService.TryParseFlexibleDate(e.DOB, out var dob)) continue;
                DateTime next;
                try { next = new DateTime(today.Year, dob.Month, dob.Day); }
                catch (ArgumentOutOfRangeException) { next = new DateTime(today.Year, 2, 28); } // Feb 29 DOB in a non-leap year — observed a day early, same convention most payroll/HR systems use
                if (next < today) next = next.AddYears(1);
                var daysAway = (next.Date - today).Days;
                if (daysAway <= 30) upcomingBirthdays.Add(new UpcomingBirthdayVm { Name = e.Name, EmpCode = e.EmpCode, DeptName = e.DeptName, NextBirthday = next, DaysAway = daysAway });
            }
            ViewBag.UpcomingBirthdays = upcomingBirthdays.OrderBy(x => x.DaysAway).Take(10).ToList();

            // ── New Joinings — last 30 days, most recent first. ──
            var employeesWithDoj = _db.Employees.Where(e => e.IsActive && e.DOJ != null && e.DOJ != "")
                .Select(e => new { e.Name, e.EmpCode, e.DOJ, DeptName = e.Department != null ? e.Department.Name : null })
                .ToList();
            var recentJoinings = new List<RecentJoiningVm>();
            foreach (var e in employeesWithDoj)
            {
                if (!HrEmailNotificationService.TryParseFlexibleDate(e.DOJ, out var doj)) continue;
                var daysAgo = (today - doj.Date).Days;
                if (daysAgo >= 0 && daysAgo <= 30) recentJoinings.Add(new RecentJoiningVm { Name = e.Name, EmpCode = e.EmpCode, DeptName = e.DeptName, DOJ = doj, DaysAgo = daysAgo });
            }
            ViewBag.RecentJoinings = recentJoinings.OrderByDescending(x => x.DOJ).Take(10).ToList();

            return View();
        }

        // ═══════════════════════════════════════════
        // EMPLOYEE MASTER
        // ═══════════════════════════════════════════
        public IActionResult Employees(string? q)
        {
            var query = _db.Employees.Include(e => e.Department).Include(e => e.Designation)
                .Include(e => e.Location).Include(e => e.Shift).Include(e => e.ReportingManager)
                .AsQueryable();
            if (!string.IsNullOrWhiteSpace(q))
                query = query.Where(e => e.Name.Contains(q) || e.EmpCode.Contains(q) || e.Email.Contains(q));
            ViewBag.Query = q;
            return View(query.OrderBy(e => e.EmpCode).ToList());
        }

        void LoadMasterDropdowns()
        {
            ViewBag.DepartmentList     = _db.Departments.Where(d => d.IsActive).OrderBy(d => d.Name).ToList();
            ViewBag.DesignationList    = _db.Designations.Where(d => d.IsActive).OrderBy(d => d.Name).ToList();
            ViewBag.LocationList       = _db.Locations.Where(l => l.IsActive).OrderBy(l => l.Name).ToList();
            ViewBag.GradeList          = _db.Grades.Where(g => g.IsActive).OrderBy(g => g.Name).ToList();
            ViewBag.EmploymentTypeList = _db.EmploymentTypes.Where(t => t.IsActive).OrderBy(t => t.Name).ToList();
            ViewBag.ShiftList          = _db.Shifts.Where(s => s.IsActive).OrderBy(s => s.Name).ToList();
            ViewBag.WeekOffList        = _db.WeekOffPolicies.Where(w => w.IsActive).OrderBy(w => w.Name).ToList();
            ViewBag.LeavePolicyList    = _db.LeavePolicies.Where(p => p.IsActive).OrderBy(p => p.Name).ToList();
            ViewBag.CompOffRuleList    = _db.CompOffRules.Where(r => r.IsActive).OrderBy(r => r.Name).ToList();
            ViewBag.OTRuleList         = _db.OTRules.Where(r => r.IsActive).OrderBy(r => r.Name).ToList();
            ViewBag.ManagerList        = _db.Employees.Where(e => e.IsActive).OrderBy(e => e.Name).ToList();
        }

        public IActionResult AddEmployee()
        {
            LoadMasterDropdowns();
            return View(new Employee());
        }

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult AddEmployee(Employee model, string password)
        {
            if (_db.Employees.Any(e => e.EmpCode == model.EmpCode))
            {
                TempData["Error"] = $"Employee code '{model.EmpCode}' already exists.";
                return RedirectToAction("AddEmployee");
            }
            model.Id = 0;
            model.PasswordHash = BCrypt.Net.BCrypt.HashPassword(string.IsNullOrWhiteSpace(password) ? "Welcome@123" : password);
            model.IsActive = true;
            model.CreatedAt = DateTime.Now;
            if (string.IsNullOrWhiteSpace(model.Status)) model.Status = "Active";
            _db.Employees.Add(model);
            _db.SaveChanges();
            TempData["Success"] = $"Employee '{model.Name}' added. Default password: {(string.IsNullOrWhiteSpace(password) ? "Welcome@123" : password)}";
            return RedirectToAction("Employees");
        }

        public IActionResult EditEmployee(int id)
        {
            var emp = _db.Employees.Find(id);
            if (emp == null) return NotFound();
            LoadMasterDropdowns();
            // Shown on the new "Face Recognition" tab — the same FaceProfile
            // table the mobile app's own self-enrollment (Profile > Enroll
            // Face) writes to, so either path keeps the other in sync.
            ViewBag.FaceProfile = _db.FaceProfiles.FirstOrDefault(f => f.EmployeeId == id && f.IsActive);
            return View(emp);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult EditEmployee(Employee model)
        {
            var existing = _db.Employees.Find(model.Id);
            if (existing == null) return NotFound();

            existing.Name               = model.Name;
            existing.Email               = model.Email;
            existing.Mobile              = model.Mobile;
            existing.Gender              = model.Gender;
            existing.DOB                 = model.DOB;
            existing.Address             = model.Address;
            existing.DOJ                 = model.DOJ;
            existing.Role                = model.Role;
            existing.DepartmentId        = model.DepartmentId;
            existing.DesignationId       = model.DesignationId;
            existing.LocationId          = model.LocationId;
            existing.GradeId             = model.GradeId;
            existing.EmploymentTypeId    = model.EmploymentTypeId;
            existing.ShiftId             = model.ShiftId;
            existing.WeekOffPolicyId     = model.WeekOffPolicyId;
            existing.LeavePolicyId       = model.LeavePolicyId;
            existing.CompOffRuleId       = model.CompOffRuleId;
            existing.OTRuleId            = model.OTRuleId;
            existing.ReportingManagerId  = model.ReportingManagerId == existing.Id ? null : model.ReportingManagerId; // can't manage yourself
            existing.Status              = model.Status;
            existing.IsActive            = model.Status == "Active";
            CopyExtendedProfile(model, existing);
            _db.SaveChanges();
            TempData["Success"] = $"'{existing.Name}' updated.";
            return RedirectToAction("Employees");
        }

        // ═══════════════════════════════════════════
        // FACE RECOGNITION PHOTO — lets HR upload/replace an employee's
        // reference face straight from Employee Master, instead of relying
        // only on the employee self-enrolling from their own phone (Profile
        // > Enroll Face in the mobile app). This matters most for exactly
        // the workforce the Attendance Kiosk is for — factory-floor staff
        // who may not carry the mobile app on a personal phone at all, so
        // self-enrollment alone would leave them permanently unable to be
        // recognized. Writes to the SAME FaceProfile table
        // MobileAttendanceController.EnrollFace uses (same retire-old-
        // enroll-new pattern), so both paths stay interchangeable — HR can
        // enroll someone here today and the employee can re-enroll from
        // their own phone later without conflict.
        // ═══════════════════════════════════════════
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> UploadEmployeeFace(int employeeId, IFormFile photo)
        {
            var emp = await _db.Employees.FindAsync(employeeId);
            if (emp == null) return NotFound();

            if (photo == null || photo.Length == 0)
            {
                TempData["Error"] = "Please choose a photo first.";
                return RedirectToAction("EditEmployee", new { id = employeeId });
            }

            var existing = await _db.FaceProfiles.Where(f => f.EmployeeId == employeeId && f.IsActive).ToListAsync();
            foreach (var f in existing) f.IsActive = false; // keep history, just retire it — same pattern as the mobile app's own EnrollFace

            var path = await FileStorageHelper.SavePhotoAsync(photo, "faces");
            _db.FaceProfiles.Add(new FaceProfile { EmployeeId = employeeId, PhotoPath = path });
            await _db.SaveChangesAsync();

            TempData["Success"] = $"Face photo saved for {emp.Name} — ready for Face Match Attendance (kiosk + mobile punches).";
            return RedirectToAction("EditEmployee", new { id = employeeId });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> RemoveEmployeeFace(int employeeId)
        {
            var existing = await _db.FaceProfiles.Where(f => f.EmployeeId == employeeId && f.IsActive).ToListAsync();
            foreach (var f in existing) f.IsActive = false;
            await _db.SaveChangesAsync();
            TempData["Success"] = "Enrolled face removed. Punches for this employee won't be face-verified until a new one is enrolled.";
            return RedirectToAction("EditEmployee", new { id = employeeId });
        }

        // Copies every "Extended Profile" field (the ones matching the
        // company's HR export format) from a posted/imported model onto the
        // tracked entity. Shared by EditEmployee and the bulk importer so
        // both stay in sync as fields are added.
        static void CopyExtendedProfile(Employee src, Employee dest)
        {
            dest.FatherOrHusbandName    = src.FatherOrHusbandName;
            dest.AlternateMobile        = src.AlternateMobile;
            dest.Nationality            = src.Nationality;
            dest.Religion               = src.Religion;
            dest.Qualification          = src.Qualification;
            dest.Country                = src.Country;
            dest.State                  = src.State;
            dest.City                   = src.City;
            dest.Pincode                = src.Pincode;
            dest.GroupDOJ               = src.GroupDOJ;
            dest.CardNumber             = src.CardNumber;
            dest.CompanyCode            = src.CompanyCode;
            dest.EmployeeRole           = src.EmployeeRole;
            dest.WorkStation            = src.WorkStation;
            dest.Category               = src.Category;
            dest.SubDepartment          = src.SubDepartment;
            dest.AdditionalShifts       = src.AdditionalShifts;
            dest.ValidFrom              = src.ValidFrom;
            dest.ValidTo                = src.ValidTo;
            dest.DateOfLeaving          = src.DateOfLeaving;
            dest.InactivationDate       = src.InactivationDate;
            dest.Experience             = src.Experience;
            dest.StandardWorkingHour    = src.StandardWorkingHour;
            dest.IsAutoShift            = src.IsAutoShift;
            dest.IsAutoInactive         = src.IsAutoInactive;
            dest.CompanyPFCode          = src.CompanyPFCode;
            dest.BankName               = src.BankName;
            dest.AccountNumber          = src.AccountNumber;
            dest.AccountHolderName      = src.AccountHolderName;
            dest.IFSCCode               = src.IFSCCode;
            dest.UANNumber              = src.UANNumber;
            dest.AadharNumber           = src.AadharNumber;
            dest.PFNumber               = src.PFNumber;
            dest.ESICNumber             = src.ESICNumber;
            dest.PANNumber              = src.PANNumber;
            dest.IPNumber               = src.IPNumber;
            dest.PaymentMode            = src.PaymentMode;
            dest.EmergencyContactName   = src.EmergencyContactName;
            dest.EmergencyContactMobile = src.EmergencyContactMobile;
            dest.EmergencyContactRelation = src.EmergencyContactRelation;
            dest.LocationType           = src.LocationType;
            dest.MappedLocations        = src.MappedLocations;
            dest.MappedSubLocations     = src.MappedSubLocations;
            dest.PunchInLocation        = src.PunchInLocation;
            dest.AppLogin               = src.AppLogin;
            dest.AttendanceAccessApp    = src.AttendanceAccessApp;
            dest.ActivateCheckIn        = src.ActivateCheckIn;
            dest.Zone                   = src.Zone;
            dest.BusAllowed             = src.BusAllowed;
            dest.BusName                = src.BusName;
            dest.FlexiHours             = src.FlexiHours;
            dest.TrainingGivenBy        = src.TrainingGivenBy;
            dest.TrainingType           = src.TrainingType;
            dest.TrainingUserType       = src.TrainingUserType;
            dest.ExternalTrainerName    = src.ExternalTrainerName;
            dest.ExternalTrainerEmail   = src.ExternalTrainerEmail;
            dest.TrainingDate           = src.TrainingDate;
            dest.TrainingStatus         = src.TrainingStatus;
            dest.Remarks                = src.Remarks;
        }

        [HttpPost]
        public IActionResult ResetPassword(int id)
        {
            var emp = _db.Employees.Find(id);
            if (emp == null) return Json(new { success = false });
            var newPassword = "Welcome@123";
            emp.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword);
            _db.SaveChanges();
            return Json(new { success = true, password = newPassword });
        }

        // ═══════════════════════════════════════════
        // BULK UPLOAD — imports the company's existing HR export format
        // (Employee Code, Employee Name, Manager Name, ... 75 columns) as-is.
        // Matched by header NAME (not column position), so column order
        // doesn't matter and unknown extra columns are simply ignored.
        // Employees are upserted by Employee Code. Department / Designation /
        // Grade / Location / Shift are looked up by name and auto-created if
        // they don't exist yet — Admin can clean these up afterwards from
        // the Masters screens. Manager Name is resolved in a second pass
        // (after every row is saved) so forward references — a manager whose
        // own row appears later in the file — still resolve correctly.
        // ═══════════════════════════════════════════
        public IActionResult BulkUpload() => View();

        public IActionResult DownloadTemplate()
        {
            using var wb = new ClosedXML.Excel.XLWorkbook();
            var ws = wb.Worksheets.Add("Employees");
            for (int i = 0; i < BulkImportColumns.Length; i++)
                ws.Cell(1, i + 1).Value = BulkImportColumns[i];
            ws.Row(1).Style.Font.Bold = true;
            ws.Cell(2, 1).Value = "00001";
            ws.Cell(2, 2).Value = "Sample Employee";
            ws.Columns().AdjustToContents();

            using var stream = new MemoryStream();
            wb.SaveAs(stream);
            return File(stream.ToArray(),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                "AMPM_Employee_Bulk_Upload_Template.xlsx");
        }

        static readonly string[] BulkImportColumns =
        {
            "Employee Code","Employee Name","Manager Name","Email Id","Father's/Husband Name","Address","Mobile",
            "Alternative Mobile","Gender","DOB","DOJ","Group DOJ","Card Number","Company Code","Company PF Code",
            "Bank Name","Account Number","Account Holder Name","IFSC Code","UAN Number","Aadhar Number","Pincode",
            "PF Number","ESIC Number","PAN Number","IP Number","Valid From","Valid To","Role","Work Station",
            "Category","Grade","Department","Sub-Department","Designation","Shift","Additional Shifts",
            "Emergency Contact Name","Emergency Contact Mobile","Emergency Contact Relation","Date of Leaving",
            "Location Type","Mapped Locations","Mapped Sub Locations","Punch In Location","Status","App Login",
            "Attendance Access (App)","Activate Check-in","Training Given By","Training Type","Training User Type",
            "External Trainer Name","External Trainer Email","Training Date","Training Status","Bus Allowed",
            "Bus Name","Zone","Flexi Hours","Nationality","Religion","Payment Mode","Qualification","Country",
            "State","City","Experience","Standred Working Hour","Is Auto Shift","Is Auto Inactive",
            "Inactivation Date","Remarks"
        };

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult BulkUpload(IFormFile file)
        {
            if (file == null || file.Length == 0)
            {
                TempData["Error"] = "Please choose a file to upload.";
                return RedirectToAction("BulkUpload");
            }

            var result = new BulkImportResult();

            try
            {
                using var stream = file.OpenReadStream();
                using var wb = new ClosedXML.Excel.XLWorkbook(stream);
                var ws = wb.Worksheet(1);
                var used = ws.RangeUsed();
                if (used == null) { TempData["Error"] = "That file appears to be empty."; return RedirectToAction("BulkUpload"); }

                int lastRow = used.LastRow().RowNumber();
                int lastCol = used.LastColumn().ColumnNumber();

                // Find the header row — search the first 10 rows for the
                // "Employee Code" column, so a blank title row (as in the
                // company's export) doesn't confuse the import.
                int headerRow = 0;
                for (int r = 1; r <= Math.Min(10, lastRow) && headerRow == 0; r++)
                    for (int c = 1; c <= lastCol; c++)
                        if (string.Equals(ws.Cell(r, c).GetString().Trim(), "Employee Code", StringComparison.OrdinalIgnoreCase))
                        { headerRow = r; break; }

                if (headerRow == 0)
                {
                    TempData["Error"] = "Couldn't find an 'Employee Code' column — please use the template.";
                    return RedirectToAction("BulkUpload");
                }

                var col = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (int c = 1; c <= lastCol; c++)
                {
                    var h = ws.Cell(headerRow, c).GetString().Trim();
                    if (!string.IsNullOrEmpty(h) && !col.ContainsKey(h)) col[h] = c;
                }

                string? S(int r, string header)
                {
                    if (!col.TryGetValue(header, out var c)) return null;
                    var cell = ws.Cell(r, c);
                    if (cell.IsEmpty()) return null;
                    if (cell.DataType == ClosedXML.Excel.XLDataType.DateTime)
                        return cell.GetDateTime().ToString("yyyy-MM-dd");
                    if (cell.DataType == ClosedXML.Excel.XLDataType.Number)
                        return cell.GetDouble().ToString("0", System.Globalization.CultureInfo.InvariantCulture);
                    var v = cell.GetString().Trim();
                    return v.Length == 0 ? null : v;
                }
                bool B(int r, string header) => string.Equals(S(r, header), "Yes", StringComparison.OrdinalIgnoreCase);
                string? Cap(string? s, int max) => s == null ? null : (s.Length > max ? s.Substring(0, max) : s);

                var deptCache  = _db.Departments.ToDictionary(x => x.Name.Trim().ToLower(), x => x, StringComparer.OrdinalIgnoreCase);
                var desigCache = _db.Designations.ToDictionary(x => x.Name.Trim().ToLower(), x => x, StringComparer.OrdinalIgnoreCase);
                var gradeCache = _db.Grades.ToDictionary(x => x.Name.Trim().ToLower(), x => x, StringComparer.OrdinalIgnoreCase);
                var locCache   = _db.Locations.ToDictionary(x => x.Name.Trim().ToLower(), x => x, StringComparer.OrdinalIgnoreCase);
                var shiftCache = _db.Shifts.ToDictionary(x => x.Name.Trim().ToLower(), x => x, StringComparer.OrdinalIgnoreCase);

                Department? FindOrCreateDept(string? name)
                {
                    if (string.IsNullOrWhiteSpace(name)) return null;
                    var key = name.Trim().ToLower();
                    if (deptCache.TryGetValue(key, out var d)) return d;
                    var nd = new Department { Name = Cap(name.Trim(), 80)! };
                    _db.Departments.Add(nd); deptCache[key] = nd; result.MastersCreated.Add($"Department: {nd.Name}");
                    return nd;
                }
                Designation? FindOrCreateDesig(string? name)
                {
                    if (string.IsNullOrWhiteSpace(name)) return null;
                    var key = name.Trim().ToLower();
                    if (desigCache.TryGetValue(key, out var d)) return d;
                    var nd = new Designation { Name = Cap(name.Trim(), 80)! };
                    _db.Designations.Add(nd); desigCache[key] = nd; result.MastersCreated.Add($"Designation: {nd.Name}");
                    return nd;
                }
                Grade? FindOrCreateGrade(string? name)
                {
                    if (string.IsNullOrWhiteSpace(name)) return null;
                    var key = name.Trim().ToLower();
                    if (gradeCache.TryGetValue(key, out var d)) return d;
                    var nd = new Grade { Name = Cap(name.Trim(), 40)! };
                    _db.Grades.Add(nd); gradeCache[key] = nd; result.MastersCreated.Add($"Grade: {nd.Name}");
                    return nd;
                }
                Location? FindOrCreateLoc(string? name)
                {
                    if (string.IsNullOrWhiteSpace(name)) return null;
                    var key = name.Trim().ToLower();
                    if (locCache.TryGetValue(key, out var d)) return d;
                    var nd = new Location { Name = Cap(name.Trim(), 80)! };
                    _db.Locations.Add(nd); locCache[key] = nd; result.MastersCreated.Add($"Location: {nd.Name}");
                    return nd;
                }
                Shift? FindOrCreateShift(string? name)
                {
                    if (string.IsNullOrWhiteSpace(name)) return null;
                    var key = name.Trim().ToLower();
                    if (shiftCache.TryGetValue(key, out var d)) return d;
                    var nd = new Shift { Name = Cap(name.Trim(), 50)! };
                    _db.Shifts.Add(nd); shiftCache[key] = nd; result.MastersCreated.Add($"Shift: {nd.Name}");
                    return nd;
                }

                var pendingManagers = new List<(Employee Employee, string ManagerName)>();

                for (int r = headerRow + 1; r <= lastRow; r++)
                {
                    var empCode = S(r, "Employee Code");
                    var name    = S(r, "Employee Name");
                    if (string.IsNullOrWhiteSpace(empCode) || string.IsNullOrWhiteSpace(name))
                        continue; // blank/trailing row

                    var emp = _db.Employees.Local.FirstOrDefault(e => e.EmpCode == empCode)
                              ?? _db.Employees.FirstOrDefault(e => e.EmpCode == empCode);
                    bool isNew = emp == null;
                    if (isNew)
                    {
                        emp = new Employee
                        {
                            EmpCode = Cap(empCode, 20)!,
                            PasswordHash = BCrypt.Net.BCrypt.HashPassword("Welcome@123"),
                            Role = "employee",
                            CreatedAt = DateTime.Now
                        };
                        _db.Employees.Add(emp);
                    }

                    emp!.Name    = Cap(name, 100)!;
                    // emp.Email defaults to "" (not null) for a brand-new
                    // employee, so a plain ?? chain would never reach the
                    // generated fallback — check for blank explicitly.
                    var importedEmail = Cap(S(r, "Email Id"), 120);
                    emp.Email    = !string.IsNullOrWhiteSpace(importedEmail) ? importedEmail!
                                    : !string.IsNullOrWhiteSpace(emp.Email) ? emp.Email
                                    : $"{empCode}@ampm.in";
                    emp.Mobile   = Cap(S(r, "Mobile"), 15);
                    emp.Gender   = Cap(S(r, "Gender"), 10);
                    emp.DOB      = S(r, "DOB");
                    emp.DOJ      = S(r, "DOJ");
                    emp.Address  = Cap(S(r, "Address"), 200);
                    var status   = S(r, "Status");
                    emp.Status   = string.IsNullOrWhiteSpace(status) ? emp.Status : status!;
                    emp.IsActive = !string.Equals(status, "Inactive", StringComparison.OrdinalIgnoreCase);

                    emp.Department  = FindOrCreateDept(S(r, "Department"));
                    emp.Designation = FindOrCreateDesig(S(r, "Designation"));
                    emp.Grade       = FindOrCreateGrade(S(r, "Grade"));
                    emp.Location    = FindOrCreateLoc(S(r, "Work Station")) ?? emp.Location;
                    emp.Shift       = FindOrCreateShift(S(r, "Shift"));

                    emp.FatherOrHusbandName      = Cap(S(r, "Father's/Husband Name"), 100);
                    emp.AlternateMobile          = Cap(S(r, "Alternative Mobile"), 15);
                    emp.Nationality              = Cap(S(r, "Nationality"), 60);
                    emp.Religion                 = Cap(S(r, "Religion"), 60);
                    emp.Qualification            = Cap(S(r, "Qualification"), 100);
                    emp.Country                  = Cap(S(r, "Country"), 60);
                    emp.State                    = Cap(S(r, "State"), 60);
                    emp.City                     = Cap(S(r, "City"), 60);
                    emp.Pincode                  = Cap(S(r, "Pincode"), 10);
                    emp.GroupDOJ                 = S(r, "Group DOJ");
                    emp.CardNumber               = Cap(S(r, "Card Number"), 40);
                    emp.CompanyCode              = Cap(S(r, "Company Code"), 40);
                    emp.EmployeeRole             = Cap(S(r, "Role"), 40);
                    emp.WorkStation              = Cap(S(r, "Work Station"), 80);
                    emp.Category                 = Cap(S(r, "Category"), 40);
                    emp.SubDepartment            = Cap(S(r, "Sub-Department"), 80);
                    emp.AdditionalShifts         = Cap(S(r, "Additional Shifts"), 120);
                    emp.ValidFrom                = S(r, "Valid From");
                    emp.ValidTo                  = S(r, "Valid To");
                    emp.DateOfLeaving            = S(r, "Date of Leaving");
                    emp.InactivationDate         = S(r, "Inactivation Date");
                    emp.Experience               = Cap(S(r, "Experience"), 30);
                    emp.StandardWorkingHour      = Cap(S(r, "Standred Working Hour"), 20);
                    emp.IsAutoShift              = B(r, "Is Auto Shift");
                    emp.IsAutoInactive           = B(r, "Is Auto Inactive");
                    emp.CompanyPFCode            = Cap(S(r, "Company PF Code"), 60);
                    emp.BankName                 = Cap(S(r, "Bank Name"), 80);
                    emp.AccountNumber            = Cap(S(r, "Account Number"), 30);
                    emp.AccountHolderName        = Cap(S(r, "Account Holder Name"), 100);
                    emp.IFSCCode                 = Cap(S(r, "IFSC Code"), 20);
                    emp.UANNumber                = Cap(S(r, "UAN Number"), 20);
                    emp.AadharNumber             = Cap(S(r, "Aadhar Number"), 20);
                    emp.PFNumber                 = Cap(S(r, "PF Number"), 30);
                    emp.ESICNumber               = Cap(S(r, "ESIC Number"), 30);
                    emp.PANNumber                = Cap(S(r, "PAN Number"), 15);
                    emp.IPNumber                 = Cap(S(r, "IP Number"), 30);
                    emp.PaymentMode              = Cap(S(r, "Payment Mode"), 30);
                    emp.EmergencyContactName     = Cap(S(r, "Emergency Contact Name"), 100);
                    emp.EmergencyContactMobile   = Cap(S(r, "Emergency Contact Mobile"), 15);
                    emp.EmergencyContactRelation = Cap(S(r, "Emergency Contact Relation"), 40);
                    emp.LocationType             = Cap(S(r, "Location Type"), 30);
                    emp.MappedLocations          = Cap(S(r, "Mapped Locations"), 300);
                    emp.MappedSubLocations       = Cap(S(r, "Mapped Sub Locations"), 300);
                    emp.PunchInLocation          = Cap(S(r, "Punch In Location"), 300);
                    emp.AppLogin                 = B(r, "App Login");
                    emp.AttendanceAccessApp      = B(r, "Attendance Access (App)");
                    emp.ActivateCheckIn          = B(r, "Activate Check-in");
                    emp.Zone                     = Cap(S(r, "Zone"), 60);
                    emp.BusAllowed               = B(r, "Bus Allowed");
                    emp.BusName                  = Cap(S(r, "Bus Name"), 80);
                    emp.FlexiHours               = Cap(S(r, "Flexi Hours"), 20);
                    emp.TrainingGivenBy          = Cap(S(r, "Training Given By"), 100);
                    emp.TrainingType             = Cap(S(r, "Training Type"), 60);
                    emp.TrainingUserType         = Cap(S(r, "Training User Type"), 60);
                    emp.ExternalTrainerName      = Cap(S(r, "External Trainer Name"), 100);
                    emp.ExternalTrainerEmail     = Cap(S(r, "External Trainer Email"), 120);
                    emp.TrainingDate             = S(r, "Training Date");
                    emp.TrainingStatus           = Cap(S(r, "Training Status"), 30);
                    emp.Remarks                  = Cap(S(r, "Remarks"), 500);

                    var managerName = S(r, "Manager Name");
                    if (!string.IsNullOrWhiteSpace(managerName))
                        pendingManagers.Add((emp, managerName));

                    if (isNew) result.Created++; else result.Updated++;
                }

                _db.SaveChanges(); // assigns real Ids to new employees + new masters

                var byName = _db.Employees.ToList()
                    .GroupBy(e => e.Name.Trim(), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

                foreach (var (employee, managerName) in pendingManagers)
                {
                    if (byName.TryGetValue(managerName.Trim(), out var mgr) && mgr.Id != employee.Id)
                        employee.ReportingManagerId = mgr.Id;
                    else
                        result.ManagerNotFound.Add($"{employee.Name} → \"{managerName}\"");
                }
                _db.SaveChanges();

                result.Success = true;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Error = ex.Message;
            }

            return View("BulkUploadResult", result);
        }

        // ═══════════════════════════════════════════
        // SIMPLE MASTERS — Department, Designation, Grade, Employment Type
        // ═══════════════════════════════════════════
        public IActionResult Masters()
        {
            ViewBag.Departments     = _db.Departments.Include(d => d.Head).OrderBy(d => d.Name).ToList();
            ViewBag.Designations    = _db.Designations.OrderBy(d => d.Name).ToList();
            ViewBag.Grades          = _db.Grades.OrderBy(g => g.Name).ToList();
            ViewBag.EmploymentTypes = _db.EmploymentTypes.OrderBy(t => t.Name).ToList();
            ViewBag.EmployeeList    = _db.Employees.Where(e => e.IsActive).OrderBy(e => e.Name).ToList();
            return View();
        }

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult SaveMaster(string masterType, int id, string name, string? code)
        {
            name = (name ?? "").Trim();
            if (string.IsNullOrWhiteSpace(name)) { TempData["Error"] = "Name is required."; return RedirectToAction("Masters"); }

            switch (masterType)
            {
                case "Department":
                    if (id > 0) { var d = _db.Departments.Find(id); if (d != null) { d.Name = name; d.Code = code; } }
                    else _db.Departments.Add(new Department { Name = name, Code = code });
                    break;
                case "Designation":
                    if (id > 0) { var d = _db.Designations.Find(id); if (d != null) { d.Name = name; d.Code = code; } }
                    else _db.Designations.Add(new Designation { Name = name, Code = code });
                    break;
                case "Grade":
                    if (id > 0) { var g = _db.Grades.Find(id); if (g != null) { g.Name = name; g.Code = code; } }
                    else _db.Grades.Add(new Grade { Name = name, Code = code });
                    break;
                case "EmploymentType":
                    if (id > 0) { var t = _db.EmploymentTypes.Find(id); if (t != null) t.Name = name; }
                    else _db.EmploymentTypes.Add(new EmploymentType { Name = name });
                    break;
                default:
                    TempData["Error"] = "Unknown master type.";
                    return RedirectToAction("Masters");
            }
            _db.SaveChanges();
            TempData["Success"] = $"{masterType} saved.";
            return RedirectToAction("Masters");
        }

        [HttpPost]
        public IActionResult ToggleMaster(string masterType, int id)
        {
            bool active;
            switch (masterType)
            {
                case "Department":     { var x = _db.Departments.Find(id);     if (x == null) return Json(new { success = false }); x.IsActive = !x.IsActive; active = x.IsActive; break; }
                case "Designation":    { var x = _db.Designations.Find(id);    if (x == null) return Json(new { success = false }); x.IsActive = !x.IsActive; active = x.IsActive; break; }
                case "Grade":          { var x = _db.Grades.Find(id);          if (x == null) return Json(new { success = false }); x.IsActive = !x.IsActive; active = x.IsActive; break; }
                case "EmploymentType": { var x = _db.EmploymentTypes.Find(id); if (x == null) return Json(new { success = false }); x.IsActive = !x.IsActive; active = x.IsActive; break; }
                default: return Json(new { success = false, message = "Unknown master type." });
            }
            _db.SaveChanges();
            return Json(new { success = true, isActive = active });
        }

        [HttpPost]
        public IActionResult DeleteMaster(string masterType, int id)
        {
            bool inUse;
            switch (masterType)
            {
                case "Department":
                    inUse = _db.Employees.Any(e => e.DepartmentId == id);
                    if (inUse) { var d = _db.Departments.Find(id); if (d != null) d.IsActive = false; _db.SaveChanges(); return Json(new { success = true, message = "In use by employees — deactivated instead of deleted." }); }
                    _db.Departments.Remove(_db.Departments.Find(id)!);
                    break;
                case "Designation":
                    inUse = _db.Employees.Any(e => e.DesignationId == id);
                    if (inUse) { var x = _db.Designations.Find(id); if (x != null) x.IsActive = false; _db.SaveChanges(); return Json(new { success = true, message = "In use by employees — deactivated instead of deleted." }); }
                    _db.Designations.Remove(_db.Designations.Find(id)!);
                    break;
                case "Grade":
                    inUse = _db.Employees.Any(e => e.GradeId == id);
                    if (inUse) { var x = _db.Grades.Find(id); if (x != null) x.IsActive = false; _db.SaveChanges(); return Json(new { success = true, message = "In use by employees — deactivated instead of deleted." }); }
                    _db.Grades.Remove(_db.Grades.Find(id)!);
                    break;
                case "EmploymentType":
                    inUse = _db.Employees.Any(e => e.EmploymentTypeId == id);
                    if (inUse) { var x = _db.EmploymentTypes.Find(id); if (x != null) x.IsActive = false; _db.SaveChanges(); return Json(new { success = true, message = "In use by employees — deactivated instead of deleted." }); }
                    _db.EmploymentTypes.Remove(_db.EmploymentTypes.Find(id)!);
                    break;
                default:
                    return Json(new { success = false, message = "Unknown master type." });
            }
            _db.SaveChanges();
            return Json(new { success = true, message = $"{masterType} deleted." });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult SetDepartmentHead(int departmentId, int? headEmployeeId)
        {
            var dept = _db.Departments.Find(departmentId);
            if (dept == null) return NotFound();
            dept.HeadEmployeeId = headEmployeeId;
            _db.SaveChanges();
            TempData["Success"] = $"Head of {dept.Name} updated.";
            return RedirectToAction("Masters");
        }

        // ═══════════════════════════════════════════
        // LOCATIONS
        // ═══════════════════════════════════════════
        public IActionResult Locations()
        {
            return View(_db.Locations.OrderBy(l => l.Name).ToList());
        }

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult SaveLocation(Location model)
        {
            if (model.Id > 0)
            {
                var existing = _db.Locations.Find(model.Id);
                if (existing == null) return NotFound();
                existing.Name = model.Name; existing.Code = model.Code; existing.Address = model.Address;
                existing.City = model.City; existing.State = model.State;
            }
            else
            {
                model.Id = 0;
                _db.Locations.Add(model);
            }
            _db.SaveChanges();
            TempData["Success"] = "Location saved.";
            return RedirectToAction("Locations");
        }

        [HttpPost]
        public IActionResult ToggleLocation(int id)
        {
            var loc = _db.Locations.Find(id);
            if (loc == null) return Json(new { success = false });
            loc.IsActive = !loc.IsActive;
            _db.SaveChanges();
            return Json(new { success = true, isActive = loc.IsActive });
        }

        [HttpPost]
        public IActionResult DeleteLocation(int id)
        {
            var loc = _db.Locations.Find(id);
            if (loc == null) return Json(new { success = false });
            if (_db.Employees.Any(e => e.LocationId == id)) { loc.IsActive = false; _db.SaveChanges(); return Json(new { success = true, message = "In use — deactivated instead of deleted." }); }
            _db.Locations.Remove(loc);
            _db.SaveChanges();
            return Json(new { success = true, message = "Location deleted." });
        }

        // ═══════════════════════════════════════════
        // WEEK-OFF POLICIES — each policy is built from one or more rules
        // (see Models/MasterData.cs — WeekOffRule / AmpmHrmsPro.Services.
        // WeekOffHelper), so Admin can express both "every Sunday" and
        // occurrence-based patterns like "1st & 3rd Saturday" in the same
        // policy, and assign different policies to different employee
        // groups (e.g. corporate vs. factory staff).
        // ═══════════════════════════════════════════
        public IActionResult WeekOffPolicies()
        {
            return View(_db.WeekOffPolicies.Include(w => w.Rules).OrderBy(w => w.Name).ToList());
        }

        public record WeekOffRuleDto(string Day, string Type, string? Occurrences);

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult SaveWeekOffPolicy(int id, string name, string? description, string rulesJson)
        {
            name = (name ?? "").Trim();
            if (string.IsNullOrWhiteSpace(name)) { TempData["Error"] = "Policy name is required."; return RedirectToAction("WeekOffPolicies"); }

            List<WeekOffRuleDto> rules;
            try { rules = System.Text.Json.JsonSerializer.Deserialize<List<WeekOffRuleDto>>(rulesJson ?? "[]") ?? new(); }
            catch { TempData["Error"] = "Couldn't read the rules — please try again."; return RedirectToAction("WeekOffPolicies"); }

            if (!rules.Any()) { TempData["Error"] = "Add at least one rule (e.g. \"Every Sunday\")."; return RedirectToAction("WeekOffPolicies"); }

            WeekOffPolicy policy;
            if (id > 0)
            {
                var existing = _db.WeekOffPolicies.Include(w => w.Rules).FirstOrDefault(w => w.Id == id);
                if (existing == null) return NotFound();
                existing.Name = name;
                existing.Description = description;
                _db.WeekOffRules.RemoveRange(existing.Rules); // rebuild the rule set from scratch each save — simplest way to keep it always consistent with what's shown in the editor
                existing.Rules = new List<WeekOffRule>();
                policy = existing;
            }
            else
            {
                policy = new WeekOffPolicy { Name = name, Description = description };
                _db.WeekOffPolicies.Add(policy);
            }

            foreach (var r in rules)
            {
                if (!Enum.TryParse<DayOfWeek>(r.Day, true, out _)) continue; // ignore anything malformed
                policy.Rules.Add(new WeekOffRule
                {
                    DayOfWeek = r.Day,
                    RuleType = r.Type == "NthOccurrence" ? "NthOccurrence" : "Weekly",
                    Occurrences = r.Type == "NthOccurrence" ? r.Occurrences : null
                });
            }

            _db.SaveChanges();
            TempData["Success"] = "Week-off policy saved.";
            return RedirectToAction("WeekOffPolicies");
        }

        // JSON preview of which dates a policy marks as week-off for a given
        // month — lets Admin visually verify a rule (e.g. "1st & 3rd
        // Saturday") before assigning it to employees.
        public IActionResult PreviewWeekOff(int id, int year, int month)
        {
            var policy = _db.WeekOffPolicies.Include(w => w.Rules).FirstOrDefault(w => w.Id == id);
            if (policy == null) return Json(new { success = false });
            var dates = AmpmHrmsPro.Services.WeekOffHelper.PreviewMonth(policy, year, month)
                .Select(d => new { date = d.ToString("yyyy-MM-dd"), day = d.DayOfWeek.ToString() });
            return Json(new { success = true, dates });
        }

        [HttpPost]
        public IActionResult ToggleWeekOffPolicy(int id)
        {
            var w = _db.WeekOffPolicies.Find(id);
            if (w == null) return Json(new { success = false });
            w.IsActive = !w.IsActive;
            _db.SaveChanges();
            return Json(new { success = true, isActive = w.IsActive });
        }

        [HttpPost]
        public IActionResult DeleteWeekOffPolicy(int id)
        {
            var w = _db.WeekOffPolicies.Find(id);
            if (w == null) return Json(new { success = false });
            if (_db.Employees.Any(e => e.WeekOffPolicyId == id)) { w.IsActive = false; _db.SaveChanges(); return Json(new { success = true, message = "In use — deactivated instead of deleted." }); }
            _db.WeekOffPolicies.Remove(w);
            _db.SaveChanges();
            return Json(new { success = true, message = "Week-off policy deleted." });
        }

        // ═══════════════════════════════════════════
        // SHIFTS
        // ═══════════════════════════════════════════
        public IActionResult Shifts()
        {
            return View(_db.Shifts.OrderBy(s => s.StartTime).ToList());
        }

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult SaveShift(Shift model)
        {
            if (model.Id > 0)
            {
                var existing = _db.Shifts.Find(model.Id);
                if (existing == null) return NotFound();
                existing.Name = model.Name; existing.StartTime = model.StartTime; existing.EndTime = model.EndTime;
                existing.GraceMinutes = model.GraceMinutes; existing.HalfDayThresholdHours = model.HalfDayThresholdHours;
                existing.FullDayThresholdHours = model.FullDayThresholdHours; existing.ShiftType = model.ShiftType;
            }
            else
            {
                model.Id = 0;
                _db.Shifts.Add(model);
            }
            _db.SaveChanges();
            TempData["Success"] = "Shift saved.";
            return RedirectToAction("Shifts");
        }

        [HttpPost]
        public IActionResult ToggleShift(int id)
        {
            var s = _db.Shifts.Find(id);
            if (s == null) return Json(new { success = false });
            s.IsActive = !s.IsActive;
            _db.SaveChanges();
            return Json(new { success = true, isActive = s.IsActive });
        }

        [HttpPost]
        public IActionResult DeleteShift(int id)
        {
            var s = _db.Shifts.Find(id);
            if (s == null) return Json(new { success = false });
            if (_db.Employees.Any(e => e.ShiftId == id)) { s.IsActive = false; _db.SaveChanges(); return Json(new { success = true, message = "In use — deactivated instead of deleted." }); }
            _db.Shifts.Remove(s);
            _db.SaveChanges();
            return Json(new { success = true, message = "Shift deleted." });
        }

        // ═══════════════════════════════════════════
        // HOLIDAYS
        // ═══════════════════════════════════════════
        public IActionResult Holidays(string? year)
        {
            year ??= DateTime.Now.Year.ToString();
            ViewBag.Year = year;
            return View(_db.Holidays.Where(h => h.Date.StartsWith(year)).OrderBy(h => h.Date).ToList());
        }

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult SaveHoliday(Holiday model)
        {
            if (model.Id > 0)
            {
                var existing = _db.Holidays.Find(model.Id);
                if (existing == null) return NotFound();
                existing.Name = model.Name; existing.Date = model.Date; existing.Type = model.Type;
            }
            else
            {
                model.Id = 0;
                _db.Holidays.Add(model);
            }
            _db.SaveChanges();
            TempData["Success"] = "Holiday saved.";
            return RedirectToAction("Holidays", new { year = model.Date?.Substring(0, 4) });
        }

        [HttpPost]
        public IActionResult DeleteHoliday(int id)
        {
            var h = _db.Holidays.Find(id);
            if (h == null) return Json(new { success = false });
            _db.Holidays.Remove(h);
            _db.SaveChanges();
            return Json(new { success = true, message = "Holiday deleted." });
        }

        // ═══════════════════════════════════════════
        // LEAVE TYPES
        // ═══════════════════════════════════════════
        public IActionResult LeaveTypes()
        {
            return View(_db.LeaveTypes.OrderBy(t => t.Name).ToList());
        }

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult SaveLeaveType(LeaveType model)
        {
            model.Alias = (model.Alias ?? "").Trim().ToUpper();
            if (string.IsNullOrWhiteSpace(model.Name) || string.IsNullOrWhiteSpace(model.Alias))
            {
                TempData["Error"] = "Name and Alias are required.";
                return RedirectToAction("LeaveTypes");
            }
            if (model.Id > 0)
            {
                var existing = _db.LeaveTypes.Find(model.Id);
                if (existing == null) return NotFound();
                if (_db.LeaveTypes.Any(t => t.Id != model.Id && t.Alias == model.Alias))
                { TempData["Error"] = $"Alias '{model.Alias}' already used."; return RedirectToAction("LeaveTypes"); }
                existing.Name = model.Name; existing.Alias = model.Alias; existing.Gender = model.Gender;
                existing.Frequency = model.Frequency; existing.DefaultAnnualDays = model.DefaultAnnualDays;
                existing.CarryForward = model.CarryForward; existing.Encashable = model.Encashable;
                existing.IsCompOff = model.IsCompOff; existing.IsPaid = model.IsPaid; existing.IsActive = model.IsActive;
            }
            else
            {
                if (_db.LeaveTypes.Any(t => t.Alias == model.Alias))
                { TempData["Error"] = $"Alias '{model.Alias}' already exists."; return RedirectToAction("LeaveTypes"); }
                model.Id = 0; model.IsActive = true;
                _db.LeaveTypes.Add(model);
            }
            _db.SaveChanges();
            TempData["Success"] = "Leave type saved.";
            return RedirectToAction("LeaveTypes");
        }

        [HttpPost]
        public IActionResult ToggleLeaveType(int id)
        {
            var t = _db.LeaveTypes.Find(id);
            if (t == null) return Json(new { success = false });
            t.IsActive = !t.IsActive;
            _db.SaveChanges();
            return Json(new { success = true, isActive = t.IsActive });
        }

        [HttpPost]
        public IActionResult DeleteLeaveType(int id)
        {
            var t = _db.LeaveTypes.Find(id);
            if (t == null) return Json(new { success = false });
            // Same "in use" guard as every other master here — a hard delete
            // would either fail (Restrict FK from LeavePolicyRule) or, worse,
            // silently orphan employee leave-balance history if it didn't.
            bool inUse = _db.LeavePolicyRules.Any(r => r.LeaveTypeId == id);
            if (inUse) { t.IsActive = false; _db.SaveChanges(); return Json(new { success = true, message = "In use by a Leave Policy — deactivated instead of deleted." }); }
            _db.LeaveTypes.Remove(t);
            _db.SaveChanges();
            return Json(new { success = true, message = "Leave type deleted." });
        }

        // ═══════════════════════════════════════════
        // LEAVE POLICIES — the accrual/carry-forward/encashment rule engine.
        // A policy is a named bundle (e.g. "Corporate Staff") of rules, one
        // per Leave Type, assigned to employees via Employee.LeavePolicyId —
        // mirrors the Week-Off Policy pattern exactly (dynamic rule-builder,
        // JSON round-trip, "delete all rows & rebuild" on edit).
        // ═══════════════════════════════════════════
        public IActionResult LeavePolicies()
        {
            ViewBag.LeaveTypeList = _db.LeaveTypes.Where(t => t.IsActive).OrderBy(t => t.Name).ToList();
            return View(_db.LeavePolicies.Include(p => p.Rules).ThenInclude(r => r.LeaveType).OrderBy(p => p.Name).ToList());
        }

        public record LeavePolicyRuleDto(int LeaveTypeId, string AccrualMethod, decimal? MonthlyAccrualDays, decimal AnnualEntitlementDays, int CycleStartMonth, decimal? CarryForwardLimit, string ExcessHandling, string EncashmentTrigger);

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult SaveLeavePolicy(int id, string name, string? description, string rulesJson)
        {
            name = (name ?? "").Trim();
            if (string.IsNullOrWhiteSpace(name)) { TempData["Error"] = "Policy name is required."; return RedirectToAction("LeavePolicies"); }

            List<LeavePolicyRuleDto> rules;
            try { rules = System.Text.Json.JsonSerializer.Deserialize<List<LeavePolicyRuleDto>>(rulesJson ?? "[]") ?? new(); }
            catch { TempData["Error"] = "Couldn't read the rules — please try again."; return RedirectToAction("LeavePolicies"); }

            if (!rules.Any()) { TempData["Error"] = "Add at least one rule (pick a Leave Type and its accrual)."; return RedirectToAction("LeavePolicies"); }

            LeavePolicy policy;
            if (id > 0)
            {
                var existing = _db.LeavePolicies.Include(p => p.Rules).FirstOrDefault(p => p.Id == id);
                if (existing == null) return NotFound();
                if (_db.LeavePolicies.Any(p => p.Id != id && p.Name == name))
                { TempData["Error"] = $"A policy named '{name}' already exists."; return RedirectToAction("LeavePolicies"); }
                existing.Name = name;
                existing.Description = description;
                _db.LeavePolicyRules.RemoveRange(existing.Rules); // rebuild from scratch each save, same as Week-Off Policy
                existing.Rules = new List<LeavePolicyRule>();
                policy = existing;
            }
            else
            {
                if (_db.LeavePolicies.Any(p => p.Name == name))
                { TempData["Error"] = $"A policy named '{name}' already exists."; return RedirectToAction("LeavePolicies"); }
                policy = new LeavePolicy { Name = name, Description = description };
                _db.LeavePolicies.Add(policy);
            }

            foreach (var r in rules)
            {
                if (r.LeaveTypeId <= 0) continue; // ignore anything malformed
                policy.Rules.Add(new LeavePolicyRule
                {
                    LeaveTypeId = r.LeaveTypeId,
                    AccrualMethod = r.AccrualMethod == "Yearly" || r.AccrualMethod == "OneTime" ? r.AccrualMethod : "Monthly",
                    MonthlyAccrualDays = r.AccrualMethod == "Monthly" ? r.MonthlyAccrualDays : null,
                    AnnualEntitlementDays = r.AnnualEntitlementDays,
                    CycleStartMonth = r.CycleStartMonth is >= 1 and <= 12 ? r.CycleStartMonth : 1,
                    CarryForwardLimit = r.CarryForwardLimit,
                    ExcessHandling = r.ExcessHandling is "Lapse" or "CarryForwardAll" ? r.ExcessHandling : "Encashment",
                    EncashmentTrigger = r.EncashmentTrigger == "AutoYearEnd" ? "AutoYearEnd" : "Manual"
                });
            }

            _db.SaveChanges();
            TempData["Success"] = "Leave policy saved.";
            return RedirectToAction("LeavePolicies");
        }

        [HttpPost]
        public IActionResult ToggleLeavePolicy(int id)
        {
            var p = _db.LeavePolicies.Find(id);
            if (p == null) return Json(new { success = false });
            p.IsActive = !p.IsActive;
            _db.SaveChanges();
            return Json(new { success = true, isActive = p.IsActive });
        }

        [HttpPost]
        public IActionResult DeleteLeavePolicy(int id)
        {
            var p = _db.LeavePolicies.Find(id);
            if (p == null) return Json(new { success = false });
            if (_db.Employees.Any(e => e.LeavePolicyId == id)) { p.IsActive = false; _db.SaveChanges(); return Json(new { success = true, message = "In use — deactivated instead of deleted." }); }
            _db.LeavePolicies.Remove(p);
            _db.SaveChanges();
            return Json(new { success = true, message = "Leave policy deleted." });
        }
    }
}
