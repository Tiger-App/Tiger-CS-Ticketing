BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261102065459_AddUserPermissionOverrides'
)
BEGIN
    CREATE TABLE [UserPermissionOverrides] (
        [UserPermissionOverrideId] bigint NOT NULL IDENTITY,
        [EmployeeId] uniqueidentifier NOT NULL,
        [PermissionKey] nvarchar(100) NOT NULL,
        [Effect] tinyint NOT NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [CreatedByEmployeeId] uniqueidentifier NULL,
        [UpdatedAtUtc] datetime2 NOT NULL,
        [UpdatedByEmployeeId] uniqueidentifier NULL,
        CONSTRAINT [PK_UserPermissionOverrides] PRIMARY KEY ([UserPermissionOverrideId]),
        CONSTRAINT [FK_UserPermissionOverrides_Employees_EmployeeId] FOREIGN KEY ([EmployeeId]) REFERENCES [Employees] ([EmployeeId]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261102065459_AddUserPermissionOverrides'
)
BEGIN
    CREATE UNIQUE INDEX [IX_UserPermissionOverrides_EmployeeId_PermissionKey] ON [UserPermissionOverrides] ([EmployeeId], [PermissionKey]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261102065459_AddUserPermissionOverrides'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261102065459_AddUserPermissionOverrides', N'10.0.11');
END;

COMMIT;
GO

