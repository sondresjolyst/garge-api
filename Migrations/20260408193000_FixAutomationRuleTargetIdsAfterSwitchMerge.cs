using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace garge_api.Migrations
{
    /// <inheritdoc />
    public partial class FixAutomationRuleTargetIdsAfterSwitchMerge : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The MergeStaleTypeSwitchRows migration deleted old 'switch'-typed rows but missed
            // AutomationRules. Re-point each broken rule to the surviving SOCKET row by name lookup.
            // Old id=3 was wiz_SOCKET_6c2990a9f1a5, old id=4 was wiz_SOCKET_d8a01198c145.
            migrationBuilder.Sql(@"
                UPDATE ""AutomationRules""
                SET ""TargetId"" = s.""Id""
                FROM ""Switches"" s
                WHERE s.""Name"" = 'wiz_SOCKET_6c2990a9f1a5'
                  AND ""AutomationRules"".""TargetType"" = 'switch'
                  AND ""AutomationRules"".""TargetId"" = 3;
            ");

            migrationBuilder.Sql(@"
                UPDATE ""AutomationRules""
                SET ""TargetId"" = s.""Id""
                FROM ""Switches"" s
                WHERE s.""Name"" = 'wiz_SOCKET_d8a01198c145'
                  AND ""AutomationRules"".""TargetType"" = 'switch'
                  AND ""AutomationRules"".""TargetId"" = 4;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
