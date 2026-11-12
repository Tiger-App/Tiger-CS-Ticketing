BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261112071745_AddTicketAssignmentSupersededAt'
)
BEGIN
    ALTER TABLE [TicketAssignments] ADD [SupersededAtUtc] datetime2 NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261112071745_AddTicketAssignmentSupersededAt'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261112071745_AddTicketAssignmentSupersededAt', N'10.0.11');
END;

COMMIT;
GO

