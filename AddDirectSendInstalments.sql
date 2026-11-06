-- Migration 20261106044011_AddDirectSendInstalments (EF Core 10.0.11): adds ONE nullable column dbo.CollectionsReviewRecords.InstalmentsJson. Idempotent. NOT executed by Claude.
-- Generated from: dotnet ef migrations script 20261105094644_AddCustomerContactDetails 20261106044011_AddDirectSendInstalments --idempotent
BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261106044011_AddDirectSendInstalments'
)
BEGIN
    ALTER TABLE [CollectionsReviewRecords] ADD [InstalmentsJson] nvarchar(max) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261106044011_AddDirectSendInstalments'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261106044011_AddDirectSendInstalments', N'10.0.11');
END;

COMMIT;
GO


