-- Migration 20261109120816_AddCollectionsExceptionInclusions (EF Core 10.0.11): adds ONE new table, dbo.CollectionsExceptionInclusions (manual GULF_CUSTOMERS inclusions: who included which unit, when, for which Manual campaign). Touches no existing table. Idempotent. NOT executed by Claude.
-- Generated from: dotnet ef migrations script 20261109074858_AddCollectionsGenesysDeliveryStats 20261109120816_AddCollectionsExceptionInclusions --idempotent
Build started...
Build succeeded.
The Entity Framework tools version '10.0.0' is older than that of the runtime '10.0.11'. Update the tools for the latest features and bug fixes. See https://aka.ms/AAc1fbw for more information.
BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261109120816_AddCollectionsExceptionInclusions'
)
BEGIN
    CREATE TABLE [CollectionsExceptionInclusions] (
        [CollectionsExceptionInclusionId] bigint NOT NULL IDENTITY,
        [CampaignKey] varchar(80) NOT NULL,
        [CompanyId] int NOT NULL,
        [TenantId] nvarchar(50) NOT NULL,
        [UnitCode] nvarchar(60) NOT NULL,
        [IncludedByEmployeeId] uniqueidentifier NOT NULL,
        [IncludedAtUtc] datetime2 NOT NULL,
        [RemovedByEmployeeId] uniqueidentifier NULL,
        [RemovedAtUtc] datetime2 NULL,
        CONSTRAINT [PK_CollectionsExceptionInclusions] PRIMARY KEY ([CollectionsExceptionInclusionId])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261109120816_AddCollectionsExceptionInclusions'
)
BEGIN
    CREATE INDEX [IX_CollectionsExceptionInclusions_Campaign] ON [CollectionsExceptionInclusions] ([CampaignKey]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261109120816_AddCollectionsExceptionInclusions'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [UX_CollectionsExceptionInclusions_Active] ON [CollectionsExceptionInclusions] ([CampaignKey], [CompanyId], [TenantId], [UnitCode]) WHERE [RemovedAtUtc] IS NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261109120816_AddCollectionsExceptionInclusions'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261109120816_AddCollectionsExceptionInclusions', N'10.0.11');
END;

COMMIT;
GO


