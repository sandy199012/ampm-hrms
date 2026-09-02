using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AmpmHrmsPro.Migrations
{
    /// <inheritdoc />
    public partial class AddAttendanceOTApplications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Applications",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EmployeeId = table.Column<int>(type: "int", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    LeaveTypeId = table.Column<int>(type: "int", nullable: true),
                    FromDate = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    ToDate = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    DurationDays = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: false),
                    DayPart = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    RequestedInTime = table.Column<TimeSpan>(type: "time", nullable: true),
                    RequestedOutTime = table.Column<TimeSpan>(type: "time", nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    AppliedOn = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ApproverEmployeeId = table.Column<int>(type: "int", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    PendingAt = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Remarks = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    DecisionAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DecisionByEmployeeId = table.Column<int>(type: "int", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Applications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Applications_Employees_ApproverEmployeeId",
                        column: x => x.ApproverEmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Applications_Employees_DecisionByEmployeeId",
                        column: x => x.DecisionByEmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Applications_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Applications_LeaveTypes_LeaveTypeId",
                        column: x => x.LeaveTypeId,
                        principalTable: "LeaveTypes",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "AttendanceDailies",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EmployeeId = table.Column<int>(type: "int", nullable: false),
                    Date = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    InTime = table.Column<TimeSpan>(type: "time", nullable: true),
                    OutTime = table.Column<TimeSpan>(type: "time", nullable: true),
                    RawStatus = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    EffectiveStatus = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    WasHoliday = table.Column<bool>(type: "bit", nullable: false),
                    WasWeekOff = table.Column<bool>(type: "bit", nullable: false),
                    WorkedMinutes = table.Column<int>(type: "int", nullable: true),
                    ExtraMinutes = table.Column<int>(type: "int", nullable: true),
                    OTRule = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    OTHours = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: true),
                    IsRetailOT = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AttendanceDailies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AttendanceDailies_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "AttendanceImportProfiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Format = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    EmployeeCodeColumn = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    DateColumn = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    TimeColumn = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    DateTimeColumn = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    DirectionColumn = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    PunchColumnsCsv = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    DateFormat = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    TimeFormat = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    IsDefault = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AttendanceImportProfiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AttendancePunches",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EmployeeId = table.Column<int>(type: "int", nullable: false),
                    PunchDateTime = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Direction = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    DeviceId = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    Source = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    SyncedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AttendancePunches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AttendancePunches_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "BiometricApiSettingsList",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    BaseUrl = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    HttpMethod = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    RequestBodyTemplate = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ApiKey = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    AuthHeaderName = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    AuthScheme = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ResponseArrayPath = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    EmployeeCodeField = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    PunchDateTimeField = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    PunchDateField = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    PunchTimeField = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    DateTimeFormat = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    DirectionField = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    InDirectionValue = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    OutDirectionValue = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    DeviceIdField = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    SyncIntervalMinutes = table.Column<int>(type: "int", nullable: false),
                    LastSyncAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastSyncStatus = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    LastSyncMessage = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    LastSampleResponse = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BiometricApiSettingsList", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Applications_ApproverEmployeeId",
                table: "Applications",
                column: "ApproverEmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_Applications_DecisionByEmployeeId",
                table: "Applications",
                column: "DecisionByEmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_Applications_EmployeeId_FromDate_ToDate",
                table: "Applications",
                columns: new[] { "EmployeeId", "FromDate", "ToDate" });

            migrationBuilder.CreateIndex(
                name: "IX_Applications_LeaveTypeId",
                table: "Applications",
                column: "LeaveTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceDailies_EmployeeId_Date",
                table: "AttendanceDailies",
                columns: new[] { "EmployeeId", "Date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AttendancePunches_EmployeeId_PunchDateTime",
                table: "AttendancePunches",
                columns: new[] { "EmployeeId", "PunchDateTime" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Applications");

            migrationBuilder.DropTable(
                name: "AttendanceDailies");

            migrationBuilder.DropTable(
                name: "AttendanceImportProfiles");

            migrationBuilder.DropTable(
                name: "AttendancePunches");

            migrationBuilder.DropTable(
                name: "BiometricApiSettingsList");
        }
    }
}
