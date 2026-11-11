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

    EXEC(N'UPDATE t SET t.[OriginalHumanAgentId] = first_cc.[AssignedEmployeeId]
    FROM [Tickets] AS t
    CROSS APPLY (
        SELECT TOP (1) a.[AssignedEmployeeId]
        FROM [TicketAssignments] AS a
        INNER JOIN [Departments] AS d ON d.[DepartmentId] = a.[AssignedDepartmentId] AND d.[Code] = N''CC''
        WHERE a.[TicketId] = t.[TicketId]
        ORDER BY a.[AssignedAtUtc], a.[TicketAssignmentId]
    ) AS first_cc
    WHERE t.[OriginalHumanAgentId] IS NULL;');
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

