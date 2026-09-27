using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace garge_api.Migrations
{
    /// <inheritdoc />
    public partial class AddPipelineHealth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PipelineGaps",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    AdminNotifiedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AllClearSentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PipelineGaps", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PipelineHeartbeats",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    LastHeartbeatAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    MqttConnected = table.Column<bool>(type: "boolean", nullable: false),
                    LastHealthyHeartbeatAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastDetectorTickAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PipelineHeartbeats", x => x.Id);
                    table.CheckConstraint("CK_PipelineHeartbeats_SingleRow", "\"Id\" = 1");
                });

            migrationBuilder.CreateIndex(
                name: "IX_PipelineGaps_EndedAt",
                table: "PipelineGaps",
                column: "EndedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PipelineGaps");

            migrationBuilder.DropTable(
                name: "PipelineHeartbeats");
        }
    }
}
