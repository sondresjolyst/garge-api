using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace garge_api.Migrations
{
    /// <inheritdoc />
    public partial class AddOdometerToSensorActivity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "OdometerKm",
                table: "SensorActivities",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OdometerKm",
                table: "SensorActivities");
        }
    }
}
