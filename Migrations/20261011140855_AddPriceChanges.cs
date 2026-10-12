using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace garge_api.Migrations
{
    /// <inheritdoc />
    public partial class AddPriceChanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "UnitPriceInOre",
                table: "Subscriptions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Existing subscribers agreed to their product's current price. A data backfill has no EF
            // migration operation, so it is SQL.
            migrationBuilder.Sql("""
                UPDATE "Subscriptions" s SET "UnitPriceInOre" = p."PriceInOre"
                FROM "Products" p WHERE s."ProductId" = p."Id";
                """);

            migrationBuilder.CreateTable(
                name: "PriceChanges",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProductId = table.Column<int>(type: "integer", nullable: false),
                    OldPriceInOre = table.Column<int>(type: "integer", nullable: false),
                    NewPriceInOre = table.Column<int>(type: "integer", nullable: false),
                    EffectiveFrom = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CancelledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AppliedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PriceChanges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PriceChanges_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PriceChangeNotices",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PriceChangeId = table.Column<int>(type: "integer", nullable: false),
                    SubscriptionId = table.Column<int>(type: "integer", nullable: false),
                    NotifiedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EffectiveFrom = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AppliedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PriceChangeNotices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PriceChangeNotices_PriceChanges_PriceChangeId",
                        column: x => x.PriceChangeId,
                        principalTable: "PriceChanges",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PriceChangeNotices_Subscriptions_SubscriptionId",
                        column: x => x.SubscriptionId,
                        principalTable: "Subscriptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PriceChangeNotices_PriceChangeId_SubscriptionId",
                table: "PriceChangeNotices",
                columns: new[] { "PriceChangeId", "SubscriptionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PriceChangeNotices_SubscriptionId",
                table: "PriceChangeNotices",
                column: "SubscriptionId");

            migrationBuilder.CreateIndex(
                name: "IX_PriceChanges_ProductId",
                table: "PriceChanges",
                column: "ProductId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PriceChangeNotices");

            migrationBuilder.DropTable(
                name: "PriceChanges");

            migrationBuilder.DropColumn(
                name: "UnitPriceInOre",
                table: "Subscriptions");
        }
    }
}
