BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261119120249_AddAssignmentHistoryPreviousDepartment'
)
BEGIN
    ALTER TABLE [TicketAssignmentHistory] ADD [PreviousDepartmentId] int NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261119120249_AddAssignmentHistoryPreviousDepartment'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261119120249_AddAssignmentHistoryPreviousDepartment', N'10.0.11');
END;

COMMIT;
GO

