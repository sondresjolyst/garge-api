using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace garge_api.Migrations
{
    /// <summary>
    /// Replaces the SensorData and BatteryHealthData indexes with (SensorId, Timestamp) indexes, the
    /// SensorData one covering Value. Raw SQL, since EF has no concurrent index operations. Each new
    /// index is built concurrently before the old one is dropped concurrently, outside a transaction,
    /// so readings keep being written and read while the indexes build. Every step is safe to run
    /// again after a failed attempt, which can leave an invalid index behind.
    /// </summary>
    public partial class SensorDataCoveringIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            Concurrently(migrationBuilder,
                """DROP INDEX CONCURRENTLY IF EXISTS "IX_SensorData_SensorId_Timestamp_Value";""",
                """CREATE INDEX CONCURRENTLY "IX_SensorData_SensorId_Timestamp_Value" ON "SensorData" ("SensorId", "Timestamp") INCLUDE ("Value");""",
                """DROP INDEX CONCURRENTLY IF EXISTS "IX_SensorData_SensorId_Timestamp";""",
                """DROP INDEX CONCURRENTLY IF EXISTS "IX_SensorData_SensorId";""",
                """DROP INDEX CONCURRENTLY IF EXISTS "IX_BatteryHealthData_SensorId_Timestamp";""",
                """CREATE INDEX CONCURRENTLY "IX_BatteryHealthData_SensorId_Timestamp" ON "BatteryHealthData" ("SensorId", "Timestamp");""",
                """DROP INDEX CONCURRENTLY IF EXISTS "IX_BatteryHealthData_SensorId";""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            Concurrently(migrationBuilder,
                """DROP INDEX CONCURRENTLY IF EXISTS "IX_SensorData_SensorId_Timestamp";""",
                """CREATE INDEX CONCURRENTLY "IX_SensorData_SensorId_Timestamp" ON "SensorData" ("SensorId", "Timestamp");""",
                """DROP INDEX CONCURRENTLY IF EXISTS "IX_SensorData_SensorId";""",
                """CREATE INDEX CONCURRENTLY "IX_SensorData_SensorId" ON "SensorData" ("SensorId");""",
                """DROP INDEX CONCURRENTLY IF EXISTS "IX_SensorData_SensorId_Timestamp_Value";""",
                """DROP INDEX CONCURRENTLY IF EXISTS "IX_BatteryHealthData_SensorId";""",
                """CREATE INDEX CONCURRENTLY "IX_BatteryHealthData_SensorId" ON "BatteryHealthData" ("SensorId");""",
                """DROP INDEX CONCURRENTLY IF EXISTS "IX_BatteryHealthData_SensorId_Timestamp";""");
        }

        /// <summary>Runs each statement on its own, outside a transaction, as concurrent index operations require.</summary>
        private static void Concurrently(MigrationBuilder migrationBuilder, params string[] statements)
        {
            foreach (var sql in statements) migrationBuilder.Sql(sql, suppressTransaction: true);
        }
    }
}
