using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace garge_api.Migrations
{
    /// <inheritdoc />
    public partial class SensorDataCoveringIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SensorData_SensorId",
                table: "SensorData");

            migrationBuilder.DropIndex(
                name: "IX_SensorData_SensorId_Timestamp",
                table: "SensorData");

            migrationBuilder.DropIndex(
                name: "IX_BatteryHealthData_SensorId",
                table: "BatteryHealthData");

            migrationBuilder.CreateIndex(
                name: "IX_SensorData_SensorId_Timestamp",
                table: "SensorData",
                columns: new[] { "SensorId", "Timestamp" })
                .Annotation("Npgsql:IndexInclude", new[] { "Value" });

            migrationBuilder.CreateIndex(
                name: "IX_BatteryHealthData_SensorId_Timestamp",
                table: "BatteryHealthData",
                columns: new[] { "SensorId", "Timestamp" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SensorData_SensorId_Timestamp",
                table: "SensorData");

            migrationBuilder.DropIndex(
                name: "IX_BatteryHealthData_SensorId_Timestamp",
                table: "BatteryHealthData");

            migrationBuilder.CreateIndex(
                name: "IX_SensorData_SensorId",
                table: "SensorData",
                column: "SensorId");

            migrationBuilder.CreateIndex(
                name: "IX_SensorData_SensorId_Timestamp",
                table: "SensorData",
                columns: new[] { "SensorId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_BatteryHealthData_SensorId",
                table: "BatteryHealthData",
                column: "SensorId");
        }
    }
}
