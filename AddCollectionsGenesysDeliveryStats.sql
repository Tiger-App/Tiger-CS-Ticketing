-- Migration 20261109074858_AddCollectionsGenesysDeliveryStats (EF Core 10.0.11): adds TWO new tables, dbo.CollectionsGenesysMessageResults and dbo.CollectionsGenesysSyncStates (delivery results saved by the manual Sync from Genesys). Touches no existing table. Idempotent. NOT executed by Claude.
-- Generated from: dotnet ef migrations script 20261106044011_AddDirectSendInstalments 20261109074858_AddCollectionsGenesysDeliveryStats --idempotent
﻿BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261109074858_AddCollectionsGenesysDeliveryStats'
)
BEGIN
    CREATE TABLE [CollectionsGenesysMessageResults] (
        [CollectionsGenesysMessageResultId] bigint NOT NULL IDENTITY,
        [CollectionsDispatchId] bigint NOT NULL,
        [CollectionsDispatchItemId] bigint NOT NULL,
        [GenesysConversationId] varchar(64) NOT NULL,
        [GenesysMessageId] varchar(128) NOT NULL,
        [GenesysContactId] varchar(64) NULL,
        [Status] varchar(16) NOT NULL,
        [SentAtUtc] datetime2 NULL,
        [DeliveredAtUtc] datetime2 NULL,
        [FailureReason] nvarchar(300) NULL,
        [SyncedAtUtc] datetime2 NOT NULL,
        CONSTRAINT [PK_CollectionsGenesysMessageResults] PRIMARY KEY ([CollectionsGenesysMessageResultId])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261109074858_AddCollectionsGenesysDeliveryStats'
)
BEGIN
    CREATE TABLE [CollectionsGenesysSyncStates] (
        [CollectionsDispatchId] bigint NOT NULL,
        [LastSyncedAtUtc] datetime2 NOT NULL,
        [Outcome] varchar(16) NOT NULL,
        [Error] nvarchar(500) NULL,
        [Contacts] int NOT NULL,
        [Sent] int NOT NULL,
        [Delivered] int NOT NULL,
        [Failed] int NOT NULL,
        [Pending] int NOT NULL,
        [NotSent] int NOT NULL,
        [MessageAttempts] int NOT NULL,
        [ConversationsFound] int NOT NULL,
        [LastCompletedAtUtc] datetime2 NULL,
        CONSTRAINT [PK_CollectionsGenesysSyncStates] PRIMARY KEY ([CollectionsDispatchId])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261109074858_AddCollectionsGenesysDeliveryStats'
)
BEGIN
    CREATE INDEX [IX_CollectionsGenesysMessageResults_Dispatch_Item] ON [CollectionsGenesysMessageResults] ([CollectionsDispatchId], [CollectionsDispatchItemId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261109074858_AddCollectionsGenesysDeliveryStats'
)
BEGIN
    CREATE UNIQUE INDEX [UX_CollectionsGenesysMessageResults_Message] ON [CollectionsGenesysMessageResults] ([GenesysConversationId], [GenesysMessageId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261109074858_AddCollectionsGenesysDeliveryStats'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261109074858_AddCollectionsGenesysDeliveryStats', N'10.0.11');
END;

COMMIT;
GO

