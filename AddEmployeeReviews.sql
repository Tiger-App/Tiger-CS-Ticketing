BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261124071552_AddEmployeeReviews'
)
BEGIN
    CREATE TABLE [EmployeeReviewDevices] (
        [Id] uniqueidentifier NOT NULL,
        [DeviceName] nvarchar(100) NOT NULL,
        [EmployeeId] uniqueidentifier NOT NULL,
        [TokenHash] varbinary(32) NULL,
        [IsActive] bit NOT NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NOT NULL,
        [LastSeenAtUtc] datetime2 NULL,
        CONSTRAINT [PK_EmployeeReviewDevices] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_EmployeeReviewDevices_Employees_EmployeeId] FOREIGN KEY ([EmployeeId]) REFERENCES [Employees] ([EmployeeId]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261124071552_AddEmployeeReviews'
)
BEGIN
    CREATE TABLE [EmployeeReviews] (
        [Id] uniqueidentifier NOT NULL,
        [DeviceId] uniqueidentifier NOT NULL,
        [EmployeeId] uniqueidentifier NOT NULL,
        [Rating] tinyint NOT NULL,
        [Comment] nvarchar(1000) NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [SubmissionId] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_EmployeeReviews] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_EmployeeReviews_Rating] CHECK ([Rating] BETWEEN 1 AND 5),
        CONSTRAINT [FK_EmployeeReviews_EmployeeReviewDevices_DeviceId] FOREIGN KEY ([DeviceId]) REFERENCES [EmployeeReviewDevices] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_EmployeeReviews_Employees_EmployeeId] FOREIGN KEY ([EmployeeId]) REFERENCES [Employees] ([EmployeeId]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261124071552_AddEmployeeReviews'
)
BEGIN
    CREATE INDEX [IX_EmployeeReviewDevices_EmployeeId] ON [EmployeeReviewDevices] ([EmployeeId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261124071552_AddEmployeeReviews'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [UX_EmployeeReviewDevices_TokenHash] ON [EmployeeReviewDevices] ([TokenHash]) WHERE [TokenHash] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261124071552_AddEmployeeReviews'
)
BEGIN
    CREATE INDEX [IX_EmployeeReviews_CreatedAtUtc] ON [EmployeeReviews] ([CreatedAtUtc]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261124071552_AddEmployeeReviews'
)
BEGIN
    CREATE INDEX [IX_EmployeeReviews_DeviceId] ON [EmployeeReviews] ([DeviceId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261124071552_AddEmployeeReviews'
)
BEGIN
    CREATE INDEX [IX_EmployeeReviews_EmployeeId_CreatedAtUtc] ON [EmployeeReviews] ([EmployeeId], [CreatedAtUtc]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261124071552_AddEmployeeReviews'
)
BEGIN
    CREATE UNIQUE INDEX [UX_EmployeeReviews_SubmissionId] ON [EmployeeReviews] ([SubmissionId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261124071552_AddEmployeeReviews'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261124071552_AddEmployeeReviews', N'10.0.11');
END;

COMMIT;
GO

