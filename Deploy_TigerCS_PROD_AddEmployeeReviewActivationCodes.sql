-- ############################################################################################
-- ## TigerCS PRODUCTION ONLY.
-- ## Server   : TGAESHJDBP02
-- ## Database : TigerCS_Ticketing_Prod   (any other server or database aborts before any change)
-- ## NOT EXECUTED by Claude. Review, then run yourself in SSMS (SQLCMD mode OFF).
-- ############################################################################################
-- Migration : 20261126154428_AddEmployeeReviewActivationCodes (EF Core 10.0.11)
--             Same statements as the EF-generated script AddEmployeeReviewActivationCodes.sql, wrapped in guards, one transaction and verification.
-- Depends on: 20261124071552_AddEmployeeReviews (must already be in __EFMigrationsHistory) and dbo.EmployeeReviewDevices.
-- Effect    : Employee Review tablets are activated with a short (8 character), 24-hour, single-use code instead of the long device token.
--               dbo.EmployeeReviewDevices gets THREE new NULLABLE columns: ActivationCodeHash varbinary(32), ActivationCodeSalt varbinary(16),
--                 ActivationCodeExpiresAtUtc datetime2 (the code is never stored - only its salted hash)
--               dbo.EmployeeReviewActivationThrottles (NEW, empty): per-client and overall counters that limit activation attempts
--             and records the migration in dbo.__EFMigrationsHistory.
--             EXISTING ROWS ARE NOT CHANGED: every tablet that is already activated keeps its TokenHash, its employee and its reviews; the new
--             columns are NULL for it. NO data is written. dbo.EmployeeReviews, Tickets, TicketReviews and audit records are not touched.
-- Idempotent: re-running after success changes nothing. If any new object already exists but the migration is not recorded, the script ABORTS
--             (a partial or hand-made state must be looked at, not guessed at).
-- Application: deploy the new TigerCS.Api and TigerCS.Web builds AFTER this script commits. The previous builds ignore the new columns and table.
--
-- HOW TO RUN (SSMS, connected to TGAESHJDBP02, database TigerCS_Ticketing_Prod):
--   1. Take / confirm a fresh full backup first.
--   2. Leave @DryRun = 1 and run. Everything is applied in a transaction, verified, then ROLLED BACK.
--   3. Read Messages + result sets: guard passed, all checks OK, "DRY RUN SUCCESS", row counts unchanged.
--   4. Set @DryRun = 0 and run again to COMMIT. The same checks run before the commit, then post-commit
--      verification result sets (section 6) show the committed state.
-- Run as ONE batch. There is intentionally no GO in this script.
--
-- ROLLBACK (only if ever needed; run ONLY after the new application builds are stopped/rolled back. Tablets already activated keep working
-- on the previous builds because their TokenHash is not touched. A tablet registered but NOT yet activated would lose its pending code):
--   BEGIN TRAN;
--     DROP TABLE dbo.EmployeeReviewActivationThrottles;
--     ALTER TABLE dbo.EmployeeReviewDevices DROP COLUMN ActivationCodeExpiresAtUtc, ActivationCodeHash, ActivationCodeSalt;
--     DELETE FROM dbo.__EFMigrationsHistory WHERE MigrationId = N'20261126154428_AddEmployeeReviewActivationCodes';
--   COMMIT;
-- ############################################################################################

SET NOCOUNT ON;
SET XACT_ABORT ON;
SET LOCK_TIMEOUT 15000;   -- fail fast instead of blocking the application if something is busy

DECLARE @DryRun bit = 1;   -- 1 = apply, verify, ROLL BACK (default).  0 = apply, verify, COMMIT.

DECLARE @Committed bit = 0;  -- set to 1 only after COMMIT TRANSACTION succeeds

DECLARE @MigrationId    nvarchar(150) = N'20261126154428_AddEmployeeReviewActivationCodes';
DECLARE @Previous       nvarchar(150) = N'20261124071552_AddEmployeeReviews';
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
    THROW 50302, 'ABORTED: 20261124071552_AddEmployeeReviews is not recorded in __EFMigrationsHistory; apply the earlier migrations first.', 1;

IF EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId > @MigrationId)
    THROW 50303, 'ABORTED: __EFMigrationsHistory contains a migration newer than 20261126154428_AddEmployeeReviewActivationCodes. Investigate manually.', 1;

IF OBJECT_ID(N'dbo.EmployeeReviewDevices', N'U') IS NULL
    THROW 50304, 'ABORTED: dbo.EmployeeReviewDevices is missing - apply 20261124071552_AddEmployeeReviews first.', 1;

-- The new objects must not exist yet unless this migration was already recorded (then the script is a no-op).
DECLARE @AlreadyRecorded bit = CASE WHEN EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId = @MigrationId) THEN 1 ELSE 0 END;
IF @AlreadyRecorded = 0 AND (
       OBJECT_ID(N'dbo.EmployeeReviewActivationThrottles', N'U') IS NOT NULL
    OR COL_LENGTH(N'dbo.EmployeeReviewDevices', N'ActivationCodeHash') IS NOT NULL
    OR COL_LENGTH(N'dbo.EmployeeReviewDevices', N'ActivationCodeSalt') IS NOT NULL
    OR COL_LENGTH(N'dbo.EmployeeReviewDevices', N'ActivationCodeExpiresAtUtc') IS NOT NULL)
    THROW 50306, 'ABORTED: one of the new objects already exists but the migration is not recorded. A partial or hand-made state must be reviewed first. Not touched.', 1;

-- ---------------------------------------------------------------------------------------------
-- 3. Before-snapshot of existing data (row counts and activated tablets, which must stay untouched)
-- ---------------------------------------------------------------------------------------------
DECLARE @Snapshot TABLE (TableName sysname PRIMARY KEY, RowsBefore bigint NOT NULL, RowsAfter bigint NULL);
DECLARE @t sysname, @sql nvarchar(max), @n bigint;
DECLARE watched CURSOR LOCAL FAST_FORWARD FOR
    SELECT v.n FROM (VALUES (N'EmployeeReviewDevices'), (N'EmployeeReviews'), (N'Tickets'), (N'AuditEntries'), (N'Employees'), (N'AspNetUsers'),
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

-- Activated tablets and a fingerprint of their credentials: both must be identical after the migration.
DECLARE @ActivatedBefore bigint = (SELECT COUNT_BIG(*) FROM dbo.EmployeeReviewDevices WHERE TokenHash IS NOT NULL);
DECLARE @TokenFingerprintBefore int = (SELECT CHECKSUM_AGG(CHECKSUM(Id, EmployeeId, TokenHash, IsActive)) FROM dbo.EmployeeReviewDevices);
DECLARE @HistoryBefore bigint = (SELECT COUNT_BIG(*) FROM dbo.__EFMigrationsHistory);
SET @msg = N'Pre-state: migration recorded=' + CAST(@AlreadyRecorded AS nvarchar(1)) + N', activated tablets=' + CAST(@ActivatedBefore AS nvarchar(20))
         + N', history rows=' + CAST(@HistoryBefore AS nvarchar(20)) + N'.';
PRINT @msg;

-- ---------------------------------------------------------------------------------------------
-- 4. Apply (single transaction, additive only, no data written)
-- ---------------------------------------------------------------------------------------------
BEGIN TRY
    BEGIN TRANSACTION;

    IF @AlreadyRecorded = 0
    BEGIN
        EXEC(N'ALTER TABLE dbo.EmployeeReviewDevices ADD ActivationCodeExpiresAtUtc datetime2 NULL');
        EXEC(N'ALTER TABLE dbo.EmployeeReviewDevices ADD ActivationCodeHash varbinary(32) NULL');
        EXEC(N'ALTER TABLE dbo.EmployeeReviewDevices ADD ActivationCodeSalt varbinary(16) NULL');

        CREATE TABLE dbo.EmployeeReviewActivationThrottles (
            [Key] nvarchar(64) NOT NULL,
            AttemptCount int NOT NULL,
            WindowStartUtc datetime2 NOT NULL,
            CONSTRAINT PK_EmployeeReviewActivationThrottles PRIMARY KEY ([Key])
        );
        PRINT 'Added 3 nullable columns to dbo.EmployeeReviewDevices and created dbo.EmployeeReviewActivationThrottles.';

        INSERT INTO dbo.__EFMigrationsHistory (MigrationId, ProductVersion) VALUES (@MigrationId, @ProductVersion);
        PRINT 'Recorded migration in __EFMigrationsHistory.';
    END
    ELSE PRINT 'Migration already recorded in __EFMigrationsHistory - nothing to create.';

    -- -----------------------------------------------------------------------------------------
    -- 5. Validation (inside the transaction, so any failure rolls everything back)
    -- -----------------------------------------------------------------------------------------
    DECLARE @bad nvarchar(max) = N'';
    SELECT @bad = @bad + e.Tbl + N'.' + e.Col + N' ' FROM (VALUES
        (N'EmployeeReviewDevices',           N'TokenHash',                  N'varbinary',       32, 1),
        (N'EmployeeReviewDevices',           N'ActivationCodeHash',         N'varbinary',       32, 1),
        (N'EmployeeReviewDevices',           N'ActivationCodeSalt',         N'varbinary',       16, 1),
        (N'EmployeeReviewDevices',           N'ActivationCodeExpiresAtUtc', N'datetime2',        8, 1),
        (N'EmployeeReviewActivationThrottles', N'Key',                      N'nvarchar',      128, 0),
        (N'EmployeeReviewActivationThrottles', N'AttemptCount',             N'int',             4, 0),
        (N'EmployeeReviewActivationThrottles', N'WindowStartUtc',           N'datetime2',       8, 0)) AS e(Tbl, Col, TypeName, MaxLen, Nullable)
      WHERE NOT EXISTS (
        SELECT 1 FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
        WHERE c.object_id = OBJECT_ID(N'dbo.' + e.Tbl) AND c.name = e.Col
          AND t.name = e.TypeName AND c.max_length = e.MaxLen AND c.is_nullable = e.Nullable);
    IF LEN(@bad) > 0
    BEGIN
        SET @msg = N'VALIDATION FAILED: column shape mismatch (' + @bad + N').';
        THROW 50320, @msg, 1;
    END;
    IF (SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.EmployeeReviewDevices')) <> 11
        THROW 50321, 'VALIDATION FAILED: dbo.EmployeeReviewDevices does not have exactly 11 columns.', 1;
    IF (SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.EmployeeReviewActivationThrottles')) <> 3
        THROW 50322, 'VALIDATION FAILED: dbo.EmployeeReviewActivationThrottles does not have exactly 3 columns.', 1;
    IF NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE parent_object_id = OBJECT_ID(N'dbo.EmployeeReviewActivationThrottles') AND type = 'PK')
        THROW 50323, 'VALIDATION FAILED: primary key of dbo.EmployeeReviewActivationThrottles missing.', 1;
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.EmployeeReviewDevices') AND name = N'UX_EmployeeReviewDevices_TokenHash' AND is_unique = 1 AND has_filter = 1)
        THROW 50329, 'VALIDATION FAILED: filtered unique index UX_EmployeeReviewDevices_TokenHash is no longer in place.', 1;

    -- Migration history: previous still there, new one recorded exactly once with the right version, +1 row on a first run
    IF NOT EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId = @Previous)
        THROW 50330, 'VALIDATION FAILED: previous migration row disappeared.', 1;
    IF (SELECT COUNT(*) FROM dbo.__EFMigrationsHistory WHERE MigrationId = @MigrationId) <> 1
        THROW 50331, 'VALIDATION FAILED: migration not recorded exactly once.', 1;
    IF NOT EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId = @MigrationId AND ProductVersion = @ProductVersion)
        THROW 50332, 'VALIDATION FAILED: migration recorded with an unexpected ProductVersion.', 1;
    IF (SELECT COUNT_BIG(*) FROM dbo.__EFMigrationsHistory) <> @HistoryBefore + CASE WHEN @AlreadyRecorded = 1 THEN 0 ELSE 1 END
        THROW 50333, 'VALIDATION FAILED: unexpected change in __EFMigrationsHistory row count.', 1;

    -- Existing tablets untouched: same count, same credentials/employee/active flag, and no pending code on any of them.
    IF (SELECT COUNT_BIG(*) FROM dbo.EmployeeReviewDevices WHERE TokenHash IS NOT NULL) <> @ActivatedBefore
        THROW 50336, 'VALIDATION FAILED: the number of activated tablets changed.', 1;
    IF ISNULL((SELECT CHECKSUM_AGG(CHECKSUM(Id, EmployeeId, TokenHash, IsActive)) FROM dbo.EmployeeReviewDevices), 0) <> ISNULL(@TokenFingerprintBefore, 0)
        THROW 50337, 'VALIDATION FAILED: a tablet''s credential, employee or active flag changed.', 1;
    -- (Dynamic SQL: the three columns do not exist when this batch is compiled, and SQL Server resolves column names at compile time.)
    DECLARE @WithActivationData bigint;
    EXEC sp_executesql
        N'SELECT @c = COUNT_BIG(*) FROM dbo.EmployeeReviewDevices WHERE ActivationCodeHash IS NOT NULL OR ActivationCodeSalt IS NOT NULL OR ActivationCodeExpiresAtUtc IS NOT NULL',
        N'@c bigint OUTPUT', @c = @WithActivationData OUTPUT;
    IF @AlreadyRecorded = 0 AND @WithActivationData <> 0
        THROW 50338, 'VALIDATION FAILED: existing tablets must have NULL activation columns right after the migration.', 1;

    -- Existing data untouched: row counts identical (history table +1 on a first run only); the new table starts empty
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

    IF @AlreadyRecorded = 0 AND (SELECT COUNT_BIG(*) FROM dbo.EmployeeReviewActivationThrottles) <> 0
        THROW 50335, 'VALIDATION FAILED: the new table must start empty (no data is written by this migration).', 1;

    IF @DryRun = 1
    BEGIN
        ROLLBACK TRANSACTION;
        PRINT 'DRY RUN SUCCESS - schema, migration-history and existing-data checks all passed inside the transaction, which was ROLLED BACK. Nothing was changed. Set @DryRun = 0 to commit.';
    END
    ELSE
    BEGIN
        COMMIT TRANSACTION;
        SET @Committed = 1;
        PRINT 'COMMITTED. Migration 20261126154428_AddEmployeeReviewActivationCodes applied to TigerCS_Ticketing_Prod.';
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
WHERE MigrationId IN (N'20261124071552_AddEmployeeReviews', N'20261126154428_AddEmployeeReviewActivationCodes')
ORDER BY MigrationId;

-- 6.2 Column counts (expect EmployeeReviewDevices 11, EmployeeReviewActivationThrottles 3)
SELECT o.name AS TableName, (SELECT COUNT(*) FROM sys.columns c WHERE c.object_id = o.object_id) AS ColumnCount
FROM sys.objects o WHERE o.type = 'U' AND o.name IN (N'EmployeeReviewDevices', N'EmployeeReviewActivationThrottles') ORDER BY o.name;

-- 6.3 Tablets: activated ones keep their credential; none has a pending code yet (expect PendingCodes = 0 right after deployment)
EXEC(N'SELECT COUNT_BIG(*) AS Tablets,
       SUM(CASE WHEN TokenHash IS NOT NULL THEN 1 ELSE 0 END) AS ActivatedTablets,
       SUM(CASE WHEN ActivationCodeHash IS NOT NULL THEN 1 ELSE 0 END) AS PendingCodes
FROM dbo.EmployeeReviewDevices');

-- 6.4 The attempt counter table starts empty (expect 0)
SELECT COUNT_BIG(*) AS ThrottleRows FROM dbo.EmployeeReviewActivationThrottles;

-- 6.5 Latest migrations (expect ...AddEmployeeReviewActivationCodes at the top)
SELECT TOP 3 MigrationId FROM dbo.__EFMigrationsHistory ORDER BY MigrationId DESC;
END;
