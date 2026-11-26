BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261126154428_AddEmployeeReviewActivationCodes'
)
BEGIN
    ALTER TABLE [EmployeeReviewDevices] ADD [ActivationCodeExpiresAtUtc] datetime2 NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261126154428_AddEmployeeReviewActivationCodes'
)
BEGIN
    ALTER TABLE [EmployeeReviewDevices] ADD [ActivationCodeHash] varbinary(32) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261126154428_AddEmployeeReviewActivationCodes'
)
BEGIN
    ALTER TABLE [EmployeeReviewDevices] ADD [ActivationCodeSalt] varbinary(16) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261126154428_AddEmployeeReviewActivationCodes'
)
BEGIN
    CREATE TABLE [EmployeeReviewActivationThrottles] (
        [Key] nvarchar(64) NOT NULL,
        [AttemptCount] int NOT NULL,
        [WindowStartUtc] datetime2 NOT NULL,
        CONSTRAINT [PK_EmployeeReviewActivationThrottles] PRIMARY KEY ([Key])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261126154428_AddEmployeeReviewActivationCodes'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261126154428_AddEmployeeReviewActivationCodes', N'10.0.11');
END;

COMMIT;
GO

