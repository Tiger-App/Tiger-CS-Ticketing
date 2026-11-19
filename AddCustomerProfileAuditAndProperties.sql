BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261119081103_AddCustomerProfileAuditAndProperties'
)
BEGIN
    ALTER TABLE [CustomerProfileOverrides] ADD [AdditionalPhones] nvarchar(400) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261119081103_AddCustomerProfileAuditAndProperties'
)
BEGIN
    ALTER TABLE [CustomerProfileOverrides] ADD [Country] nvarchar(100) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261119081103_AddCustomerProfileAuditAndProperties'
)
BEGIN
    ALTER TABLE [CustomerProfileOverrides] ADD [CustomerProfileId] uniqueidentifier NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000';
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261119081103_AddCustomerProfileAuditAndProperties'
)
BEGIN
    ALTER TABLE [CustomerProfileOverrides] ADD [Email] nvarchar(256) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261119081103_AddCustomerProfileAuditAndProperties'
)
BEGIN
    ALTER TABLE [CustomerProfileOverrides] ADD [Emirate] nvarchar(100) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261119081103_AddCustomerProfileAuditAndProperties'
)
BEGIN
    ALTER TABLE [CustomerProfileOverrides] ADD [FullName] nvarchar(200) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261119081103_AddCustomerProfileAuditAndProperties'
)
BEGIN
    ALTER TABLE [CustomerProfileOverrides] ADD [LandlineNumber] nvarchar(32) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261119081103_AddCustomerProfileAuditAndProperties'
)
BEGIN
    ALTER TABLE [CustomerProfileOverrides] ADD [Location] nvarchar(200) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261119081103_AddCustomerProfileAuditAndProperties'
)
BEGIN
    ALTER TABLE [CustomerProfileOverrides] ADD [Nationality] nvarchar(100) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261119081103_AddCustomerProfileAuditAndProperties'
)
BEGIN
    ALTER TABLE [CustomerProfileOverrides] ADD [PreferredLanguage] nvarchar(100) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261119081103_AddCustomerProfileAuditAndProperties'
)
BEGIN
    CREATE TABLE [CustomerProfileChanges] (
        [CustomerProfileChangeId] bigint NOT NULL IDENTITY,
        [CustomerProfileId] uniqueidentifier NOT NULL,
        [CustomerKey] nvarchar(300) NOT NULL,
        [FieldName] nvarchar(100) NOT NULL,
        [OldValue] nvarchar(1000) NULL,
        [NewValue] nvarchar(1000) NULL,
        [ModifiedByEmployeeId] uniqueidentifier NOT NULL,
        [ModifiedByName] nvarchar(200) NOT NULL,
        [ModifiedAtUtc] datetime2 NOT NULL,
        [Source] nvarchar(50) NOT NULL,
        [ChangeReason] nvarchar(500) NULL,
        [RelatedTicketId] bigint NULL,
        [CorrelationId] uniqueidentifier NOT NULL,
        [ChangeKey] nchar(64) NOT NULL,
        CONSTRAINT [PK_CustomerProfileChanges] PRIMARY KEY ([CustomerProfileChangeId])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261119081103_AddCustomerProfileAuditAndProperties'
)
BEGIN
    CREATE TABLE [CustomerPropertyAssociations] (
        [CustomerPropertyAssociationId] bigint NOT NULL IDENTITY,
        [CustomerKey] nvarchar(300) NOT NULL,
        [CustomerProfileId] uniqueidentifier NOT NULL,
        [ProjectName] nvarchar(200) NOT NULL,
        [UnitNumber] nvarchar(100) NOT NULL,
        [Relationship] tinyint NOT NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [CreatedByEmployeeId] uniqueidentifier NOT NULL,
        [UpdatedAtUtc] datetime2 NOT NULL,
        [UpdatedByEmployeeId] uniqueidentifier NOT NULL,
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_CustomerPropertyAssociations] PRIMARY KEY ([CustomerPropertyAssociationId])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261119081103_AddCustomerProfileAuditAndProperties'
)
BEGIN
    EXEC(N'UPDATE [CustomerProfileOverrides] SET [CustomerProfileId] = NEWID() WHERE [CustomerProfileId] = ''00000000-0000-0000-0000-000000000000'';');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261119081103_AddCustomerProfileAuditAndProperties'
)
BEGIN
    CREATE UNIQUE INDEX [IX_CustomerProfileOverrides_CustomerProfileId] ON [CustomerProfileOverrides] ([CustomerProfileId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261119081103_AddCustomerProfileAuditAndProperties'
)
BEGIN
    CREATE UNIQUE INDEX [IX_CustomerProfileChanges_ChangeKey] ON [CustomerProfileChanges] ([ChangeKey]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261119081103_AddCustomerProfileAuditAndProperties'
)
BEGIN
    CREATE INDEX [IX_CustomerProfileChanges_CorrelationId] ON [CustomerProfileChanges] ([CorrelationId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261119081103_AddCustomerProfileAuditAndProperties'
)
BEGIN
    CREATE INDEX [IX_CustomerProfileChanges_CustomerKey] ON [CustomerProfileChanges] ([CustomerKey]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261119081103_AddCustomerProfileAuditAndProperties'
)
BEGIN
    CREATE INDEX [IX_CustomerProfileChanges_CustomerProfileId_ModifiedAtUtc] ON [CustomerProfileChanges] ([CustomerProfileId], [ModifiedAtUtc]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261119081103_AddCustomerProfileAuditAndProperties'
)
BEGIN
    CREATE INDEX [IX_CustomerPropertyAssociations_CustomerKey] ON [CustomerPropertyAssociations] ([CustomerKey]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261119081103_AddCustomerProfileAuditAndProperties'
)
BEGIN
    CREATE UNIQUE INDEX [IX_CustomerPropertyAssociations_CustomerProfileId_ProjectName_UnitNumber] ON [CustomerPropertyAssociations] ([CustomerProfileId], [ProjectName], [UnitNumber]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261119081103_AddCustomerProfileAuditAndProperties'
)
BEGIN
    EXEC(N'CREATE TRIGGER [TR_CustomerProfileChanges_AppendOnly] ON [CustomerProfileChanges] INSTEAD OF UPDATE, DELETE AS BEGIN SET NOCOUNT ON; THROW 51000, ''CustomerProfileChanges is append-only: audit rows cannot be updated or deleted.'', 1; END');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261119081103_AddCustomerProfileAuditAndProperties'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261119081103_AddCustomerProfileAuditAndProperties', N'10.0.11');
END;

COMMIT;
GO

