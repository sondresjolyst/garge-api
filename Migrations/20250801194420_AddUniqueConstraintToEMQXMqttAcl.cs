using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace garge_api.Migrations
{
    /// <inheritdoc />
    public partial class AddUniqueConstraintToEMQXMqttAcl : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_EMQXMqttAcls_Username_Permission_Action_Topic_Qos_Retain",
                table: "EMQXMqttAcls",
                columns: new[] { "Username", "Permission", "Action", "Topic", "Qos", "Retain" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_EMQXMqttAcls_Username_Permission_Action_Topic_Qos_Retain",
                table: "EMQXMqttAcls");
        }
    }
}
