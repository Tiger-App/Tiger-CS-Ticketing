/* Idempotent script for EF migration 20261031094748_AddDepartmentAssignmentRotation (Round Robin cursor, one row per department).
   NOT executed by this change. Review, then run once per environment, in a window, after a backup.
   Rollback (manual): IF OBJECT_ID(N'dbo.DepartmentAssignmentRotations', N'U') IS NOT NULL DROP TABLE dbo.DepartmentAssignmentRotations;
                      DELETE FROM dbo.__EFMigrationsHistory WHERE MigrationId = N'20261031094748_AddDepartmentAssignmentRotation'; */
BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031094748_AddDepartmentAssignmentRotation'
)
BEGIN
    CREATE TABLE [DepartmentAssignmentRotations] (
        [DepartmentId] int NOT NULL,
        [LastAssignedEmployeeId] uniqueidentifier NULL,
        [UpdatedAtUtc] datetime2 NOT NULL,
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_DepartmentAssignmentRotations] PRIMARY KEY ([DepartmentId]),
        CONSTRAINT [FK_DepartmentAssignmentRotations_Departments_DepartmentId] FOREIGN KEY ([DepartmentId]) REFERENCES [Departments] ([DepartmentId]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031094748_AddDepartmentAssignmentRotation'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261031094748_AddDepartmentAssignmentRotation', N'10.0.11');
END;

COMMIT;
GO
