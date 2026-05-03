using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace garge_api.Migrations
{
    /// <inheritdoc />
    public partial class FixOfflineThresholdDefault : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Fix existing rows where EF migration set defaultValue: 0 instead of 4
            migrationBuilder.Sql(
                "UPDATE \"UserProfiles\" SET \"OfflineAlertThresholdHours\" = 4 WHERE \"OfflineAlertThresholdHours\" = 0");

            migrationBuilder.AlterColumn<int>(
                name: "OfflineAlertThresholdHours",
                table: "UserProfiles",
                type: "integer",
                nullable: false,
                defaultValue: 4,
                oldClrType: typeof(int),
                oldType: "integer",
                oldDefaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "OfflineAlertThresholdHours",
                table: "UserProfiles",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldDefaultValue: 4);
        }
    }
}
