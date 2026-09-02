using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AmpmHrmsPro.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Designations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Code = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Designations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EmploymentTypes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmploymentTypes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Grades",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Code = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Grades", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Holidays",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Date = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Type = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Holidays", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LeaveTypes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    Alias = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Gender = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Frequency = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    DefaultAnnualDays = table.Column<decimal>(type: "decimal(5,1)", precision: 5, scale: 1, nullable: false),
                    CarryForward = table.Column<bool>(type: "bit", nullable: false),
                    Encashable = table.Column<bool>(type: "bit", nullable: false),
                    IsCompOff = table.Column<bool>(type: "bit", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeaveTypes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Locations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Code = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    Address = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    City = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    State = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Locations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Shifts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    StartTime = table.Column<TimeSpan>(type: "time", nullable: false),
                    EndTime = table.Column<TimeSpan>(type: "time", nullable: false),
                    GraceMinutes = table.Column<int>(type: "int", nullable: false),
                    HalfDayThresholdHours = table.Column<decimal>(type: "decimal(4,1)", precision: 4, scale: 1, nullable: false),
                    FullDayThresholdHours = table.Column<decimal>(type: "decimal(4,1)", precision: 4, scale: 1, nullable: false),
                    ShiftType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Shifts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WeekOffPolicies",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    Days = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WeekOffPolicies", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Departments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Code = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    HeadEmployeeId = table.Column<int>(type: "int", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Departments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Employees",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EmpCode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    PasswordHash = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Role = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Mobile = table.Column<string>(type: "nvarchar(15)", maxLength: 15, nullable: true),
                    Gender = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    DOB = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    Address = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    PhotoUrl = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DOJ = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    DepartmentId = table.Column<int>(type: "int", nullable: true),
                    DesignationId = table.Column<int>(type: "int", nullable: true),
                    LocationId = table.Column<int>(type: "int", nullable: true),
                    GradeId = table.Column<int>(type: "int", nullable: true),
                    EmploymentTypeId = table.Column<int>(type: "int", nullable: true),
                    ShiftId = table.Column<int>(type: "int", nullable: true),
                    WeekOffPolicyId = table.Column<int>(type: "int", nullable: true),
                    ReportingManagerId = table.Column<int>(type: "int", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    FatherOrHusbandName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    AlternateMobile = table.Column<string>(type: "nvarchar(15)", maxLength: 15, nullable: true),
                    Nationality = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    Religion = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    Qualification = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Country = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    State = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    City = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    Pincode = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    GroupDOJ = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    CardNumber = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    CompanyCode = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    EmployeeRole = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    WorkStation = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    Category = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    SubDepartment = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    AdditionalShifts = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    ValidFrom = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    ValidTo = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    DateOfLeaving = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    InactivationDate = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    Experience = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    StandardWorkingHour = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    IsAutoShift = table.Column<bool>(type: "bit", nullable: false),
                    IsAutoInactive = table.Column<bool>(type: "bit", nullable: false),
                    CompanyPFCode = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    BankName = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    AccountNumber = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    AccountHolderName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    IFSCCode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    UANNumber = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    AadharNumber = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    PFNumber = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    ESICNumber = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    PANNumber = table.Column<string>(type: "nvarchar(15)", maxLength: 15, nullable: true),
                    IPNumber = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    PaymentMode = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    EmergencyContactName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    EmergencyContactMobile = table.Column<string>(type: "nvarchar(15)", maxLength: 15, nullable: true),
                    EmergencyContactRelation = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    LocationType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    MappedLocations = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    MappedSubLocations = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    PunchInLocation = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    AppLogin = table.Column<bool>(type: "bit", nullable: false),
                    AttendanceAccessApp = table.Column<bool>(type: "bit", nullable: false),
                    ActivateCheckIn = table.Column<bool>(type: "bit", nullable: false),
                    Zone = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    BusAllowed = table.Column<bool>(type: "bit", nullable: false),
                    BusName = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    FlexiHours = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    TrainingGivenBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    TrainingType = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    TrainingUserType = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    ExternalTrainerName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ExternalTrainerEmail = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    TrainingDate = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    TrainingStatus = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    Remarks = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Employees", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Employees_Departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalTable: "Departments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Employees_Designations_DesignationId",
                        column: x => x.DesignationId,
                        principalTable: "Designations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Employees_Employees_ReportingManagerId",
                        column: x => x.ReportingManagerId,
                        principalTable: "Employees",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Employees_EmploymentTypes_EmploymentTypeId",
                        column: x => x.EmploymentTypeId,
                        principalTable: "EmploymentTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Employees_Grades_GradeId",
                        column: x => x.GradeId,
                        principalTable: "Grades",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Employees_Locations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "Locations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Employees_Shifts_ShiftId",
                        column: x => x.ShiftId,
                        principalTable: "Shifts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Employees_WeekOffPolicies_WeekOffPolicyId",
                        column: x => x.WeekOffPolicyId,
                        principalTable: "WeekOffPolicies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Departments_HeadEmployeeId",
                table: "Departments",
                column: "HeadEmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_Departments_Name",
                table: "Departments",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Designations_Name",
                table: "Designations",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Employees_DepartmentId",
                table: "Employees",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_Employees_DesignationId",
                table: "Employees",
                column: "DesignationId");

            migrationBuilder.CreateIndex(
                name: "IX_Employees_Email",
                table: "Employees",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Employees_EmpCode",
                table: "Employees",
                column: "EmpCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Employees_EmploymentTypeId",
                table: "Employees",
                column: "EmploymentTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_Employees_GradeId",
                table: "Employees",
                column: "GradeId");

            migrationBuilder.CreateIndex(
                name: "IX_Employees_LocationId",
                table: "Employees",
                column: "LocationId");

            migrationBuilder.CreateIndex(
                name: "IX_Employees_ReportingManagerId",
                table: "Employees",
                column: "ReportingManagerId");

            migrationBuilder.CreateIndex(
                name: "IX_Employees_ShiftId",
                table: "Employees",
                column: "ShiftId");

            migrationBuilder.CreateIndex(
                name: "IX_Employees_WeekOffPolicyId",
                table: "Employees",
                column: "WeekOffPolicyId");

            migrationBuilder.CreateIndex(
                name: "IX_EmploymentTypes_Name",
                table: "EmploymentTypes",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Grades_Name",
                table: "Grades",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Holidays_Date",
                table: "Holidays",
                column: "Date");

            migrationBuilder.CreateIndex(
                name: "IX_LeaveTypes_Alias",
                table: "LeaveTypes",
                column: "Alias",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Locations_Name",
                table: "Locations",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WeekOffPolicies_Name",
                table: "WeekOffPolicies",
                column: "Name",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Departments_Employees_HeadEmployeeId",
                table: "Departments",
                column: "HeadEmployeeId",
                principalTable: "Employees",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Departments_Employees_HeadEmployeeId",
                table: "Departments");

            migrationBuilder.DropTable(
                name: "Holidays");

            migrationBuilder.DropTable(
                name: "LeaveTypes");

            migrationBuilder.DropTable(
                name: "Employees");

            migrationBuilder.DropTable(
                name: "Departments");

            migrationBuilder.DropTable(
                name: "Designations");

            migrationBuilder.DropTable(
                name: "EmploymentTypes");

            migrationBuilder.DropTable(
                name: "Grades");

            migrationBuilder.DropTable(
                name: "Locations");

            migrationBuilder.DropTable(
                name: "Shifts");

            migrationBuilder.DropTable(
                name: "WeekOffPolicies");
        }
    }
}
