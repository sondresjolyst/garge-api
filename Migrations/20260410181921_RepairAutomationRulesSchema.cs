using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace garge_api.Migrations
{
    /// <inheritdoc />
    public partial class RepairAutomationRulesSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Add columns that may be missing on databases created from a now-deleted migration
            migrationBuilder.Sql(@"
                ALTER TABLE ""AutomationRules"" ADD COLUMN IF NOT EXISTS ""SensorType"" text NOT NULL DEFAULT '';
                ALTER TABLE ""AutomationRules"" ADD COLUMN IF NOT EXISTS ""SensorId""   integer NOT NULL DEFAULT 0;
                ALTER TABLE ""AutomationRules"" ADD COLUMN IF NOT EXISTS ""Condition""  text NOT NULL DEFAULT '==';
                ALTER TABLE ""AutomationRules"" ADD COLUMN IF NOT EXISTS ""Threshold""  double precision NOT NULL DEFAULT 0;
                ALTER TABLE ""AutomationRules"" DROP COLUMN IF EXISTS ""LogicalOperator"";
            ");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                ALTER TABLE ""AutomationRules"" ADD COLUMN IF NOT EXISTS ""LogicalOperator"" text;
                ALTER TABLE ""AutomationRules"" DROP COLUMN IF EXISTS ""SensorType"";
                ALTER TABLE ""AutomationRules"" DROP COLUMN IF EXISTS ""SensorId"";
                ALTER TABLE ""AutomationRules"" DROP COLUMN IF EXISTS ""Condition"";
                ALTER TABLE ""AutomationRules"" DROP COLUMN IF EXISTS ""Threshold"";
            ");
        }
    }
}
