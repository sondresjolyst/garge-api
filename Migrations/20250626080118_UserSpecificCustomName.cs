using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace garge_api.Migrations
{
    /// <inheritdoc />
    public partial class UserSpecificCustomName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CustomName",
                table: "Sensors");

            migrationBuilder.CreateTable(
                name: "UserSensorCustomNames",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "text", nullable: false),
                    SensorId = table.Column<int>(type: "integer", nullable: false),
                    CustomName = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserSensorCustomNames", x => new { x.UserId, x.SensorId });
                    table.ForeignKey(
                        name: "FK_UserSensorCustomNames_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserSensorCustomNames_Sensors_SensorId",
                        column: x => x.SensorId,
                        principalTable: "Sensors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserSensorCustomNames_SensorId",
                table: "UserSensorCustomNames",
                column: "SensorId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserSensorCustomNames");

            migrationBuilder.AddColumn<string>(
                name: "CustomName",
                table: "Sensors",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);
        }
    }
}
