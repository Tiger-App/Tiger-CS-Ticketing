using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TigerCS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTicketOriginalHumanAgent : Migration
    {
        /// <summary>
        /// Sets <c>Tickets.OriginalHumanAgentId</c> from history, only where it can be verified, and never overwrites a value:
        /// <list type="bullet">
        /// <item>Only the EARLIEST Call Center (<c>CC</c>) assignment of the ticket is considered. If its assignee fails any rule below the ticket is left
        /// NULL - a later assignment is NOT used, because it would credit somebody who is not the first owner.</item>
        /// <item>The assignee must be an ACTIVE employee, not locked out, an active member of the Call Center holding the CS Agent role, and show no
        /// integration-account evidence (name hints such as genesys/integration/service/svc, or having created Genesys tickets without ever handling an
        /// interaction or having a Genesys user mapping). Service identities live in configuration, not data, so this is evidence-based and errs to NULL.
        /// A collector (anyone holding a Collections tower assignment) is never selected.</item>
        /// <item>The assignment must be a human act: made by a person (<c>AssigningActorEmployeeId</c> not null), or - when dealt by the system
        /// (rotation / rule, actor null) - the same employee must have acted on that ticket (an audit entry with them as actor).</item>
        /// </list>
        /// </summary>
        public const string BackfillSql = @"
DECLARE @cc int = (SELECT TOP (1) [DepartmentId] FROM [Departments] WHERE [Code] = N'CC' AND [IsActive] = 1);
IF @cc IS NOT NULL
BEGIN
        UPDATE t SET t.[OriginalHumanAgentId] = first_cc.[AssignedEmployeeId]
        FROM [Tickets] AS t
        CROSS APPLY (
            SELECT TOP (1) a.[AssignedEmployeeId], a.[AssigningActorEmployeeId]
            FROM [TicketAssignments] AS a
            WHERE a.[TicketId] = t.[TicketId] AND a.[AssignedDepartmentId] = @cc
            ORDER BY a.[AssignedAtUtc], a.[TicketAssignmentId]
        ) AS first_cc
        WHERE t.[OriginalHumanAgentId] IS NULL
          AND (first_cc.[AssigningActorEmployeeId] IS NOT NULL
               OR EXISTS (SELECT 1 FROM [AuditEntries] AS au
                          WHERE au.[EntityType] = N'Ticket' AND au.[EntityId] = CAST(t.[TicketId] AS nvarchar(64))
                            AND au.[ActorEmployeeId] = first_cc.[AssignedEmployeeId]))
          AND EXISTS (SELECT 1 FROM [Employees] AS e
                      JOIN [AspNetUsers] AS u ON u.[Id] = e.[EmployeeId]
                      WHERE e.[EmployeeId] = first_cc.[AssignedEmployeeId]
                        AND e.[DeactivatedAtUtc] IS NULL
                        AND NOT (u.[LockoutEnabled] = 1 AND u.[LockoutEnd] IS NOT NULL AND u.[LockoutEnd] > SYSDATETIMEOFFSET())
                        AND LOWER(ISNULL(u.[UserName], N'') + N' ' + ISNULL(u.[Email], N'') + N' ' + ISNULL(e.[DisplayName], N'')) NOT LIKE N'%genesys%'
                        AND LOWER(ISNULL(u.[UserName], N'') + N' ' + ISNULL(u.[Email], N'') + N' ' + ISNULL(e.[DisplayName], N'')) NOT LIKE N'%integration%'
                        AND LOWER(ISNULL(u.[UserName], N'') + N' ' + ISNULL(u.[Email], N'') + N' ' + ISNULL(e.[DisplayName], N'')) NOT LIKE N'%service%'
                        AND LOWER(ISNULL(u.[UserName], N'') + N' ' + ISNULL(u.[Email], N'') + N' ' + ISNULL(e.[DisplayName], N'')) NOT LIKE N'%svc%'
                        AND NOT (NULLIF(LTRIM(RTRIM(u.[GenesysUserId])), N'') IS NULL
                                 AND NOT EXISTS (SELECT 1 FROM [TicketInteractions] AS hi WHERE hi.[HandledByUserId] = e.[EmployeeId])
                                 AND NOT EXISTS (SELECT 1 FROM [TicketInteractionHandlers] AS hh WHERE hh.[HandledByUserId] = e.[EmployeeId])
                                 AND EXISTS (SELECT 1 FROM [Tickets] AS ct
                                             WHERE ct.[CreatedByEmployeeId] = e.[EmployeeId]
                                               AND EXISTS (SELECT 1 FROM [TicketInteractions] AS gi WHERE gi.[TicketId] = ct.[TicketId] AND gi.[Source] = 2))))
          AND EXISTS (SELECT 1 FROM [UserDepartmentAssignments] AS m WHERE m.[EmployeeId] = first_cc.[AssignedEmployeeId] AND m.[DepartmentId] = @cc)
          AND EXISTS (SELECT 1 FROM [AspNetUserRoles] AS ur JOIN [AspNetRoles] AS r ON r.[Id] = ur.[RoleId]
                      WHERE ur.[UserId] = first_cc.[AssignedEmployeeId] AND r.[Name] = N'CS Agent')
          AND NOT EXISTS (SELECT 1 FROM [CollectionsTowerAssignments] AS c WHERE c.[EmployeeId] = first_cc.[AssignedEmployeeId]);
END";

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

            // Conservative backfill for tickets that already exist. A wrong Original Human Agent would send a returning ticket to the wrong person, so
            // the field stays NULL unless ownership can be established reliably (see BackfillSql). Dynamic SQL because the column is added in this batch.
            migrationBuilder.Sql($"EXEC(N'{BackfillSql.Replace("'", "''")}');");
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
