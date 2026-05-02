using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace garge_api.Migrations
{
    /// <inheritdoc />
    public partial class AddWebhookSubscriptionSecret : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "EmailVerificationCode",
                table: "AspNetUsers",
                newName: "EmailVerificationCodeHash");

            migrationBuilder.AddColumn<string>(
                name: "UserId",
                table: "WebhookSubscriptions",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "WebhookSecret",
                table: "WebhookSubscriptions",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PasswordResetAttempts",
                table: "AspNetUsers",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UserId",
                table: "WebhookSubscriptions");

            migrationBuilder.DropColumn(
                name: "WebhookSecret",
                table: "WebhookSubscriptions");

            migrationBuilder.DropColumn(
                name: "PasswordResetAttempts",
                table: "AspNetUsers");

            migrationBuilder.RenameColumn(
                name: "EmailVerificationCodeHash",
                table: "AspNetUsers",
                newName: "EmailVerificationCode");
        }
    }
}
