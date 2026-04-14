using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace garge_api.Migrations
{
    /// <inheritdoc />
    public partial class AddElectricityPriceConditionAndStoredPrices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ElectricityPriceArea",
                table: "AutomationRules",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ElectricityPriceCondition",
                table: "AutomationRules",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ElectricityPriceOperator",
                table: "AutomationRules",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "ElectricityPriceThreshold",
                table: "AutomationRules",
                type: "double precision",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "StoredElectricityPrices",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Area = table.Column<string>(type: "text", nullable: false),
                    DeliveryStart = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeliveryEnd = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Value = table.Column<double>(type: "double precision", nullable: false),
                    Currency = table.Column<string>(type: "text", nullable: false),
                    FetchedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StoredElectricityPrices", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StoredElectricityPrices_Area_DeliveryStart",
                table: "StoredElectricityPrices",
                columns: new[] { "Area", "DeliveryStart" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StoredElectricityPrices");

            migrationBuilder.DropColumn(
                name: "ElectricityPriceArea",
                table: "AutomationRules");

            migrationBuilder.DropColumn(
                name: "ElectricityPriceCondition",
                table: "AutomationRules");

            migrationBuilder.DropColumn(
                name: "ElectricityPriceOperator",
                table: "AutomationRules");

            migrationBuilder.DropColumn(
                name: "ElectricityPriceThreshold",
                table: "AutomationRules");
        }
    }
}
