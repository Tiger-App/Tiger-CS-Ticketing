-- ############################################################################################
-- ## TigerCS PRODUCTION ONLY.
-- ## Server   : TGAESHJDBP02
-- ## Database : TigerCS_Ticketing_Prod   (any other server or database aborts before any change)
-- ## NOT EXECUTED by Claude. Review, then run yourself in SSMS (SQLCMD mode OFF).
-- ############################################################################################
-- Migration : 20261119120249_AddAssignmentHistoryPreviousDepartment (EF Core 10.0.11)
--             Same single ALTER TABLE ... ADD statement as the EF-generated script AddAssignmentHistoryPreviousDepartment.sql,
--             wrapped in guards, one transaction and verification. Part of hotfix PR #292 (ticket 1878).
-- Depends on: 20261119081103_AddCustomerProfileAuditAndProperties (must already be in __EFMigrationsHistory)
--             and dbo.TicketAssignmentHistory with its Genesys columns (20261112065853_AddGenesysAssignmentHistorySource).
-- Effect    : adds ONE NULLABLE column to dbo.TicketAssignmentHistory: PreviousDepartmentId int NULL (the department a ticket was in before a
--             department-changing assignment-history entry) and records the migration in dbo.__EFMigrationsHistory.
--             Metadata-only change. NO existing row, ticket, assignment (dbo.TicketAssignments) or audit record is touched. NO backfill.
-- Idempotent: re-running after success changes nothing. A column that already exists with the exact shape is kept; one with a different shape aborts.
-- Application: deploy the new TigerCS.Api build AFTER this script commits (the new code writes the column). The previous build ignores it.
--
-- HOW TO RUN (SSMS, connected to TGAESHJDBP02, database TigerCS_Ticketing_Prod):
--   1. Take / confirm a fresh full backup first.
--   2. Leave @DryRun = 1 and run. Everything is applied in a transaction, verified, then ROLLED BACK.
--   3. Read Messages + result sets: guard passed, all checks OK, "DRY RUN SUCCESS", row counts unchanged.
--   4. Set @DryRun = 0 and run again to COMMIT. The same checks run before the commit, then post-commit
--      verification result sets (section 6) show the committed state.
-- Run as ONE batch. There is intentionally no GO in this script.
--
-- ROLLBACK (only if ever needed; run ONLY after the new application build is stopped/rolled back, because the
-- column then holds previous-department data that the DROP would delete):
--   BEGIN TRAN;
--     ALTER TABLE dbo.TicketAssignmentHistory DROP COLUMN PreviousDepartmentId;
--     DELETE FROM dbo.__EFMigrationsHistory WHERE MigrationId = N'20261119120249_AddAssignmentHistoryPreviousDepartment';
--   COMMIT;
-- ############################################################################################

SET NOCOUNT ON;
SET XACT_ABORT ON;
SET LOCK_TIMEOUT 15000;   -- fail fast instead of blocking the application if the table is busy

DECLARE @DryRun bit = 1;   -- 1 = apply, verify, ROLL BACK (default).  0 = apply, verify, COMMIT.

DECLARE @Committed bit = 0;  -- set to 1 only after COMMIT TRANSACTION succeeds

DECLARE @MigrationId    nvarchar(150) = N'20261119120249_AddAssignmentHistoryPreviousDepartment';
DECLARE @Previous       nvarchar(150) = N'20261119081103_AddCustomerProfileAuditAndProperties';
DECLARE @ProductVersion nvarchar(32)  = N'10.0.11';
DECLARE @ExpectedServer sysname       = N'TGAESHJDBP02';
DECLARE @ExpectedDb     sysname       = N'TigerCS_Ticketing_Prod';
DECLARE @msg nvarchar(2000);

-- ---------------------------------------------------------------------------------------------
-- 1. Server + database protection (nothing has been touched yet)
-- ---------------------------------------------------------------------------------------------
DECLARE @ServerName sysname = CAST(@@SERVERNAME AS sysname);
IF DB_NAME() IS NULL OR DB_NAME() <> @ExpectedDb
   OR @ServerName IS NULL OR (UPPER(@ServerName) <> UPPER(@ExpectedServer) AND UPPER(@ServerName) NOT LIKE UPPER(@ExpectedServer) + N'\%')
BEGIN
    SET @msg = N'ABORTED: connected to database [' + ISNULL(DB_NAME(), N'?') + N'] on server [' + ISNULL(@ServerName, N'?')
             + N']; this Production script only runs against [' + @ExpectedDb + N'] on [' + @ExpectedServer + N']. Nothing was changed.';
    THROW 50200, @msg, 1;
END;
SET @msg = N'Guard passed: server [' + @ServerName + N'], database [' + DB_NAME() + N'], DryRun=' + CAST(@DryRun AS nvarchar(1)) + N'.';
PRINT @msg;

-- ---------------------------------------------------------------------------------------------
-- 2. Dependency / schema pre-checks (read-only)
-- ---------------------------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.__EFMigrationsHistory', N'U') IS NULL
    THROW 50201, 'ABORTED: dbo.__EFMigrationsHistory is missing - wrong database?', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId = @Previous)
    THROW 50202, 'ABORTED: 20261119081103_AddCustomerProfileAuditAndProperties is not recorded in __EFMigrationsHistory; apply the earlier migrations first (AddTicketOriginalHumanAgent, AddOriginalAgentWorkflowCutoff).', 1;

IF EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId > @MigrationId)
    THROW 50203, 'ABORTED: __EFMigrationsHistory contains a migration newer than 20261119120249_AddAssignmentHistoryPreviousDepartment. Investigate manually.', 1;

-- 2a. dbo.TicketAssignmentHistory (target): table + PK + existing 16 columns, exact shape
IF OBJECT_ID(N'dbo.TicketAssignmentHistory', N'U') IS NULL
    THROW 50204, 'ABORTED: dbo.TicketAssignmentHistory is missing (migration 20261104130835_AddCollectionsTowerAssignments not applied?).', 1;

DECLARE @bad nvarchar(max) = N'';
SELECT @bad = @bad + e.Col + N' ' FROM (VALUES
    (N'TicketAssignmentHistoryEntryId', N'bigint',           8,   0),
    (N'TicketId',                       N'bigint',           8,   0),
    (N'DepartmentId',                   N'int',              4,   0),
    (N'PreviousEmployeeId',             N'uniqueidentifier', 16,  1),
    (N'NewEmployeeId',                  N'uniqueidentifier', 16,  0),
    (N'Kind',                           N'tinyint',          1,   0),
    (N'TowerId',                        N'int',              4,   1),
    (N'TowerCode',                      N'nvarchar',         40,  1),
    (N'TowerName',                      N'nvarchar',         400, 1),
    (N'UnitNumber',                     N'nvarchar',         200, 1),
    (N'ChangedByEmployeeId',            N'uniqueidentifier', 16,  1),
    (N'ChangedAtUtc',                   N'datetime2',        8,   0),
    (N'Reason',                         N'nvarchar',         1000,1),
    (N'GenesysConversationId',          N'nvarchar',         128, 1),
    (N'Source',                         N'tinyint',          1,   1),
    (N'SourceEventAtUtc',               N'datetime2',        8,   1)) AS e(Col, TypeName, MaxLen, Nullable)
  WHERE NOT EXISTS (
    SELECT 1 FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
    WHERE c.object_id = OBJECT_ID(N'dbo.TicketAssignmentHistory') AND c.name = e.Col
      AND t.name = e.TypeName AND c.max_length = e.MaxLen AND c.is_nullable = e.Nullable);
IF LEN(@bad) > 0
BEGIN
    SET @msg = N'ABORTED: dbo.TicketAssignmentHistory does not have the expected existing columns (check: ' + @bad + N'). Not touched.';
    THROW 50205, @msg, 1;
END;

IF NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE parent_object_id = OBJECT_ID(N'dbo.TicketAssignmentHistory') AND type = 'PK')
    THROW 50206, 'ABORTED: dbo.TicketAssignmentHistory has no primary key.', 1;

-- 2b. New columns: each either absent, or already present with EXACTLY the EF shape (anything else aborts)
SET @bad = N'';
SELECT @bad = @bad + n.Col + N' ' FROM (VALUES
    (N'PreviousDepartmentId', N'int', 4)) AS n(Col, TypeName, MaxLen)
  WHERE EXISTS (SELECT 1 FROM sys.columns c WHERE c.object_id = OBJECT_ID(N'dbo.TicketAssignmentHistory') AND c.name = n.Col)
    AND NOT EXISTS (
      SELECT 1 FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
      WHERE c.object_id = OBJECT_ID(N'dbo.TicketAssignmentHistory') AND c.name = n.Col
        AND t.name = n.TypeName AND c.max_length = n.MaxLen AND c.is_nullable = 1 AND c.is_identity = 0 AND c.is_computed = 0);
IF LEN(@bad) > 0
BEGIN
    SET @msg = N'ABORTED: dbo.TicketAssignmentHistory already has column(s) of this name with a different shape (' + @bad + N'). Not touched.';
    THROW 50207, @msg, 1;
END;

-- 2c. dbo.TicketAssignments (current-assignee table; READ-ONLY here): must exist with the columns the application relies on,
--     and keep its "one current assignment per ticket" filtered unique index. This script never modifies it.
IF OBJECT_ID(N'dbo.TicketAssignments', N'U') IS NULL
    THROW 50208, 'ABORTED: dbo.TicketAssignments is missing - wrong database?', 1;

SET @bad = N'';
SELECT @bad = @bad + e.Col + N' ' FROM (VALUES
    (N'TicketAssignmentId', N'bigint'), (N'TicketId', N'bigint'), (N'AssignedEmployeeId', N'uniqueidentifier'),
    (N'AssignedDepartmentId', N'int'), (N'AssignedAtUtc', N'datetime2'), (N'AssigningActorEmployeeId', N'uniqueidentifier'),
    (N'IsCurrent', N'bit')) AS e(Col, TypeName)
  WHERE NOT EXISTS (
    SELECT 1 FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
    WHERE c.object_id = OBJECT_ID(N'dbo.TicketAssignments') AND c.name = e.Col AND t.name = e.TypeName);
IF LEN(@bad) > 0
BEGIN
    SET @msg = N'ABORTED: dbo.TicketAssignments is missing expected columns (' + @bad + N').';
    THROW 50209, @msg, 1;
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.TicketAssignments') AND is_unique = 1 AND has_filter = 1
               AND filter_definition LIKE N'%IsCurrent%')
    THROW 50210, 'ABORTED: the filtered unique index on dbo.TicketAssignments (TicketId WHERE IsCurrent = 1) is missing; investigate before deploying.', 1;

-- ---------------------------------------------------------------------------------------------
-- 3. Before-snapshot of existing data (row counts of tables that must stay untouched)
-- ---------------------------------------------------------------------------------------------
DECLARE @Snapshot TABLE (TableName sysname PRIMARY KEY, RowsBefore bigint NOT NULL, RowsAfter bigint NULL);
DECLARE @t sysname, @sql nvarchar(max), @n bigint;
DECLARE watched CURSOR LOCAL FAST_FORWARD FOR
    SELECT v.n FROM (VALUES (N'Tickets'), (N'TicketAssignments'), (N'TicketAssignmentHistory'), (N'AuditEntries'), (N'TicketInteractions'),
        (N'TicketInteractionHandlers'), (N'TicketWorkflowEvents'), (N'TicketStatusHistory'), (N'Employees'), (N'AspNetUsers'),
        (N'__EFMigrationsHistory')) v(n)
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
DECLARE @ColsExisting int = (SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.TicketAssignmentHistory')
                             AND name IN (N'PreviousDepartmentId'));
SET @msg = N'Pre-state: new columns already present=' + CAST(@ColsExisting AS nvarchar(2)) + N' of 1, migration recorded='
         + CAST(@AlreadyRecorded AS nvarchar(1)) + N', history rows=' + CAST(@HistoryBefore AS nvarchar(20)) + N'.';
PRINT @msg;

-- ---------------------------------------------------------------------------------------------
-- 4. Apply (single transaction, additive only, no data written)
-- ---------------------------------------------------------------------------------------------
BEGIN TRY
    BEGIN TRANSACTION;

    IF COL_LENGTH(N'dbo.TicketAssignmentHistory', N'PreviousDepartmentId') IS NULL
    BEGIN
        ALTER TABLE dbo.TicketAssignmentHistory ADD PreviousDepartmentId int NULL;
        PRINT 'Added column PreviousDepartmentId.';
    END
    ELSE PRINT 'Column PreviousDepartmentId already exists with the expected shape - not added again.';

    IF NOT EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId = @MigrationId)
    BEGIN
        INSERT INTO dbo.__EFMigrationsHistory (MigrationId, ProductVersion) VALUES (@MigrationId, @ProductVersion);
        PRINT 'Recorded migration in __EFMigrationsHistory.';
    END
    ELSE PRINT 'Migration already recorded in __EFMigrationsHistory - not inserted again.';

    -- -----------------------------------------------------------------------------------------
    -- 5. Validation (inside the transaction, so any failure rolls everything back)
    -- -----------------------------------------------------------------------------------------
    -- 5a. The new column has the exact shape; the table has exactly 17 columns
    SET @bad = N'';
    SELECT @bad = @bad + n.Col + N' ' FROM (VALUES
        (N'PreviousDepartmentId', N'int', 4)) AS n(Col, TypeName, MaxLen)
      WHERE NOT EXISTS (
        SELECT 1 FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
        WHERE c.object_id = OBJECT_ID(N'dbo.TicketAssignmentHistory') AND c.name = n.Col
          AND t.name = n.TypeName AND c.max_length = n.MaxLen AND c.is_nullable = 1);
    IF LEN(@bad) > 0
    BEGIN
        SET @msg = N'VALIDATION FAILED: new column shape mismatch (' + @bad + N').';
        THROW 50220, @msg, 1;
    END;
    IF (SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.TicketAssignmentHistory')) <> 17
        THROW 50221, 'VALIDATION FAILED: dbo.TicketAssignmentHistory does not have exactly 17 columns.', 1;

    -- 5b. Primary key and the existing index are still in place
    IF NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE parent_object_id = OBJECT_ID(N'dbo.TicketAssignmentHistory') AND type = 'PK')
        THROW 50222, 'VALIDATION FAILED: primary key of dbo.TicketAssignmentHistory missing.', 1;
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.TicketAssignmentHistory') AND name = N'IX_TicketAssignmentHistory_Ticket_Changed')
        THROW 50223, 'VALIDATION FAILED: index IX_TicketAssignmentHistory_Ticket_Changed missing.', 1;

    -- 5c. Migration history: previous still there, new one recorded exactly once with the right version, +1 row on a first run
    IF NOT EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId = @Previous)
        THROW 50224, 'VALIDATION FAILED: previous migration row disappeared.', 1;
    IF (SELECT COUNT(*) FROM dbo.__EFMigrationsHistory WHERE MigrationId = @MigrationId) <> 1
        THROW 50225, 'VALIDATION FAILED: migration not recorded exactly once.', 1;
    IF NOT EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId = @MigrationId AND ProductVersion = @ProductVersion)
        THROW 50226, 'VALIDATION FAILED: migration recorded with an unexpected ProductVersion.', 1;
    IF (SELECT COUNT_BIG(*) FROM dbo.__EFMigrationsHistory) <> @HistoryBefore + CASE WHEN @AlreadyRecorded = 1 THEN 0 ELSE 1 END
        THROW 50227, 'VALIDATION FAILED: unexpected change in __EFMigrationsHistory row count.', 1;

    -- 5d. Existing data untouched: row counts identical (history table +1 on a first run only)
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
        THROW 50228, 'VALIDATION FAILED: an existing table changed its row count.', 1;

    SELECT TableName, RowsBefore, RowsAfter,
           CASE WHEN RowsBefore = RowsAfter THEN 'unchanged'
                WHEN TableName = N'__EFMigrationsHistory' AND RowsAfter = RowsBefore + 1 THEN '+1 (this migration)'
                ELSE 'CHANGED' END AS Result
    FROM @Snapshot ORDER BY TableName;

    -- 5e. No backfill: on a first run every existing history row has NULL in the new column.
    --     (Dynamic SQL: the columns did not exist when this batch was compiled.)
    IF @ColsExisting = 0
    BEGIN
        DECLARE @filled bigint;
        EXEC sp_executesql
            N'SELECT @f = COUNT_BIG(*) FROM dbo.TicketAssignmentHistory
              WHERE PreviousDepartmentId IS NOT NULL;',
            N'@f bigint OUTPUT', @f = @filled OUTPUT;
        IF @filled <> 0
            THROW 50229, 'VALIDATION FAILED: new columns are not NULL on existing history rows (no backfill is allowed).', 1;
    END;

    IF @DryRun = 1
    BEGIN
        ROLLBACK TRANSACTION;
        PRINT 'DRY RUN SUCCESS - schema, migration-history, no-backfill and existing-data checks all passed inside the transaction, which was ROLLED BACK. Nothing was changed. Set @DryRun = 0 to commit.';
    END
    ELSE
    BEGIN
        COMMIT TRANSACTION;
        SET @Committed = 1;
        PRINT 'COMMITTED. Migration 20261119120249_AddAssignmentHistoryPreviousDepartment applied to TigerCS_Ticketing_Prod.';
    END;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    PRINT 'FAILED - transaction rolled back, nothing was changed.';
    THROW;
END CATCH;

-- ---------------------------------------------------------------------------------------------
-- 6. Post-commit verification (read-only). Runs ONLY when @DryRun = 0 AND the commit succeeded.
--    Safe to re-run manually in a new window at any time after a real deployment.
-- ---------------------------------------------------------------------------------------------
IF @DryRun = 0 AND @Committed = 1
BEGIN
PRINT 'Post-commit verification results follow (expected values in comments).';

-- 6.1 Migration history (expect 2 rows; the new one with ProductVersion 10.0.11)
SELECT MigrationId, ProductVersion FROM dbo.__EFMigrationsHistory
WHERE MigrationId IN (N'20261119081103_AddCustomerProfileAuditAndProperties', N'20261119120249_AddAssignmentHistoryPreviousDepartment')
ORDER BY MigrationId;

-- 6.2 New column (expect 1 row: PreviousDepartmentId int max_length 4, is_nullable = 1)
SELECT c.name AS ColumnName, t.name AS TypeName, c.max_length, c.is_nullable
FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
WHERE c.object_id = OBJECT_ID(N'dbo.TicketAssignmentHistory')
  AND c.name = N'PreviousDepartmentId' ORDER BY c.name;

-- 6.3 Column count (expect 17)
SELECT COUNT(*) AS ColumnCount FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.TicketAssignmentHistory');

-- 6.4 No backfill (expect 0 right after deployment; grows only as the new application build records department changes)
SELECT COUNT_BIG(*) AS RowsWithPreviousDepartment FROM dbo.TicketAssignmentHistory WITH (NOLOCK)
WHERE PreviousDepartmentId IS NOT NULL;

-- 6.5 Existing data sanity (compare with the row counts shown above; none of these are modified)
SELECT (SELECT COUNT_BIG(*) FROM dbo.Tickets) AS Tickets,
       (SELECT COUNT_BIG(*) FROM dbo.TicketAssignments) AS TicketAssignments,
       (SELECT COUNT_BIG(*) FROM dbo.TicketAssignmentHistory) AS TicketAssignmentHistory,
       (SELECT COUNT_BIG(*) FROM dbo.AuditEntries) AS AuditEntries,
       (SELECT COUNT_BIG(*) FROM dbo.__EFMigrationsHistory) AS MigrationHistoryRows;

-- 6.6 Exactly one current assignment per ticket is still enforced (expect 0 rows)
SELECT TicketId, COUNT(*) AS CurrentRows FROM dbo.TicketAssignments WHERE IsCurrent = 1 GROUP BY TicketId HAVING COUNT(*) > 1;

-- 6.7 Latest migrations (expect ...AddAssignmentHistoryPreviousDepartment at the top)
SELECT TOP 3 MigrationId FROM dbo.__EFMigrationsHistory ORDER BY MigrationId DESC;
END;
