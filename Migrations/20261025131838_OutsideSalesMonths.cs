using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace garge_api.Migrations
{
    /// <inheritdoc />
    public partial class OutsideSalesMonths : Migration
    {
        /// <summary>
        /// Moves the one figure for sales outside garge-api into the row for the current month in Norway,
        /// since its months are not known.
        /// </summary>
        internal const string MoveToMonthSql = """
            INSERT INTO "OutsideSalesMonths" ("Month", "AmountInOre", "UpdatedAt")
            SELECT date_trunc('month', now() AT TIME ZONE 'Europe/Oslo')::date, "OtherTurnoverInOre", now()
            FROM "AppSettings"
            WHERE "Id" = 1 AND "OtherTurnoverInOre" <> 0;
            """;

        /// <summary>Puts the months back together as one figure.</summary>
        internal const string MoveBackSql = """
            UPDATE "AppSettings"
            SET "OtherTurnoverInOre" = (SELECT coalesce(sum("AmountInOre"), 0) FROM "OutsideSalesMonths")
            WHERE "Id" = 1;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OutsideSalesMonths",
                columns: table => new
                {
                    Month = table.Column<DateOnly>(type: "date", nullable: false),
                    AmountInOre = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutsideSalesMonths", x => x.Month);
                });

            migrationBuilder.Sql(MoveToMonthSql);

            migrationBuilder.DropColumn(
                name: "OtherTurnoverInOre",
                table: "AppSettings");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "OtherTurnoverInOre",
                table: "AppSettings",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.UpdateData(
                table: "AppSettings",
                keyColumn: "Id",
                keyValue: 1,
                column: "OtherTurnoverInOre",
                value: 0L);

            migrationBuilder.Sql(MoveBackSql);

            migrationBuilder.DropTable(
                name: "OutsideSalesMonths");
        }
    }
}
