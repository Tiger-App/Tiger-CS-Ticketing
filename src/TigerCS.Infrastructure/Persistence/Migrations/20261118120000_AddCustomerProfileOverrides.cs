using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TigerCS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomerProfileOverrides : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CustomerProfileOverrides",
                columns: table => new
                {
                    CustomerProfileOverrideId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CustomerKey = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    IdentityKind = table.Column<int>(type: "int", nullable: false),
                    CrmBuyerCustomerId = table.Column<int>(type: "int", nullable: true),
                    ExternalSource = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ExternalCustomerId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    IdentityPhone = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    PhoneNumber = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    PhoneLocation = table.Column<byte>(type: "tinyint", nullable: true),
                    ProjectName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    UnitNumber = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedByEmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerProfileOverrides", x => x.CustomerProfileOverrideId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerProfileOverrides_CustomerKey",
                table: "CustomerProfileOverrides",
                column: "CustomerKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CustomerProfileOverrides");
        }
    }
}
