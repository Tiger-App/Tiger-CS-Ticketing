BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261120074059_AddInteractionEndReceivedAt'
)
BEGIN
    ALTER TABLE [TicketInteractions] ADD [EndReceivedAtUtc] datetime2 NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261120074059_AddInteractionEndReceivedAt'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261120074059_AddInteractionEndReceivedAt', N'10.0.11');
END;

COMMIT;
GO

