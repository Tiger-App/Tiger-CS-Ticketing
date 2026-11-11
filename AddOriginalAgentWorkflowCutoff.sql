BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261111132429_AddOriginalAgentWorkflowCutoff'
)
BEGIN
    ALTER TABLE [Tickets] ADD [OriginalAgentWorkflowEnabled] bit NOT NULL DEFAULT CAST(0 AS bit);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261111132429_AddOriginalAgentWorkflowCutoff'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261111132429_AddOriginalAgentWorkflowCutoff', N'10.0.11');
END;

COMMIT;
GO

