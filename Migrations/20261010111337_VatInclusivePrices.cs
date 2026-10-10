using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace garge_api.Migrations
{
    /// <inheritdoc />
    public partial class VatInclusivePrices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "VatPercentage",
                table: "Invoices",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Prices become what the customer pays. With VAT on, the stored prices excluded VAT, so
            // they take the VAT in. With VAT off nothing changes.
            migrationBuilder.Sql("""
                UPDATE "ShopItems" SET "PriceInOre" = ROUND("PriceInOre" * 1.25)
                WHERE EXISTS (SELECT 1 FROM "AppSettings" WHERE "Id" = 1 AND "VatEnabled");
                UPDATE "Products" SET "PriceInOre" = ROUND("PriceInOre" * 1.25)
                WHERE EXISTS (SELECT 1 FROM "AppSettings" WHERE "Id" = 1 AND "VatEnabled");
                """);

            // Order invoices take the rate their lines were sold at. Subscription invoices take the
            // current setting, the only record there is of the rate at the time.
            migrationBuilder.Sql("""
                UPDATE "Invoices" i SET "VatPercentage" = COALESCE(
                    (SELECT MAX(oi."VatPercentage") FROM "OrderItems" oi WHERE oi."OrderId" = i."OrderId"), 0)
                WHERE i."OrderId" IS NOT NULL;
                UPDATE "Invoices" SET "VatPercentage" = 25
                WHERE "SubscriptionId" IS NOT NULL
                  AND EXISTS (SELECT 1 FROM "AppSettings" WHERE "Id" = 1 AND "VatEnabled");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE "ShopItems" SET "PriceInOre" = ROUND("PriceInOre" / 1.25)
                WHERE EXISTS (SELECT 1 FROM "AppSettings" WHERE "Id" = 1 AND "VatEnabled");
                UPDATE "Products" SET "PriceInOre" = ROUND("PriceInOre" / 1.25)
                WHERE EXISTS (SELECT 1 FROM "AppSettings" WHERE "Id" = 1 AND "VatEnabled");
                """);

            migrationBuilder.DropColumn(
                name: "VatPercentage",
                table: "Invoices");
        }
    }
}
