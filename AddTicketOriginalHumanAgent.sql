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
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261111073632_AddTicketOriginalHumanAgent', N'10.0.11');
END;

COMMIT;
GO

