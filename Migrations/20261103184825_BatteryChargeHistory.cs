using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace garge_api.Migrations
{
    /// <inheritdoc />
    public partial class BatteryChargeHistory : Migration
    {
        internal const string DeleteChargeEventsSql = """
            DELETE FROM "BatteryChargeEvents";
            """;

        internal const string ClearZeroRestingSql = """
            UPDATE "BatteryHealthData" SET "RestingMedian" = NULL WHERE "RestingMedian" = 0;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Charge events are derived from readings. They are deleted so they can be
            // rebuilt from each sensor's whole reading history with IsFull and
            // SettledVoltage filled in. An online sensor rebuilds on its first analyzer
            // run. An offline sensor rebuilds on an admin reanalyze or when it reports again.
            migrationBuilder.Sql(DeleteChargeEventsSql);

            migrationBuilder.DropColumn(
                name: "PeakResting",
                table: "BatteryHealthData");

            migrationBuilder.DropColumn(
                name: "PeakRatio",
                table: "BatteryChargeEvents");

            migrationBuilder.DropColumn(
                name: "ChargeAcceptanceRatio",
                table: "BatteryHealthData");

            migrationBuilder.AlterColumn<float>(
                name: "RestingMedian",
                table: "BatteryHealthData",
                type: "real",
                nullable: true,
                oldClrType: typeof(float),
                oldType: "real");

            migrationBuilder.AddColumn<bool>(
                name: "IsFull",
                table: "BatteryChargeEvents",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<float>(
                name: "SettledVoltage",
                table: "BatteryChargeEvents",
                type: "real",
                nullable: true);

            migrationBuilder.AddColumn<float>(
                name: "SelfDischargePctPerWeek",
                table: "BatteryHealthData",
                type: "real",
                nullable: true);

            // A resting voltage is unknown when no rest readings exist, which is now null and never 0.
            migrationBuilder.Sql(ClearZeroRestingSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsFull",
                table: "BatteryChargeEvents");

            migrationBuilder.DropColumn(
                name: "SettledVoltage",
                table: "BatteryChargeEvents");

            migrationBuilder.DropColumn(
                name: "SelfDischargePctPerWeek",
                table: "BatteryHealthData");

            migrationBuilder.AddColumn<float>(
                name: "ChargeAcceptanceRatio",
                table: "BatteryHealthData",
                type: "real",
                nullable: true);

            migrationBuilder.AlterColumn<float>(
                name: "RestingMedian",
                table: "BatteryHealthData",
                type: "real",
                nullable: false,
                defaultValue: 0f,
                oldClrType: typeof(float),
                oldType: "real",
                oldNullable: true);

            migrationBuilder.AddColumn<float>(
                name: "PeakResting",
                table: "BatteryHealthData",
                type: "real",
                nullable: false,
                defaultValue: 0f);

            migrationBuilder.AddColumn<float>(
                name: "PeakRatio",
                table: "BatteryChargeEvents",
                type: "real",
                nullable: false,
                defaultValue: 0f);
        }
    }
}
