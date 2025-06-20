using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace garge_api.Migrations
{
    /// <inheritdoc />
    public partial class AddEmailVerificationCodeToApplicationUser : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EmailVerificationCode",
                table: "AspNetUsers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "EmailVerificationCodeExpiration",
                table: "AspNetUsers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SensorData_Timestamp",
                table: "SensorData",
                column: "Timestamp");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SensorData_Timestamp",
                table: "SensorData");

            migrationBuilder.DropColumn(
                name: "EmailVerificationCode",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "EmailVerificationCodeExpiration",
                table: "AspNetUsers");
        }
    }
}
