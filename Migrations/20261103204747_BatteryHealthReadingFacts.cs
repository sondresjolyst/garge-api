using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace garge_api.Migrations
{
    /// <inheritdoc />
    public partial class BatteryHealthReadingFacts : Migration
    {
        /// <summary>
        /// Moves each existing row from the time of its run to the time of the reading it holds, the
        /// latest valid reading of that sensor at or before the run. Valid here means a plain decimal
        /// from 5 to 20, the plausible range the analyzer uses. The CASE keeps the cast away from text
        /// that is no number. A row with no such reading keeps its run time, which means its readings
        /// have been deleted and no reading time can be known. Raw SQL because this is one set-based
        /// update over rows that EF would otherwise load one by one.
        /// </summary>
        internal const string MoveRowsToReadingTimeSql = """
            UPDATE "BatteryHealthData" AS bh
            SET "Timestamp" = COALESCE((
                SELECT sd."Timestamp"
                FROM "SensorData" AS sd
                WHERE sd."SensorId" = bh."SensorId"
                  AND sd."Timestamp" <= bh."Timestamp"
                  AND CASE WHEN sd."Value" ~ '^[0-9]+([.][0-9]+)?$'
                           THEN sd."Value"::double precision BETWEEN 5 AND 20
                           ELSE FALSE END
                ORDER BY sd."Timestamp" DESC
                LIMIT 1), bh."Timestamp");
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Charge rows are kept, since read-time status needs them for every sensor,
            // also an offline one. SelfDischargePctPerWeek starts as null on them. The next
            // analyzer run fills it from the latest final charge onward, and an admin
            // reanalyze fills it on the older rows. It is information only, so status is
            // right from the first read.
            migrationBuilder.Sql(MoveRowsToReadingTimeSql);

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
            // Run times cannot be restored either, so Timestamp keeps the reading time.
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
