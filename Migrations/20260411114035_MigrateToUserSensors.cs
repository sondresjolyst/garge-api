using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace garge_api.Migrations
{
    /// <inheritdoc />
    public partial class MigrateToUserSensors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UserSensors",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "text", nullable: false),
                    SensorId = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserSensors", x => new { x.UserId, x.SensorId });
                    table.ForeignKey(
                        name: "FK_UserSensors_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserSensors_Sensors_SensorId",
                        column: x => x.SensorId,
                        principalTable: "Sensors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserSensors_SensorId",
                table: "UserSensors",
                column: "SensorId");

            // Backfill: copy existing sensor ownership from AspNetUserRoles → UserSensors
            migrationBuilder.Sql(@"
                INSERT INTO ""UserSensors"" (""UserId"", ""SensorId"", ""CreatedAt"")
                SELECT ur.""UserId"", s.""Id"", NOW()
                FROM ""AspNetUserRoles"" ur
                JOIN ""AspNetRoles"" r ON r.""Id"" = ur.""RoleId""
                JOIN ""Sensors"" s ON s.""Role"" = r.""Name""
                ON CONFLICT (""UserId"", ""SensorId"") DO NOTHING;
            ");

            // Clean up: remove sensor-specific role assignments from AspNetUserRoles
            migrationBuilder.Sql(@"
                DELETE FROM ""AspNetUserRoles""
                WHERE ""RoleId"" IN (
                    SELECT r.""Id"" FROM ""AspNetRoles"" r
                    JOIN ""Sensors"" s ON s.""Role"" = r.""Name""
                );
            ");

            // Clean up: remove sensor-specific role definitions from AspNetRoles
            migrationBuilder.Sql(@"
                DELETE FROM ""AspNetRoles""
                WHERE ""Name"" IN (SELECT ""Role"" FROM ""Sensors"");
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserSensors");
        }
    }
}
