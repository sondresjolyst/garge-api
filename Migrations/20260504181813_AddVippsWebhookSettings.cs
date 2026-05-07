using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace garge_api.Migrations
{
    /// <inheritdoc />
    public partial class AddVippsWebhookSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "VippsShopWebhookId",
                table: "AppSettings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VippsShopWebhookSecret",
                table: "AppSettings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VippsSubscriptionWebhookId",
                table: "AppSettings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VippsSubscriptionWebhookSecret",
                table: "AppSettings",
                type: "text",
                nullable: true);

            migrationBuilder.UpdateData(
                table: "AppSettings",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "VippsShopWebhookId", "VippsShopWebhookSecret", "VippsSubscriptionWebhookId", "VippsSubscriptionWebhookSecret" },
                values: new object[] { null, null, null, null });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "VippsShopWebhookId",
                table: "AppSettings");

            migrationBuilder.DropColumn(
                name: "VippsShopWebhookSecret",
                table: "AppSettings");

            migrationBuilder.DropColumn(
                name: "VippsSubscriptionWebhookId",
                table: "AppSettings");

            migrationBuilder.DropColumn(
                name: "VippsSubscriptionWebhookSecret",
                table: "AppSettings");
        }
    }
}
