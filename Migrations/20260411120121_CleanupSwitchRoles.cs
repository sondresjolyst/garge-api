using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace garge_api.Migrations
{
    /// <inheritdoc />
    public partial class CleanupSwitchRoles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Remove switch-specific role assignments from AspNetUserRoles
            migrationBuilder.Sql(@"
                DELETE FROM ""AspNetUserRoles""
                WHERE ""RoleId"" IN (
                    SELECT r.""Id"" FROM ""AspNetRoles"" r
                    JOIN ""Switches"" s ON s.""Role"" = r.""Name""
                );
            ");

            // Remove switch-specific role definitions from AspNetRoles
            migrationBuilder.Sql(@"
                DELETE FROM ""AspNetRoles""
                WHERE ""Name"" IN (SELECT ""Role"" FROM ""Switches"");
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Role data is not restored on rollback
        }
    }
}
