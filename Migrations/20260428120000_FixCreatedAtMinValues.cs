using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace garge_api.Migrations
{
    /// <inheritdoc />
    public partial class FixCreatedAtMinValues : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Npgsql maps DateTime.MinValue to PostgreSQL -infinity. Rows that existed before
            // CreatedAt tracking was introduced got -infinity when the column was added.
            // Replace those with the timestamp when this migration first ran, which is the
            // best approximation of "we don't know when this was created".
            migrationBuilder.Sql(@"
                UPDATE ""AspNetUsers""    SET ""CreatedAt"" = NOW() WHERE ""CreatedAt"" = '-infinity';
                UPDATE ""Sensors""        SET ""CreatedAt"" = NOW() WHERE ""CreatedAt"" = '-infinity';
                UPDATE ""Switches""       SET ""CreatedAt"" = NOW() WHERE ""CreatedAt"" = '-infinity';
                UPDATE ""AutomationRules"" SET ""CreatedAt"" = NOW() WHERE ""CreatedAt"" = '-infinity';
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The original dates are unknown — cannot reverse this.
        }
    }
}
