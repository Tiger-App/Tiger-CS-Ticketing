using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TigerCS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTicketOriginalHumanAgent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "OriginalHumanAgentId",
                table: "Tickets",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_OriginalHumanAgentId",
                table: "Tickets",
                column: "OriginalHumanAgentId");

            // Deliberately NO backfill: OriginalHumanAgentId is never populated for tickets that already exist (see AddOriginalAgentWorkflowCutoff,
            // which marks them legacy). An earlier revision of this migration inferred it from assignment history; that data statement was removed.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Tickets_OriginalHumanAgentId",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "OriginalHumanAgentId",
                table: "Tickets");
        }
    }
}
