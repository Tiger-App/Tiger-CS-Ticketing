using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TigerCS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGenesysAssignmentHistorySource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "GenesysConversationId",
                table: "TicketAssignmentHistory",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<byte>(
                name: "Source",
                table: "TicketAssignmentHistory",
                type: "tinyint",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SourceEventAtUtc",
                table: "TicketAssignmentHistory",
                type: "datetime2",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GenesysConversationId",
                table: "TicketAssignmentHistory");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "TicketAssignmentHistory");

            migrationBuilder.DropColumn(
                name: "SourceEventAtUtc",
                table: "TicketAssignmentHistory");
        }
    }
}
