BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261126175759_AddQueueingTicketTypeName'
)
BEGIN
    ALTER TABLE [QueueingTicketLinks] ADD [ExternalTicketTypeName] nvarchar(200) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261126175759_AddQueueingTicketTypeName'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261126175759_AddQueueingTicketTypeName', N'10.0.11');
END;

COMMIT;
GO

