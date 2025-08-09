using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace garge_api.Migrations
{
    /// <inheritdoc />
    public partial class addSensorParentName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ParentName",
                table: "Sensors",
                type: "text",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ParentName",
                table: "Sensors");
        }
    }
}
