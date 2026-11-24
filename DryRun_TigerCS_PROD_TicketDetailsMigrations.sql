-- ############################################################################################
-- ## TigerCS PRODUCTION ONLY - COMBINED DRY RUN (ALWAYS ROLLS BACK; THERE IS NO COMMIT PATH)
-- ## Server   : TGAESHJDBP02
-- ## Database : TigerCS_Ticketing_Prod   (any other server or database aborts before any change)
-- ## NOT EXECUTED by Claude. Review, then run yourself in SSMS (SQLCMD mode OFF), as ONE batch (no GO).
-- ############################################################################################
-- Purpose   : rehearse BOTH missing migrations in their required order, inside one transaction that is ALWAYS rolled back:
--               1. 20261119120249_AddAssignmentHistoryPreviousDepartment  ->  dbo.TicketAssignmentHistory.PreviousDepartmentId int NULL
--               2. 20261120074059_AddInteractionEndReceivedAt             ->  dbo.TicketInteractions.EndReceivedAtUtc datetime2 NULL
--             Needed because the stand-alone Deploy_..._AddInteractionEndReceivedAt.sql cannot pass its own dry run until migration 1 is committed
--             (it requires migration 1 in __EFMigrationsHistory).
-- Not a deployment: this script cannot commit. To deploy, run the two reviewed Deploy_TigerCS_PROD_*.sql scripts, in order, each with @DryRun = 1 and then 0.
-- History rows are written here ONLY next to the real DDL, exactly as EF would, and only inside the rolled-back transaction.
-- Safety    : additive nullable columns only; no data written; XACT_ABORT ON; LOCK_TIMEOUT 15 s (Msg 1222 = busy table, nothing changed, retry off-peak).
--             The ALTERs take a short schema lock on two tables while the transaction is open: run at a quiet time. A live insert between the
--             row-count snapshot and the check can trip error 50424 - that is a safe rollback; just re-run.
-- ############################################################################################

SET NOCOUNT ON;
SET XACT_ABORT ON;
SET LOCK_TIMEOUT 15000;

DECLARE @M1 nvarchar(150) = N'20261119120249_AddAssignmentHistoryPreviousDepartment';
DECLARE @M2 nvarchar(150) = N'20261120074059_AddInteractionEndReceivedAt';
DECLARE @Prereq nvarchar(150) = N'20261119081103_AddCustomerProfileAuditAndProperties';
DECLARE @ProductVersion nvarchar(32) = N'10.0.11';
DECLARE @ExpectedServer sysname = N'TGAESHJDBP02';
DECLARE @ExpectedDb sysname = N'TigerCS_Ticketing_Prod';
DECLARE @msg nvarchar(2000);

-- 1. Server + database protection (nothing touched yet)
DECLARE @ServerName sysname = CAST(@@SERVERNAME AS sysname);
IF DB_NAME() IS NULL OR DB_NAME() <> @ExpectedDb
   OR @ServerName IS NULL OR (UPPER(@ServerName) <> UPPER(@ExpectedServer) AND UPPER(@ServerName) NOT LIKE UPPER(@ExpectedServer) + N'\%')
BEGIN
    SET @msg = N'ABORTED: connected to database [' + ISNULL(DB_NAME(), N'?') + N'] on server [' + ISNULL(@ServerName, N'?')
             + N']; this Production dry run only runs against [' + @ExpectedDb + N'] on [' + @ExpectedServer + N']. Nothing was changed.';
    THROW 50400, @msg, 1;
END;
PRINT N'Guard passed: server [' + @ServerName + N'], database [' + DB_NAME() + N']. DRY RUN ONLY - this script always rolls back.';

-- 2. Read-only pre-checks
IF OBJECT_ID(N'dbo.__EFMigrationsHistory', N'U') IS NULL
    THROW 50401, 'ABORTED: dbo.__EFMigrationsHistory is missing - wrong database?', 1;
IF NOT EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId = @Prereq)
    THROW 50402, 'ABORTED: 20261119081103_AddCustomerProfileAuditAndProperties is not recorded; earlier migrations must be applied first.', 1;
IF EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId > @M2)
    THROW 50403, 'ABORTED: __EFMigrationsHistory contains a migration newer than 20261120074059_AddInteractionEndReceivedAt. Investigate manually.', 1;
IF OBJECT_ID(N'dbo.TicketAssignmentHistory', N'U') IS NULL OR OBJECT_ID(N'dbo.TicketInteractions', N'U') IS NULL
   OR OBJECT_ID(N'dbo.Tickets', N'U') IS NULL OR OBJECT_ID(N'dbo.TicketAssignments', N'U') IS NULL
    THROW 50404, 'ABORTED: an expected table (TicketAssignmentHistory, TicketInteractions, Tickets, TicketAssignments) is missing - wrong database?', 1;

-- A column that already exists must have EXACTLY the EF shape, otherwise abort (it is never altered or dropped here).
IF EXISTS (SELECT 1 FROM sys.columns c WHERE c.object_id = OBJECT_ID(N'dbo.TicketAssignmentHistory') AND c.name = N'PreviousDepartmentId')
   AND NOT EXISTS (SELECT 1 FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
                   WHERE c.object_id = OBJECT_ID(N'dbo.TicketAssignmentHistory') AND c.name = N'PreviousDepartmentId'
                     AND t.name = N'int' AND c.is_nullable = 1 AND c.is_identity = 0 AND c.is_computed = 0)
    THROW 50405, 'ABORTED: TicketAssignmentHistory.PreviousDepartmentId already exists with a different shape. Not touched.', 1;
IF EXISTS (SELECT 1 FROM sys.columns c WHERE c.object_id = OBJECT_ID(N'dbo.TicketInteractions') AND c.name = N'EndReceivedAtUtc')
   AND NOT EXISTS (SELECT 1 FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
                   WHERE c.object_id = OBJECT_ID(N'dbo.TicketInteractions') AND c.name = N'EndReceivedAtUtc'
                     AND t.name = N'datetime2' AND c.is_nullable = 1 AND c.is_identity = 0 AND c.is_computed = 0)
    THROW 50406, 'ABORTED: TicketInteractions.EndReceivedAtUtc already exists with a different shape. Not touched.', 1;

DECLARE @Col1Before bit = CASE WHEN COL_LENGTH(N'dbo.TicketAssignmentHistory', N'PreviousDepartmentId') IS NULL THEN 0 ELSE 1 END;
DECLARE @Col2Before bit = CASE WHEN COL_LENGTH(N'dbo.TicketInteractions', N'EndReceivedAtUtc') IS NULL THEN 0 ELSE 1 END;
DECLARE @Rec1Before bit = CASE WHEN EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId = @M1) THEN 1 ELSE 0 END;
DECLARE @Rec2Before bit = CASE WHEN EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId = @M2) THEN 1 ELSE 0 END;
DECLARE @History bigint = (SELECT COUNT_BIG(*) FROM dbo.__EFMigrationsHistory);
DECLARE @Hist bigint = (SELECT COUNT_BIG(*) FROM dbo.TicketAssignmentHistory);
DECLARE @Inter bigint = (SELECT COUNT_BIG(*) FROM dbo.TicketInteractions);
DECLARE @Tick bigint = (SELECT COUNT_BIG(*) FROM dbo.Tickets);
DECLARE @Assign bigint = (SELECT COUNT_BIG(*) FROM dbo.TicketAssignments);
PRINT N'Pre-state: PreviousDepartmentId present=' + CAST(@Col1Before AS nvarchar(1)) + N', recorded=' + CAST(@Rec1Before AS nvarchar(1))
    + N' | EndReceivedAtUtc present=' + CAST(@Col2Before AS nvarchar(1)) + N', recorded=' + CAST(@Rec2Before AS nvarchar(1)) + N'.';
PRINT N'Rows: TicketAssignmentHistory=' + CAST(@Hist AS nvarchar(20)) + N', TicketInteractions=' + CAST(@Inter AS nvarchar(20))
    + N', Tickets=' + CAST(@Tick AS nvarchar(20)) + N', TicketAssignments=' + CAST(@Assign AS nvarchar(20)) + N', __EFMigrationsHistory=' + CAST(@History AS nvarchar(20)) + N'.';

-- 3. Apply both migrations in order, verify, ALWAYS roll back
BEGIN TRY
    BEGIN TRANSACTION;

    -- Migration 1
    IF @Col1Before = 0
    BEGIN
        ALTER TABLE dbo.TicketAssignmentHistory ADD PreviousDepartmentId int NULL;
        PRINT N'[1] Added TicketAssignmentHistory.PreviousDepartmentId.';
    END ELSE PRINT N'[1] Column already present with the exact shape - not added again.';
    IF @Rec1Before = 0
    BEGIN
        INSERT INTO dbo.__EFMigrationsHistory (MigrationId, ProductVersion) VALUES (@M1, @ProductVersion);
        PRINT N'[1] Recorded migration 1 (inside the transaction).';
    END;

    -- Migration 2 (requires migration 1 recorded - guaranteed by the step above)
    IF @Col2Before = 0
    BEGIN
        ALTER TABLE dbo.TicketInteractions ADD EndReceivedAtUtc datetime2 NULL;
        PRINT N'[2] Added TicketInteractions.EndReceivedAtUtc.';
    END ELSE PRINT N'[2] Column already present with the exact shape - not added again.';
    IF @Rec2Before = 0
    BEGIN
        INSERT INTO dbo.__EFMigrationsHistory (MigrationId, ProductVersion) VALUES (@M2, @ProductVersion);
        PRINT N'[2] Recorded migration 2 (inside the transaction).';
    END;

    -- 4. Validation inside the transaction (any failure -> CATCH -> rollback)
    IF NOT EXISTS (SELECT 1 FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
                   WHERE c.object_id = OBJECT_ID(N'dbo.TicketAssignmentHistory') AND c.name = N'PreviousDepartmentId' AND t.name = N'int' AND c.is_nullable = 1)
        THROW 50420, 'VALIDATION FAILED: TicketAssignmentHistory.PreviousDepartmentId has the wrong shape.', 1;
    IF NOT EXISTS (SELECT 1 FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
                   WHERE c.object_id = OBJECT_ID(N'dbo.TicketInteractions') AND c.name = N'EndReceivedAtUtc' AND t.name = N'datetime2' AND c.is_nullable = 1)
        THROW 50421, 'VALIDATION FAILED: TicketInteractions.EndReceivedAtUtc has the wrong shape.', 1;
    IF (SELECT COUNT(*) FROM dbo.__EFMigrationsHistory WHERE MigrationId IN (@M1, @M2)) <> 2
        THROW 50422, 'VALIDATION FAILED: the two migrations are not each recorded exactly once.', 1;
    IF EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId IN (@M1, @M2) AND ProductVersion <> @ProductVersion)
        THROW 50423, 'VALIDATION FAILED: unexpected ProductVersion on a new history row.', 1;
    IF (SELECT COUNT_BIG(*) FROM dbo.__EFMigrationsHistory) <> @History + (1 - @Rec1Before) + (1 - @Rec2Before)
       OR (SELECT COUNT_BIG(*) FROM dbo.TicketAssignmentHistory) <> @Hist
       OR (SELECT COUNT_BIG(*) FROM dbo.TicketInteractions) <> @Inter
       OR (SELECT COUNT_BIG(*) FROM dbo.Tickets) <> @Tick
       OR (SELECT COUNT_BIG(*) FROM dbo.TicketAssignments) <> @Assign
        THROW 50424, 'VALIDATION FAILED: a row count changed unexpectedly (a live insert during the run is the usual cause - re-run).', 1;

    -- Columns just added must be NULL on every existing row (no backfill). Dynamic SQL: the batch must compile before the columns exist.
    IF @Col1Before = 0 OR @Col2Before = 0
    BEGIN
        DECLARE @f1 bigint = 0, @f2 bigint = 0;
        IF @Col1Before = 0
            EXEC sp_executesql N'SELECT @f = COUNT_BIG(*) FROM dbo.TicketAssignmentHistory WHERE PreviousDepartmentId IS NOT NULL;', N'@f bigint OUTPUT', @f = @f1 OUTPUT;
        IF @Col2Before = 0
            EXEC sp_executesql N'SELECT @f = COUNT_BIG(*) FROM dbo.TicketInteractions WHERE EndReceivedAtUtc IS NOT NULL;', N'@f bigint OUTPUT', @f = @f2 OUTPUT;
        IF @f1 <> 0 OR @f2 <> 0
            THROW 50425, 'VALIDATION FAILED: a new column is not NULL on existing rows (no backfill is allowed).', 1;
    END;

    -- 5. ALWAYS roll back
    ROLLBACK TRANSACTION;
    PRINT N'DRY RUN SUCCESS - both migrations applied in order, every check passed, transaction ROLLED BACK. Nothing was changed.';
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    PRINT N'FAILED - transaction rolled back, nothing was changed.';
    THROW;
END CATCH;

-- 6. Read-only proof that the rollback left the database exactly as found (expect Unchanged = 1 on every row)
SELECT CAST(N'PreviousDepartmentId column' AS nvarchar(60)) AS CheckName,
       CASE WHEN (CASE WHEN COL_LENGTH(N'dbo.TicketAssignmentHistory', N'PreviousDepartmentId') IS NULL THEN 0 ELSE 1 END) = @Col1Before THEN 1 ELSE 0 END AS Unchanged
UNION ALL SELECT N'EndReceivedAtUtc column',
       CASE WHEN (CASE WHEN COL_LENGTH(N'dbo.TicketInteractions', N'EndReceivedAtUtc') IS NULL THEN 0 ELSE 1 END) = @Col2Before THEN 1 ELSE 0 END
UNION ALL SELECT N'__EFMigrationsHistory row count',
       CASE WHEN (SELECT COUNT_BIG(*) FROM dbo.__EFMigrationsHistory) = @History THEN 1 ELSE 0 END
UNION ALL SELECT N'Migration 1 still unrecorded (if it was)',
       CASE WHEN @Rec1Before = 1 OR NOT EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId = @M1) THEN 1 ELSE 0 END
UNION ALL SELECT N'Migration 2 still unrecorded (if it was)',
       CASE WHEN @Rec2Before = 1 OR NOT EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId = @M2) THEN 1 ELSE 0 END
UNION ALL SELECT N'Open transactions = 0', CASE WHEN @@TRANCOUNT = 0 THEN 1 ELSE 0 END;
