using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace garge_api.Migrations
{
    /// <inheritdoc />
    public partial class RemoveLegacyAutomationFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SensorType",
                table: "AutomationRules");

            migrationBuilder.DropColumn(
                name: "SensorId",
                table: "AutomationRules");

            migrationBuilder.DropColumn(
                name: "Condition",
                table: "AutomationRules");

            migrationBuilder.DropColumn(
                name: "Threshold",
                table: "AutomationRules");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SensorType",
                table: "AutomationRules",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SensorId",
                table: "AutomationRules",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Condition",
                table: "AutomationRules",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "Threshold",
                table: "AutomationRules",
                type: "double precision",
                nullable: true);
        }
    }
}