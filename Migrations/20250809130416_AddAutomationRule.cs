using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace garge_api.Migrations
{
    /// <inheritdoc />
    public partial class AddAutomationRule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AutomationRules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TargetType = table.Column<string>(type: "text", nullable: false),
                    TargetId = table.Column<int>(type: "integer", nullable: false),
                    SensorType = table.Column<string>(type: "text", nullable: false),
                    SensorId = table.Column<int>(type: "integer", nullable: false),
                    Condition = table.Column<string>(type: "text", nullable: false),
                    Threshold = table.Column<double>(type: "double precision", nullable: false),
                    Action = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutomationRules", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AutomationRules_TargetType_TargetId_SensorType_SensorId_Con~",
                table: "AutomationRules",
                columns: new[] { "TargetType", "TargetId", "SensorType", "SensorId", "Condition", "Threshold", "Action" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AutomationRules");
        }
    }
}
