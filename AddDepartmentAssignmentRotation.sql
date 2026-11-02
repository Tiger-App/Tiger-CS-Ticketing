-- ############################################################################################
-- ## UAT ONLY. DO NOT RUN ON PRODUCTION, AND DO NOT DEPLOY ANY MIGRATION SCRIPT BY ITSELF.
-- ## Production gets Deploy_TigerCS_Prod_Migrations.sql (all pending migrations, in order, with
-- ## pre-checks, dry run and verification). This file is kept only as the UAT record.
-- ############################################################################################
-- Migration: 20261031094748_AddDepartmentAssignmentRotation (EF Core 10.0.11)
-- Target:    TigerCsTicketing UAT (10.10.10.117). Review before running; not executed by author.
-- Additive and idempotent: creates one new table only; no existing data is modified.
SET XACT_ABORT ON;
GO
BEGIN TRANSACTION;

-- Pre-checks
IF OBJECT_ID(N'[__EFMigrationsHistory]', N'U') IS NULL
    THROW 50001, 'Missing [__EFMigrationsHistory]; wrong database?', 1;
IF OBJECT_ID(N'[Departments]', N'U') IS NULL
    THROW 50002, 'Missing [Departments] table (FK target); wrong database?', 1;
IF NOT EXISTS (SELECT 1 FROM [__EFMigrationsHistory] WHERE [MigrationId] = N'20261030073857_AddTicketCreatedBy')
    THROW 50003, 'Previous migration 20261030073857_AddTicketCreatedBy not applied; apply earlier migrations first.', 1;

-- Create table (skipped if already applied)
IF NOT EXISTS (SELECT 1 FROM [__EFMigrationsHistory] WHERE [MigrationId] = N'20261031094748_AddDepartmentAssignmentRotation')
BEGIN
    IF OBJECT_ID(N'[DepartmentAssignmentRotations]', N'U') IS NULL
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

    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261031094748_AddDepartmentAssignmentRotation', N'10.0.11');
END;

COMMIT;
GO

-- Verification
SELECT [MigrationId] FROM [__EFMigrationsHistory] WHERE [MigrationId] = N'20261031094748_AddDepartmentAssignmentRotation';
SELECT OBJECT_ID(N'[DepartmentAssignmentRotations]', N'U') AS TableObjectId;
GO
