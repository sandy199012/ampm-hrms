using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AmpmHrmsPro.Migrations
{
    /// <inheritdoc />
    public partial class AddMobileAppSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "FaceMatchConfidence",
                table: "AttendancePunches",
                type: "decimal(5,2)",
                precision: 5,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "FaceMatched",
                table: "AttendancePunches",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "Latitude",
                table: "AttendancePunches",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LocationAddress",
                table: "AttendancePunches",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "Longitude",
                table: "AttendancePunches",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PhotoPath",
                table: "AttendancePunches",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "FaceMatchApiSettingsList",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    VerifyUrl = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    ApiKey = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    AuthHeaderName = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    AuthScheme = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ConfidenceField = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    ConfidenceIsFraction = table.Column<bool>(type: "bit", nullable: false),
                    IsIdenticalField = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    MinConfidencePercent = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: false),
                    LastTestAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastTestStatus = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    LastTestMessage = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FaceMatchApiSettingsList", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FaceProfiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EmployeeId = table.Column<int>(type: "int", nullable: false),
                    PhotoPath = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    EnrolledAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FaceProfiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FaceProfiles_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Notifications",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EmployeeId = table.Column<int>(type: "int", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Message = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Type = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    RelatedApplicationId = table.Column<int>(type: "int", nullable: true),
                    IsRead = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Notifications_Applications_RelatedApplicationId",
                        column: x => x.RelatedApplicationId,
                        principalTable: "Applications",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Notifications_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_FaceProfiles_EmployeeId",
                table: "FaceProfiles",
                column: "EmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_EmployeeId_IsRead_CreatedAt",
                table: "Notifications",
                columns: new[] { "EmployeeId", "IsRead", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_RelatedApplicationId",
                table: "Notifications",
                column: "RelatedApplicationId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FaceMatchApiSettingsList");

            migrationBuilder.DropTable(
                name: "FaceProfiles");

            migrationBuilder.DropTable(
                name: "Notifications");

            migrationBuilder.DropColumn(
                name: "FaceMatchConfidence",
                table: "AttendancePunches");

            migrationBuilder.DropColumn(
                name: "FaceMatched",
                table: "AttendancePunches");

            migrationBuilder.DropColumn(
                name: "Latitude",
                table: "AttendancePunches");

            migrationBuilder.DropColumn(
                name: "LocationAddress",
                table: "AttendancePunches");

            migrationBuilder.DropColumn(
                name: "Longitude",
                table: "AttendancePunches");

            migrationBuilder.DropColumn(
                name: "PhotoPath",
                table: "AttendancePunches");
        }
    }
}
