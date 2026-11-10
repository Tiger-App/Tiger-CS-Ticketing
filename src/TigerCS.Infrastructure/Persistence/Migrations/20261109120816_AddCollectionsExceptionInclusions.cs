using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TigerCS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCollectionsExceptionInclusions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CollectionsExceptionInclusions",
                columns: table => new
                {
                    CollectionsExceptionInclusionId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CampaignKey = table.Column<string>(type: "varchar(80)", unicode: false, maxLength: 80, nullable: false),
                    CompanyId = table.Column<int>(type: "int", nullable: false),
                    TenantId = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    UnitCode = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    IncludedByEmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IncludedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RemovedByEmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RemovedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CollectionsExceptionInclusions", x => x.CollectionsExceptionInclusionId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CollectionsExceptionInclusions_Campaign",
                table: "CollectionsExceptionInclusions",
                column: "CampaignKey");

            migrationBuilder.CreateIndex(
                name: "UX_CollectionsExceptionInclusions_Active",
                table: "CollectionsExceptionInclusions",
                columns: new[] { "CampaignKey", "CompanyId", "TenantId", "UnitCode" },
                unique: true,
                filter: "[RemovedAtUtc] IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CollectionsExceptionInclusions");
        }
    }
}
