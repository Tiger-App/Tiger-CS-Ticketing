-- ############################################################################################
-- ## TigerCS PRODUCTION ONLY.   Database : TigerCS_Ticketing_Prod   (any other database name aborts before any change)
-- ## NOT EXECUTED by Claude. @DryRun = 1 by default (everything is applied, then ROLLED BACK). Review, then set @DryRun = 0 and run once in SSMS.
-- ## Derived unchanged from AddDirectSendInstalments.sql (UAT); only the database guard, the dry-run switch and the checks differ.
-- ############################################################################################
-- Migration : 20261106044011_AddDirectSendInstalments (EF Core 10.0.11)
-- Depends on: 20261105094644_AddCustomerContactDetails and dbo.CollectionsReviewRecords.
-- Effect    : adds ONE nullable column dbo.CollectionsReviewRecords.InstalmentsJson nvarchar(max) NULL and records the migration in dbo.__EFMigrationsHistory.
--             No row is read-modified, altered or deleted; no table is created or dropped. Existing records keep NULL (only Direct Send records use it), so Manual
--             and Auto behave exactly as before. This script does NOT enable sending: Direct Send additionally needs Collections:GenesysOutbound:ContactListIds:DirectSend.
-- Idempotent: safe to run repeatedly (guarded by __EFMigrationsHistory). Runs in one transaction.
-- ############################################################################################
SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @DryRun bit = 1;   -- 1 = apply, then ROLL BACK (default).  0 = COMMIT.
IF DB_NAME() <> N'TigerCS_Ticketing_Prod'
    THROW 50100, N'ABORTED: this script only runs against database [TigerCS_Ticketing_Prod]. Nothing was changed.', 1;
IF OBJECT_ID(N'dbo.__EFMigrationsHistory', N'U') IS NULL
    THROW 50101, N'ABORTED: dbo.__EFMigrationsHistory is missing - wrong database?', 1;
IF NOT EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId = N'20261105094644_AddCustomerContactDetails')
    THROW 50102, N'ABORTED: 20261105094644_AddCustomerContactDetails is not recorded; apply earlier migrations first.', 1;
IF OBJECT_ID(N'dbo.CollectionsReviewRecords', N'U') IS NULL
    THROW 50103, N'ABORTED: dbo.CollectionsReviewRecords is missing; apply AddCollectionsReviewAndGenesysDispatch first.', 1;
-- (no GO between the guards and the migration: a failed guard must abort the whole batch)

BEGIN TRY
    BEGIN TRANSACTION;
    DECLARE @RowsBefore bigint = (SELECT COUNT_BIG(*) FROM dbo.CollectionsReviewRecords);

    IF NOT EXISTS (SELECT * FROM [__EFMigrationsHistory] WHERE [MigrationId] = N'20261106044011_AddDirectSendInstalments')
    BEGIN
        IF COL_LENGTH(N'dbo.CollectionsReviewRecords', N'InstalmentsJson') IS NULL
            ALTER TABLE [CollectionsReviewRecords] ADD [InstalmentsJson] nvarchar(max) NULL;
        INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion]) VALUES (N'20261106044011_AddDirectSendInstalments', N'10.0.11');
    END;

    IF NOT EXISTS (SELECT 1 FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
                   WHERE c.object_id = OBJECT_ID(N'dbo.CollectionsReviewRecords') AND c.name = N'InstalmentsJson' AND t.name = N'nvarchar' AND c.max_length = -1 AND c.is_nullable = 1)
        THROW 50110, 'VALIDATION FAILED: InstalmentsJson is missing or is not nvarchar(max) NULL.', 1;
    IF (SELECT COUNT_BIG(*) FROM dbo.CollectionsReviewRecords) <> @RowsBefore
        THROW 50111, 'VALIDATION FAILED: dbo.CollectionsReviewRecords changed its row count.', 1;
    IF EXISTS (SELECT 1 FROM dbo.CollectionsReviewRecords WHERE InstalmentsJson IS NOT NULL)
        THROW 50112, 'VALIDATION FAILED: existing records must keep a NULL InstalmentsJson.', 1;

    IF @DryRun = 1
    BEGIN
        ROLLBACK TRANSACTION;
        PRINT 'DRY RUN SUCCESS - all checks passed inside the transaction, which was ROLLED BACK. Nothing was changed. Set @DryRun = 0 to commit.';
    END
    ELSE
    BEGIN
        COMMIT TRANSACTION;
        PRINT 'COMMITTED. Migration 20261106044011_AddDirectSendInstalments applied to TigerCS_Ticketing_Prod.';
    END;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    PRINT 'FAILED - transaction rolled back, nothing was changed.';
    THROW;
END CATCH;

/* ---------------------------------------------------------------------------------------------
   POST-DEPLOY VERIFICATION (read-only; run separately after a COMMITTED run)
   1. Expect 1 row (nvarchar, max_length -1, is_nullable 1):
        SELECT c.name, t.name AS type, c.max_length, c.is_nullable FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
        WHERE c.object_id = OBJECT_ID(N'dbo.CollectionsReviewRecords') AND c.name = N'InstalmentsJson';
   2. Expect exactly 1 row for the migration and none newer:
        SELECT MigrationId, ProductVersion FROM dbo.__EFMigrationsHistory WHERE MigrationId >= N'20261105094644_AddCustomerContactDetails' ORDER BY MigrationId;
   3. Expect 0 until a Direct Send review refresh has run: SELECT COUNT_BIG(*) FROM dbo.CollectionsReviewRecords WHERE InstalmentsJson IS NOT NULL;
   ROLLBACK: ALTER TABLE dbo.CollectionsReviewRecords DROP COLUMN InstalmentsJson; DELETE FROM dbo.__EFMigrationsHistory WHERE MigrationId = N'20261106044011_AddDirectSendInstalments';
   Safe: the column is derived data; a review refresh rebuilds it. The previous build ignores it.
--------------------------------------------------------------------------------------------- */
