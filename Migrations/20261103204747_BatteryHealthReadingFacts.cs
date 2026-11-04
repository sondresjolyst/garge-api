using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace garge_api.Migrations
{
    /// <inheritdoc />
    public partial class BatteryHealthReadingFacts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Charge rows are kept, since read-time status needs them for every sensor,
            // also an offline one. SelfDischargePctPerWeek starts as null on them. The next
            // analyzer run fills it from the latest final charge onward, and an admin
            // reanalyze fills it on the older rows. It is information only, so status is
            // right from the first read.
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
                name: "SelfDischargePctPerWeek",
                table: "BatteryHealthData");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "BatteryHealthData");

            migrationBuilder.AddColumn<float>(
                name: "SelfDischargePctPerWeek",
                table: "BatteryChargeEvents",
                type: "real",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The dropped values cannot be restored. Existing rows read as learning with
            // no last full charge until the next analyzer run adds a new row.
            migrationBuilder.DropColumn(
                name: "SelfDischargePctPerWeek",
                table: "BatteryChargeEvents");

            migrationBuilder.AddColumn<float>(
                name: "DailyDropPctPerWeek",
                table: "BatteryHealthData",
                type: "real",
                nullable: true);

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

            migrationBuilder.AddColumn<float>(
                name: "SelfDischargePctPerWeek",
                table: "BatteryHealthData",
                type: "real",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "BatteryHealthData",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "learning");
        }
    }
}
