using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace garge_api.Migrations
{
    /// <inheritdoc />
    public partial class AddSensorOwnershipPeriod : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SensorOwnershipPeriods",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    SensorId = table.Column<int>(type: "integer", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SensorOwnershipPeriods", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SensorOwnershipPeriods_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SensorOwnershipPeriods_Sensors_SensorId",
                        column: x => x.SensorId,
                        principalTable: "Sensors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SensorOwnershipPeriods_SensorId_StartedAt",
                table: "SensorOwnershipPeriods",
                columns: new[] { "SensorId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SensorOwnershipPeriods_UserId_SensorId",
                table: "SensorOwnershipPeriods",
                columns: new[] { "UserId", "SensorId" });

            // Backfill: every existing ownership becomes an open period starting at the epoch
            // sentinel (1970-01-01) so current owners keep their full history — no behaviour
            // change at rollout. The resale boundary only takes effect for future re-claims.
            migrationBuilder.Sql(@"
                INSERT INTO ""SensorOwnershipPeriods"" (""UserId"", ""SensorId"", ""StartedAt"", ""EndedAt"")
                SELECT ""UserId"", ""SensorId"", TIMESTAMPTZ '1970-01-01 00:00:00+00', NULL
                FROM ""UserSensors"";
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SensorOwnershipPeriods");
        }
    }
}
