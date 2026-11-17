using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TigerCS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddQueueingIntegration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExternalReference",
                table: "Tickets",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalTicketId",
                table: "Tickets",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IntegrationSource",
                table: "Tickets",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "QueueingCompanyMappings",
                columns: table => new
                {
                    QueueingCompanyMappingId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CompanyId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    DepartmentId = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QueueingCompanyMappings", x => x.QueueingCompanyMappingId);
                    table.ForeignKey(
                        name: "FK_QueueingCompanyMappings_Departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalTable: "Departments",
                        principalColumn: "DepartmentId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "QueueingEvents",
                columns: table => new
                {
                    QueueingEventId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Source = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    EventId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    EventType = table.Column<byte>(type: "tinyint", nullable: false),
                    ExternalTicketId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    EventSequence = table.Column<long>(type: "bigint", nullable: false),
                    OccurredAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Payload = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PayloadHash = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    PayloadPurgedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    OutcomeCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    OutcomeDetail = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    TicketId = table.Column<long>(type: "bigint", nullable: true),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    NextRetryAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ReceivedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ProcessedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QueueingEvents", x => x.QueueingEventId);
                });

            migrationBuilder.CreateTable(
                name: "QueueingTicketLinks",
                columns: table => new
                {
                    QueueingTicketLinkId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TicketId = table.Column<long>(type: "bigint", nullable: false),
                    ExternalTicketId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    QueueNumber = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CompanyId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ExternalTicketTypeId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    LastAppliedSequence = table.Column<long>(type: "bigint", nullable: false),
                    LastAppliedOccurredAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    State = table.Column<byte>(type: "tinyint", nullable: false),
                    CounterId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    AssignedExternalUserId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QueueingTicketLinks", x => x.QueueingTicketLinkId);
                    table.ForeignKey(
                        name: "FK_QueueingTicketLinks_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "TicketId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "QueueingTicketTypeMappings",
                columns: table => new
                {
                    QueueingTicketTypeMappingId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CompanyId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    TicketTypeId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    RequestTypeId = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QueueingTicketTypeMappings", x => x.QueueingTicketTypeMappingId);
                });

            migrationBuilder.CreateTable(
                name: "QueueingUserMappings",
                columns: table => new
                {
                    QueueingUserMappingId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ExternalUserId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QueueingUserMappings", x => x.QueueingUserMappingId);
                });

            migrationBuilder.CreateIndex(
                name: "UX_Tickets_IntegrationSource_ExternalTicketId",
                table: "Tickets",
                columns: new[] { "IntegrationSource", "ExternalTicketId" },
                unique: true,
                filter: "[IntegrationSource] IS NOT NULL AND [ExternalTicketId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_QueueingCompanyMappings_DepartmentId",
                table: "QueueingCompanyMappings",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "UX_QueueingCompanyMappings_CompanyId",
                table: "QueueingCompanyMappings",
                column: "CompanyId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_QueueingEvents_PurgeablePayloads",
                table: "QueueingEvents",
                column: "ProcessedAtUtc",
                filter: "[Payload] IS NOT NULL AND [Status] IN (2, 4, 5)");

            migrationBuilder.CreateIndex(
                name: "IX_QueueingEvents_Status_NextRetry",
                table: "QueueingEvents",
                columns: new[] { "Status", "NextRetryAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_QueueingEvents_Ticket_Sequence",
                table: "QueueingEvents",
                columns: new[] { "Source", "ExternalTicketId", "EventSequence" });

            migrationBuilder.CreateIndex(
                name: "UX_QueueingEvents_Source_EventId",
                table: "QueueingEvents",
                columns: new[] { "Source", "EventId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_QueueingTicketLinks_ExternalTicketId",
                table: "QueueingTicketLinks",
                column: "ExternalTicketId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_QueueingTicketLinks_TicketId",
                table: "QueueingTicketLinks",
                column: "TicketId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_QueueingTicketTypeMappings_Company_Type",
                table: "QueueingTicketTypeMappings",
                columns: new[] { "CompanyId", "TicketTypeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_QueueingUserMappings_EmployeeId",
                table: "QueueingUserMappings",
                column: "EmployeeId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_QueueingUserMappings_ExternalUserId",
                table: "QueueingUserMappings",
                column: "ExternalUserId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "QueueingCompanyMappings");

            migrationBuilder.DropTable(
                name: "QueueingEvents");

            migrationBuilder.DropTable(
                name: "QueueingTicketLinks");

            migrationBuilder.DropTable(
                name: "QueueingTicketTypeMappings");

            migrationBuilder.DropTable(
                name: "QueueingUserMappings");

            migrationBuilder.DropIndex(
                name: "UX_Tickets_IntegrationSource_ExternalTicketId",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "ExternalReference",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "ExternalTicketId",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "IntegrationSource",
                table: "Tickets");
        }
    }
}
