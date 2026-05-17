using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace garge_api.Migrations
{
    /// <inheritdoc />
    public partial class DropLegacyBatteryHealthFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Baseline",
                table: "BatteryHealthData");

            migrationBuilder.DropColumn(
                name: "ChargesRecorded",
                table: "BatteryHealthData");

            migrationBuilder.DropColumn(
                name: "DropPct",
                table: "BatteryHealthData");

            migrationBuilder.DropColumn(
                name: "LastCharge",
                table: "BatteryHealthData");

            migrationBuilder.DropColumn(
                name: "LastChargedAt",
                table: "BatteryHealthData");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<float>(
                name: "Baseline",
                table: "BatteryHealthData",
                type: "real",
                nullable: false,
                defaultValue: 0f);

            migrationBuilder.AddColumn<int>(
                name: "ChargesRecorded",
                table: "BatteryHealthData",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<float>(
                name: "DropPct",
                table: "BatteryHealthData",
                type: "real",
                nullable: false,
                defaultValue: 0f);

            migrationBuilder.AddColumn<float>(
                name: "LastCharge",
                table: "BatteryHealthData",
                type: "real",
                nullable: false,
                defaultValue: 0f);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastChargedAt",
                table: "BatteryHealthData",
                type: "timestamp with time zone",
                nullable: true);
        }
    }
}
