using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace garge_api.Migrations
{
    /// <inheritdoc />
    public partial class CleanupLegacyBatteryHealthRows : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Drop pre-analyzer "zombie" rows left by the old firmware-driven
            // POST /api/battery-health flow. Identified by all analyzer-computed
            // fields being zero — a real analyzer row always populates at least
            // CurrentVoltage when SensorData exists. Also clears stale
            // BatteryChargeEvents so the analyzer re-derives them cleanly.
            // Safe to rerun; affects only rows the analyzer would overwrite.
            migrationBuilder.Sql(@"
                DELETE FROM ""BatteryHealthData""
                WHERE ""RestingMedian"" = 0
                  AND ""PeakResting"" = 0
                  AND ""CurrentVoltage"" = 0;
            ");

            migrationBuilder.Sql(@"DELETE FROM ""BatteryChargeEvents"";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // No-op: deleted rows are unrecoverable. Re-running the analyzer
            // regenerates BatteryHealthData and BatteryChargeEvents from
            // SensorData, so a rollback is not meaningful.
        }
    }
}
