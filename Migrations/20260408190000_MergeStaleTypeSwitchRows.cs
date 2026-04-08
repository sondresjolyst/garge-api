using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace garge_api.Migrations
{
    /// <inheritdoc />
    public partial class MergeStaleTypeSwitchRows : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // For every Switch row with Type='switch' that has a duplicate with the same Name
            // but Type='SOCKET', re-point all SwitchData to the SOCKET row then delete the stale row.
            // The AspNetRole is shared (Role = Name) so we leave it intact.
            migrationBuilder.Sql(@"
                UPDATE ""SwitchData"" sd
                SET ""SwitchId"" = new_sw.""Id""
                FROM ""Switches"" old_sw
                INNER JOIN ""Switches"" new_sw ON new_sw.""Name"" = old_sw.""Name"" AND new_sw.""Type"" = 'SOCKET'
                WHERE sd.""SwitchId"" = old_sw.""Id"" AND old_sw.""Type"" = 'switch';
            ");

            // Delete any SwitchData still pointing to 'switch' rows that had no SOCKET counterpart
            // (e.g. wiz_null_null)
            migrationBuilder.Sql(@"
                DELETE FROM ""SwitchData""
                WHERE ""SwitchId"" IN (
                    SELECT sw.""Id"" FROM ""Switches"" sw
                    WHERE sw.""Type"" = 'switch'
                    AND NOT EXISTS (
                        SELECT 1 FROM ""Switches"" new_sw
                        WHERE new_sw.""Name"" = sw.""Name"" AND new_sw.""Type"" = 'SOCKET'
                    )
                );
            ");

            // Remove AspNetRoles for switches with no SOCKET counterpart (orphaned roles)
            migrationBuilder.Sql(@"
                DELETE FROM ""AspNetRoles""
                WHERE ""Name"" IN (
                    SELECT sw.""Name"" FROM ""Switches"" sw
                    WHERE sw.""Type"" = 'switch'
                    AND NOT EXISTS (
                        SELECT 1 FROM ""Switches"" new_sw
                        WHERE new_sw.""Name"" = sw.""Name"" AND new_sw.""Type"" = 'SOCKET'
                    )
                );
            ");

            // Delete all stale 'switch'-typed rows
            migrationBuilder.Sql(@"
                DELETE FROM ""Switches"" WHERE ""Type"" = 'switch';
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Intentionally not reversible — restoring deleted rows is not practical
        }
    }
}
