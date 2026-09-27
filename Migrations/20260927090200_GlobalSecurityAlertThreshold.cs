using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace garge_api.Migrations
{
    /// <inheritdoc />
    public partial class GlobalSecurityAlertThreshold : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ThresholdMinutes",
                table: "UserSensorSecurities");

            migrationBuilder.AddColumn<int>(
                name: "SecurityAlertThresholdMinutes",
                table: "AppSettings",
                type: "integer",
                nullable: false,
                defaultValue: 25);

            migrationBuilder.UpdateData(
                table: "AppSettings",
                keyColumn: "Id",
                keyValue: 1,
                column: "SecurityAlertThresholdMinutes",
                value: 25);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SecurityAlertThresholdMinutes",
                table: "AppSettings");

            migrationBuilder.AddColumn<int>(
                name: "ThresholdMinutes",
                table: "UserSensorSecurities",
                type: "integer",
                nullable: false,
                defaultValue: 25);
        }
    }
}
