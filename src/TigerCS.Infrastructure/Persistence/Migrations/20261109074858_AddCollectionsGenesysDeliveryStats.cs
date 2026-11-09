using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TigerCS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCollectionsGenesysDeliveryStats : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CollectionsGenesysMessageResults",
                columns: table => new
                {
                    CollectionsGenesysMessageResultId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CollectionsDispatchId = table.Column<long>(type: "bigint", nullable: false),
                    CollectionsDispatchItemId = table.Column<long>(type: "bigint", nullable: false),
                    GenesysConversationId = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    GenesysMessageId = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: false),
                    GenesysContactId = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                    Status = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    SentAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DeliveredAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    FailureReason = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    SyncedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CollectionsGenesysMessageResults", x => x.CollectionsGenesysMessageResultId);
                });

            migrationBuilder.CreateTable(
                name: "CollectionsGenesysSyncStates",
                columns: table => new
                {
                    CollectionsDispatchId = table.Column<long>(type: "bigint", nullable: false),
                    LastSyncedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Outcome = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    Error = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Contacts = table.Column<int>(type: "int", nullable: false),
                    Sent = table.Column<int>(type: "int", nullable: false),
                    Delivered = table.Column<int>(type: "int", nullable: false),
                    Failed = table.Column<int>(type: "int", nullable: false),
                    Pending = table.Column<int>(type: "int", nullable: false),
                    NotSent = table.Column<int>(type: "int", nullable: false),
                    MessageAttempts = table.Column<int>(type: "int", nullable: false),
                    ConversationsFound = table.Column<int>(type: "int", nullable: false),
                    LastCompletedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CollectionsGenesysSyncStates", x => x.CollectionsDispatchId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CollectionsGenesysMessageResults_Dispatch_Item",
                table: "CollectionsGenesysMessageResults",
                columns: new[] { "CollectionsDispatchId", "CollectionsDispatchItemId" });

            migrationBuilder.CreateIndex(
                name: "UX_CollectionsGenesysMessageResults_Message",
                table: "CollectionsGenesysMessageResults",
                columns: new[] { "GenesysConversationId", "GenesysMessageId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CollectionsGenesysMessageResults");

            migrationBuilder.DropTable(
                name: "CollectionsGenesysSyncStates");
        }
    }
}
