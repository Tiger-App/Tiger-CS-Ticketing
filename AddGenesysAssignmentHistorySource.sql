BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261112065853_AddGenesysAssignmentHistorySource'
)
BEGIN
    ALTER TABLE [TicketAssignmentHistory] ADD [GenesysConversationId] nvarchar(64) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261112065853_AddGenesysAssignmentHistorySource'
)
BEGIN
    ALTER TABLE [TicketAssignmentHistory] ADD [Source] tinyint NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261112065853_AddGenesysAssignmentHistorySource'
)
BEGIN
    ALTER TABLE [TicketAssignmentHistory] ADD [SourceEventAtUtc] datetime2 NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261112065853_AddGenesysAssignmentHistorySource'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261112065853_AddGenesysAssignmentHistorySource', N'10.0.11');
END;

COMMIT;
GO

