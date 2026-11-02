-- ############################################################################################
-- ## UAT ONLY. Database must be named TigerCsTicketing; any other name aborts before any change.
-- ## DO NOT RUN ON PRODUCTION from this file (Production gets its own reviewed script).
-- ############################################################################################
-- Migration : 20261102065459_AddUserPermissionOverrides (EF Core 10.0.11)
-- Depends on: 20261031094748_AddDepartmentAssignmentRotation (already committed on UAT) and dbo.Employees.
-- Effect    : creates ONE new table dbo.UserPermissionOverrides (+ unique index, FK to Employees)
--             and records the migration in dbo.__EFMigrationsHistory.
--             No existing table, row, user, role or permission is read-modified, altered or deleted.
--             Fresh table = no overrides = every user keeps exactly their role-based permissions.
-- Idempotent: safe to run repeatedly. Re-running after success changes nothing.
--             A partially created table (e.g. table but no index) is completed, never recreated.
--             A pre-existing table with a different shape aborts instead of being touched.
--
-- HOW TO RUN (SSMS, connected to the UAT server, database TigerCsTicketing - or sqlcmd -d TigerCsTicketing):
--   1. Leave @DryRun = 1 below and run. It applies everything inside a transaction, verifies, then ROLLS BACK.
--   2. Read the messages/result sets: guard passed, "DRY RUN - rolled back", row counts unchanged.
--   3. Set @DryRun = 0 and run again to commit. The same checks run and the final result sets show the committed state.
-- Run it as ONE batch (there is intentionally no GO inside the script body).
-- ############################################################################################

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @DryRun bit = 1;   -- 1 = apply, verify, ROLL BACK (default).  0 = apply and COMMIT.

DECLARE @MigrationId nvarchar(150) = N'20261102065459_AddUserPermissionOverrides';
DECLARE @Previous    nvarchar(150) = N'20261031094748_AddDepartmentAssignmentRotation';
DECLARE @ExpectedDb  sysname       = N'TigerCsTicketing';
DECLARE @msg nvarchar(2000);

-- ---------------------------------------------------------------------------------------------
-- 1. Database-name protection (before anything else)
-- ---------------------------------------------------------------------------------------------
IF DB_NAME() <> @ExpectedDb
BEGIN
    SET @msg = N'ABORTED: connected to database [' + ISNULL(DB_NAME(), N'?') + N'] on server [' + CAST(@@SERVERNAME AS nvarchar(128))
             + N']; this UAT script only runs against [' + @ExpectedDb + N']. Nothing was changed.';
    THROW 50100, @msg, 1;
END;
SET @msg = N'Guard passed: server [' + CAST(@@SERVERNAME AS nvarchar(128)) + N'], database [' + DB_NAME() + N'], DryRun=' + CAST(@DryRun AS nvarchar(1));
PRINT @msg;

-- ---------------------------------------------------------------------------------------------
-- 2. Dependency / schema pre-checks (read-only)
-- ---------------------------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.__EFMigrationsHistory', N'U') IS NULL
    THROW 50101, 'ABORTED: dbo.__EFMigrationsHistory is missing - wrong database?', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId = @Previous)
    THROW 50102, 'ABORTED: 20261031094748_AddDepartmentAssignmentRotation is not recorded; apply earlier migrations first.', 1;

IF OBJECT_ID(N'dbo.Employees', N'U') IS NULL
    THROW 50103, 'ABORTED: dbo.Employees (FK target) is missing - wrong database?', 1;

IF NOT EXISTS (
    SELECT 1 FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
    WHERE c.object_id = OBJECT_ID(N'dbo.Employees') AND c.name = N'EmployeeId' AND t.name = N'uniqueidentifier')
    THROW 50104, 'ABORTED: dbo.Employees.EmployeeId is not a uniqueidentifier.', 1;

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes i
    JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
    JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
    WHERE i.object_id = OBJECT_ID(N'dbo.Employees') AND (i.is_primary_key = 1 OR i.is_unique_constraint = 1)
      AND c.name = N'EmployeeId' AND ic.key_ordinal = 1
      AND (SELECT COUNT(*) FROM sys.index_columns x WHERE x.object_id = i.object_id AND x.index_id = i.index_id) = 1)
    THROW 50105, 'ABORTED: dbo.Employees.EmployeeId is not a primary/unique key, so the foreign key cannot be created.', 1;

-- A table of this name that was created by hand must have exactly the EF shape, otherwise stop and let a human look.
IF OBJECT_ID(N'dbo.UserPermissionOverrides', N'U') IS NOT NULL
BEGIN
    DECLARE @bad nvarchar(max) = N'';
    SELECT @bad = @bad + e.Col + N' ' FROM (VALUES
        (N'UserPermissionOverrideId', N'bigint',           8,   0),
        (N'EmployeeId',               N'uniqueidentifier', 16,  0),
        (N'PermissionKey',            N'nvarchar',         200, 0),   -- nvarchar(100) = 200 bytes
        (N'Effect',                   N'tinyint',          1,   0),
        (N'CreatedAtUtc',             N'datetime2',        8,   0),
        (N'CreatedByEmployeeId',      N'uniqueidentifier', 16,  1),
        (N'UpdatedAtUtc',             N'datetime2',        8,   0),
        (N'UpdatedByEmployeeId',      N'uniqueidentifier', 16,  1)) AS e(Col, TypeName, MaxLen, Nullable)
      WHERE NOT EXISTS (
        SELECT 1 FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
        WHERE c.object_id = OBJECT_ID(N'dbo.UserPermissionOverrides') AND c.name = e.Col
          AND t.name = e.TypeName AND c.max_length = e.MaxLen AND c.is_nullable = e.Nullable);
    IF LEN(@bad) > 0
    BEGIN
        SET @msg = N'ABORTED: dbo.UserPermissionOverrides already exists with a different shape (check: ' + @bad + N'). Not touched.';
        THROW 50106, @msg, 1;
    END;
END;

-- ---------------------------------------------------------------------------------------------
-- 3. Before-snapshot of existing data (row counts of tables that must stay untouched)
-- ---------------------------------------------------------------------------------------------
DECLARE @Snapshot TABLE (TableName sysname PRIMARY KEY, RowsBefore bigint NOT NULL, RowsAfter bigint NULL);
DECLARE @t sysname, @sql nvarchar(max), @n bigint;
DECLARE watched CURSOR LOCAL FAST_FORWARD FOR
    SELECT v.n FROM (VALUES (N'Employees'), (N'Tickets'), (N'TicketAssignments'), (N'TicketInteractions'), (N'TicketStatusHistory'),
        (N'Departments'), (N'UserDepartmentAssignments'), (N'DepartmentAssignmentRotations'),
        (N'AspNetUsers'), (N'AspNetRoles'), (N'AspNetUserRoles')) v(n)
    WHERE OBJECT_ID(N'dbo.' + v.n, N'U') IS NOT NULL;
OPEN watched; FETCH NEXT FROM watched INTO @t;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @sql = N'SELECT @n = COUNT_BIG(*) FROM dbo.' + QUOTENAME(@t) + N';';
    EXEC sp_executesql @sql, N'@n bigint OUTPUT', @n = @n OUTPUT;
    INSERT @Snapshot (TableName, RowsBefore) VALUES (@t, @n);
    FETCH NEXT FROM watched INTO @t;
END;
CLOSE watched; DEALLOCATE watched;

-- ---------------------------------------------------------------------------------------------
-- 4. Apply (single transaction, additive only)
-- ---------------------------------------------------------------------------------------------
BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.UserPermissionOverrides', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.UserPermissionOverrides (
            UserPermissionOverrideId bigint NOT NULL IDENTITY,
            EmployeeId uniqueidentifier NOT NULL,
            PermissionKey nvarchar(100) NOT NULL,
            Effect tinyint NOT NULL,
            CreatedAtUtc datetime2 NOT NULL,
            CreatedByEmployeeId uniqueidentifier NULL,
            UpdatedAtUtc datetime2 NOT NULL,
            UpdatedByEmployeeId uniqueidentifier NULL,
            CONSTRAINT PK_UserPermissionOverrides PRIMARY KEY (UserPermissionOverrideId),
            CONSTRAINT FK_UserPermissionOverrides_Employees_EmployeeId FOREIGN KEY (EmployeeId)
                REFERENCES dbo.Employees (EmployeeId) ON DELETE CASCADE
        );
        PRINT 'Created table dbo.UserPermissionOverrides.';
    END
    ELSE PRINT 'Table dbo.UserPermissionOverrides already exists with the expected columns - not recreated.';

    -- Complete a partially applied state (hand-created table missing PK / FK / index).
    IF NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE parent_object_id = OBJECT_ID(N'dbo.UserPermissionOverrides') AND type = 'PK')
        ALTER TABLE dbo.UserPermissionOverrides ADD CONSTRAINT PK_UserPermissionOverrides PRIMARY KEY (UserPermissionOverrideId);

    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE parent_object_id = OBJECT_ID(N'dbo.UserPermissionOverrides')
                   AND referenced_object_id = OBJECT_ID(N'dbo.Employees'))
        ALTER TABLE dbo.UserPermissionOverrides ADD CONSTRAINT FK_UserPermissionOverrides_Employees_EmployeeId
            FOREIGN KEY (EmployeeId) REFERENCES dbo.Employees (EmployeeId) ON DELETE CASCADE;

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.UserPermissionOverrides')
                   AND name = N'IX_UserPermissionOverrides_EmployeeId_PermissionKey')
        CREATE UNIQUE INDEX IX_UserPermissionOverrides_EmployeeId_PermissionKey
            ON dbo.UserPermissionOverrides (EmployeeId, PermissionKey);

    IF NOT EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId = @MigrationId)
    BEGIN
        INSERT INTO dbo.__EFMigrationsHistory (MigrationId, ProductVersion) VALUES (@MigrationId, N'10.0.11');
        PRINT 'Recorded migration in __EFMigrationsHistory.';
    END
    ELSE PRINT 'Migration already recorded in __EFMigrationsHistory.';

    -- -----------------------------------------------------------------------------------------
    -- 5. Validation (inside the transaction, so a failure rolls everything back)
    -- -----------------------------------------------------------------------------------------
    IF OBJECT_ID(N'dbo.UserPermissionOverrides', N'U') IS NULL
        THROW 50110, 'VALIDATION FAILED: table dbo.UserPermissionOverrides missing.', 1;
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.UserPermissionOverrides')
                   AND name = N'IX_UserPermissionOverrides_EmployeeId_PermissionKey' AND is_unique = 1)
        THROW 50111, 'VALIDATION FAILED: unique index missing.', 1;
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE parent_object_id = OBJECT_ID(N'dbo.UserPermissionOverrides')
                   AND referenced_object_id = OBJECT_ID(N'dbo.Employees') AND delete_referential_action = 1)
        THROW 50112, 'VALIDATION FAILED: FK to Employees (ON DELETE CASCADE) missing.', 1;
    IF NOT EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId = @MigrationId)
        THROW 50113, 'VALIDATION FAILED: migration not recorded.', 1;

    DECLARE watched2 CURSOR LOCAL FAST_FORWARD FOR SELECT TableName FROM @Snapshot;
    OPEN watched2; FETCH NEXT FROM watched2 INTO @t;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        SET @sql = N'SELECT @n = COUNT_BIG(*) FROM dbo.' + QUOTENAME(@t) + N';';
        EXEC sp_executesql @sql, N'@n bigint OUTPUT', @n = @n OUTPUT;
        UPDATE @Snapshot SET RowsAfter = @n WHERE TableName = @t;
        FETCH NEXT FROM watched2 INTO @t;
    END;
    CLOSE watched2; DEALLOCATE watched2;

    IF EXISTS (SELECT 1 FROM @Snapshot WHERE RowsBefore <> RowsAfter)
        THROW 50114, 'VALIDATION FAILED: an existing table changed its row count.', 1;

    SELECT TableName, RowsBefore, RowsAfter, CASE WHEN RowsBefore = RowsAfter THEN 'unchanged' ELSE 'CHANGED' END AS Result
    FROM @Snapshot ORDER BY TableName;

    SELECT c.name AS ColumnName, t.name AS TypeName, c.max_length, c.is_nullable, c.is_identity
    FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
    WHERE c.object_id = OBJECT_ID(N'dbo.UserPermissionOverrides') ORDER BY c.column_id;

    SELECT COUNT_BIG(*) AS OverrideRows_ExpectedZeroOnFirstRun FROM dbo.UserPermissionOverrides;

    IF @DryRun = 1
    BEGIN
        ROLLBACK TRANSACTION;
        PRINT 'DRY RUN - all checks passed and the transaction was ROLLED BACK. Nothing was changed. Set @DryRun = 0 to commit.';
    END
    ELSE
    BEGIN
        COMMIT TRANSACTION;
        PRINT 'COMMITTED. Migration 20261102065459_AddUserPermissionOverrides applied.';
    END;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    PRINT 'FAILED - transaction rolled back, nothing was changed.';
    THROW;
END CATCH;

-- ---------------------------------------------------------------------------------------------
-- 6. Post-run validation queries (read-only; run any time)
-- ---------------------------------------------------------------------------------------------
SELECT MigrationId, ProductVersion FROM dbo.__EFMigrationsHistory
WHERE MigrationId IN (N'20261031094748_AddDepartmentAssignmentRotation', N'20261102065459_AddUserPermissionOverrides')
ORDER BY MigrationId;                                                  -- DryRun: only the first row. After commit: both rows.
SELECT OBJECT_ID(N'dbo.UserPermissionOverrides', N'U') AS TableObjectId;   -- NULL after a dry run; a number after commit.
