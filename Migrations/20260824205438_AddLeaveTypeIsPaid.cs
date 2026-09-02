using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AmpmHrmsPro.Migrations
{
    /// <inheritdoc />
    public partial class AddLeaveTypeIsPaid : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsPaid",
                table: "LeaveTypes",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "OutDateColumn",
                table: "AttendanceImportProfiles",
                type: "nvarchar(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OutTimeColumn",
                table: "AttendanceImportProfiles",
                type: "nvarchar(80)",
                maxLength: 80,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsPaid",
                table: "LeaveTypes");

            migrationBuilder.DropColumn(
                name: "OutDateColumn",
                table: "AttendanceImportProfiles");

            migrationBuilder.DropColumn(
                name: "OutTimeColumn",
                table: "AttendanceImportProfiles");
        }
    }
}
