using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace garge_api.Migrations
{
    /// <inheritdoc />
    public partial class RefundCreditNotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Invoices_CreditsInvoiceId",
                table: "Invoices");

            migrationBuilder.AddColumn<bool>(
                name: "ForRefund",
                table: "Invoices",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "RefundedInOre",
                table: "Invoices",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_CreditsInvoiceId",
                table: "Invoices",
                column: "CreditsInvoiceId",
                unique: true,
                filter: "\"CreditsInvoiceId\" IS NOT NULL AND NOT \"ForRefund\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Invoices_CreditsInvoiceId",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "ForRefund",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "RefundedInOre",
                table: "Invoices");

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_CreditsInvoiceId",
                table: "Invoices",
                column: "CreditsInvoiceId",
                unique: true,
                filter: "\"CreditsInvoiceId\" IS NOT NULL");
        }
    }
}
