-- ############################################################################################
-- ## TigerCS UAT ONLY.
-- ## Database : TigerCsTicketing   (any other database name aborts before any change)
-- ## NOT EXECUTED by Claude. Review, then run yourself in SSMS (SQLCMD mode OFF).
-- ############################################################################################
-- Migration : 20261118120000_AddCustomerProfileOverrides (EF Core 10.0.11)
--             Taken from src/TigerCS.Infrastructure/Persistence/Migrations/20261118120000_AddCustomerProfileOverrides.cs
--             (the repository ships no standalone script for this migration, so this file is the EF Up() translated one-to-one:
--              CreateTable + one unique index; nothing else).
-- Why now   : UAT recorded 20261118124858_AddTicketAiAgentOwner (PR #288) but never received this older migration (PR #285), so
--             dbo.CustomerProfileOverrides does not exist. Migration 20261119081103_AddCustomerProfileAuditAndProperties (step 02)
--             alters that table and cannot run without it.
-- Effect    :  1. creates dbo.CustomerProfileOverrides (15 columns, PK on the identity column, unique index on CustomerKey).
--              2. records the migration in dbo.__EFMigrationsHistory (the same transaction - the row is written ONLY together with the table).
--             No existing table, column, index or row is changed or dropped. The new table starts empty.
-- Idempotent: re-running after success changes nothing. A table that exists without the history row (hand-made / partial) aborts untouched.
--
-- HOW TO RUN (SSMS connected to the UAT server, database TigerCsTicketing):
--   1. Leave @DryRun = 1 and run. Everything is applied inside a transaction, verified, then ROLLED BACK.
--   2. Read Messages + result sets: "DRY RUN SUCCESS".
--   3. Set @DryRun = 0 and run again to COMMIT. Post-commit verification result sets (section 6) follow.
-- Run as ONE batch. There is intentionally no GO in this script.
--
-- ROLLBACK (only if ever needed; the table is new and empty right after deployment):
--   BEGIN TRAN;
--     DROP TABLE dbo.CustomerProfileOverrides;
--     DELETE FROM dbo.__EFMigrationsHistory WHERE MigrationId = N'20261118120000_AddCustomerProfileOverrides';
--   COMMIT;
-- ############################################################################################

SET NOCOUNT ON;
SET XACT_ABORT ON;
SET LOCK_TIMEOUT 15000;   -- fail fast instead of blocking the application if something is busy

DECLARE @DryRun bit = 1;   -- 1 = apply, verify, ROLL BACK (default).  0 = apply, verify, COMMIT.

DECLARE @Committed bit = 0;  -- set to 1 only after COMMIT TRANSACTION succeeds

DECLARE @MigrationId    nvarchar(150) = N'20261118120000_AddCustomerProfileOverrides';
DECLARE @Previous       nvarchar(150) = N'20261118124858_AddTicketAiAgentOwner';   -- the latest migration UAT recorded before this one
DECLARE @ProductVersion nvarchar(32)  = N'10.0.11';
DECLARE @ExpectedDb     sysname       = N'TigerCsTicketing';
DECLARE @msg nvarchar(2000);

-- ---------------------------------------------------------------------------------------------
-- 1. Database protection (nothing has been touched yet)
-- ---------------------------------------------------------------------------------------------
IF DB_NAME() IS NULL OR DB_NAME() <> @ExpectedDb
BEGIN
    SET @msg = N'ABORTED: connected to database [' + ISNULL(DB_NAME(), N'?') + N'] on server [' + CAST(@@SERVERNAME AS nvarchar(128))
             + N']; this UAT script only runs against [' + @ExpectedDb + N']. Nothing was changed.';
    THROW 50400, @msg, 1;
END;
SET @msg = N'Guard passed: server [' + CAST(@@SERVERNAME AS nvarchar(128)) + N'], database [' + DB_NAME() + N'], DryRun=' + CAST(@DryRun AS nvarchar(1)) + N'.';
PRINT @msg;

-- ---------------------------------------------------------------------------------------------
-- 2. Dependency / schema pre-checks (read-only)
-- ---------------------------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.__EFMigrationsHistory', N'U') IS NULL
    THROW 50401, 'ABORTED: dbo.__EFMigrationsHistory is missing - wrong database?', 1;

DECLARE @AlreadyRecorded bit = CASE WHEN EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId = @MigrationId) THEN 1 ELSE 0 END;

IF @AlreadyRecorded = 0 AND NOT EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId = @Previous)
    THROW 50402, 'ABORTED: 20261118124858_AddTicketAiAgentOwner is not recorded in __EFMigrationsHistory - this is not the expected UAT state. Run the verification script and investigate.', 1;

-- Out-of-order guard: on a first run no migration newer than the one UAT already holds may be recorded (steps 02-05 come AFTER this one).
IF @AlreadyRecorded = 0 AND EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId > @Previous)
    THROW 50403, 'ABORTED: __EFMigrationsHistory already contains a migration newer than 20261118124858_AddTicketAiAgentOwner while 20261118120000_AddCustomerProfileOverrides is missing. Investigate manually.', 1;

IF @AlreadyRecorded = 0 AND OBJECT_ID(N'dbo.CustomerProfileOverrides', N'U') IS NOT NULL
    THROW 50404, 'ABORTED: dbo.CustomerProfileOverrides already exists but the migration is not recorded. A partial or hand-made state must be reviewed first. Not touched. (Do NOT insert the history row by hand.)', 1;

IF @AlreadyRecorded = 1 AND OBJECT_ID(N'dbo.CustomerProfileOverrides', N'U') IS NULL
    THROW 50405, 'ABORTED: the migration is recorded but dbo.CustomerProfileOverrides does not exist. Investigate manually.', 1;

-- ---------------------------------------------------------------------------------------------
-- 3. Before-snapshot of existing data (row counts of tables that must stay untouched)
-- ---------------------------------------------------------------------------------------------
DECLARE @Snapshot TABLE (TableName sysname PRIMARY KEY, RowsBefore bigint NOT NULL, RowsAfter bigint NULL);
DECLARE @t sysname, @sql nvarchar(max), @n bigint;
DECLARE watched CURSOR LOCAL FAST_FORWARD FOR
    SELECT v.n FROM (VALUES (N'Tickets'), (N'AuditEntries'), (N'Employees'), (N'AspNetUsers'), (N'TicketInteractions'), (N'TicketAssignmentHistory'),
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
SET @msg = N'Pre-state: migration recorded=' + CAST(@AlreadyRecorded AS nvarchar(1)) + N', history rows=' + CAST(@HistoryBefore AS nvarchar(20)) + N'.';
PRINT @msg;

-- ---------------------------------------------------------------------------------------------
-- 4. Apply (single transaction, additive only, no data written)
-- ---------------------------------------------------------------------------------------------
BEGIN TRY
    BEGIN TRANSACTION;

    IF @AlreadyRecorded = 0
    BEGIN
        CREATE TABLE dbo.CustomerProfileOverrides (
            CustomerProfileOverrideId bigint NOT NULL IDENTITY(1,1),
            CustomerKey nvarchar(300) NOT NULL,
            IdentityKind int NOT NULL,
            CrmBuyerCustomerId int NULL,
            ExternalSource nvarchar(200) NULL,
            ExternalCustomerId nvarchar(200) NULL,
            IdentityPhone nvarchar(32) NULL,
            PhoneNumber nvarchar(32) NULL,
            PhoneLocation tinyint NULL,
            ProjectName nvarchar(200) NULL,
            UnitNumber nvarchar(100) NULL,
            CreatedAtUtc datetime2 NOT NULL,
            UpdatedAtUtc datetime2 NOT NULL,
            UpdatedByEmployeeId uniqueidentifier NOT NULL,
            RowVersion rowversion NOT NULL,
            CONSTRAINT PK_CustomerProfileOverrides PRIMARY KEY (CustomerProfileOverrideId)
        );

        CREATE UNIQUE INDEX IX_CustomerProfileOverrides_CustomerKey ON dbo.CustomerProfileOverrides (CustomerKey);
        PRINT 'Created dbo.CustomerProfileOverrides with its primary key and unique index.';

        INSERT INTO dbo.__EFMigrationsHistory (MigrationId, ProductVersion) VALUES (@MigrationId, @ProductVersion);
        PRINT 'Recorded migration in __EFMigrationsHistory.';
    END
    ELSE PRINT 'Migration already recorded in __EFMigrationsHistory - nothing to create.';

    -- -----------------------------------------------------------------------------------------
    -- 5. Validation (inside the transaction, so any failure rolls everything back)
    -- -----------------------------------------------------------------------------------------
    -- 5a. Exact column shapes (max_length is in bytes: nvarchar(n) = 2n; rowversion is reported as 'timestamp')
    DECLARE @bad nvarchar(max) = N'';
    SELECT @bad = @bad + e.Col + N' ' FROM (VALUES
        (N'CustomerProfileOverrideId', N'bigint',           8,   0, 1),
        (N'CustomerKey',               N'nvarchar',         600, 0, 0),
        (N'IdentityKind',              N'int',              4,   0, 0),
        (N'CrmBuyerCustomerId',        N'int',              4,   1, 0),
        (N'ExternalSource',            N'nvarchar',         400, 1, 0),
        (N'ExternalCustomerId',        N'nvarchar',         400, 1, 0),
        (N'IdentityPhone',             N'nvarchar',         64,  1, 0),
        (N'PhoneNumber',               N'nvarchar',         64,  1, 0),
        (N'PhoneLocation',             N'tinyint',          1,   1, 0),
        (N'ProjectName',               N'nvarchar',         400, 1, 0),
        (N'UnitNumber',                N'nvarchar',         200, 1, 0),
        (N'CreatedAtUtc',              N'datetime2',        8,   0, 0),
        (N'UpdatedAtUtc',              N'datetime2',        8,   0, 0),
        (N'UpdatedByEmployeeId',       N'uniqueidentifier', 16,  0, 0),
        (N'RowVersion',                N'timestamp',        8,   0, 0)) AS e(Col, TypeName, MaxLen, Nullable, IsIdentity)
      WHERE NOT EXISTS (
        SELECT 1 FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
        WHERE c.object_id = OBJECT_ID(N'dbo.CustomerProfileOverrides') AND c.name = e.Col
          AND t.name = e.TypeName AND c.max_length = e.MaxLen AND c.is_nullable = e.Nullable AND c.is_identity = e.IsIdentity);
    IF LEN(@bad) > 0
    BEGIN
        SET @msg = N'VALIDATION FAILED: column shape mismatch (' + @bad + N').';
        THROW 50420, @msg, 1;
    END;
    IF (SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.CustomerProfileOverrides')) <> 15
        THROW 50421, 'VALIDATION FAILED: dbo.CustomerProfileOverrides does not have exactly 15 columns.', 1;

    -- 5b. Primary key on the identity column and the unique CustomerKey index
    IF NOT EXISTS (SELECT 1 FROM sys.key_constraints k
                   JOIN sys.index_columns ic ON ic.object_id = k.parent_object_id AND ic.index_id = k.unique_index_id
                   JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                   WHERE k.parent_object_id = OBJECT_ID(N'dbo.CustomerProfileOverrides') AND k.type = 'PK' AND k.name = N'PK_CustomerProfileOverrides'
                     AND c.name = N'CustomerProfileOverrideId')
        THROW 50422, 'VALIDATION FAILED: primary key PK_CustomerProfileOverrides on CustomerProfileOverrideId missing.', 1;
    IF NOT EXISTS (SELECT 1 FROM sys.indexes i
                   JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal = 1
                   JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                   WHERE i.object_id = OBJECT_ID(N'dbo.CustomerProfileOverrides') AND i.name = N'IX_CustomerProfileOverrides_CustomerKey'
                     AND i.is_unique = 1 AND i.has_filter = 0 AND c.name = N'CustomerKey')
        THROW 50423, 'VALIDATION FAILED: unique index IX_CustomerProfileOverrides_CustomerKey on CustomerKey missing.', 1;

    -- 5c. Migration history: the previous row is still there, the new one is recorded exactly once with the right version, +1 row on a first run
    IF NOT EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId = @Previous)
        THROW 50424, 'VALIDATION FAILED: previous migration row disappeared.', 1;
    IF (SELECT COUNT(*) FROM dbo.__EFMigrationsHistory WHERE MigrationId = @MigrationId) <> 1
        THROW 50425, 'VALIDATION FAILED: migration not recorded exactly once.', 1;
    IF NOT EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId = @MigrationId AND ProductVersion = @ProductVersion)
        THROW 50426, 'VALIDATION FAILED: migration recorded with an unexpected ProductVersion.', 1;
    IF (SELECT COUNT_BIG(*) FROM dbo.__EFMigrationsHistory) <> @HistoryBefore + CASE WHEN @AlreadyRecorded = 1 THEN 0 ELSE 1 END
        THROW 50427, 'VALIDATION FAILED: unexpected change in __EFMigrationsHistory row count.', 1;

    -- 5d. Existing data untouched: row counts identical (history table +1 on a first run only); the new table starts empty
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
        THROW 50428, 'VALIDATION FAILED: an existing table changed its row count.', 1;

    SELECT TableName, RowsBefore, RowsAfter,
           CASE WHEN RowsBefore = RowsAfter THEN 'unchanged'
                WHEN TableName = N'__EFMigrationsHistory' AND RowsAfter = RowsBefore + 1 THEN '+1 (this migration)'
                ELSE 'CHANGED' END AS Result
    FROM @Snapshot ORDER BY TableName;

    IF @AlreadyRecorded = 0 AND (SELECT COUNT_BIG(*) FROM dbo.CustomerProfileOverrides) <> 0
        THROW 50429, 'VALIDATION FAILED: the new table must start empty (no data is written by this migration).', 1;

    IF @DryRun = 1
    BEGIN
        ROLLBACK TRANSACTION;
        PRINT 'DRY RUN SUCCESS - schema, migration-history and existing-data checks all passed inside the transaction, which was ROLLED BACK. Nothing was changed. Set @DryRun = 0 to commit.';
    END
    ELSE
    BEGIN
        COMMIT TRANSACTION;
        SET @Committed = 1;
        PRINT 'COMMITTED. Migration 20261118120000_AddCustomerProfileOverrides applied to TigerCsTicketing.';
    END;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    PRINT 'FAILED - transaction rolled back, nothing was changed.';
    THROW;
END CATCH;

-- ---------------------------------------------------------------------------------------------
-- 6. Post-commit verification (read-only). Runs ONLY when @DryRun = 0 AND the commit succeeded.
-- ---------------------------------------------------------------------------------------------
IF @DryRun = 0 AND @Committed = 1
BEGIN
PRINT 'Post-commit verification results follow (expected values in comments).';

-- 6.1 Migration history (expect 2 rows; the new one with ProductVersion 10.0.11)
SELECT MigrationId, ProductVersion FROM dbo.__EFMigrationsHistory
WHERE MigrationId IN (N'20261118124858_AddTicketAiAgentOwner', N'20261118120000_AddCustomerProfileOverrides')
ORDER BY MigrationId;

-- 6.2 The table and its column count (expect 15)
SELECT o.name AS TableName, (SELECT COUNT(*) FROM sys.columns c WHERE c.object_id = o.object_id) AS ColumnCount
FROM sys.objects o WHERE o.type = 'U' AND o.name = N'CustomerProfileOverrides';

-- 6.3 Unique index (expect 1 row, is_unique = 1)
SELECT OBJECT_NAME(i.object_id) AS TableName, i.name AS IndexName, i.is_unique, i.has_filter
FROM sys.indexes i WHERE i.name = N'IX_CustomerProfileOverrides_CustomerKey';

-- 6.4 The table starts empty (expect 0 right after deployment)
SELECT COUNT_BIG(*) AS CustomerProfileOverridesRows FROM dbo.CustomerProfileOverrides;
END;
