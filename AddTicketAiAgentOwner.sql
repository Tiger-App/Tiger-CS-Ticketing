BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118124858_AddTicketAiAgentOwner'
)
BEGIN
    ALTER TABLE [Tickets] ADD [AiAgentOwnerEmployeeId] uniqueidentifier NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118124858_AddTicketAiAgentOwner'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261118124858_AddTicketAiAgentOwner', N'10.0.11');
END;

COMMIT;
GO

