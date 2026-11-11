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

            // Best-effort backfill for tickets that already exist: the first person ever assigned the ticket while it sat in the Call Center
            // (department code CC). Assignments are never deleted (superseded only), so this is the agent who first owned it. Tickets that never
            // had a Call Center assignment stay NULL and are filled when their first eligible Call Center agent takes them.
            migrationBuilder.Sql(@"
EXEC(N'UPDATE t SET t.[OriginalHumanAgentId] = first_cc.[AssignedEmployeeId]
FROM [Tickets] AS t
CROSS APPLY (
    SELECT TOP (1) a.[AssignedEmployeeId]
    FROM [TicketAssignments] AS a
    INNER JOIN [Departments] AS d ON d.[DepartmentId] = a.[AssignedDepartmentId] AND d.[Code] = N''CC''
    WHERE a.[TicketId] = t.[TicketId]
    ORDER BY a.[AssignedAtUtc], a.[TicketAssignmentId]
) AS first_cc
WHERE t.[OriginalHumanAgentId] IS NULL;');");
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
