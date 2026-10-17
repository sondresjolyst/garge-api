using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace garge_api.Migrations
{
    /// <inheritdoc />
    public partial class SplitCompanyAddress : Migration
    {
        /// <summary>
        /// Splits "Mårvegen 21a, 4347 Lye" into the street, the postal code and the place. An address in
        /// another form stays whole in the street, for the admin to split.
        /// </summary>
        internal const string SplitAddressSql = """
            UPDATE "AppSettings"
            SET "PostalCode" = substring("StreetAddress" from ',\s*(\d{4})\s+\S'),
                "AddressLocality" = btrim(substring("StreetAddress" from ',\s*\d{4}\s+(.+)$')),
                "StreetAddress" = btrim(substring("StreetAddress" from '^(.*?),\s*\d{4}\s+\S'))
            WHERE "StreetAddress" ~ ',\s*\d{4}\s+\S';
            """;

        /// <summary>Joins the parts back into one line, skipping blank parts.</summary>
        internal const string JoinAddressSql = """
            UPDATE "AppSettings"
            SET "StreetAddress" = concat_ws(', ', nullif("StreetAddress", ''),
                nullif(concat_ws(' ', nullif("PostalCode", ''), nullif("AddressLocality", '')), ''));
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "CompanyOrgNumber",
                table: "AppSettings",
                newName: "OrgNumber");

            migrationBuilder.RenameColumn(
                name: "CompanyEmail",
                table: "AppSettings",
                newName: "PublicEmail");

            migrationBuilder.RenameColumn(
                name: "CompanyAddress",
                table: "AppSettings",
                newName: "StreetAddress");

            migrationBuilder.AddColumn<string>(
                name: "PostalCode",
                table: "AppSettings",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "AddressLocality",
                table: "AppSettings",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "AddressRegion",
                table: "AppSettings",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ContactRecipientEmail",
                table: "AppSettings",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "PublicPhone",
                table: "AppSettings",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "LogoData",
                table: "AppSettings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LogoContentType",
                table: "AppSettings",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IconData",
                table: "AppSettings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IconContentType",
                table: "AppSettings",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            // The row is moved by hand rather than by seed data, so an admin's edits to it stay.
            migrationBuilder.Sql(SplitAddressSql);
            migrationBuilder.Sql("""UPDATE "AppSettings" SET "ContactRecipientEmail" = "PublicEmail";""");

            // The package allows 200 characters for the street. This runs after the split, so only an
            // address that could not be split and is still longer stops the migration.
            migrationBuilder.AlterColumn<string>(
                name: "StreetAddress",
                table: "AppSettings",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(500)",
                oldMaxLength: 500);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "StreetAddress",
                table: "AppSettings",
                type: "character varying(500)",
                maxLength: 500,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200);

            migrationBuilder.Sql(JoinAddressSql);

            migrationBuilder.DropColumn(
                name: "PostalCode",
                table: "AppSettings");

            migrationBuilder.DropColumn(
                name: "AddressLocality",
                table: "AppSettings");

            migrationBuilder.DropColumn(
                name: "AddressRegion",
                table: "AppSettings");

            migrationBuilder.DropColumn(
                name: "ContactRecipientEmail",
                table: "AppSettings");

            migrationBuilder.DropColumn(
                name: "PublicPhone",
                table: "AppSettings");

            migrationBuilder.DropColumn(
                name: "LogoData",
                table: "AppSettings");

            migrationBuilder.DropColumn(
                name: "LogoContentType",
                table: "AppSettings");

            migrationBuilder.DropColumn(
                name: "IconData",
                table: "AppSettings");

            migrationBuilder.DropColumn(
                name: "IconContentType",
                table: "AppSettings");

            migrationBuilder.RenameColumn(
                name: "StreetAddress",
                table: "AppSettings",
                newName: "CompanyAddress");

            migrationBuilder.RenameColumn(
                name: "PublicEmail",
                table: "AppSettings",
                newName: "CompanyEmail");

            migrationBuilder.RenameColumn(
                name: "OrgNumber",
                table: "AppSettings",
                newName: "CompanyOrgNumber");
        }
    }
}
