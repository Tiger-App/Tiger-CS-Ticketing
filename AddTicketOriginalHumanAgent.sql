BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261111073632_AddTicketOriginalHumanAgent'
)
BEGIN
    ALTER TABLE [Tickets] ADD [OriginalHumanAgentId] uniqueidentifier NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261111073632_AddTicketOriginalHumanAgent'
)
BEGIN
    CREATE INDEX [IX_Tickets_OriginalHumanAgentId] ON [Tickets] ([OriginalHumanAgentId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261111073632_AddTicketOriginalHumanAgent'
)
BEGIN
    EXEC(N'
    DECLARE @cc int = (SELECT TOP (1) [DepartmentId] FROM [Departments] WHERE [Code] = N''CC'' AND [IsActive] = 1);
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
                              WHERE au.[EntityType] = N''Ticket'' AND au.[EntityId] = CAST(t.[TicketId] AS nvarchar(64))
                                AND au.[ActorEmployeeId] = first_cc.[AssignedEmployeeId]))
              AND EXISTS (SELECT 1 FROM [Employees] AS e
                          JOIN [AspNetUsers] AS u ON u.[Id] = e.[EmployeeId]
                          WHERE e.[EmployeeId] = first_cc.[AssignedEmployeeId]
                            AND e.[DeactivatedAtUtc] IS NULL
                            AND NOT (u.[LockoutEnabled] = 1 AND u.[LockoutEnd] IS NOT NULL AND u.[LockoutEnd] > SYSDATETIMEOFFSET())
                            AND LOWER(ISNULL(u.[UserName], N'''') + N'' '' + ISNULL(u.[Email], N'''') + N'' '' + ISNULL(e.[DisplayName], N'''')) NOT LIKE N''%genesys%''
                            AND LOWER(ISNULL(u.[UserName], N'''') + N'' '' + ISNULL(u.[Email], N'''') + N'' '' + ISNULL(e.[DisplayName], N'''')) NOT LIKE N''%integration%''
                            AND LOWER(ISNULL(u.[UserName], N'''') + N'' '' + ISNULL(u.[Email], N'''') + N'' '' + ISNULL(e.[DisplayName], N'''')) NOT LIKE N''%service%''
                            AND LOWER(ISNULL(u.[UserName], N'''') + N'' '' + ISNULL(u.[Email], N'''') + N'' '' + ISNULL(e.[DisplayName], N'''')) NOT LIKE N''%svc%''
                            AND NOT (NULLIF(LTRIM(RTRIM(u.[GenesysUserId])), N'''') IS NULL
                                     AND NOT EXISTS (SELECT 1 FROM [TicketInteractions] AS hi WHERE hi.[HandledByUserId] = e.[EmployeeId])
                                     AND NOT EXISTS (SELECT 1 FROM [TicketInteractionHandlers] AS hh WHERE hh.[HandledByUserId] = e.[EmployeeId])
                                     AND EXISTS (SELECT 1 FROM [Tickets] AS ct
                                                 WHERE ct.[CreatedByEmployeeId] = e.[EmployeeId]
                                                   AND EXISTS (SELECT 1 FROM [TicketInteractions] AS gi WHERE gi.[TicketId] = ct.[TicketId] AND gi.[Source] = 2))))
              AND EXISTS (SELECT 1 FROM [UserDepartmentAssignments] AS m WHERE m.[EmployeeId] = first_cc.[AssignedEmployeeId] AND m.[DepartmentId] = @cc)
              AND EXISTS (SELECT 1 FROM [AspNetUserRoles] AS ur JOIN [AspNetRoles] AS r ON r.[Id] = ur.[RoleId]
                          WHERE ur.[UserId] = first_cc.[AssignedEmployeeId] AND r.[Name] = N''CS Agent'')
              AND NOT EXISTS (SELECT 1 FROM [CollectionsTowerAssignments] AS c WHERE c.[EmployeeId] = first_cc.[AssignedEmployeeId]);
    END');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261111073632_AddTicketOriginalHumanAgent'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261111073632_AddTicketOriginalHumanAgent', N'10.0.11');
END;

COMMIT;
GO

