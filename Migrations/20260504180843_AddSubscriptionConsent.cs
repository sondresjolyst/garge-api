using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace garge_api.Migrations
{
    /// <inheritdoc />
    public partial class AddSubscriptionConsent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Guard: AddAppSettings migration had an empty Up() in dev — create the table if it wasn't created.
            migrationBuilder.Sql(@"
                CREATE TABLE IF NOT EXISTS ""AppSettings"" (
                    ""Id"" integer NOT NULL DEFAULT 1,
                    ""CookieBannerEnabled"" boolean NOT NULL DEFAULT true,
                    CONSTRAINT ""PK_AppSettings"" PRIMARY KEY (""Id""),
                    CONSTRAINT ""CK_AppSettings_SingleRow"" CHECK (""Id"" = 1)
                );
                INSERT INTO ""AppSettings"" (""Id"", ""CookieBannerEnabled"") VALUES (1, true) ON CONFLICT DO NOTHING;
            ");

            migrationBuilder.AddColumn<DateTime>(
                name: "ConsentAcceptedAt",
                table: "Subscriptions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ConsentIp",
                table: "Subscriptions",
                type: "character varying(45)",
                maxLength: 45,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "VatEnabled",
                table: "AppSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.UpdateData(
                table: "AppSettings",
                keyColumn: "Id",
                keyValue: 1,
                column: "VatEnabled",
                value: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ConsentAcceptedAt",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "ConsentIp",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "VatEnabled",
                table: "AppSettings");
        }
    }
}
