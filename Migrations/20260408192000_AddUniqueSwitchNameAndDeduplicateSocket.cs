using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace garge_api.Migrations
{
    /// <inheritdoc />
    public partial class AddUniqueSwitchNameAndDeduplicateSocket : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // For each set of duplicate SOCKET rows with the same Name,
            // keep the one with the lowest Id and re-point SwitchData to it.
            migrationBuilder.Sql(@"
                UPDATE ""SwitchData"" sd
                SET ""SwitchId"" = keep.""Id""
                FROM ""Switches"" dup
                INNER JOIN (
                    SELECT ""Name"", MIN(""Id"") AS ""Id""
                    FROM ""Switches""
                    GROUP BY ""Name""
                ) keep ON keep.""Name"" = dup.""Name"" AND keep.""Id"" <> dup.""Id""
                WHERE sd.""SwitchId"" = dup.""Id"";
            ");

            // Delete duplicate roles for the same switch name (keep the one matching the canonical row)
            migrationBuilder.Sql(@"
                DELETE FROM ""AspNetRoles""
                WHERE ""Name"" IN (
                    SELECT ""Name"" FROM ""Switches""
                    GROUP BY ""Name"" HAVING COUNT(*) > 1
                )
                AND ""Id"" NOT IN (
                    SELECT ar.""Id""
                    FROM ""AspNetRoles"" ar
                    INNER JOIN (
                        SELECT ""Name"", MIN(""Id"") AS ""Id""
                        FROM ""Switches""
                        GROUP BY ""Name""
                    ) keep ON keep.""Name"" = ar.""Name""
                );
            ");

            // Delete all non-canonical duplicate Switch rows
            migrationBuilder.Sql(@"
                DELETE FROM ""Switches""
                WHERE ""Id"" NOT IN (
                    SELECT MIN(""Id"") FROM ""Switches"" GROUP BY ""Name""
                );
            ");

            // Add unique constraint to prevent future duplicates
            migrationBuilder.CreateIndex(
                name: "IX_Switches_Name",
                table: "Switches",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Switches_Name",
                table: "Switches");
        }
    }
}
