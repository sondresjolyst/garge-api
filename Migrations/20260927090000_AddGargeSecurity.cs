using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace garge_api.Migrations
{
    /// <inheritdoc />
    public partial class AddGargeSecurity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SensorOfflineNotifications_UserId_SensorId_ResolvedAt",
                table: "SensorOfflineNotifications");

            migrationBuilder.AddColumn<bool>(
                name: "EmailNotificationsEnabled",
                table: "UserProfiles",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "SensorOfflineNotifications",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "offline");

            migrationBuilder.CreateTable(
                name: "SensorSecurityStates",
                columns: table => new
                {
                    SensorId = table.Column<int>(type: "integer", nullable: false),
                    RequestedSleepSeconds = table.Column<int>(type: "integer", nullable: false),
                    RequestedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FloorMillivolts = table.Column<int>(type: "integer", nullable: true),
                    AppliedSleepSeconds = table.Column<int>(type: "integer", nullable: true),
                    AppliedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SecurityModeReported = table.Column<bool>(type: "boolean", nullable: false),
                    ReportedFirmwareVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    ArmedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    OfflineDisarmedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastPublishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SensorSecurityStates", x => x.SensorId);
                    table.ForeignKey(
                        name: "FK_SensorSecurityStates_Sensors_SensorId",
                        column: x => x.SensorId,
                        principalTable: "Sensors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserSensorSecurities",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "text", nullable: false),
                    SensorId = table.Column<int>(type: "integer", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    ThresholdMinutes = table.Column<int>(type: "integer", nullable: false),
                    EnabledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EnforcingAutomationRuleId = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserSensorSecurities", x => new { x.UserId, x.SensorId });
                    table.ForeignKey(
                        name: "FK_UserSensorSecurities_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserSensorSecurities_Sensors_SensorId",
                        column: x => x.SensorId,
                        principalTable: "Sensors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SensorOfflineNotifications_UserId_SensorId_Kind_ResolvedAt",
                table: "SensorOfflineNotifications",
                columns: new[] { "UserId", "SensorId", "Kind", "ResolvedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SensorSecurityStates_ArmedAt",
                table: "SensorSecurityStates",
                column: "ArmedAt");

            migrationBuilder.CreateIndex(
                name: "IX_UserSensorSecurities_Enabled_SensorId",
                table: "UserSensorSecurities",
                columns: new[] { "Enabled", "SensorId" });

            migrationBuilder.CreateIndex(
                name: "IX_UserSensorSecurities_SensorId",
                table: "UserSensorSecurities",
                column: "SensorId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SensorSecurityStates");

            migrationBuilder.DropTable(
                name: "UserSensorSecurities");

            migrationBuilder.DropIndex(
                name: "IX_SensorOfflineNotifications_UserId_SensorId_Kind_ResolvedAt",
                table: "SensorOfflineNotifications");

            migrationBuilder.DropColumn(
                name: "EmailNotificationsEnabled",
                table: "UserProfiles");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "SensorOfflineNotifications");

            migrationBuilder.CreateIndex(
                name: "IX_SensorOfflineNotifications_UserId_SensorId_ResolvedAt",
                table: "SensorOfflineNotifications",
                columns: new[] { "UserId", "SensorId", "ResolvedAt" });
        }
    }
}
