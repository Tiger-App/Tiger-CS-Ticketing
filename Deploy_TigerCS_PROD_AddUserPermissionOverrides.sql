-- ############################################################################################
-- ## TigerCS PRODUCTION ONLY.
-- ## Server   : 10.10.10.124
-- ## Database : TigerCS_Ticketing_Prod   (any other database name aborts before any change)
-- ## NOT EXECUTED by Claude. Review, then run yourself in SSMS (SQLCMD mode OFF).
-- ############################################################################################
-- Migration : 20261102065459_AddUserPermissionOverrides (EF Core 10.0.11)
--             Taken verbatim from src/TigerCS.Infrastructure/Persistence/Migrations/
--             20261102065459_AddUserPermissionOverrides.cs (CreateTable + CreateIndex).
-- Depends on: 20261031094748_AddDepartmentAssignmentRotation (must already be in __EFMigrationsHistory)
--             and dbo.Employees(EmployeeId uniqueidentifier, primary/unique key).
-- Effect    : creates ONE new table dbo.UserPermissionOverrides (PK, unique index, FK to Employees)
--             and records the migration in dbo.__EFMigrationsHistory.
--             No existing table, column, index, row, user, role or permission is modified or deleted.
--             An empty overrides table = every user keeps exactly their role-based permissions.
-- Idempotent: re-running after success changes nothing (history row + table are only verified).
--             A partially created table with the exact EF shape is completed, never recreated.
--             A pre-existing table with a different shape aborts without being touched.
--
-- HOW TO RUN (SSMS connected to 10.10.10.124, database TigerCS_Ticketing_Prod):
--   1. Leave @DryRun = 1 and run. Everything is applied inside a transaction, verified, then ROLLED BACK.
--   2. Read Messages + result sets: guard passed, all checks OK, "DRY RUN SUCCESS", row counts unchanged. A dry run runs no post-commit queries.
--   3. Set @DryRun = 0 and run again to COMMIT. The same checks run before the commit, then post-commit
--      verification result sets (section 6) show the committed state.
-- Run as ONE batch. There is intentionally no GO in this script.
--
-- ROLLBACK (only if ever needed, and only while dbo.UserPermissionOverrides is still empty/unused):
--   BEGIN TRAN;
--     DROP TABLE dbo.UserPermissionOverrides;
--     DELETE FROM dbo.__EFMigrationsHistory WHERE MigrationId = N'20261102065459_AddUserPermissionOverrides';
--   COMMIT;
-- ############################################################################################

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @DryRun bit = 1;   -- 1 = apply, verify, ROLL BACK (default).  0 = apply, verify, COMMIT.

DECLARE @Committed bit = 0;  -- set to 1 only after COMMIT TRANSACTION succeeds

DECLARE @MigrationId   nvarchar(150) = N'20261102065459_AddUserPermissionOverrides';
DECLARE @Previous      nvarchar(150) = N'20261031094748_AddDepartmentAssignmentRotation';
DECLARE @ProductVersion nvarchar(32) = N'10.0.11';
DECLARE @ExpectedDb    sysname       = N'TigerCS_Ticketing_Prod';
DECLARE @msg nvarchar(2000);

-- ---------------------------------------------------------------------------------------------
-- 1. Database-name protection (first statement that can fail; nothing has been touched yet)
-- ---------------------------------------------------------------------------------------------
IF DB_NAME() IS NULL OR DB_NAME() <> @ExpectedDb
BEGIN
    SET @msg = N'ABORTED: connected to database [' + ISNULL(DB_NAME(), N'?') + N'] on server [' + CAST(@@SERVERNAME AS nvarchar(128))
             + N']; this Production script only runs against [' + @ExpectedDb + N']. Nothing was changed.';
    THROW 50100, @msg, 1;
END;
SET @msg = N'Guard passed: server [' + CAST(@@SERVERNAME AS nvarchar(128)) + N'], database [' + DB_NAME()
         + N'], DryRun=' + CAST(@DryRun AS nvarchar(1)) + N'. (Expected host 10.10.10.124 - confirm in the SSMS connection bar.)';
PRINT @msg;

-- ---------------------------------------------------------------------------------------------
-- 2. Dependency / schema pre-checks (read-only)
-- ---------------------------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.__EFMigrationsHistory', N'U') IS NULL
    THROW 50101, 'ABORTED: dbo.__EFMigrationsHistory is missing - wrong database?', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId = @Previous)
    THROW 50102, 'ABORTED: 20261031094748_AddDepartmentAssignmentRotation is not recorded in __EFMigrationsHistory; apply it first (Deploy_TigerCS_PROD_AddDepartmentAssignmentRotation.sql).', 1;

-- Ordering: nothing newer than this migration may already be recorded (history must end at the previous one,
-- or already include this one).
IF EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId > @MigrationId)
    THROW 50107, 'ABORTED: __EFMigrationsHistory contains a migration newer than 20261102065459_AddUserPermissionOverrides. Investigate manually.', 1;

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

-- A table of this name that already exists must have exactly the EF shape; otherwise stop and let a human look.
IF OBJECT_ID(N'dbo.UserPermissionOverrides', N'U') IS NOT NULL
BEGIN
    DECLARE @bad nvarchar(max) = N'';
    SELECT @bad = @bad + e.Col + N' ' FROM (VALUES
        (N'UserPermissionOverrideId', N'bigint',           8,   0, 1),
        (N'EmployeeId',               N'uniqueidentifier', 16,  0, 0),
        (N'PermissionKey',            N'nvarchar',         200, 0, 0),   -- nvarchar(100) = 200 bytes
        (N'Effect',                   N'tinyint',          1,   0, 0),
        (N'CreatedAtUtc',             N'datetime2',        8,   0, 0),
        (N'CreatedByEmployeeId',      N'uniqueidentifier', 16,  1, 0),
        (N'UpdatedAtUtc',             N'datetime2',        8,   0, 0),
        (N'UpdatedByEmployeeId',      N'uniqueidentifier', 16,  1, 0)) AS e(Col, TypeName, MaxLen, Nullable, IsIdentity)
      WHERE NOT EXISTS (
        SELECT 1 FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
        WHERE c.object_id = OBJECT_ID(N'dbo.UserPermissionOverrides') AND c.name = e.Col
          AND t.name = e.TypeName AND c.max_length = e.MaxLen AND c.is_nullable = e.Nullable
          AND c.is_identity = e.IsIdentity);
    IF (SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.UserPermissionOverrides')) <> 8
        SET @bad = @bad + N'(column count) ';
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
        (N'AspNetUsers'), (N'AspNetRoles'), (N'AspNetUserRoles'), (N'__EFMigrationsHistory')) v(n)
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

DECLARE @HistoryBefore bigint = (SELECT COUNT_BIG(*) FROM dbo.__EFMigrationsHistory);
DECLARE @AlreadyRecorded bit = CASE WHEN EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId = @MigrationId) THEN 1 ELSE 0 END;
DECLARE @TableExisted bit = CASE WHEN OBJECT_ID(N'dbo.UserPermissionOverrides', N'U') IS NOT NULL THEN 1 ELSE 0 END;
SET @msg = N'Pre-state: table exists=' + CAST(@TableExisted AS nvarchar(1)) + N', migration recorded=' + CAST(@AlreadyRecorded AS nvarchar(1))
         + N', history rows=' + CAST(@HistoryBefore AS nvarchar(20)) + N'.';
PRINT @msg;

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

    -- Complete a partially applied state (table present but PK / FK / index missing). No-ops on a normal run.
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
        INSERT INTO dbo.__EFMigrationsHistory (MigrationId, ProductVersion) VALUES (@MigrationId, @ProductVersion);
        PRINT 'Recorded migration in __EFMigrationsHistory.';
    END
    ELSE PRINT 'Migration already recorded in __EFMigrationsHistory - not inserted again.';

    -- -----------------------------------------------------------------------------------------
    -- 5. Validation (inside the transaction, so any failure rolls everything back)
    -- -----------------------------------------------------------------------------------------
    -- 5a. Schema: table + all 8 columns with exact type / length / nullability / identity
    IF OBJECT_ID(N'dbo.UserPermissionOverrides', N'U') IS NULL
        THROW 50110, 'VALIDATION FAILED: table dbo.UserPermissionOverrides missing.', 1;

    DECLARE @badCols nvarchar(max) = N'';
    SELECT @badCols = @badCols + e.Col + N' ' FROM (VALUES
        (N'UserPermissionOverrideId', N'bigint',           8,   0, 1),
        (N'EmployeeId',               N'uniqueidentifier', 16,  0, 0),
        (N'PermissionKey',            N'nvarchar',         200, 0, 0),
        (N'Effect',                   N'tinyint',          1,   0, 0),
        (N'CreatedAtUtc',             N'datetime2',        8,   0, 0),
        (N'CreatedByEmployeeId',      N'uniqueidentifier', 16,  1, 0),
        (N'UpdatedAtUtc',             N'datetime2',        8,   0, 0),
        (N'UpdatedByEmployeeId',      N'uniqueidentifier', 16,  1, 0)) AS e(Col, TypeName, MaxLen, Nullable, IsIdentity)
      WHERE NOT EXISTS (
        SELECT 1 FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
        WHERE c.object_id = OBJECT_ID(N'dbo.UserPermissionOverrides') AND c.name = e.Col
          AND t.name = e.TypeName AND c.max_length = e.MaxLen AND c.is_nullable = e.Nullable AND c.is_identity = e.IsIdentity);
    IF LEN(@badCols) > 0 OR (SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.UserPermissionOverrides')) <> 8
    BEGIN
        SET @msg = N'VALIDATION FAILED: column shape mismatch (' + @badCols + N').';
        THROW 50115, @msg, 1;
    END;

    -- 5b. Primary key on UserPermissionOverrideId only
    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes i
        JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
        JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
        WHERE i.object_id = OBJECT_ID(N'dbo.UserPermissionOverrides') AND i.is_primary_key = 1
          AND i.name = N'PK_UserPermissionOverrides' AND c.name = N'UserPermissionOverrideId'
          AND (SELECT COUNT(*) FROM sys.index_columns x WHERE x.object_id = i.object_id AND x.index_id = i.index_id) = 1)
        THROW 50116, 'VALIDATION FAILED: PK_UserPermissionOverrides missing or on wrong column.', 1;

    -- 5c. Unique index on (EmployeeId, PermissionKey), in that order, no included columns
    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes i
        WHERE i.object_id = OBJECT_ID(N'dbo.UserPermissionOverrides')
          AND i.name = N'IX_UserPermissionOverrides_EmployeeId_PermissionKey' AND i.is_unique = 1 AND i.is_disabled = 0
          AND (SELECT COUNT(*) FROM sys.index_columns x WHERE x.object_id = i.object_id AND x.index_id = i.index_id) = 2
          AND EXISTS (SELECT 1 FROM sys.index_columns x JOIN sys.columns c ON c.object_id = x.object_id AND c.column_id = x.column_id
                      WHERE x.object_id = i.object_id AND x.index_id = i.index_id AND x.key_ordinal = 1 AND x.is_included_column = 0 AND c.name = N'EmployeeId')
          AND EXISTS (SELECT 1 FROM sys.index_columns x JOIN sys.columns c ON c.object_id = x.object_id AND c.column_id = x.column_id
                      WHERE x.object_id = i.object_id AND x.index_id = i.index_id AND x.key_ordinal = 2 AND x.is_included_column = 0 AND c.name = N'PermissionKey'))
        THROW 50111, 'VALIDATION FAILED: unique index IX_UserPermissionOverrides_EmployeeId_PermissionKey (EmployeeId, PermissionKey) missing or wrong.', 1;

    -- 5d. Foreign key UserPermissionOverrides.EmployeeId -> Employees.EmployeeId, ON DELETE CASCADE, enabled and trusted
    IF NOT EXISTS (
        SELECT 1 FROM sys.foreign_keys fk
        JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
        JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
        JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
        WHERE fk.parent_object_id = OBJECT_ID(N'dbo.UserPermissionOverrides')
          AND fk.referenced_object_id = OBJECT_ID(N'dbo.Employees')
          AND fk.name = N'FK_UserPermissionOverrides_Employees_EmployeeId'
          AND pc.name = N'EmployeeId' AND rc.name = N'EmployeeId'
          AND fk.delete_referential_action = 1 AND fk.is_disabled = 0 AND fk.is_not_trusted = 0)
        THROW 50112, 'VALIDATION FAILED: FK_UserPermissionOverrides_Employees_EmployeeId (ON DELETE CASCADE, enabled, trusted) missing or wrong.', 1;

    -- 5e. Migration history: previous still there, new one recorded exactly once with the right version
    IF NOT EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId = @Previous)
        THROW 50117, 'VALIDATION FAILED: previous migration row disappeared.', 1;
    IF (SELECT COUNT(*) FROM dbo.__EFMigrationsHistory WHERE MigrationId = @MigrationId) <> 1
        THROW 50113, 'VALIDATION FAILED: migration not recorded exactly once.', 1;
    IF NOT EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId = @MigrationId AND ProductVersion = @ProductVersion)
        THROW 50118, 'VALIDATION FAILED: migration recorded with an unexpected ProductVersion.', 1;
    IF (SELECT COUNT_BIG(*) FROM dbo.__EFMigrationsHistory) <> @HistoryBefore + CASE WHEN @AlreadyRecorded = 1 THEN 0 ELSE 1 END
        THROW 50119, 'VALIDATION FAILED: unexpected change in __EFMigrationsHistory row count.', 1;

    -- 5f. Existing data untouched (the history table is expected to grow by one row on a first run only)
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

    IF EXISTS (SELECT 1 FROM @Snapshot WHERE TableName <> N'__EFMigrationsHistory' AND RowsBefore <> RowsAfter)
        THROW 50114, 'VALIDATION FAILED: an existing table changed its row count.', 1;

    SELECT TableName, RowsBefore, RowsAfter,
           CASE WHEN RowsBefore = RowsAfter THEN 'unchanged'
                WHEN TableName = N'__EFMigrationsHistory' AND RowsAfter = RowsBefore + 1 THEN '+1 (this migration)'
                ELSE 'CHANGED' END AS Result
    FROM @Snapshot ORDER BY TableName;

    -- On a first run the new table must be empty (no overrides = role-based permissions only).
    IF @TableExisted = 0 AND EXISTS (SELECT 1 FROM dbo.UserPermissionOverrides)
        THROW 50120, 'VALIDATION FAILED: newly created dbo.UserPermissionOverrides is not empty.', 1;

    IF @DryRun = 1
    BEGIN
        ROLLBACK TRANSACTION;
        PRINT 'DRY RUN SUCCESS - schema, index, foreign key, migration-history and existing-data checks all passed inside the transaction, which was ROLLED BACK. Nothing was changed. Set @DryRun = 0 to commit.';
    END
    ELSE
    BEGIN
        COMMIT TRANSACTION;
        SET @Committed = 1;
        PRINT 'COMMITTED. Migration 20261102065459_AddUserPermissionOverrides applied to TigerCS_Ticketing_Prod.';
    END;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    PRINT 'FAILED - transaction rolled back, nothing was changed.';
    THROW;
END CATCH;

-- ---------------------------------------------------------------------------------------------
-- 6. Post-commit verification (read-only). Runs ONLY when @DryRun = 0 AND the commit succeeded.
--    On a dry run nothing is queried here (the table was rolled back and does not exist), so there is no Msg 208.
--    Any failure above re-throws from CATCH and ends the batch, so this section is never reached after an error.
--    The same queries can be re-run manually in a new window at any time after a real deployment.
-- ---------------------------------------------------------------------------------------------
IF @DryRun = 0 AND @Committed = 1
BEGIN
PRINT 'Post-commit verification results follow (expect values noted in comments).';

-- 6.1 Migration history (expect 2 rows after commit; ProductVersion 10.0.11 on the new one)
SELECT MigrationId, ProductVersion FROM dbo.__EFMigrationsHistory
WHERE MigrationId IN (N'20261031094748_AddDepartmentAssignmentRotation', N'20261102065459_AddUserPermissionOverrides')
ORDER BY MigrationId;

-- 6.2 Table exists (expect an object id, not NULL)
SELECT OBJECT_ID(N'dbo.UserPermissionOverrides', N'U') AS TableObjectId;

-- 6.3 Columns (expect 8: bigint identity NOT NULL, uniqueidentifier NOT NULL, nvarchar(100) NOT NULL [max_length 200],
--     tinyint NOT NULL, datetime2 NOT NULL, uniqueidentifier NULL, datetime2 NOT NULL, uniqueidentifier NULL)
SELECT c.column_id, c.name AS ColumnName, t.name AS TypeName, c.max_length, c.is_nullable, c.is_identity
FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
WHERE c.object_id = OBJECT_ID(N'dbo.UserPermissionOverrides') ORDER BY c.column_id;

-- 6.4 Primary key + indexes (expect PK_UserPermissionOverrides on UserPermissionOverrideId, and the unique IX on EmployeeId, PermissionKey)
SELECT i.name AS IndexName, i.type_desc, i.is_primary_key, i.is_unique, i.is_disabled,
       STUFF((SELECT N', ' + c.name FROM sys.index_columns ic JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
              WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0 ORDER BY ic.key_ordinal
              FOR XML PATH(N'')), 1, 2, N'') AS KeyColumns
FROM sys.indexes i WHERE i.object_id = OBJECT_ID(N'dbo.UserPermissionOverrides') ORDER BY i.index_id;

-- 6.5 Foreign key (expect FK_UserPermissionOverrides_Employees_EmployeeId -> Employees, CASCADE, is_disabled 0, is_not_trusted 0)
SELECT fk.name, OBJECT_NAME(fk.referenced_object_id) AS ReferencedTable, fk.delete_referential_action_desc,
       fk.is_disabled, fk.is_not_trusted
FROM sys.foreign_keys fk WHERE fk.parent_object_id = OBJECT_ID(N'dbo.UserPermissionOverrides');

-- 6.6 Table starts empty (expect 0 on the first deployment: no overrides, every user keeps role-based permissions)
SELECT COUNT_BIG(*) AS OverrideRows FROM dbo.UserPermissionOverrides;

-- 6.7 Existing data sanity (compare with the before-snapshot printed above; none of these tables are touched)
SELECT (SELECT COUNT_BIG(*) FROM dbo.Employees) AS Employees,
       (SELECT COUNT_BIG(*) FROM dbo.Tickets)   AS Tickets,
       (SELECT COUNT_BIG(*) FROM dbo.Departments) AS Departments,
       (SELECT COUNT_BIG(*) FROM dbo.__EFMigrationsHistory) AS MigrationHistoryRows;

-- 6.8 Latest migrations (expect ...AddDepartmentAssignmentRotation, then ...AddUserPermissionOverrides at the top)
SELECT TOP 3 MigrationId FROM dbo.__EFMigrationsHistory ORDER BY MigrationId DESC;
END;
