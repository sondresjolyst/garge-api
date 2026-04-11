using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace garge_api.Migrations
{
    /// <inheritdoc />
    public partial class FixPendingModelSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GroupSwitches",
                columns: table => new
                {
                    GroupId = table.Column<int>(type: "integer", nullable: false),
                    SwitchId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupSwitches", x => new { x.GroupId, x.SwitchId });
                    table.ForeignKey(
                        name: "FK_GroupSwitches_Groups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "Groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GroupSwitches_Switches_SwitchId",
                        column: x => x.SwitchId,
                        principalTable: "Switches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserSwitchCustomNames",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "text", nullable: false),
                    SwitchId = table.Column<int>(type: "integer", nullable: false),
                    CustomName = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserSwitchCustomNames", x => new { x.UserId, x.SwitchId });
                    table.ForeignKey(
                        name: "FK_UserSwitchCustomNames_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserSwitchCustomNames_Switches_SwitchId",
                        column: x => x.SwitchId,
                        principalTable: "Switches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GroupSwitches_SwitchId",
                table: "GroupSwitches",
                column: "SwitchId");

            migrationBuilder.CreateIndex(
                name: "IX_UserSwitchCustomNames_SwitchId",
                table: "UserSwitchCustomNames",
                column: "SwitchId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GroupSwitches");

            migrationBuilder.DropTable(
                name: "UserSwitchCustomNames");
        }
    }
}
