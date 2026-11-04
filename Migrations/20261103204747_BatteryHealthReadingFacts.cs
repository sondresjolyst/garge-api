using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace garge_api.Migrations
{
    /// <inheritdoc />
    public partial class BatteryHealthReadingFacts : Migration
    {
        /// <summary>
        /// Moves each existing row from the time of its run to the time of the reading that plausibly
        /// produced it: the latest valid reading of that sensor at or before the run, within 3 days of
        /// the run, the analyzer's own offline limit. Every other row is deleted. That covers a row
        /// with no earlier reading and a row whose own reading was deleted with nothing recent before
        /// it. Rows are derived, and a new reading writes a new one. Valid here means a plain decimal
        /// of at most 3 integer digits, from 5 to 20, the plausible range the analyzer uses. The regex
        /// keeps a long number and any text that is no number away from the cast. Raw SQL because this
        /// is one set-based statement over rows that EF would otherwise load one by one.
        /// </summary>
        internal const string MoveRowsToReadingTimeSql = """
            WITH readings AS (
                SELECT bh."Id", bh."Timestamp" AS run_time, (
                    SELECT sd."Timestamp"
                    FROM "SensorData" AS sd
                    WHERE sd."SensorId" = bh."SensorId"
                      AND sd."Timestamp" <= bh."Timestamp"
                      AND CASE WHEN sd."Value" ~ '^[0-9]{1,3}([.][0-9]+)?$'
                               THEN sd."Value"::double precision BETWEEN 5 AND 20
                               ELSE FALSE END
                    ORDER BY sd."Timestamp" DESC
                    LIMIT 1) AS reading_time
                FROM "BatteryHealthData" AS bh
            ), removed AS (
                DELETE FROM "BatteryHealthData" AS bh
                USING readings AS r
                WHERE bh."Id" = r."Id"
                  AND (r.reading_time IS NULL OR r.reading_time < r.run_time - INTERVAL '3 days')
            )
            UPDATE "BatteryHealthData" AS bh
            SET "Timestamp" = r.reading_time
            FROM readings AS r
            WHERE bh."Id" = r."Id"
              AND r.reading_time >= r.run_time - INTERVAL '3 days';
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // On the live schema this runs together with BatteryChargeHistory, which deletes all
            // charge rows first. Status is therefore learning until each sensor's first analyzer
            // run rebuilds its charges, with SelfDischargePctPerWeek filled in. An offline sensor
            // needs an admin reanalyze. Where charge rows were already rebuilt, they stay, and an
            // admin reanalyze fills SelfDischargePctPerWeek on those older than the final charge.
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
