using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace garge_api.Migrations
{
    /// <inheritdoc />
    public partial class VatThreshold : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RefundedInOre",
                table: "Orders",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "CreditsInvoiceId",
                table: "Invoices",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Kind",
                table: "Invoices",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "PdfAttemptedAt",
                table: "Invoices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReplacesInvoiceId",
                table: "Invoices",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "VatCorrectedAt",
                table: "Invoices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "VatCorrectionEmailedAt",
                table: "Invoices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "OtherTurnoverInOre",
                table: "AppSettings",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<DateTime>(
                name: "VatCrossedAt",
                table: "AppSettings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "VatCrossingInvoiceId",
                table: "AppSettings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "VatThresholdWarnedPercent",
                table: "AppSettings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.UpdateData(
                table: "AppSettings",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "OtherTurnoverInOre", "VatCrossedAt", "VatCrossingInvoiceId", "VatThresholdWarnedPercent" },
                values: new object[] { 0L, null, null, 0 });

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_CreditsInvoiceId",
                table: "Invoices",
                column: "CreditsInvoiceId",
                unique: true,
                filter: "\"CreditsInvoiceId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_ReplacesInvoiceId",
                table: "Invoices",
                column: "ReplacesInvoiceId",
                unique: true,
                filter: "\"ReplacesInvoiceId\" IS NOT NULL");

            // Orders refunded in full before the refunded amount was stored count as fully refunded.
            migrationBuilder.Sql("""UPDATE "Orders" SET "RefundedInOre" = "TotalInOre" WHERE "Status" = 3;""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Invoices_CreditsInvoiceId",
                table: "Invoices");

            migrationBuilder.DropIndex(
                name: "IX_Invoices_ReplacesInvoiceId",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "RefundedInOre",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "CreditsInvoiceId",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "PdfAttemptedAt",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "ReplacesInvoiceId",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "VatCorrectedAt",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "VatCorrectionEmailedAt",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "OtherTurnoverInOre",
                table: "AppSettings");

            migrationBuilder.DropColumn(
                name: "VatCrossedAt",
                table: "AppSettings");

            migrationBuilder.DropColumn(
                name: "VatCrossingInvoiceId",
                table: "AppSettings");

            migrationBuilder.DropColumn(
                name: "VatThresholdWarnedPercent",
                table: "AppSettings");
        }
    }
}
