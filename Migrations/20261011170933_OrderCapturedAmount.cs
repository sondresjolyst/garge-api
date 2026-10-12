using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace garge_api.Migrations
{
    /// <inheritdoc />
    public partial class OrderCapturedAmount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CapturedInOre",
                table: "Orders",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Paid and refunded orders were captured in full, the only way an order became paid.
            // A data backfill has no EF migration operation, so it is SQL.
            migrationBuilder.Sql("""UPDATE "Orders" SET "CapturedInOre" = "TotalInOre" WHERE "Status" IN (1, 3);""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CapturedInOre",
                table: "Orders");
        }
    }
}
