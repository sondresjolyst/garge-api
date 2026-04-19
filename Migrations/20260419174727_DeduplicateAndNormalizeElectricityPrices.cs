using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace garge_api.Migrations
{
    /// <inheritdoc />
    public partial class DeduplicateAndNormalizeElectricityPrices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Step 1: Drop the unique index so we can manipulate duplicate rows freely
            migrationBuilder.DropIndex(
                name: "IX_StoredElectricityPrices_Area_Resolution_DeliveryStart",
                table: "StoredElectricityPrices");

            // Step 2: For DAILY and MONTHLY rows, normalize DeliveryStart and DeliveryEnd to
            // UTC midnight of their calendar date (DATE_TRUNC('day', ...) AT TIME ZONE 'UTC').
            // This makes rows created on a UTC+2 machine (22:00 prev day) and rows created on a
            // UTC Docker container (00:00) converge to the same timestamp.
            migrationBuilder.Sql(@"
                UPDATE ""StoredElectricityPrices""
                SET
                    ""DeliveryStart"" = DATE_TRUNC('day', ""DeliveryStart"" AT TIME ZONE 'UTC') AT TIME ZONE 'UTC',
                    ""DeliveryEnd""   = DATE_TRUNC('day', ""DeliveryEnd""   AT TIME ZONE 'UTC') AT TIME ZONE 'UTC'
                WHERE ""Resolution"" IN ('DAILY', 'MONTHLY');
            ");

            // Step 3: Delete duplicate rows keeping the one with the latest FetchedAt per
            // (Area, Resolution, DeliveryStart) group.
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

            // Step 4: Recreate the unique index now that duplicates are gone
            migrationBuilder.CreateIndex(
                name: "IX_StoredElectricityPrices_Area_Resolution_DeliveryStart",
                table: "StoredElectricityPrices",
                columns: new[] { "Area", "Resolution", "DeliveryStart" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Normalization is non-reversible; we can only restore the index
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
