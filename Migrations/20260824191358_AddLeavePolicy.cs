using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AmpmHrmsPro.Migrations
{
    /// <inheritdoc />
    public partial class AddLeavePolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LeavePolicyId",
                table: "Employees",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "LeavePolicies",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeavePolicies", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LeavePolicyRules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    LeavePolicyId = table.Column<int>(type: "int", nullable: false),
                    LeaveTypeId = table.Column<int>(type: "int", nullable: false),
                    AccrualMethod = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    MonthlyAccrualDays = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: true),
                    AnnualEntitlementDays = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: false),
                    CycleStartMonth = table.Column<int>(type: "int", nullable: false),
                    CarryForwardLimit = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: true),
                    ExcessHandling = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    EncashmentTrigger = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeavePolicyRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LeavePolicyRules_LeavePolicies_LeavePolicyId",
                        column: x => x.LeavePolicyId,
                        principalTable: "LeavePolicies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_LeavePolicyRules_LeaveTypes_LeaveTypeId",
                        column: x => x.LeaveTypeId,
                        principalTable: "LeaveTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Employees_LeavePolicyId",
                table: "Employees",
                column: "LeavePolicyId");

            migrationBuilder.CreateIndex(
                name: "IX_LeavePolicies_Name",
                table: "LeavePolicies",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LeavePolicyRules_LeavePolicyId",
                table: "LeavePolicyRules",
                column: "LeavePolicyId");

            migrationBuilder.CreateIndex(
                name: "IX_LeavePolicyRules_LeaveTypeId",
                table: "LeavePolicyRules",
                column: "LeaveTypeId");

            migrationBuilder.AddForeignKey(
                name: "FK_Employees_LeavePolicies_LeavePolicyId",
                table: "Employees",
                column: "LeavePolicyId",
                principalTable: "LeavePolicies",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Employees_LeavePolicies_LeavePolicyId",
                table: "Employees");

            migrationBuilder.DropTable(
                name: "LeavePolicyRules");

            migrationBuilder.DropTable(
                name: "LeavePolicies");

            migrationBuilder.DropIndex(
                name: "IX_Employees_LeavePolicyId",
                table: "Employees");

            migrationBuilder.DropColumn(
                name: "LeavePolicyId",
                table: "Employees");
        }
    }
}
