-- ############################################################################################
-- ## UAT ONLY. Run against database TigerCsTicketing; any other name aborts before any change.
-- ## DO NOT RUN ON PRODUCTION from this file (Production gets its own reviewed script).
-- ############################################################################################
-- Migration : 20261104123248_AddCollectionsTowerAssignments (EF Core 10.0.11)
-- Depends on: 20261102065459_AddUserPermissionOverrides and dbo.Employees / dbo.Departments / dbo.Tickets.
-- Effect    : creates FOUR new, empty tables (+ indexes, FKs) and records the migration in dbo.__EFMigrationsHistory:
--               CollectionsTowerAssignments        monthly tower -> collector mapping (unique primary collector per tower and month)
--               CollectionsTowerAssignmentHistory  append-only history of changes to that mapping
--               TicketAssignmentHistory            append-only assignment event log of Collections tickets
--               TicketCollectionsUnits             the verified Tower + Unit selected when a ticket is moved into Collections
--             No existing table, row, user, role or permission is read-modified, altered or deleted. Empty tables = no tower is
--             assigned = Collections tickets keep arriving unassigned exactly as before until a manager activates a month.
-- Idempotent: safe to run repeatedly (guarded by __EFMigrationsHistory). Runs in one transaction.
-- Generated from: dotnet ef migrations script 20261102065459_AddUserPermissionOverrides 20261104123248_AddCollectionsTowerAssignments --idempotent
-- ############################################################################################
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF DB_NAME() <> N'TigerCsTicketing'
    THROW 50100, N'ABORTED: this script only runs against database [TigerCsTicketing]. Nothing was changed.', 1;
IF OBJECT_ID(N'dbo.__EFMigrationsHistory', N'U') IS NULL
    THROW 50101, N'ABORTED: dbo.__EFMigrationsHistory is missing - wrong database?', 1;
IF NOT EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId = N'20261102065459_AddUserPermissionOverrides')
    THROW 50102, N'ABORTED: 20261102065459_AddUserPermissionOverrides is not recorded; apply earlier migrations first.', 1;
-- (no GO between the guards and the migration: a failed guard must abort the whole batch)

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261104123248_AddCollectionsTowerAssignments'
)
BEGIN
    CREATE TABLE [CollectionsTowerAssignmentHistory] (
        [CollectionsTowerAssignmentHistoryId] bigint NOT NULL IDENTITY,
        [TowerId] int NOT NULL,
        [TowerCode] nvarchar(20) NOT NULL,
        [TowerName] nvarchar(200) NOT NULL,
        [Year] int NOT NULL,
        [Month] int NOT NULL,
        [PreviousEmployeeId] uniqueidentifier NULL,
        [NewEmployeeId] uniqueidentifier NULL,
        [Action] tinyint NOT NULL,
        [ChangedByEmployeeId] uniqueidentifier NOT NULL,
        [ChangedAtUtc] datetime2 NOT NULL,
        CONSTRAINT [PK_CollectionsTowerAssignmentHistory] PRIMARY KEY ([CollectionsTowerAssignmentHistoryId]),
        CONSTRAINT [FK_CollectionsTowerAssignmentHistory_Employees_ChangedByEmployeeId] FOREIGN KEY ([ChangedByEmployeeId]) REFERENCES [Employees] ([EmployeeId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_CollectionsTowerAssignmentHistory_Employees_NewEmployeeId] FOREIGN KEY ([NewEmployeeId]) REFERENCES [Employees] ([EmployeeId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_CollectionsTowerAssignmentHistory_Employees_PreviousEmployeeId] FOREIGN KEY ([PreviousEmployeeId]) REFERENCES [Employees] ([EmployeeId]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261104123248_AddCollectionsTowerAssignments'
)
BEGIN
    CREATE TABLE [CollectionsTowerAssignments] (
        [CollectionsTowerAssignmentId] bigint NOT NULL IDENTITY,
        [TowerId] int NOT NULL,
        [TowerCode] nvarchar(20) NOT NULL,
        [TowerName] nvarchar(200) NOT NULL,
        [CompanyId] int NOT NULL,
        [Year] int NOT NULL,
        [Month] int NOT NULL,
        [EmployeeId] uniqueidentifier NOT NULL,
        [IsPrimary] bit NOT NULL,
        [Status] tinyint NOT NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [CreatedByEmployeeId] uniqueidentifier NOT NULL,
        [UpdatedAtUtc] datetime2 NOT NULL,
        [UpdatedByEmployeeId] uniqueidentifier NOT NULL,
        [ActivatedAtUtc] datetime2 NULL,
        [ActivatedByEmployeeId] uniqueidentifier NULL,
        CONSTRAINT [PK_CollectionsTowerAssignments] PRIMARY KEY ([CollectionsTowerAssignmentId]),
        CONSTRAINT [FK_CollectionsTowerAssignments_Employees_EmployeeId] FOREIGN KEY ([EmployeeId]) REFERENCES [Employees] ([EmployeeId]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261104123248_AddCollectionsTowerAssignments'
)
BEGIN
    CREATE TABLE [TicketAssignmentHistory] (
        [TicketAssignmentHistoryEntryId] bigint NOT NULL IDENTITY,
        [TicketId] bigint NOT NULL,
        [DepartmentId] int NOT NULL,
        [PreviousEmployeeId] uniqueidentifier NULL,
        [NewEmployeeId] uniqueidentifier NOT NULL,
        [Kind] tinyint NOT NULL,
        [TowerId] int NULL,
        [TowerCode] nvarchar(20) NULL,
        [TowerName] nvarchar(200) NULL,
        [UnitNumber] nvarchar(100) NULL,
        [ChangedByEmployeeId] uniqueidentifier NULL,
        [ChangedAtUtc] datetime2 NOT NULL,
        [Reason] nvarchar(500) NULL,
        CONSTRAINT [PK_TicketAssignmentHistory] PRIMARY KEY ([TicketAssignmentHistoryEntryId]),
        CONSTRAINT [FK_TicketAssignmentHistory_Departments_DepartmentId] FOREIGN KEY ([DepartmentId]) REFERENCES [Departments] ([DepartmentId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_TicketAssignmentHistory_Employees_ChangedByEmployeeId] FOREIGN KEY ([ChangedByEmployeeId]) REFERENCES [Employees] ([EmployeeId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_TicketAssignmentHistory_Employees_NewEmployeeId] FOREIGN KEY ([NewEmployeeId]) REFERENCES [Employees] ([EmployeeId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_TicketAssignmentHistory_Employees_PreviousEmployeeId] FOREIGN KEY ([PreviousEmployeeId]) REFERENCES [Employees] ([EmployeeId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_TicketAssignmentHistory_Tickets_TicketId] FOREIGN KEY ([TicketId]) REFERENCES [Tickets] ([TicketId]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261104123248_AddCollectionsTowerAssignments'
)
BEGIN
    CREATE TABLE [TicketCollectionsUnits] (
        [TicketId] bigint NOT NULL,
        [TowerId] int NOT NULL,
        [TowerCode] nvarchar(20) NOT NULL,
        [TowerName] nvarchar(200) NOT NULL,
        [CompanyId] int NOT NULL,
        [CrmUnitId] int NOT NULL,
        [CrmProjectId] int NOT NULL,
        [UnitNumber] nvarchar(100) NOT NULL,
        [SelectedByEmployeeId] uniqueidentifier NOT NULL,
        [SelectedAtUtc] datetime2 NOT NULL,
        CONSTRAINT [PK_TicketCollectionsUnits] PRIMARY KEY ([TicketId]),
        CONSTRAINT [FK_TicketCollectionsUnits_Employees_SelectedByEmployeeId] FOREIGN KEY ([SelectedByEmployeeId]) REFERENCES [Employees] ([EmployeeId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_TicketCollectionsUnits_Tickets_TicketId] FOREIGN KEY ([TicketId]) REFERENCES [Tickets] ([TicketId]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261104123248_AddCollectionsTowerAssignments'
)
BEGIN
    CREATE INDEX [IX_CollectionsTowerAssignmentHistory_ChangedByEmployeeId] ON [CollectionsTowerAssignmentHistory] ([ChangedByEmployeeId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261104123248_AddCollectionsTowerAssignments'
)
BEGIN
    CREATE INDEX [IX_CollectionsTowerAssignmentHistory_Month_Tower] ON [CollectionsTowerAssignmentHistory] ([Year], [Month], [TowerId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261104123248_AddCollectionsTowerAssignments'
)
BEGIN
    CREATE INDEX [IX_CollectionsTowerAssignmentHistory_NewEmployeeId] ON [CollectionsTowerAssignmentHistory] ([NewEmployeeId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261104123248_AddCollectionsTowerAssignments'
)
BEGIN
    CREATE INDEX [IX_CollectionsTowerAssignmentHistory_PreviousEmployeeId] ON [CollectionsTowerAssignmentHistory] ([PreviousEmployeeId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261104123248_AddCollectionsTowerAssignments'
)
BEGIN
    CREATE INDEX [IX_CollectionsTowerAssignments_EmployeeId] ON [CollectionsTowerAssignments] ([EmployeeId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261104123248_AddCollectionsTowerAssignments'
)
BEGIN
    CREATE INDEX [IX_CollectionsTowerAssignments_Month_Employee] ON [CollectionsTowerAssignments] ([Year], [Month], [EmployeeId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261104123248_AddCollectionsTowerAssignments'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [UX_CollectionsTowerAssignments_Tower_Month_Primary] ON [CollectionsTowerAssignments] ([TowerId], [Year], [Month]) WHERE [IsPrimary] = 1');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261104123248_AddCollectionsTowerAssignments'
)
BEGIN
    CREATE INDEX [IX_TicketAssignmentHistory_ChangedByEmployeeId] ON [TicketAssignmentHistory] ([ChangedByEmployeeId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261104123248_AddCollectionsTowerAssignments'
)
BEGIN
    CREATE INDEX [IX_TicketAssignmentHistory_DepartmentId] ON [TicketAssignmentHistory] ([DepartmentId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261104123248_AddCollectionsTowerAssignments'
)
BEGIN
    CREATE INDEX [IX_TicketAssignmentHistory_NewEmployeeId] ON [TicketAssignmentHistory] ([NewEmployeeId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261104123248_AddCollectionsTowerAssignments'
)
BEGIN
    CREATE INDEX [IX_TicketAssignmentHistory_PreviousEmployeeId] ON [TicketAssignmentHistory] ([PreviousEmployeeId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261104123248_AddCollectionsTowerAssignments'
)
BEGIN
    CREATE INDEX [IX_TicketAssignmentHistory_Ticket_Changed] ON [TicketAssignmentHistory] ([TicketId], [ChangedAtUtc]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261104123248_AddCollectionsTowerAssignments'
)
BEGIN
    CREATE INDEX [IX_TicketCollectionsUnits_SelectedByEmployeeId] ON [TicketCollectionsUnits] ([SelectedByEmployeeId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261104123248_AddCollectionsTowerAssignments'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261104123248_AddCollectionsTowerAssignments', N'10.0.11');
END;

COMMIT;
GO
