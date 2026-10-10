using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace garge_api.Migrations
{
    /// <inheritdoc />
    public partial class AddVippsTestWebhookRegistrations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "VippsTestShopWebhookId",
                table: "AppSettings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VippsTestShopWebhookSecret",
                table: "AppSettings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VippsTestSubscriptionWebhookId",
                table: "AppSettings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VippsTestSubscriptionWebhookSecret",
                table: "AppSettings",
                type: "text",
                nullable: true);

            migrationBuilder.UpdateData(
                table: "AppSettings",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "VippsTestShopWebhookId", "VippsTestShopWebhookSecret", "VippsTestSubscriptionWebhookId", "VippsTestSubscriptionWebhookSecret" },
                values: new object[] { null, null, null, null });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "VippsTestShopWebhookId",
                table: "AppSettings");

            migrationBuilder.DropColumn(
                name: "VippsTestShopWebhookSecret",
                table: "AppSettings");

            migrationBuilder.DropColumn(
                name: "VippsTestSubscriptionWebhookId",
                table: "AppSettings");

            migrationBuilder.DropColumn(
                name: "VippsTestSubscriptionWebhookSecret",
                table: "AppSettings");
        }
    }
}
