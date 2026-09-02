using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AmpmHrmsPro.Migrations
{
    /// <inheritdoc />
    public partial class AddSalaryTaxModule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SalaryComponents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Code = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    ComponentType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CalculationType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    DefaultValue = table.Column<decimal>(type: "decimal(12,2)", precision: 12, scale: 2, nullable: false),
                    IsBasic = table.Column<bool>(type: "bit", nullable: false),
                    IsTaxable = table.Column<bool>(type: "bit", nullable: false),
                    IsHRA = table.Column<bool>(type: "bit", nullable: false),
                    DisplayOrder = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalaryComponents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SalaryStructureTemplates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    GradeId = table.Column<int>(type: "int", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalaryStructureTemplates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SalaryStructureTemplates_Grades_GradeId",
                        column: x => x.GradeId,
                        principalTable: "Grades",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "TaxDeclarationHeaders",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EmployeeId = table.Column<int>(type: "int", nullable: false),
                    FinancialYear = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    RegimeChoice = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    AnnualRentPaid = table.Column<decimal>(type: "decimal(12,2)", precision: 12, scale: 2, nullable: false),
                    IsMetroCity = table.Column<bool>(type: "bit", nullable: false),
                    RentReceiptDocumentUrl = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    SubmittedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaxDeclarationHeaders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaxDeclarationHeaders_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "TaxSectionMasters",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Code = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    MaxLimit = table.Column<decimal>(type: "decimal(12,2)", precision: 12, scale: 2, nullable: true),
                    ApplicableRegime = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    RequiresDocument = table.Column<bool>(type: "bit", nullable: false),
                    DisplayOrder = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaxSectionMasters", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TaxSlabSettingsList",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FinancialYear = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Regime = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    StandardDeduction = table.Column<decimal>(type: "decimal(12,2)", precision: 12, scale: 2, nullable: false),
                    Rebate87AIncomeLimit = table.Column<decimal>(type: "decimal(12,2)", precision: 12, scale: 2, nullable: false),
                    Rebate87AMaxAmount = table.Column<decimal>(type: "decimal(12,2)", precision: 12, scale: 2, nullable: false),
                    CessPercent = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaxSlabSettingsList", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EmployeeSalaryStructures",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EmployeeId = table.Column<int>(type: "int", nullable: false),
                    EffectiveFrom = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    EffectiveTo = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    AnnualCTC = table.Column<decimal>(type: "decimal(14,2)", precision: 14, scale: 2, nullable: false),
                    SourceTemplateId = table.Column<int>(type: "int", nullable: true),
                    CreatedByEmployeeId = table.Column<int>(type: "int", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmployeeSalaryStructures", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EmployeeSalaryStructures_Employees_CreatedByEmployeeId",
                        column: x => x.CreatedByEmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_EmployeeSalaryStructures_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_EmployeeSalaryStructures_SalaryStructureTemplates_SourceTemplateId",
                        column: x => x.SourceTemplateId,
                        principalTable: "SalaryStructureTemplates",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "SalaryStructureTemplateItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SalaryStructureTemplateId = table.Column<int>(type: "int", nullable: false),
                    SalaryComponentId = table.Column<int>(type: "int", nullable: false),
                    CalculationType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Value = table.Column<decimal>(type: "decimal(12,2)", precision: 12, scale: 2, nullable: false),
                    DisplayOrder = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalaryStructureTemplateItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SalaryStructureTemplateItems_SalaryComponents_SalaryComponentId",
                        column: x => x.SalaryComponentId,
                        principalTable: "SalaryComponents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalaryStructureTemplateItems_SalaryStructureTemplates_SalaryStructureTemplateId",
                        column: x => x.SalaryStructureTemplateId,
                        principalTable: "SalaryStructureTemplates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TaxDeclarationItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TaxDeclarationHeaderId = table.Column<int>(type: "int", nullable: false),
                    TaxSectionMasterId = table.Column<int>(type: "int", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    DeclaredAmount = table.Column<decimal>(type: "decimal(12,2)", precision: 12, scale: 2, nullable: false),
                    ApprovedAmount = table.Column<decimal>(type: "decimal(12,2)", precision: 12, scale: 2, nullable: true),
                    DocumentUrl = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    AdminRemarks = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    ReviewedByEmployeeId = table.Column<int>(type: "int", nullable: true),
                    ReviewedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaxDeclarationItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaxDeclarationItems_Employees_ReviewedByEmployeeId",
                        column: x => x.ReviewedByEmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_TaxDeclarationItems_TaxDeclarationHeaders_TaxDeclarationHeaderId",
                        column: x => x.TaxDeclarationHeaderId,
                        principalTable: "TaxDeclarationHeaders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TaxDeclarationItems_TaxSectionMasters_TaxSectionMasterId",
                        column: x => x.TaxSectionMasterId,
                        principalTable: "TaxSectionMasters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TaxSlabs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TaxSlabSettingsId = table.Column<int>(type: "int", nullable: false),
                    FromAmount = table.Column<decimal>(type: "decimal(14,2)", precision: 14, scale: 2, nullable: false),
                    ToAmount = table.Column<decimal>(type: "decimal(14,2)", precision: 14, scale: 2, nullable: true),
                    RatePercent = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: false),
                    DisplayOrder = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaxSlabs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaxSlabs_TaxSlabSettingsList_TaxSlabSettingsId",
                        column: x => x.TaxSlabSettingsId,
                        principalTable: "TaxSlabSettingsList",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TaxSurchargeSlabs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TaxSlabSettingsId = table.Column<int>(type: "int", nullable: false),
                    FromAmount = table.Column<decimal>(type: "decimal(14,2)", precision: 14, scale: 2, nullable: false),
                    ToAmount = table.Column<decimal>(type: "decimal(14,2)", precision: 14, scale: 2, nullable: true),
                    RatePercent = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: false),
                    DisplayOrder = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaxSurchargeSlabs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaxSurchargeSlabs_TaxSlabSettingsList_TaxSlabSettingsId",
                        column: x => x.TaxSlabSettingsId,
                        principalTable: "TaxSlabSettingsList",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EmployeeSalaryComponents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EmployeeSalaryStructureId = table.Column<int>(type: "int", nullable: false),
                    SalaryComponentId = table.Column<int>(type: "int", nullable: false),
                    CalculationType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Value = table.Column<decimal>(type: "decimal(12,2)", precision: 12, scale: 2, nullable: false),
                    MonthlyAmount = table.Column<decimal>(type: "decimal(12,2)", precision: 12, scale: 2, nullable: false),
                    DisplayOrder = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmployeeSalaryComponents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EmployeeSalaryComponents_EmployeeSalaryStructures_EmployeeSalaryStructureId",
                        column: x => x.EmployeeSalaryStructureId,
                        principalTable: "EmployeeSalaryStructures",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_EmployeeSalaryComponents_SalaryComponents_SalaryComponentId",
                        column: x => x.SalaryComponentId,
                        principalTable: "SalaryComponents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EmployeeSalaryComponents_EmployeeSalaryStructureId",
                table: "EmployeeSalaryComponents",
                column: "EmployeeSalaryStructureId");

            migrationBuilder.CreateIndex(
                name: "IX_EmployeeSalaryComponents_SalaryComponentId",
                table: "EmployeeSalaryComponents",
                column: "SalaryComponentId");

            migrationBuilder.CreateIndex(
                name: "IX_EmployeeSalaryStructures_CreatedByEmployeeId",
                table: "EmployeeSalaryStructures",
                column: "CreatedByEmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_EmployeeSalaryStructures_EmployeeId_EffectiveTo",
                table: "EmployeeSalaryStructures",
                columns: new[] { "EmployeeId", "EffectiveTo" });

            migrationBuilder.CreateIndex(
                name: "IX_EmployeeSalaryStructures_SourceTemplateId",
                table: "EmployeeSalaryStructures",
                column: "SourceTemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_SalaryStructureTemplateItems_SalaryComponentId",
                table: "SalaryStructureTemplateItems",
                column: "SalaryComponentId");

            migrationBuilder.CreateIndex(
                name: "IX_SalaryStructureTemplateItems_SalaryStructureTemplateId",
                table: "SalaryStructureTemplateItems",
                column: "SalaryStructureTemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_SalaryStructureTemplates_GradeId",
                table: "SalaryStructureTemplates",
                column: "GradeId");

            migrationBuilder.CreateIndex(
                name: "IX_SalaryStructureTemplates_Name",
                table: "SalaryStructureTemplates",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaxDeclarationHeaders_EmployeeId_FinancialYear",
                table: "TaxDeclarationHeaders",
                columns: new[] { "EmployeeId", "FinancialYear" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaxDeclarationItems_ReviewedByEmployeeId",
                table: "TaxDeclarationItems",
                column: "ReviewedByEmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_TaxDeclarationItems_TaxDeclarationHeaderId",
                table: "TaxDeclarationItems",
                column: "TaxDeclarationHeaderId");

            migrationBuilder.CreateIndex(
                name: "IX_TaxDeclarationItems_TaxSectionMasterId",
                table: "TaxDeclarationItems",
                column: "TaxSectionMasterId");

            migrationBuilder.CreateIndex(
                name: "IX_TaxSectionMasters_Code",
                table: "TaxSectionMasters",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaxSlabs_TaxSlabSettingsId",
                table: "TaxSlabs",
                column: "TaxSlabSettingsId");

            migrationBuilder.CreateIndex(
                name: "IX_TaxSlabSettingsList_FinancialYear_Regime",
                table: "TaxSlabSettingsList",
                columns: new[] { "FinancialYear", "Regime" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaxSurchargeSlabs_TaxSlabSettingsId",
                table: "TaxSurchargeSlabs",
                column: "TaxSlabSettingsId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EmployeeSalaryComponents");

            migrationBuilder.DropTable(
                name: "SalaryStructureTemplateItems");

            migrationBuilder.DropTable(
                name: "TaxDeclarationItems");

            migrationBuilder.DropTable(
                name: "TaxSlabs");

            migrationBuilder.DropTable(
                name: "TaxSurchargeSlabs");

            migrationBuilder.DropTable(
                name: "EmployeeSalaryStructures");

            migrationBuilder.DropTable(
                name: "SalaryComponents");

            migrationBuilder.DropTable(
                name: "TaxDeclarationHeaders");

            migrationBuilder.DropTable(
                name: "TaxSectionMasters");

            migrationBuilder.DropTable(
                name: "TaxSlabSettingsList");

            migrationBuilder.DropTable(
                name: "SalaryStructureTemplates");
        }
    }
}
