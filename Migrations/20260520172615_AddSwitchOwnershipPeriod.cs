using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace garge_api.Migrations
{
    /// <inheritdoc />
    public partial class AddSwitchOwnershipPeriod : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SwitchOwnershipPeriods",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    SwitchId = table.Column<int>(type: "integer", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SwitchOwnershipPeriods", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SwitchOwnershipPeriods_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SwitchOwnershipPeriods_Switches_SwitchId",
                        column: x => x.SwitchId,
                        principalTable: "Switches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SwitchOwnershipPeriods_SwitchId_StartedAt",
                table: "SwitchOwnershipPeriods",
                columns: new[] { "SwitchId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SwitchOwnershipPeriods_UserId_SwitchId",
                table: "SwitchOwnershipPeriods",
                columns: new[] { "UserId", "SwitchId" });

            // Backfill: every existing direct ownership becomes an open period starting at the epoch
            // sentinel (1970-01-01) so current direct owners keep their full history — no behaviour
            // change at rollout. The resale boundary only takes effect for future re-claims.
            migrationBuilder.Sql(@"
                INSERT INTO ""SwitchOwnershipPeriods"" (""UserId"", ""SwitchId"", ""StartedAt"", ""EndedAt"")
                SELECT ""UserId"", ""SwitchId"", TIMESTAMPTZ '1970-01-01 00:00:00+00', NULL
                FROM ""UserSwitches"";
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SwitchOwnershipPeriods");
        }
    }
}
