/* =============================================================================
   TigerCS PRODUCTION - AddDepartmentAssignmentRotation (EF 20261031094748)
   Target : server 10.10.10.124 / database TigerCS_Ticketing_Prod
   Effect : creates ONE new table dbo.DepartmentAssignmentRotations and records
            the migration in __EFMigrationsHistory. No existing table, column,
            index or row is modified or deleted.
   Safe   : idempotent (re-run = no-op), single transaction, XACT_ABORT ON,
            aborts with a clear error if prerequisites are not met.
   NOT EXECUTED by Claude. Review, then run once, in SSMS, with SQLCMD off.
   ============================================================================= */
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF DB_NAME() <> N'TigerCS_Ticketing_Prod'
    THROW 50000, N'Wrong database. Expected TigerCS_Ticketing_Prod.', 1;

DECLARE @MigrationId nvarchar(150) = N'20261031094748_AddDepartmentAssignmentRotation';
DECLARE @Previous    nvarchar(150) = N'20261030073857_AddTicketCreatedBy';

/* ---- Pre-flight: history table, previous migration, dependency table ---- */
IF OBJECT_ID(N'dbo.__EFMigrationsHistory', N'U') IS NULL
    THROW 50001, N'__EFMigrationsHistory missing. Aborting.', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId = @Previous)
    THROW 50002, N'Previous migration 20261030073857_AddTicketCreatedBy is NOT applied. Apply earlier migrations first.', 1;

IF OBJECT_ID(N'dbo.Departments', N'U') IS NULL
    THROW 50003, N'dbo.Departments missing. Aborting.', 1;

IF NOT EXISTS (SELECT 1 FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
               WHERE c.object_id = OBJECT_ID(N'dbo.Departments') AND c.name = N'DepartmentId' AND t.name = N'int')
    THROW 50004, N'dbo.Departments.DepartmentId (int) not found. Aborting.', 1;

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE object_id = OBJECT_ID(N'dbo.Departments') AND is_primary_key = 1)
    THROW 50005, N'dbo.Departments has no primary key (required for the FK). Aborting.', 1;

/* ---- Already applied? ---- */
IF EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId = @MigrationId)
BEGIN
    PRINT N'Migration already recorded - nothing to do.';
    IF OBJECT_ID(N'dbo.DepartmentAssignmentRotations', N'U') IS NULL
        THROW 50006, N'History says applied but table is missing. Investigate manually.', 1;
    RETURN;
END

/* ---- Table exists but migration not recorded: do not guess ---- */
IF OBJECT_ID(N'dbo.DepartmentAssignmentRotations', N'U') IS NOT NULL
    THROW 50007, N'dbo.DepartmentAssignmentRotations already exists without a history row. Compare its definition manually; script will not touch it.', 1;

/* ---- Apply ---- */
BEGIN TRANSACTION;

CREATE TABLE dbo.DepartmentAssignmentRotations (
    DepartmentId           int              NOT NULL,
    LastAssignedEmployeeId uniqueidentifier NULL,
    UpdatedAtUtc           datetime2        NOT NULL,
    RowVersion             rowversion       NOT NULL,
    CONSTRAINT PK_DepartmentAssignmentRotations PRIMARY KEY (DepartmentId),
    CONSTRAINT FK_DepartmentAssignmentRotations_Departments_DepartmentId
        FOREIGN KEY (DepartmentId) REFERENCES dbo.Departments (DepartmentId) ON DELETE CASCADE
);

INSERT INTO dbo.__EFMigrationsHistory (MigrationId, ProductVersion)
VALUES (@MigrationId, N'10.0.11');

COMMIT TRANSACTION;
PRINT N'Applied 20261031094748_AddDepartmentAssignmentRotation.';
GO

/* =============================================================================
   POST-DEPLOYMENT VALIDATION (SELECT only)
   ============================================================================= */
-- 1. History row present (expect 1 row, ProductVersion 10.0.11)
SELECT MigrationId, ProductVersion FROM dbo.__EFMigrationsHistory
 WHERE MigrationId = N'20261031094748_AddDepartmentAssignmentRotation';

-- 2. Columns (expect 4: DepartmentId int NOT NULL, LastAssignedEmployeeId uniqueidentifier NULL,
--    UpdatedAtUtc datetime2 NOT NULL, RowVersion timestamp NOT NULL)
SELECT c.name, t.name AS type_name, c.is_nullable
  FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
 WHERE c.object_id = OBJECT_ID(N'dbo.DepartmentAssignmentRotations') ORDER BY c.column_id;

-- 3. PK + FK (expect PK_DepartmentAssignmentRotations and the FK, delete_referential_action_desc = CASCADE)
SELECT name, type_desc FROM sys.key_constraints WHERE parent_object_id = OBJECT_ID(N'dbo.DepartmentAssignmentRotations');
SELECT name, delete_referential_action_desc, is_disabled, is_not_trusted
  FROM sys.foreign_keys WHERE parent_object_id = OBJECT_ID(N'dbo.DepartmentAssignmentRotations');

-- 4. Table starts empty (expect 0; rows are created lazily by Round Robin)
SELECT COUNT(*) AS RotationRows FROM dbo.DepartmentAssignmentRotations;

-- 5. Existing data untouched: current-assignment integrity (expect 0 rows)
SELECT TicketId, COUNT(*) AS CurrentRows FROM dbo.TicketAssignments
 WHERE IsCurrent = 1 GROUP BY TicketId HAVING COUNT(*) > 1;
SELECT COUNT(*) AS TicketAssignmentsTotal, SUM(CAST(IsCurrent AS int)) AS CurrentAssignments FROM dbo.TicketAssignments;
-- Compare these two counts with the pre-deployment baseline taken below.

-- 6. Latest migrations (expect ...AddTicketCreatedBy then ...AddDepartmentAssignmentRotation at the top)
SELECT TOP 3 MigrationId FROM dbo.__EFMigrationsHistory ORDER BY MigrationId DESC;

/* PRE-DEPLOYMENT BASELINE (run BEFORE the script, record the numbers):
   SELECT COUNT(*) AS TicketAssignmentsTotal, SUM(CAST(IsCurrent AS int)) AS CurrentAssignments FROM dbo.TicketAssignments;
   SELECT COUNT(*) AS Tickets FROM dbo.Tickets;  SELECT COUNT(*) AS Departments FROM dbo.Departments; */

/* ROLLBACK (only if needed, and only while the table is still empty / unused):
   BEGIN TRAN;
     DROP TABLE dbo.DepartmentAssignmentRotations;
     DELETE FROM dbo.__EFMigrationsHistory WHERE MigrationId = N'20261031094748_AddDepartmentAssignmentRotation';
   COMMIT; */
