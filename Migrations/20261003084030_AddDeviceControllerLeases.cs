using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace garge_api.Migrations
{
    /// <inheritdoc />
    public partial class AddDeviceControllerLeases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DeviceControllers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Target = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ControllerDeviceName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    LeaseExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSeenFromTarget = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceControllers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DeviceDesiredStates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Target = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DesiredState = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    DesiredStateAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ObservedState = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    ObservedStateAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    Settled = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceDesiredStates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceControllers_Target",
                table: "DeviceControllers",
                column: "Target",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeviceDesiredStates_Target",
                table: "DeviceDesiredStates",
                column: "Target",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeviceControllers");

            migrationBuilder.DropTable(
                name: "DeviceDesiredStates");
        }
    }
}
