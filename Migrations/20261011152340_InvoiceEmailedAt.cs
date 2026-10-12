using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace garge_api.Migrations
{
    /// <inheritdoc />
    public partial class InvoiceEmailedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "EmailedAt",
                table: "Invoices",
                type: "timestamp with time zone",
                nullable: true);

            // Invoices from before this column with a PDF were emailed when it was made, so the retry job leaves them be.
            // One without a PDF is emailed when the retry job makes it.
            // A data backfill has no EF migration operation, so it is SQL.
            migrationBuilder.Sql("""UPDATE "Invoices" SET "EmailedAt" = "IssuedAt" WHERE length("PdfData") > 0;""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EmailedAt",
                table: "Invoices");
        }
    }
}
