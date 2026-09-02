using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AmpmHrmsPro.Migrations
{
    /// <inheritdoc />
    public partial class AddCompOffModule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CompOffRuleId",
                table: "Employees",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CompOffRules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    MinHoursForFullDay = table.Column<decimal>(type: "decimal(4,1)", precision: 4, scale: 1, nullable: false),
                    MinHoursForHalfDay = table.Column<decimal>(type: "decimal(4,1)", precision: 4, scale: 1, nullable: false),
                    CountHolidays = table.Column<bool>(type: "bit", nullable: false),
                    CountWeekOffs = table.Column<bool>(type: "bit", nullable: false),
                    AutoCredit = table.Column<bool>(type: "bit", nullable: false),
                    ExpiryDays = table.Column<int>(type: "int", nullable: false),
                    MaxOpenBalance = table.Column<decimal>(type: "decimal(5,1)", precision: 5, scale: 1, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CompOffRules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CompOffLedgers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EmployeeId = table.Column<int>(type: "int", nullable: false),
                    CompOffRuleId = table.Column<int>(type: "int", nullable: true),
                    EarnedDate = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    EarnedDays = table.Column<decimal>(type: "decimal(5,1)", precision: 5, scale: 1, nullable: false),
                    UsedDays = table.Column<decimal>(type: "decimal(5,1)", precision: 5, scale: 1, nullable: false),
                    Source = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ExpiryDate = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Remarks = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    CreatedByEmployeeId = table.Column<int>(type: "int", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CompOffLedgers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CompOffLedgers_CompOffRules_CompOffRuleId",
                        column: x => x.CompOffRuleId,
                        principalTable: "CompOffRules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CompOffLedgers_Employees_CreatedByEmployeeId",
                        column: x => x.CreatedByEmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_CompOffLedgers_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "CompOffConsumptions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ApplicationId = table.Column<int>(type: "int", nullable: false),
                    CompOffLedgerId = table.Column<int>(type: "int", nullable: false),
                    DaysConsumed = table.Column<decimal>(type: "decimal(5,1)", precision: 5, scale: 1, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CompOffConsumptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CompOffConsumptions_Applications_ApplicationId",
                        column: x => x.ApplicationId,
                        principalTable: "Applications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CompOffConsumptions_CompOffLedgers_CompOffLedgerId",
                        column: x => x.CompOffLedgerId,
                        principalTable: "CompOffLedgers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Employees_CompOffRuleId",
                table: "Employees",
                column: "CompOffRuleId");

            migrationBuilder.CreateIndex(
                name: "IX_CompOffConsumptions_ApplicationId",
                table: "CompOffConsumptions",
                column: "ApplicationId");

            migrationBuilder.CreateIndex(
                name: "IX_CompOffConsumptions_CompOffLedgerId",
                table: "CompOffConsumptions",
                column: "CompOffLedgerId");

            migrationBuilder.CreateIndex(
                name: "IX_CompOffLedgers_CompOffRuleId",
                table: "CompOffLedgers",
                column: "CompOffRuleId");

            migrationBuilder.CreateIndex(
                name: "IX_CompOffLedgers_CreatedByEmployeeId",
                table: "CompOffLedgers",
                column: "CreatedByEmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_CompOffLedgers_EmployeeId_EarnedDate_Source",
                table: "CompOffLedgers",
                columns: new[] { "EmployeeId", "EarnedDate", "Source" },
                unique: true,
                filter: "[Source] = 'Auto'");

            migrationBuilder.CreateIndex(
                name: "IX_CompOffRules_Name",
                table: "CompOffRules",
                column: "Name",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Employees_CompOffRules_CompOffRuleId",
                table: "Employees",
                column: "CompOffRuleId",
                principalTable: "CompOffRules",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Employees_CompOffRules_CompOffRuleId",
                table: "Employees");

            migrationBuilder.DropTable(
                name: "CompOffConsumptions");

            migrationBuilder.DropTable(
                name: "CompOffLedgers");

            migrationBuilder.DropTable(
                name: "CompOffRules");

            migrationBuilder.DropIndex(
                name: "IX_Employees_CompOffRuleId",
                table: "Employees");

            migrationBuilder.DropColumn(
                name: "CompOffRuleId",
                table: "Employees");
        }
    }
}
