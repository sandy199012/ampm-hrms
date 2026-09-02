using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AmpmHrmsPro.Migrations
{
    /// <inheritdoc />
    public partial class WeekOffRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Days",
                table: "WeekOffPolicies");

            migrationBuilder.AddColumn<string>(
                name: "Description",
                table: "WeekOffPolicies",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "WeekOffRules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    WeekOffPolicyId = table.Column<int>(type: "int", nullable: false),
                    DayOfWeek = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    RuleType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Occurrences = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WeekOffRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WeekOffRules_WeekOffPolicies_WeekOffPolicyId",
                        column: x => x.WeekOffPolicyId,
                        principalTable: "WeekOffPolicies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WeekOffRules_WeekOffPolicyId",
                table: "WeekOffRules",
                column: "WeekOffPolicyId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WeekOffRules");

            migrationBuilder.DropColumn(
                name: "Description",
                table: "WeekOffPolicies");

            migrationBuilder.AddColumn<string>(
                name: "Days",
                table: "WeekOffPolicies",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");
        }
    }
}
