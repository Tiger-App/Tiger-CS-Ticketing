-- Migration 20261109123253_AddManualGenesysDispatchItemFields (EF Core 10.0.11): adds TWO columns to dbo.CollectionsDispatchItems, ProjectName and TowerName (nvarchar(200) NOT NULL DEFAULT empty), which the Manual Genesys contact list (eleven official columns) sends. Existing rows get an empty value. No table is added. Idempotent. NOT executed by Claude.
-- Generated from: dotnet ef migrations script 20261109074858_AddCollectionsGenesysDeliveryStats 20261109123253_AddManualGenesysDispatchItemFields --idempotent
﻿BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261109123253_AddManualGenesysDispatchItemFields'
)
BEGIN
    ALTER TABLE [CollectionsDispatchItems] ADD [ProjectName] nvarchar(200) NOT NULL DEFAULT N'';
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261109123253_AddManualGenesysDispatchItemFields'
)
BEGIN
    ALTER TABLE [CollectionsDispatchItems] ADD [TowerName] nvarchar(200) NOT NULL DEFAULT N'';
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261109123253_AddManualGenesysDispatchItemFields'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261109123253_AddManualGenesysDispatchItemFields', N'10.0.11');
END;

COMMIT;
GO

