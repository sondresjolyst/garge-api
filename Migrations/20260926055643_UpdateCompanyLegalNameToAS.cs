using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace garge_api.Migrations
{
    /// <inheritdoc />
    public partial class UpdateCompanyLegalNameToAS : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "AppSettings",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "CompanyLegalName", "CompanyOrgNumber" },
                values: new object[] { "Sjølyst Innovation AS", "938 517 789" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "AppSettings",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "CompanyLegalName", "CompanyOrgNumber" },
                values: new object[] { "Sjølyst Innovations", "934 531 035" });
        }
    }
}
