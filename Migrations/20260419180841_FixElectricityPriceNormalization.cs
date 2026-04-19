using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace garge_api.Migrations
{
    /// <inheritdoc />
    public partial class FixElectricityPriceNormalization : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The previous migration (DeduplicateAndNormalizeElectricityPrices) used
            // DATE_TRUNC('day', DeliveryStart AT TIME ZONE 'UTC') which floors to UTC midnight.
            // This produced wrong dates for Norway-local timestamps: e.g. Feb 1 stored as
            // 2026-01-31T23:00:00Z (UTC+1) was truncated to 2026-01-31 instead of 2026-02-01.
            //
            // Fix: add 12 hours before truncating to round to the nearest UTC day, which
            // correctly handles UTC offsets up to ±12h (Norway is UTC+1/+2).

            migrationBuilder.DropIndex(
                name: "IX_StoredElectricityPrices_Area_Resolution_DeliveryStart",
                table: "StoredElectricityPrices");

            migrationBuilder.Sql(@"
                UPDATE ""StoredElectricityPrices""
                SET
                    ""DeliveryStart"" = DATE_TRUNC('day', (""DeliveryStart"" + INTERVAL '12 hours') AT TIME ZONE 'UTC') AT TIME ZONE 'UTC',
                    ""DeliveryEnd""   = DATE_TRUNC('day', (""DeliveryEnd""   + INTERVAL '12 hours') AT TIME ZONE 'UTC') AT TIME ZONE 'UTC'
                WHERE ""Resolution"" IN ('DAILY', 'MONTHLY');
            ");

            migrationBuilder.Sql(@"
                DELETE FROM ""StoredElectricityPrices""
                WHERE ""Id"" IN (
                    SELECT ""Id""
                    FROM (
                        SELECT
                            ""Id"",
                            ROW_NUMBER() OVER (
                                PARTITION BY ""Area"", ""Resolution"", ""DeliveryStart""
                                ORDER BY ""FetchedAt"" DESC
                            ) AS rn
                        FROM ""StoredElectricityPrices""
                    ) ranked
                    WHERE rn > 1
                );
            ");

            migrationBuilder.CreateIndex(
                name: "IX_StoredElectricityPrices_Area_Resolution_DeliveryStart",
                table: "StoredElectricityPrices",
                columns: new[] { "Area", "Resolution", "DeliveryStart" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_StoredElectricityPrices_Area_Resolution_DeliveryStart",
                table: "StoredElectricityPrices");

            migrationBuilder.CreateIndex(
                name: "IX_StoredElectricityPrices_Area_Resolution_DeliveryStart",
                table: "StoredElectricityPrices",
                columns: new[] { "Area", "Resolution", "DeliveryStart" },
                unique: true);
        }
    }
}
