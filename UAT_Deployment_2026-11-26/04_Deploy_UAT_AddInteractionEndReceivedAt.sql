-- ############################################################################################
-- ## TigerCS UAT ONLY.
-- ## Database : TigerCsTicketing   (any other database name aborts before any change)
-- ## Adapted for UAT from the repository's Deploy_TigerCS_PROD_AddInteractionEndReceivedAt.sql (same DDL, same validation); only the guard differs.
-- ## NOT EXECUTED by Claude. Review, then run yourself in SSMS (SQLCMD mode OFF).
-- ############################################################################################
-- Migration : 20261120074059_AddInteractionEndReceivedAt (EF Core 10.0.11)
--             Same single ALTER TABLE ... ADD statement as the EF-generated script AddInteractionEndReceivedAt.sql, wrapped in guards,
--             one transaction and verification. Part of PR #292 (voice ownership rules).
-- Depends on: 20261119120249_AddAssignmentHistoryPreviousDepartment (must already be in __EFMigrationsHistory).
-- Effect    : adds ONE NULLABLE column to dbo.TicketInteractions: EndReceivedAtUtc datetime2 NULL - the moment TigerCS RECEIVED a conversation's end
--             event. It anchors the voice no-answer reconciliation window (VoiceCallReconciliation:NoAnswerWindowSeconds). Metadata-only change.
--             NO existing row, ticket, assignment or audit record is touched. NO backfill (rows ended before this column existed fall back to EndedAtUtc).
-- Idempotent: re-running after success changes nothing. A column that already exists with the exact shape is kept; one with a different shape aborts.
-- Application: deploy the new TigerCS.Api build AFTER this script commits (the new code writes the column). The previous build ignores it.
--
-- HOW TO RUN (SSMS, connected to the UAT server, database TigerCsTicketing):
--   1. Take / confirm a fresh full backup first.
--   2. Leave @DryRun = 1 and run. Everything is applied in a transaction, verified, then ROLLED BACK.
--   3. Read Messages: guard passed, all checks OK, "DRY RUN SUCCESS", row counts unchanged.
--   4. Set @DryRun = 0 and run again to COMMIT.
-- Run as ONE batch. There is intentionally no GO in this script.
--
-- ROLLBACK (only if ever needed; run ONLY after the new application build is stopped / rolled back, because the column then holds data):
--   BEGIN TRAN;
--     ALTER TABLE dbo.TicketInteractions DROP COLUMN EndReceivedAtUtc;
--     DELETE FROM dbo.__EFMigrationsHistory WHERE MigrationId = N'20261120074059_AddInteractionEndReceivedAt';
--   COMMIT;
-- ############################################################################################

SET NOCOUNT ON;
SET XACT_ABORT ON;
SET LOCK_TIMEOUT 15000;   -- fail fast instead of blocking the application if the table is busy

DECLARE @DryRun bit = 1;   -- 1 = apply, verify, ROLL BACK (default).  0 = apply, verify, COMMIT.
DECLARE @Committed bit = 0;

DECLARE @MigrationId    nvarchar(150) = N'20261120074059_AddInteractionEndReceivedAt';
DECLARE @Previous       nvarchar(150) = N'20261119120249_AddAssignmentHistoryPreviousDepartment';
DECLARE @ProductVersion nvarchar(32)  = N'10.0.11';
DECLARE @ExpectedDb     sysname       = N'TigerCsTicketing';
DECLARE @msg nvarchar(2000);

-- 1. Server + database protection (nothing has been touched yet)
DECLARE @ServerName sysname = CAST(@@SERVERNAME AS sysname);
IF DB_NAME() IS NULL OR DB_NAME() <> @ExpectedDb
BEGIN
    SET @msg = N'ABORTED: connected to database [' + ISNULL(DB_NAME(), N'?') + N'] on server [' + ISNULL(@ServerName, N'?')
             + N']; this UAT script only runs against [' + @ExpectedDb + N']. Nothing was changed.';
    THROW 50300, @msg, 1;
END;
SET @msg = N'Guard passed: server [' + @ServerName + N'], database [' + DB_NAME() + N'], DryRun=' + CAST(@DryRun AS nvarchar(1)) + N'.';
PRINT @msg;

-- 2. Dependency / schema pre-checks (read-only)
IF OBJECT_ID(N'dbo.__EFMigrationsHistory', N'U') IS NULL
    THROW 50301, 'ABORTED: dbo.__EFMigrationsHistory is missing - wrong database?', 1;
IF NOT EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId = @Previous)
    THROW 50302, 'ABORTED: 20261119120249_AddAssignmentHistoryPreviousDepartment is not recorded in __EFMigrationsHistory; apply the earlier migrations first.', 1;
IF EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId > @MigrationId)
    THROW 50303, 'ABORTED: __EFMigrationsHistory contains a migration newer than 20261120074059_AddInteractionEndReceivedAt. Investigate manually.', 1;
IF OBJECT_ID(N'dbo.TicketInteractions', N'U') IS NULL
    THROW 50304, 'ABORTED: dbo.TicketInteractions is missing - wrong database?', 1;
IF EXISTS (SELECT 1 FROM sys.columns c WHERE c.object_id = OBJECT_ID(N'dbo.TicketInteractions') AND c.name = N'EndReceivedAtUtc')
   AND NOT EXISTS (
      SELECT 1 FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
      WHERE c.object_id = OBJECT_ID(N'dbo.TicketInteractions') AND c.name = N'EndReceivedAtUtc'
        AND t.name = N'datetime2' AND c.is_nullable = 1 AND c.is_identity = 0 AND c.is_computed = 0)
    THROW 50305, 'ABORTED: dbo.TicketInteractions.EndReceivedAtUtc already exists with a different shape. Not touched.', 1;

-- 3. Before-snapshot of row counts (must stay untouched)
DECLARE @Interactions bigint = (SELECT COUNT_BIG(*) FROM dbo.TicketInteractions);
DECLARE @Tickets bigint = (SELECT COUNT_BIG(*) FROM dbo.Tickets);
DECLARE @Assignments bigint = (SELECT COUNT_BIG(*) FROM dbo.TicketAssignments);
DECLARE @HistoryBefore bigint = (SELECT COUNT_BIG(*) FROM dbo.__EFMigrationsHistory);
DECLARE @AlreadyRecorded bit = CASE WHEN EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId = @MigrationId) THEN 1 ELSE 0 END;
DECLARE @ColExisting bit = CASE WHEN COL_LENGTH(N'dbo.TicketInteractions', N'EndReceivedAtUtc') IS NULL THEN 0 ELSE 1 END;
SET @msg = N'Pre-state: column present=' + CAST(@ColExisting AS nvarchar(1)) + N', migration recorded=' + CAST(@AlreadyRecorded AS nvarchar(1))
         + N', TicketInteractions rows=' + CAST(@Interactions AS nvarchar(20)) + N'.';
PRINT @msg;

-- 4. Apply (single transaction, additive only, no data written)
BEGIN TRY
    BEGIN TRANSACTION;

    IF COL_LENGTH(N'dbo.TicketInteractions', N'EndReceivedAtUtc') IS NULL
    BEGIN
        ALTER TABLE dbo.TicketInteractions ADD EndReceivedAtUtc datetime2 NULL;
        PRINT 'Added column EndReceivedAtUtc.';
    END
    ELSE PRINT 'Column EndReceivedAtUtc already exists with the expected shape - not added again.';

    IF NOT EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId = @MigrationId)
    BEGIN
        INSERT INTO dbo.__EFMigrationsHistory (MigrationId, ProductVersion) VALUES (@MigrationId, @ProductVersion);
        PRINT 'Recorded migration in __EFMigrationsHistory.';
    END
    ELSE PRINT 'Migration already recorded in __EFMigrationsHistory - not inserted again.';

    -- 5. Validation (inside the transaction, so any failure rolls everything back)
    IF NOT EXISTS (
        SELECT 1 FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
        WHERE c.object_id = OBJECT_ID(N'dbo.TicketInteractions') AND c.name = N'EndReceivedAtUtc' AND t.name = N'datetime2' AND c.is_nullable = 1)
        THROW 50320, 'VALIDATION FAILED: TicketInteractions.EndReceivedAtUtc has the wrong shape.', 1;
    IF (SELECT COUNT(*) FROM dbo.__EFMigrationsHistory WHERE MigrationId = @MigrationId) <> 1
        THROW 50321, 'VALIDATION FAILED: migration not recorded exactly once.', 1;
    IF NOT EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId = @MigrationId AND ProductVersion = @ProductVersion)
        THROW 50322, 'VALIDATION FAILED: migration recorded with an unexpected ProductVersion.', 1;
    IF (SELECT COUNT_BIG(*) FROM dbo.__EFMigrationsHistory) <> @HistoryBefore + CASE WHEN @AlreadyRecorded = 1 THEN 0 ELSE 1 END
        THROW 50323, 'VALIDATION FAILED: unexpected change in __EFMigrationsHistory row count.', 1;
    IF (SELECT COUNT_BIG(*) FROM dbo.TicketInteractions) <> @Interactions
       OR (SELECT COUNT_BIG(*) FROM dbo.Tickets) <> @Tickets
       OR (SELECT COUNT_BIG(*) FROM dbo.TicketAssignments) <> @Assignments
        THROW 50324, 'VALIDATION FAILED: an existing table changed its row count.', 1;
    IF @ColExisting = 0
    BEGIN
        DECLARE @filled bigint;
        EXEC sp_executesql N'SELECT @f = COUNT_BIG(*) FROM dbo.TicketInteractions WHERE EndReceivedAtUtc IS NOT NULL;', N'@f bigint OUTPUT', @f = @filled OUTPUT;
        IF @filled <> 0
            THROW 50325, 'VALIDATION FAILED: the new column is not NULL on existing rows (no backfill is allowed).', 1;
    END;

    IF @DryRun = 1
    BEGIN
        ROLLBACK TRANSACTION;
        PRINT 'DRY RUN SUCCESS - schema, migration-history, no-backfill and row-count checks passed inside the transaction, which was ROLLED BACK. Nothing was changed. Set @DryRun = 0 to commit.';
    END
    ELSE
    BEGIN
        COMMIT TRANSACTION;
        SET @Committed = 1;
        PRINT 'COMMITTED. Migration 20261120074059_AddInteractionEndReceivedAt applied to TigerCsTicketing.';
    END;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    PRINT 'FAILED - transaction rolled back, nothing was changed.';
    THROW;
END CATCH;

-- 6. Post-commit verification (read-only). Runs ONLY when @DryRun = 0 AND the commit succeeded.
IF @DryRun = 0 AND @Committed = 1
BEGIN
    SELECT TOP 3 MigrationId FROM dbo.__EFMigrationsHistory ORDER BY MigrationId DESC;   -- expect ...AddInteractionEndReceivedAt on top
    SELECT c.name AS ColumnName, t.name AS TypeName, c.is_nullable FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
    WHERE c.object_id = OBJECT_ID(N'dbo.TicketInteractions') AND c.name = N'EndReceivedAtUtc';   -- expect 1 row: datetime2, is_nullable = 1
    -- (dynamic SQL: after a DRY RUN the column was rolled back, so a plain reference would not even compile - the whole batch would fail before the guard runs)
    EXEC sp_executesql N'SELECT COUNT_BIG(*) AS RowsWithEndReceived FROM dbo.TicketInteractions WHERE EndReceivedAtUtc IS NOT NULL;';   -- 0 right after deployment; grows as ended calls arrive
END;
