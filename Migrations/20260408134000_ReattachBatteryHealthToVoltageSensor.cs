using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace garge_api.Migrations
{
    /// <inheritdoc />
    public partial class ReattachBatteryHealthToVoltageSensor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Re-point BatteryHealth records from the battery sensor to the voltage sensor
            // on the same device (same ParentName).
            migrationBuilder.Sql(@"
                UPDATE ""BatteryHealthData"" bh
                SET ""SensorId"" = v.""Id""
                FROM ""Sensors"" b
                JOIN ""Sensors"" v ON v.""ParentName"" = b.""ParentName"" AND v.""Type"" = 'voltage'
                WHERE bh.""SensorId"" = b.""Id""
                  AND b.""Type"" = 'battery';
            ");

            // Remove any custom-name overrides users may have set for battery sensors.
            migrationBuilder.Sql(@"
                DELETE FROM ""UserSensorCustomNames""
                WHERE ""SensorId"" IN (
                    SELECT ""Id"" FROM ""Sensors"" WHERE ""Type"" = 'battery'
                );
            ");

            // Remove the ASP.NET Identity roles that were auto-created for battery sensors.
            migrationBuilder.Sql(@"
                DELETE FROM ""AspNetRoles""
                WHERE ""Name"" IN (
                    SELECT ""Name"" FROM ""Sensors"" WHERE ""Type"" = 'battery'
                );
            ");

            // Delete the now-redundant battery sensor rows.
            migrationBuilder.Sql(@"
                DELETE FROM ""Sensors"" WHERE ""Type"" = 'battery';
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Intentionally not reversible: re-creating deleted sensor rows and
            // re-pointing BatteryHealth records back would require data we no longer have.
            throw new NotSupportedException(
                "ReattachBatteryHealthToVoltageSensor is not reversible.");
        }
    }
}
