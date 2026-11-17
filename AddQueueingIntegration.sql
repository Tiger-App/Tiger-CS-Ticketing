BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117101035_AddQueueingIntegration'
)
BEGIN
    ALTER TABLE [Tickets] ADD [ExternalReference] nvarchar(100) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117101035_AddQueueingIntegration'
)
BEGIN
    ALTER TABLE [Tickets] ADD [ExternalTicketId] nvarchar(100) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117101035_AddQueueingIntegration'
)
BEGIN
    ALTER TABLE [Tickets] ADD [IntegrationSource] nvarchar(50) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117101035_AddQueueingIntegration'
)
BEGIN
    CREATE TABLE [QueueingCompanyMappings] (
        [QueueingCompanyMappingId] int NOT NULL IDENTITY,
        [CompanyId] nvarchar(100) NOT NULL,
        [DepartmentId] int NOT NULL,
        [IsActive] bit NOT NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        CONSTRAINT [PK_QueueingCompanyMappings] PRIMARY KEY ([QueueingCompanyMappingId]),
        CONSTRAINT [FK_QueueingCompanyMappings_Departments_DepartmentId] FOREIGN KEY ([DepartmentId]) REFERENCES [Departments] ([DepartmentId]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117101035_AddQueueingIntegration'
)
BEGIN
    CREATE TABLE [QueueingEvents] (
        [QueueingEventId] bigint NOT NULL IDENTITY,
        [Source] nvarchar(50) NOT NULL,
        [EventId] nvarchar(100) NOT NULL,
        [EventType] tinyint NOT NULL,
        [ExternalTicketId] nvarchar(100) NOT NULL,
        [EventSequence] bigint NOT NULL,
        [OccurredAtUtc] datetime2 NOT NULL,
        [Payload] nvarchar(max) NULL,
        [PayloadHash] char(64) NOT NULL,
        [PayloadPurgedAtUtc] datetime2 NULL,
        [Status] tinyint NOT NULL,
        [OutcomeCode] nvarchar(64) NULL,
        [OutcomeDetail] nvarchar(1000) NULL,
        [TicketId] bigint NULL,
        [AttemptCount] int NOT NULL,
        [NextRetryAtUtc] datetime2 NULL,
        [ReceivedAtUtc] datetime2 NOT NULL,
        [ProcessedAtUtc] datetime2 NULL,
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_QueueingEvents] PRIMARY KEY ([QueueingEventId])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117101035_AddQueueingIntegration'
)
BEGIN
    CREATE TABLE [QueueingTicketLinks] (
        [QueueingTicketLinkId] bigint NOT NULL IDENTITY,
        [TicketId] bigint NOT NULL,
        [ExternalTicketId] nvarchar(100) NOT NULL,
        [QueueNumber] nvarchar(100) NULL,
        [CompanyId] nvarchar(100) NULL,
        [ExternalTicketTypeId] nvarchar(100) NULL,
        [LastAppliedSequence] bigint NOT NULL,
        [LastAppliedOccurredAtUtc] datetime2 NOT NULL,
        [State] tinyint NOT NULL,
        [CounterId] nvarchar(100) NULL,
        [AssignedExternalUserId] nvarchar(100) NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_QueueingTicketLinks] PRIMARY KEY ([QueueingTicketLinkId]),
        CONSTRAINT [FK_QueueingTicketLinks_Tickets_TicketId] FOREIGN KEY ([TicketId]) REFERENCES [Tickets] ([TicketId]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117101035_AddQueueingIntegration'
)
BEGIN
    CREATE TABLE [QueueingTicketTypeMappings] (
        [QueueingTicketTypeMappingId] int NOT NULL IDENTITY,
        [CompanyId] nvarchar(100) NOT NULL,
        [TicketTypeId] nvarchar(100) NOT NULL,
        [RequestTypeId] int NOT NULL,
        [IsActive] bit NOT NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        CONSTRAINT [PK_QueueingTicketTypeMappings] PRIMARY KEY ([QueueingTicketTypeMappingId])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117101035_AddQueueingIntegration'
)
BEGIN
    CREATE TABLE [QueueingUserMappings] (
        [QueueingUserMappingId] int NOT NULL IDENTITY,
        [ExternalUserId] nvarchar(100) NOT NULL,
        [EmployeeId] uniqueidentifier NOT NULL,
        [IsActive] bit NOT NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        CONSTRAINT [PK_QueueingUserMappings] PRIMARY KEY ([QueueingUserMappingId])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117101035_AddQueueingIntegration'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [UX_Tickets_IntegrationSource_ExternalTicketId] ON [Tickets] ([IntegrationSource], [ExternalTicketId]) WHERE [IntegrationSource] IS NOT NULL AND [ExternalTicketId] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117101035_AddQueueingIntegration'
)
BEGIN
    CREATE INDEX [IX_QueueingCompanyMappings_DepartmentId] ON [QueueingCompanyMappings] ([DepartmentId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117101035_AddQueueingIntegration'
)
BEGIN
    CREATE UNIQUE INDEX [UX_QueueingCompanyMappings_CompanyId] ON [QueueingCompanyMappings] ([CompanyId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117101035_AddQueueingIntegration'
)
BEGIN
    EXEC(N'CREATE INDEX [IX_QueueingEvents_PurgeablePayloads] ON [QueueingEvents] ([ProcessedAtUtc]) WHERE [Payload] IS NOT NULL AND [Status] IN (2, 4, 5)');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117101035_AddQueueingIntegration'
)
BEGIN
    CREATE INDEX [IX_QueueingEvents_Status_NextRetry] ON [QueueingEvents] ([Status], [NextRetryAtUtc]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117101035_AddQueueingIntegration'
)
BEGIN
    CREATE INDEX [IX_QueueingEvents_Ticket_Sequence] ON [QueueingEvents] ([Source], [ExternalTicketId], [EventSequence]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117101035_AddQueueingIntegration'
)
BEGIN
    CREATE UNIQUE INDEX [UX_QueueingEvents_Source_EventId] ON [QueueingEvents] ([Source], [EventId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117101035_AddQueueingIntegration'
)
BEGIN
    CREATE UNIQUE INDEX [UX_QueueingTicketLinks_ExternalTicketId] ON [QueueingTicketLinks] ([ExternalTicketId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117101035_AddQueueingIntegration'
)
BEGIN
    CREATE UNIQUE INDEX [UX_QueueingTicketLinks_TicketId] ON [QueueingTicketLinks] ([TicketId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117101035_AddQueueingIntegration'
)
BEGIN
    CREATE UNIQUE INDEX [UX_QueueingTicketTypeMappings_Company_Type] ON [QueueingTicketTypeMappings] ([CompanyId], [TicketTypeId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117101035_AddQueueingIntegration'
)
BEGIN
    CREATE UNIQUE INDEX [UX_QueueingUserMappings_EmployeeId] ON [QueueingUserMappings] ([EmployeeId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117101035_AddQueueingIntegration'
)
BEGIN
    CREATE UNIQUE INDEX [UX_QueueingUserMappings_ExternalUserId] ON [QueueingUserMappings] ([ExternalUserId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117101035_AddQueueingIntegration'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261117101035_AddQueueingIntegration', N'10.0.11');
END;

COMMIT;
GO

