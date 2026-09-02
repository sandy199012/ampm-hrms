using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AmpmHrmsPro.Migrations
{
    /// <inheritdoc />
    public partial class AddKioskDevices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "KioskDevices",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DeviceName = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    LocationLabel = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    ApiKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KioskDevices", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_KioskDevices_ApiKey",
                table: "KioskDevices",
                column: "ApiKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "KioskDevices");
        }
    }
}
