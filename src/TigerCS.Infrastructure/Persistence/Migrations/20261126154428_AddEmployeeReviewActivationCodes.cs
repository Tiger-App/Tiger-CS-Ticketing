using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TigerCS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEmployeeReviewActivationCodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ActivationCodeExpiresAtUtc",
                table: "EmployeeReviewDevices",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "ActivationCodeHash",
                table: "EmployeeReviewDevices",
                type: "varbinary(32)",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "ActivationCodeSalt",
                table: "EmployeeReviewDevices",
                type: "varbinary(16)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "EmployeeReviewActivationThrottles",
                columns: table => new
                {
                    Key = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    WindowStartUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmployeeReviewActivationThrottles", x => x.Key);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EmployeeReviewActivationThrottles");

            migrationBuilder.DropColumn(
                name: "ActivationCodeExpiresAtUtc",
                table: "EmployeeReviewDevices");

            migrationBuilder.DropColumn(
                name: "ActivationCodeHash",
                table: "EmployeeReviewDevices");

            migrationBuilder.DropColumn(
                name: "ActivationCodeSalt",
                table: "EmployeeReviewDevices");
        }
    }
}
