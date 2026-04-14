using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace garge_api.Migrations
{
    /// <inheritdoc />
    public partial class AddElectricityPriceResolution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_StoredElectricityPrices_Area_DeliveryStart",
                table: "StoredElectricityPrices");

            migrationBuilder.AddColumn<string>(
                name: "Resolution",
                table: "StoredElectricityPrices",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_StoredElectricityPrices_Area_Resolution_DeliveryStart",
                table: "StoredElectricityPrices",
                columns: new[] { "Area", "Resolution", "DeliveryStart" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_StoredElectricityPrices_Area_Resolution_DeliveryStart",
                table: "StoredElectricityPrices");

            migrationBuilder.DropColumn(
                name: "Resolution",
                table: "StoredElectricityPrices");

            migrationBuilder.CreateIndex(
                name: "IX_StoredElectricityPrices_Area_DeliveryStart",
                table: "StoredElectricityPrices",
                columns: new[] { "Area", "DeliveryStart" },
                unique: true);
        }
    }
}
