using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace garge_api.Migrations
{
    /// <inheritdoc />
    public partial class AddAnonymizedTelemetry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AnonymizedSeries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SourceType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CalibrationOffsetV = table.Column<float>(type: "real", nullable: true),
                    AnonymizedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnonymizedSeries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AnonymizedReadings",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SeriesId = table.Column<long>(type: "bigint", nullable: false),
                    Value = table.Column<double>(type: "double precision", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnonymizedReadings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AnonymizedReadings_AnonymizedSeries_SeriesId",
                        column: x => x.SeriesId,
                        principalTable: "AnonymizedSeries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AnonymizedReadings_SeriesId_Timestamp",
                table: "AnonymizedReadings",
                columns: new[] { "SeriesId", "Timestamp" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AnonymizedReadings");

            migrationBuilder.DropTable(
                name: "AnonymizedSeries");
        }
    }
}
