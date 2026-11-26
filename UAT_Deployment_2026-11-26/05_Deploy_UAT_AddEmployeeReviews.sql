-- ############################################################################################
-- ## TigerCS UAT ONLY.
-- ## Database : TigerCsTicketing   (any other database name aborts before any change)
-- ## Adapted for UAT from the repository's Deploy_TigerCS_PROD_AddEmployeeReviews.sql (same DDL, same validation); only the guard differs.
-- ## NOT EXECUTED by Claude. Review, then run yourself in SSMS (SQLCMD mode OFF).
-- ############################################################################################
-- Migration : 20261124071552_AddEmployeeReviews (EF Core 10.0.11)
--             Same statements as the EF-generated script AddEmployeeReviews.sql, wrapped in guards, one transaction and verification.
-- Depends on: 20261120074059_AddInteractionEndReceivedAt (must already be in __EFMigrationsHistory) and dbo.Employees.
-- Effect    : creates TWO new, empty tables for the Employee Review feature (customer star ratings of employees, collected on review tablets):
--               dbo.EmployeeReviewDevices (tablets: name, assigned employee, SHA-256 token hash, active flag, last activity)
--               dbo.EmployeeReviews       (reviews: device, employee at the time of the review, rating 1-5, comment, UTC time, unique SubmissionId)
--             and records the migration in dbo.__EFMigrationsHistory. Both tables reference dbo.Employees (no second employee table).
--             NO existing table, ticket, ticket review (TicketReviews / TicketReviewInvitations) or audit record is touched. NO data is written.
-- Idempotent: re-running after success changes nothing. If either table already exists but the migration is not recorded, the script ABORTS
--             (a partial or hand-made state must be looked at, not guessed at).
-- Application: deploy the new TigerCS.Api and TigerCS.Web builds AFTER this script commits. The previous builds ignore the tables.
--
-- HOW TO RUN (SSMS, connected to the UAT server, database TigerCsTicketing):
--   1. Take / confirm a fresh full backup first.
--   2. Leave @DryRun = 1 and run. Everything is applied in a transaction, verified, then ROLLED BACK.
--   3. Read Messages + result sets: guard passed, all checks OK, "DRY RUN SUCCESS", row counts unchanged.
--   4. Set @DryRun = 0 and run again to COMMIT. The same checks run before the commit, then post-commit
--      verification result sets (section 6) show the committed state.
-- Run as ONE batch. There is intentionally no GO in this script.
--
-- ROLLBACK (only if ever needed; run ONLY after the new application builds are stopped/rolled back, because the tables
-- then hold collected reviews that the DROP would delete):
--   BEGIN TRAN;
--     DROP TABLE dbo.EmployeeReviews;
--     DROP TABLE dbo.EmployeeReviewDevices;
--     DELETE FROM dbo.__EFMigrationsHistory WHERE MigrationId = N'20261124071552_AddEmployeeReviews';
--   COMMIT;
-- ############################################################################################

SET NOCOUNT ON;
SET XACT_ABORT ON;
SET LOCK_TIMEOUT 15000;   -- fail fast instead of blocking the application if something is busy

DECLARE @DryRun bit = 1;   -- 1 = apply, verify, ROLL BACK (default).  0 = apply, verify, COMMIT.

DECLARE @Committed bit = 0;  -- set to 1 only after COMMIT TRANSACTION succeeds

DECLARE @MigrationId    nvarchar(150) = N'20261124071552_AddEmployeeReviews';
DECLARE @Previous       nvarchar(150) = N'20261120074059_AddInteractionEndReceivedAt';
DECLARE @ProductVersion nvarchar(32)  = N'10.0.11';
DECLARE @ExpectedDb     sysname       = N'TigerCsTicketing';
DECLARE @msg nvarchar(2000);

-- ---------------------------------------------------------------------------------------------
-- 1. Server + database protection (nothing has been touched yet)
-- ---------------------------------------------------------------------------------------------
DECLARE @ServerName sysname = CAST(@@SERVERNAME AS sysname);
IF DB_NAME() IS NULL OR DB_NAME() <> @ExpectedDb
BEGIN
    SET @msg = N'ABORTED: connected to database [' + ISNULL(DB_NAME(), N'?') + N'] on server [' + ISNULL(@ServerName, N'?')
             + N']; this UAT script only runs against [' + @ExpectedDb + N']. Nothing was changed.';
    THROW 50300, @msg, 1;
END;
SET @msg = N'Guard passed: server [' + @ServerName + N'], database [' + DB_NAME() + N'], DryRun=' + CAST(@DryRun AS nvarchar(1)) + N'.';
PRINT @msg;

-- ---------------------------------------------------------------------------------------------
-- 2. Dependency / schema pre-checks (read-only)
-- ---------------------------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.__EFMigrationsHistory', N'U') IS NULL
    THROW 50301, 'ABORTED: dbo.__EFMigrationsHistory is missing - wrong database?', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId = @Previous)
    THROW 50302, 'ABORTED: 20261120074059_AddInteractionEndReceivedAt is not recorded in __EFMigrationsHistory; apply the earlier migrations first.', 1;

IF EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId > @MigrationId)
    THROW 50303, 'ABORTED: __EFMigrationsHistory contains a migration newer than 20261124071552_AddEmployeeReviews. Investigate manually.', 1;

-- 2a. dbo.Employees (referenced by both new tables): table + uniqueidentifier primary key EmployeeId
IF OBJECT_ID(N'dbo.Employees', N'U') IS NULL
    THROW 50304, 'ABORTED: dbo.Employees is missing - wrong database?', 1;
IF NOT EXISTS (
    SELECT 1 FROM sys.key_constraints k
    JOIN sys.index_columns ic ON ic.object_id = k.parent_object_id AND ic.index_id = k.unique_index_id
    JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
    JOIN sys.types t ON t.user_type_id = c.user_type_id
    WHERE k.parent_object_id = OBJECT_ID(N'dbo.Employees') AND k.type = 'PK' AND c.name = N'EmployeeId' AND t.name = N'uniqueidentifier')
    THROW 50305, 'ABORTED: dbo.Employees has no uniqueidentifier primary key EmployeeId.', 1;

-- 2b. The new tables must not exist yet unless this migration was already recorded (then the script is a no-op).
DECLARE @AlreadyRecorded bit = CASE WHEN EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId = @MigrationId) THEN 1 ELSE 0 END;
IF @AlreadyRecorded = 0 AND (OBJECT_ID(N'dbo.EmployeeReviewDevices', N'U') IS NOT NULL OR OBJECT_ID(N'dbo.EmployeeReviews', N'U') IS NOT NULL)
    THROW 50306, 'ABORTED: dbo.EmployeeReviewDevices or dbo.EmployeeReviews already exists but the migration is not recorded. A partial or hand-made state must be reviewed first. Not touched.', 1;

-- ---------------------------------------------------------------------------------------------
-- 3. Before-snapshot of existing data (row counts of tables that must stay untouched)
-- ---------------------------------------------------------------------------------------------
DECLARE @Snapshot TABLE (TableName sysname PRIMARY KEY, RowsBefore bigint NOT NULL, RowsAfter bigint NULL);
DECLARE @t sysname, @sql nvarchar(max), @n bigint;
DECLARE watched CURSOR LOCAL FAST_FORWARD FOR
    SELECT v.n FROM (VALUES (N'Tickets'), (N'TicketReviews'), (N'TicketReviewInvitations'), (N'AuditEntries'), (N'Employees'), (N'AspNetUsers'),
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
        CREATE TABLE dbo.EmployeeReviewDevices (
            Id uniqueidentifier NOT NULL,
            DeviceName nvarchar(100) NOT NULL,
            EmployeeId uniqueidentifier NOT NULL,
            TokenHash varbinary(32) NULL,
            IsActive bit NOT NULL,
            CreatedAtUtc datetime2 NOT NULL,
            UpdatedAtUtc datetime2 NOT NULL,
            LastSeenAtUtc datetime2 NULL,
            CONSTRAINT PK_EmployeeReviewDevices PRIMARY KEY (Id),
            CONSTRAINT FK_EmployeeReviewDevices_Employees_EmployeeId FOREIGN KEY (EmployeeId) REFERENCES dbo.Employees (EmployeeId) ON DELETE NO ACTION
        );

        CREATE TABLE dbo.EmployeeReviews (
            Id uniqueidentifier NOT NULL,
            DeviceId uniqueidentifier NOT NULL,
            EmployeeId uniqueidentifier NOT NULL,
            Rating tinyint NOT NULL,
            Comment nvarchar(1000) NULL,
            CreatedAtUtc datetime2 NOT NULL,
            SubmissionId uniqueidentifier NOT NULL,
            CONSTRAINT PK_EmployeeReviews PRIMARY KEY (Id),
            CONSTRAINT CK_EmployeeReviews_Rating CHECK (Rating BETWEEN 1 AND 5),
            CONSTRAINT FK_EmployeeReviews_EmployeeReviewDevices_DeviceId FOREIGN KEY (DeviceId) REFERENCES dbo.EmployeeReviewDevices (Id) ON DELETE NO ACTION,
            CONSTRAINT FK_EmployeeReviews_Employees_EmployeeId FOREIGN KEY (EmployeeId) REFERENCES dbo.Employees (EmployeeId) ON DELETE NO ACTION
        );

        CREATE INDEX IX_EmployeeReviewDevices_EmployeeId ON dbo.EmployeeReviewDevices (EmployeeId);
        EXEC(N'CREATE UNIQUE INDEX UX_EmployeeReviewDevices_TokenHash ON dbo.EmployeeReviewDevices (TokenHash) WHERE TokenHash IS NOT NULL');
        CREATE INDEX IX_EmployeeReviews_CreatedAtUtc ON dbo.EmployeeReviews (CreatedAtUtc);
        CREATE INDEX IX_EmployeeReviews_DeviceId ON dbo.EmployeeReviews (DeviceId);
        CREATE INDEX IX_EmployeeReviews_EmployeeId_CreatedAtUtc ON dbo.EmployeeReviews (EmployeeId, CreatedAtUtc);
        CREATE UNIQUE INDEX UX_EmployeeReviews_SubmissionId ON dbo.EmployeeReviews (SubmissionId);
        PRINT 'Created dbo.EmployeeReviewDevices and dbo.EmployeeReviews with their keys and indexes.';

        INSERT INTO dbo.__EFMigrationsHistory (MigrationId, ProductVersion) VALUES (@MigrationId, @ProductVersion);
        PRINT 'Recorded migration in __EFMigrationsHistory.';
    END
    ELSE PRINT 'Migration already recorded in __EFMigrationsHistory - nothing to create.';

    -- -----------------------------------------------------------------------------------------
    -- 5. Validation (inside the transaction, so any failure rolls everything back)
    -- -----------------------------------------------------------------------------------------
    -- 5a. Exact column shapes
    DECLARE @bad nvarchar(max) = N'';
    SELECT @bad = @bad + e.Tbl + N'.' + e.Col + N' ' FROM (VALUES
        (N'EmployeeReviewDevices', N'Id',            N'uniqueidentifier', 16,  0),
        (N'EmployeeReviewDevices', N'DeviceName',    N'nvarchar',         200, 0),
        (N'EmployeeReviewDevices', N'EmployeeId',    N'uniqueidentifier', 16,  0),
        (N'EmployeeReviewDevices', N'TokenHash',     N'varbinary',        32,  1),
        (N'EmployeeReviewDevices', N'IsActive',      N'bit',              1,   0),
        (N'EmployeeReviewDevices', N'CreatedAtUtc',  N'datetime2',        8,   0),
        (N'EmployeeReviewDevices', N'UpdatedAtUtc',  N'datetime2',        8,   0),
        (N'EmployeeReviewDevices', N'LastSeenAtUtc', N'datetime2',        8,   1),
        (N'EmployeeReviews',       N'Id',            N'uniqueidentifier', 16,  0),
        (N'EmployeeReviews',       N'DeviceId',      N'uniqueidentifier', 16,  0),
        (N'EmployeeReviews',       N'EmployeeId',    N'uniqueidentifier', 16,  0),
        (N'EmployeeReviews',       N'Rating',        N'tinyint',          1,   0),
        (N'EmployeeReviews',       N'Comment',       N'nvarchar',         2000,1),
        (N'EmployeeReviews',       N'CreatedAtUtc',  N'datetime2',        8,   0),
        (N'EmployeeReviews',       N'SubmissionId',  N'uniqueidentifier', 16,  0)) AS e(Tbl, Col, TypeName, MaxLen, Nullable)
      WHERE NOT EXISTS (
        SELECT 1 FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
        WHERE c.object_id = OBJECT_ID(N'dbo.' + e.Tbl) AND c.name = e.Col
          AND t.name = e.TypeName AND c.max_length = e.MaxLen AND c.is_nullable = e.Nullable);
    IF LEN(@bad) > 0
    BEGIN
        SET @msg = N'VALIDATION FAILED: column shape mismatch (' + @bad + N').';
        THROW 50320, @msg, 1;
    END;
    IF (SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.EmployeeReviewDevices')) <> 8
        THROW 50321, 'VALIDATION FAILED: dbo.EmployeeReviewDevices does not have exactly 8 columns.', 1;
    IF (SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.EmployeeReviews')) <> 7
        THROW 50322, 'VALIDATION FAILED: dbo.EmployeeReviews does not have exactly 7 columns.', 1;

    -- 5b. Keys, check constraint, foreign keys and the two unique indexes (the idempotency key and the token lookup)
    IF NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE parent_object_id = OBJECT_ID(N'dbo.EmployeeReviewDevices') AND type = 'PK')
        THROW 50323, 'VALIDATION FAILED: primary key of dbo.EmployeeReviewDevices missing.', 1;
    IF NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE parent_object_id = OBJECT_ID(N'dbo.EmployeeReviews') AND type = 'PK')
        THROW 50324, 'VALIDATION FAILED: primary key of dbo.EmployeeReviews missing.', 1;
    IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE parent_object_id = OBJECT_ID(N'dbo.EmployeeReviews') AND name = N'CK_EmployeeReviews_Rating')
        THROW 50325, 'VALIDATION FAILED: CK_EmployeeReviews_Rating missing.', 1;
    IF (SELECT COUNT(*) FROM sys.foreign_keys WHERE parent_object_id = OBJECT_ID(N'dbo.EmployeeReviewDevices')) <> 1
        THROW 50326, 'VALIDATION FAILED: dbo.EmployeeReviewDevices must have exactly 1 foreign key (Employees).', 1;
    IF (SELECT COUNT(*) FROM sys.foreign_keys WHERE parent_object_id = OBJECT_ID(N'dbo.EmployeeReviews')) <> 2
        THROW 50327, 'VALIDATION FAILED: dbo.EmployeeReviews must have exactly 2 foreign keys (device, Employees).', 1;
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.EmployeeReviews') AND name = N'UX_EmployeeReviews_SubmissionId' AND is_unique = 1)
        THROW 50328, 'VALIDATION FAILED: unique index UX_EmployeeReviews_SubmissionId missing.', 1;
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.EmployeeReviewDevices') AND name = N'UX_EmployeeReviewDevices_TokenHash' AND is_unique = 1 AND has_filter = 1)
        THROW 50329, 'VALIDATION FAILED: filtered unique index UX_EmployeeReviewDevices_TokenHash missing.', 1;

    -- 5c. Migration history: previous still there, new one recorded exactly once with the right version, +1 row on a first run
    IF NOT EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId = @Previous)
        THROW 50330, 'VALIDATION FAILED: previous migration row disappeared.', 1;
    IF (SELECT COUNT(*) FROM dbo.__EFMigrationsHistory WHERE MigrationId = @MigrationId) <> 1
        THROW 50331, 'VALIDATION FAILED: migration not recorded exactly once.', 1;
    IF NOT EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId = @MigrationId AND ProductVersion = @ProductVersion)
        THROW 50332, 'VALIDATION FAILED: migration recorded with an unexpected ProductVersion.', 1;
    IF (SELECT COUNT_BIG(*) FROM dbo.__EFMigrationsHistory) <> @HistoryBefore + CASE WHEN @AlreadyRecorded = 1 THEN 0 ELSE 1 END
        THROW 50333, 'VALIDATION FAILED: unexpected change in __EFMigrationsHistory row count.', 1;

    -- 5d. Existing data untouched: row counts identical (history table +1 on a first run only); the new tables start empty
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
        THROW 50334, 'VALIDATION FAILED: an existing table changed its row count.', 1;

    SELECT TableName, RowsBefore, RowsAfter,
           CASE WHEN RowsBefore = RowsAfter THEN 'unchanged'
                WHEN TableName = N'__EFMigrationsHistory' AND RowsAfter = RowsBefore + 1 THEN '+1 (this migration)'
                ELSE 'CHANGED' END AS Result
    FROM @Snapshot ORDER BY TableName;

    IF @AlreadyRecorded = 0 AND ((SELECT COUNT_BIG(*) FROM dbo.EmployeeReviewDevices) <> 0 OR (SELECT COUNT_BIG(*) FROM dbo.EmployeeReviews) <> 0)
        THROW 50335, 'VALIDATION FAILED: the new tables must start empty (no data is written by this migration).', 1;

    IF @DryRun = 1
    BEGIN
        ROLLBACK TRANSACTION;
        PRINT 'DRY RUN SUCCESS - schema, migration-history and existing-data checks all passed inside the transaction, which was ROLLED BACK. Nothing was changed. Set @DryRun = 0 to commit.';
    END
    ELSE
    BEGIN
        COMMIT TRANSACTION;
        SET @Committed = 1;
        PRINT 'COMMITTED. Migration 20261124071552_AddEmployeeReviews applied to TigerCsTicketing.';
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
WHERE MigrationId IN (N'20261120074059_AddInteractionEndReceivedAt', N'20261124071552_AddEmployeeReviews')
ORDER BY MigrationId;

-- 6.2 The two tables and their column counts (expect EmployeeReviewDevices 8, EmployeeReviews 7)
SELECT o.name AS TableName, (SELECT COUNT(*) FROM sys.columns c WHERE c.object_id = o.object_id) AS ColumnCount
FROM sys.objects o WHERE o.type = 'U' AND o.name IN (N'EmployeeReviewDevices', N'EmployeeReviews') ORDER BY o.name;

-- 6.3 Unique indexes (expect 2 rows: the token lookup is filtered, the submission id is not)
SELECT OBJECT_NAME(i.object_id) AS TableName, i.name AS IndexName, i.is_unique, i.has_filter
FROM sys.indexes i WHERE i.name IN (N'UX_EmployeeReviews_SubmissionId', N'UX_EmployeeReviewDevices_TokenHash') ORDER BY i.name;

-- 6.4 Both tables start empty (expect 0 and 0 right after deployment)
SELECT (SELECT COUNT_BIG(*) FROM dbo.EmployeeReviewDevices) AS Tablets, (SELECT COUNT_BIG(*) FROM dbo.EmployeeReviews) AS Reviews;

-- 6.5 Existing data sanity (compare with the row counts shown above; none of these are modified)
SELECT (SELECT COUNT_BIG(*) FROM dbo.Tickets) AS Tickets,
       (SELECT COUNT_BIG(*) FROM dbo.TicketReviews) AS TicketReviews,
       (SELECT COUNT_BIG(*) FROM dbo.Employees) AS Employees,
       (SELECT COUNT_BIG(*) FROM dbo.AuditEntries) AS AuditEntries,
       (SELECT COUNT_BIG(*) FROM dbo.__EFMigrationsHistory) AS MigrationHistoryRows;

-- 6.6 Latest migrations (expect ...AddEmployeeReviews at the top)
SELECT TOP 3 MigrationId FROM dbo.__EFMigrationsHistory ORDER BY MigrationId DESC;
END;
