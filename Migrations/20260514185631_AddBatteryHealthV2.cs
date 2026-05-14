using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace garge_api.Migrations
{
    /// <inheritdoc />
    public partial class AddBatteryHealthV2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<float>(
                name: "CalibrationOffsetV",
                table: "Sensors",
                type: "real",
                nullable: true);

            migrationBuilder.AddColumn<float>(
                name: "ChargeAcceptanceRatio",
                table: "BatteryHealthData",
                type: "real",
                nullable: true);

            migrationBuilder.AddColumn<float>(
                name: "CurrentVoltage",
                table: "BatteryHealthData",
                type: "real",
                nullable: false,
                defaultValue: 0f);

            migrationBuilder.AddColumn<float>(
                name: "DailyDropPctPerWeek",
                table: "BatteryHealthData",
                type: "real",
                nullable: false,
                defaultValue: 0f);

            migrationBuilder.AddColumn<int>(
                name: "FullChargesLast30d",
                table: "BatteryHealthData",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastFullChargeAt",
                table: "BatteryHealthData",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<float>(
                name: "LastFullChargePeak",
                table: "BatteryHealthData",
                type: "real",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "OnChargerNow",
                table: "BatteryHealthData",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<float>(
                name: "PeakResting",
                table: "BatteryHealthData",
                type: "real",
                nullable: false,
                defaultValue: 0f);

            migrationBuilder.AddColumn<float>(
                name: "RestingMedian",
                table: "BatteryHealthData",
                type: "real",
                nullable: false,
                defaultValue: 0f);

            migrationBuilder.AddColumn<float>(
                name: "VoltageMin24h",
                table: "BatteryHealthData",
                type: "real",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BatteryChargeEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SensorId = table.Column<int>(type: "integer", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PeakVoltage = table.Column<float>(type: "real", nullable: false),
                    RestingAtTime = table.Column<float>(type: "real", nullable: false),
                    PeakRatio = table.Column<float>(type: "real", nullable: false),
                    DurationMinutes = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BatteryChargeEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BatteryChargeEvents_Sensors_SensorId",
                        column: x => x.SensorId,
                        principalTable: "Sensors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BatteryChargeEvents_SensorId_StartedAt",
                table: "BatteryChargeEvents",
                columns: new[] { "SensorId", "StartedAt" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BatteryChargeEvents");

            migrationBuilder.DropColumn(
                name: "CalibrationOffsetV",
                table: "Sensors");

            migrationBuilder.DropColumn(
                name: "ChargeAcceptanceRatio",
                table: "BatteryHealthData");

            migrationBuilder.DropColumn(
                name: "CurrentVoltage",
                table: "BatteryHealthData");

            migrationBuilder.DropColumn(
                name: "DailyDropPctPerWeek",
                table: "BatteryHealthData");

            migrationBuilder.DropColumn(
                name: "FullChargesLast30d",
                table: "BatteryHealthData");

            migrationBuilder.DropColumn(
                name: "LastFullChargeAt",
                table: "BatteryHealthData");

            migrationBuilder.DropColumn(
                name: "LastFullChargePeak",
                table: "BatteryHealthData");

            migrationBuilder.DropColumn(
                name: "OnChargerNow",
                table: "BatteryHealthData");

            migrationBuilder.DropColumn(
                name: "PeakResting",
                table: "BatteryHealthData");

            migrationBuilder.DropColumn(
                name: "RestingMedian",
                table: "BatteryHealthData");

            migrationBuilder.DropColumn(
                name: "VoltageMin24h",
                table: "BatteryHealthData");
        }
    }
}
