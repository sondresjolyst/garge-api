using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace garge_api.Migrations
{
    /// <inheritdoc />
    public partial class VatRegisteredFrom : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "VatRegisteredFrom",
                table: "AppSettings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.UpdateData(
                table: "AppSettings",
                keyColumn: "Id",
                keyValue: 1,
                column: "VatRegisteredFrom",
                value: null);

            // VAT already on: it started with the first invoice that carried VAT, or now if none did.
            migrationBuilder.Sql("""
                UPDATE "AppSettings" SET "VatRegisteredFrom" = COALESCE(
                    (SELECT MIN("IssuedAt") FROM "Invoices" WHERE "VatPercentage" > 0), now())
                WHERE "VatEnabled";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "VatRegisteredFrom",
                table: "AppSettings");
        }
    }
}
