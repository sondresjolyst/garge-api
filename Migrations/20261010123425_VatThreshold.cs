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

            migrationBuilder.AddColumn<DateTime>(
                name: "PdfAttemptedAt",
                table: "Invoices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "VatSupplementIssuedAt",
                table: "Invoices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "VatSupplementNumber",
                table: "Invoices",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "VatSupplementPdf",
                table: "Invoices",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LastVatSupplementNumber",
                table: "AppSettings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

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
                columns: new[] { "LastVatSupplementNumber", "OtherTurnoverInOre", "VatCrossedAt", "VatCrossingInvoiceId", "VatThresholdWarnedPercent" },
                values: new object[] { 0, 0L, null, null, 0 });

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_VatSupplementNumber",
                table: "Invoices",
                column: "VatSupplementNumber",
                unique: true,
                filter: "\"VatSupplementNumber\" IS NOT NULL");

            // Orders refunded in full before the refunded amount was stored count as fully refunded.
            migrationBuilder.Sql("""UPDATE "Orders" SET "RefundedInOre" = "TotalInOre" WHERE "Status" = 3;""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Invoices_VatSupplementNumber",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "RefundedInOre",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "PdfAttemptedAt",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "VatSupplementIssuedAt",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "VatSupplementNumber",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "VatSupplementPdf",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "LastVatSupplementNumber",
                table: "AppSettings");

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
