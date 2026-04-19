using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace garge_api.Migrations
{
    /// <inheritdoc />
    public partial class CleanupBadElectricityPriceRows : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The FixElectricityPriceNormalization migration applied DATE_TRUNC + 12h to round
            // Norway-local timestamps. However, rows that were already at UTC midnight on the
            // WRONG day (created by the prior DeduplicateAndNormalizeElectricityPrices migration)
            // could not be fixed by adding 12h — 2026-01-31T00:00:00Z + 12h still truncates to
            // 2026-01-31, not 2026-02-01. So those bad rows remain.
            //
            // MONTHLY entries from Nord Pool always have deliveryStart on the 1st of the month.
            // Any row where the UTC day is not 1 is a wrong-date artifact.
            migrationBuilder.Sql(@"
                DELETE FROM ""StoredElectricityPrices""
                WHERE ""Resolution"" = 'MONTHLY'
                  AND EXTRACT(DAY FROM ""DeliveryStart"" AT TIME ZONE 'UTC') != 1;
            ");

            // DAILY rows from a Norway-local run were shifted one day back by the prior
            // normalization (e.g. Apr 11 became Apr 10). Delete all DAILY rows for a clean slate;
            // they will be re-fetched correctly on the next service startup via FetchAllOnStartupAsync.
            migrationBuilder.Sql(@"
                DELETE FROM ""StoredElectricityPrices""
                WHERE ""Resolution"" = 'DAILY';
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deleted rows will be re-fetched by the background service; no rollback possible.
        }
    }
}
