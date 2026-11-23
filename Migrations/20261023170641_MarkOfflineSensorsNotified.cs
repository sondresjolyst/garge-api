using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace garge_api.Migrations
{
    /// <inheritdoc />
    public partial class MarkOfflineSensorsNotified : Migration
    {
        /// <summary>
        /// Marks every sensor that is offline at deploy time as already notified, so the new offline email
        /// does not go out in one burst. The back-online check resolves these rows when the sensor reports.
        /// </summary>
        internal const string MarkOfflineNotifiedSql = """
            INSERT INTO "SensorOfflineNotifications" ("UserId", "SensorId", "Kind", "NotifiedAt", "ResolvedAt")
            SELECT us."UserId", us."SensorId", 'offline', now(), NULL
            FROM "UserSensors" us
            JOIN "UserProfiles" up ON up."Id" = us."UserId"
            WHERE (up."EmailNotificationsEnabled" OR up."PushNotificationsEnabled")
              AND COALESCE(
                    (SELECT max(sd."Timestamp") FROM "SensorData" sd WHERE sd."SensorId" = us."SensorId"),
                    '-infinity'::timestamptz
                  ) < now() - make_interval(hours => up."OfflineAlertThresholdHours")
              AND NOT EXISTS (
                    SELECT 1 FROM "SensorOfflineNotifications" n
                    WHERE n."UserId" = us."UserId" AND n."SensorId" = us."SensorId"
                      AND n."ResolvedAt" IS NULL AND n."Kind" IN ('offline', 'security'));
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(MarkOfflineNotifiedSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing to undo. The inserted rows are indistinguishable from real notifications,
            // and deleting them would make the next check email every offline sensor.
        }
    }
}
