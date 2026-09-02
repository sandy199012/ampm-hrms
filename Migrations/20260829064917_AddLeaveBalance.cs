using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AmpmHrmsPro.Migrations
{
    /// <inheritdoc />
    public partial class AddLeaveBalance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "OTRuleId",
                table: "Employees",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "LeaveBalances",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EmployeeId = table.Column<int>(type: "int", nullable: false),
                    LeaveTypeCode = table.Column<string>(type: "nvarchar(5)", maxLength: 5, nullable: false),
                    Year = table.Column<int>(type: "int", nullable: false),
                    CarryForward = table.Column<decimal>(type: "decimal(7,3)", nullable: false),
                    EarnedJan = table.Column<decimal>(type: "decimal(5,3)", nullable: false),
                    EarnedFeb = table.Column<decimal>(type: "decimal(5,3)", nullable: false),
                    EarnedMar = table.Column<decimal>(type: "decimal(5,3)", nullable: false),
                    EarnedApr = table.Column<decimal>(type: "decimal(5,3)", nullable: false),
                    EarnedMay = table.Column<decimal>(type: "decimal(5,3)", nullable: false),
                    EarnedJun = table.Column<decimal>(type: "decimal(5,3)", nullable: false),
                    EarnedJul = table.Column<decimal>(type: "decimal(5,3)", nullable: false),
                    EarnedAug = table.Column<decimal>(type: "decimal(5,3)", nullable: false),
                    EarnedSep = table.Column<decimal>(type: "decimal(5,3)", nullable: false),
                    EarnedOct = table.Column<decimal>(type: "decimal(5,3)", nullable: false),
                    EarnedNov = table.Column<decimal>(type: "decimal(5,3)", nullable: false),
                    EarnedDec = table.Column<decimal>(type: "decimal(5,3)", nullable: false),
                    ConsumedJan = table.Column<decimal>(type: "decimal(5,3)", nullable: false),
                    ConsumedFeb = table.Column<decimal>(type: "decimal(5,3)", nullable: false),
                    ConsumedMar = table.Column<decimal>(type: "decimal(5,3)", nullable: false),
                    ConsumedApr = table.Column<decimal>(type: "decimal(5,3)", nullable: false),
                    ConsumedMay = table.Column<decimal>(type: "decimal(5,3)", nullable: false),
                    ConsumedJun = table.Column<decimal>(type: "decimal(5,3)", nullable: false),
                    ConsumedJul = table.Column<decimal>(type: "decimal(5,3)", nullable: false),
                    ConsumedAug = table.Column<decimal>(type: "decimal(5,3)", nullable: false),
                    ConsumedSep = table.Column<decimal>(type: "decimal(5,3)", nullable: false),
                    ConsumedOct = table.Column<decimal>(type: "decimal(5,3)", nullable: false),
                    ConsumedNov = table.Column<decimal>(type: "decimal(5,3)", nullable: false),
                    ConsumedDec = table.Column<decimal>(type: "decimal(5,3)", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeaveBalances", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LeaveBalances_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "OTRules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    OTType = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    CountAfterShiftHours = table.Column<bool>(type: "bit", nullable: false),
                    CountHolidays = table.Column<bool>(type: "bit", nullable: false),
                    CountWeekOffs = table.Column<bool>(type: "bit", nullable: false),
                    MinOTMinutesPerDay = table.Column<int>(type: "int", nullable: false),
                    MaxOTMinutesPerDay = table.Column<int>(type: "int", nullable: true),
                    NormalOTMultiplier = table.Column<decimal>(type: "decimal(4,2)", nullable: false),
                    HolidayOTMultiplier = table.Column<decimal>(type: "decimal(4,2)", nullable: false),
                    MinutesPerOTLeaveDay = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OTRules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OTLedgers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EmployeeId = table.Column<int>(type: "int", nullable: false),
                    OTRuleId = table.Column<int>(type: "int", nullable: true),
                    Date = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    OTMinutes = table.Column<int>(type: "int", nullable: false),
                    OTKind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    OTType = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Source = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Remarks = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    CreatedByEmployeeId = table.Column<int>(type: "int", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OTLedgers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OTLedgers_Employees_CreatedByEmployeeId",
                        column: x => x.CreatedByEmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_OTLedgers_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_OTLedgers_OTRules_OTRuleId",
                        column: x => x.OTRuleId,
                        principalTable: "OTRules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Employees_OTRuleId",
                table: "Employees",
                column: "OTRuleId");

            migrationBuilder.CreateIndex(
                name: "IX_LeaveBalances_EmployeeId_LeaveTypeCode_Year",
                table: "LeaveBalances",
                columns: new[] { "EmployeeId", "LeaveTypeCode", "Year" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OTLedgers_CreatedByEmployeeId",
                table: "OTLedgers",
                column: "CreatedByEmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_OTLedgers_EmployeeId_Date_Source",
                table: "OTLedgers",
                columns: new[] { "EmployeeId", "Date", "Source" },
                unique: true,
                filter: "[Source] = 'Auto'");

            migrationBuilder.CreateIndex(
                name: "IX_OTLedgers_OTRuleId",
                table: "OTLedgers",
                column: "OTRuleId");

            migrationBuilder.AddForeignKey(
                name: "FK_Employees_OTRules_OTRuleId",
                table: "Employees",
                column: "OTRuleId",
                principalTable: "OTRules",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Employees_OTRules_OTRuleId",
                table: "Employees");

            migrationBuilder.DropTable(
                name: "LeaveBalances");

            migrationBuilder.DropTable(
                name: "OTLedgers");

            migrationBuilder.DropTable(
                name: "OTRules");

            migrationBuilder.DropIndex(
                name: "IX_Employees_OTRuleId",
                table: "Employees");

            migrationBuilder.DropColumn(
                name: "OTRuleId",
                table: "Employees");
        }
    }
}
