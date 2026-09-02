using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AmpmHrmsPro.Migrations
{
    /// <inheritdoc />
    public partial class AddEmailSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EmailSettingsList",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    SmtpHost = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    SmtpPort = table.Column<int>(type: "int", nullable: false),
                    SmtpUsername = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    SmtpPassword = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    SmtpUseSsl = table.Column<bool>(type: "bit", nullable: false),
                    FromEmail = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    FromName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    DailyAttendanceAlertEnabled = table.Column<bool>(type: "bit", nullable: false),
                    DailyAttendanceAlertTime = table.Column<TimeSpan>(type: "time", nullable: false),
                    LastDailyAlertRunDate = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    BirthdayEnabled = table.Column<bool>(type: "bit", nullable: false),
                    BirthdayTime = table.Column<TimeSpan>(type: "time", nullable: false),
                    LastBirthdayRunDate = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    WeeklyReportEnabled = table.Column<bool>(type: "bit", nullable: false),
                    WeeklyReportDay = table.Column<int>(type: "int", nullable: false),
                    WeeklyReportTime = table.Column<TimeSpan>(type: "time", nullable: false),
                    LastWeeklyRunDate = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    LastActivityAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastActivityMessage = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailSettingsList", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EmailSettingsList");
        }
    }
}
