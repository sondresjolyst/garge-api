using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace garge_api.Migrations
{
    /// <inheritdoc />
    public partial class AddUserSwitchSharePermission : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Every existing UserSwitch row is an owner (sharing did not exist before), so backfill
            // true. New shared rows set IsOwner=false explicitly.
            migrationBuilder.AddColumn<bool>(
                name: "IsOwner",
                table: "UserSwitches",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "Permission",
                table: "UserSwitches",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsOwner",
                table: "UserSwitches");

            migrationBuilder.DropColumn(
                name: "Permission",
                table: "UserSwitches");
        }
    }
}
