-- ############################################################################################
-- ## TigerCS PRODUCTION ONLY.   Database: TigerCS_Ticketing_Prod (any other name aborts before any change)
-- ## NOT EXECUTED by Claude. Review, then run yourself in SSMS (SQLCMD mode OFF).
-- ############################################################################################
-- Migration : 20261105094644_AddCustomerContactDetails (EF Core 10.0.11)
--             Taken verbatim from src/TigerCS.Infrastructure/Persistence/Migrations/20261105094644_AddCustomerContactDetails.cs
-- Depends on: 20261104130835_AddCollectionsTowerAssignments (must already be in __EFMigrationsHistory).
-- Effect    : creates ONE new table dbo.CustomerContactDetails (+ unique index on PhoneKey) and records the migration.
--             It holds the customer name / e-mail an agent typed in New Ticket or Customer Details, one row per canonical phone.
--             No existing table, column, row, user, role or permission is modified. An empty table = every customer is shown
--             exactly as before (the display falls back to the ticket / CRM / PACT snapshots).
-- Idempotent: re-running after success changes nothing. A pre-existing table with a different shape aborts untouched.
--
-- HOW TO RUN: leave @DryRun = 1 and run (applies, verifies, ROLLS BACK); read the messages; set @DryRun = 0 and run again to COMMIT.
-- Run as ONE batch (no GO).
--
-- ROLLBACK (only while the table is still unused):
--   BEGIN TRAN;
--     DROP TABLE dbo.CustomerContactDetails;
--     DELETE FROM dbo.__EFMigrationsHistory WHERE MigrationId = N'20261105094644_AddCustomerContactDetails';
--   COMMIT;
-- ############################################################################################
SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @DryRun bit = 1;   -- 1 = apply, verify, ROLL BACK (default).  0 = apply, verify, COMMIT.

DECLARE @MigrationId    nvarchar(150) = N'20261105094644_AddCustomerContactDetails';
DECLARE @Previous       nvarchar(150) = N'20261104130835_AddCollectionsTowerAssignments';
DECLARE @ProductVersion nvarchar(32)  = N'10.0.11';
DECLARE @ExpectedDb     sysname       = N'TigerCS_Ticketing_Prod';
DECLARE @msg nvarchar(2000);

IF DB_NAME() IS NULL OR DB_NAME() <> @ExpectedDb
BEGIN
    SET @msg = N'ABORTED: connected to database [' + ISNULL(DB_NAME(), N'?') + N']; this script only runs against [' + @ExpectedDb + N']. Nothing was changed.';
    THROW 50100, @msg, 1;
END;
PRINT N'Guard passed: database [' + DB_NAME() + N'], DryRun=' + CAST(@DryRun AS nvarchar(1)) + N'.';

IF OBJECT_ID(N'dbo.__EFMigrationsHistory', N'U') IS NULL
    THROW 50101, 'ABORTED: dbo.__EFMigrationsHistory is missing - wrong database?', 1;
IF NOT EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId = @Previous)
    THROW 50102, 'ABORTED: 20261104130835_AddCollectionsTowerAssignments is not recorded; apply it first.', 1;
IF EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId > @MigrationId)
    THROW 50107, 'ABORTED: __EFMigrationsHistory contains a migration newer than this one. Investigate manually.', 1;

-- The exact column shape EF defines (max_length is in BYTES: nvarchar(32) = 64, (200) = 400, (256) = 512).
DECLARE @Expected TABLE (Col sysname, TypeName sysname, MaxLen smallint, Nullable bit, IsIdentity bit);
INSERT @Expected VALUES
    (N'CustomerContactDetailId', N'bigint',           8,   0, 1),
    (N'PhoneKey',                N'nvarchar',         64,  0, 0),
    (N'Name',                    N'nvarchar',         400, 0, 0),
    (N'Email',                   N'nvarchar',         512, 1, 0),
    (N'CreatedAtUtc',            N'datetime2',        8,   0, 0),
    (N'UpdatedAtUtc',            N'datetime2',        8,   0, 0),
    (N'UpdatedByEmployeeId',     N'uniqueidentifier', 16,  0, 0),
    (N'RowVersion',              N'timestamp',        8,   0, 0);

-- An existing table of this name must have exactly the EF shape; otherwise stop and let a human look.
IF OBJECT_ID(N'dbo.CustomerContactDetails', N'U') IS NOT NULL
BEGIN
    DECLARE @bad nvarchar(max) = N'';
    SELECT @bad = @bad + e.Col + N' ' FROM @Expected e
      WHERE NOT EXISTS (
        SELECT 1 FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
        WHERE c.object_id = OBJECT_ID(N'dbo.CustomerContactDetails') AND c.name = e.Col
          AND t.name = e.TypeName AND c.max_length = e.MaxLen AND c.is_nullable = e.Nullable AND c.is_identity = e.IsIdentity);
    IF LEN(@bad) > 0 OR (SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.CustomerContactDetails')) <> 8
    BEGIN
        SET @msg = N'ABORTED: dbo.CustomerContactDetails already exists with a different shape (check: ' + @bad + N'). Not touched.';
        THROW 50106, @msg, 1;
    END;
END;

DECLARE @TicketsBefore bigint = (SELECT COUNT_BIG(*) FROM dbo.Tickets);
DECLARE @HistoryBefore bigint = (SELECT COUNT_BIG(*) FROM dbo.__EFMigrationsHistory);

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.CustomerContactDetails', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.CustomerContactDetails (
            CustomerContactDetailId bigint NOT NULL IDENTITY,
            PhoneKey nvarchar(32) NOT NULL,
            Name nvarchar(200) NOT NULL,
            Email nvarchar(256) NULL,
            CreatedAtUtc datetime2 NOT NULL,
            UpdatedAtUtc datetime2 NOT NULL,
            UpdatedByEmployeeId uniqueidentifier NOT NULL,
            RowVersion rowversion NOT NULL,
            CONSTRAINT PK_CustomerContactDetails PRIMARY KEY (CustomerContactDetailId)
        );
        PRINT 'Created table dbo.CustomerContactDetails.';
    END
    ELSE PRINT 'Table dbo.CustomerContactDetails already exists with the expected columns - not recreated.';

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.CustomerContactDetails') AND name = N'IX_CustomerContactDetails_PhoneKey')
        CREATE UNIQUE INDEX IX_CustomerContactDetails_PhoneKey ON dbo.CustomerContactDetails (PhoneKey);

    IF NOT EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId = @MigrationId)
        INSERT INTO dbo.__EFMigrationsHistory (MigrationId, ProductVersion) VALUES (@MigrationId, @ProductVersion);

    -- Validation (inside the transaction: any failure rolls everything back)
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.CustomerContactDetails')
                   AND name = N'PK_CustomerContactDetails' AND is_primary_key = 1 AND is_disabled = 0)
        THROW 50110, 'VALIDATION FAILED: primary key PK_CustomerContactDetails missing.', 1;
    IF NOT EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                   WHERE ic.object_id = OBJECT_ID(N'dbo.CustomerContactDetails') AND ic.index_id = INDEXPROPERTY(ic.object_id, N'PK_CustomerContactDetails', 'IndexId')
                   AND c.name = N'CustomerContactDetailId')
        THROW 50115, 'VALIDATION FAILED: primary key is not on CustomerContactDetailId.', 1;
    IF EXISTS (SELECT 1 FROM @Expected e WHERE NOT EXISTS (
            SELECT 1 FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
            WHERE c.object_id = OBJECT_ID(N'dbo.CustomerContactDetails') AND c.name = e.Col
              AND t.name = e.TypeName AND c.max_length = e.MaxLen AND c.is_nullable = e.Nullable AND c.is_identity = e.IsIdentity))
       OR (SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.CustomerContactDetails')) <> 8
        THROW 50116, 'VALIDATION FAILED: dbo.CustomerContactDetails columns differ from the EF model.', 1;
    IF NOT EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                   WHERE ic.object_id = OBJECT_ID(N'dbo.CustomerContactDetails') AND ic.index_id = INDEXPROPERTY(ic.object_id, N'IX_CustomerContactDetails_PhoneKey', 'IndexId')
                   AND c.name = N'PhoneKey')
        THROW 50117, 'VALIDATION FAILED: unique index is not on PhoneKey.', 1;
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.CustomerContactDetails')
                   AND name = N'IX_CustomerContactDetails_PhoneKey' AND is_unique = 1 AND is_disabled = 0)
        THROW 50111, 'VALIDATION FAILED: unique index IX_CustomerContactDetails_PhoneKey missing.', 1;
    IF (SELECT COUNT(*) FROM dbo.__EFMigrationsHistory WHERE MigrationId = @MigrationId) <> 1
        THROW 50113, 'VALIDATION FAILED: migration not recorded exactly once.', 1;
    IF (SELECT COUNT_BIG(*) FROM dbo.Tickets) <> @TicketsBefore
        THROW 50114, 'VALIDATION FAILED: dbo.Tickets changed its row count.', 1;

    IF @DryRun = 1
    BEGIN
        ROLLBACK TRANSACTION;
        PRINT 'DRY RUN SUCCESS - all checks passed inside the transaction, which was ROLLED BACK. Nothing was changed. Set @DryRun = 0 to commit.';
    END
    ELSE
    BEGIN
        COMMIT TRANSACTION;
        PRINT 'COMMITTED. Migration 20261105094644_AddCustomerContactDetails applied to TigerCS_Ticketing_Prod.';
    END;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    PRINT 'FAILED - transaction rolled back, nothing was changed.';
    THROW;
END CATCH;

/* ---------------------------------------------------------------------------------------------
   POST-DEPLOY VERIFICATION (read-only; run separately after a COMMITTED run)
   1. Expect 1 row, Present = 1:
        SELECT OBJECT_ID(N'dbo.CustomerContactDetails', N'U') IS NOT NULL AS Present;
   2. Expect 8 columns, shape exactly as EF (max_length is bytes):
        SELECT c.column_id, c.name, t.name AS type, c.max_length, c.is_nullable, c.is_identity
        FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
        WHERE c.object_id = OBJECT_ID(N'dbo.CustomerContactDetails') ORDER BY c.column_id;
   3. Expect PK_CustomerContactDetails (primary key) and IX_CustomerContactDetails_PhoneKey (unique):
        SELECT name, is_primary_key, is_unique FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.CustomerContactDetails') AND name IS NOT NULL;
   4. Expect exactly 1 row, and no row newer than it:
        SELECT MigrationId, ProductVersion FROM dbo.__EFMigrationsHistory WHERE MigrationId >= N'20261104130835_AddCollectionsTowerAssignments' ORDER BY MigrationId;
   5. Expect 0 rows (table starts empty; every customer then shows exactly as before):
        SELECT COUNT_BIG(*) FROM dbo.CustomerContactDetails;
   6. Re-run Verify_TigerCS_PROD_PendingMigrations_ReadOnly.sql: no pending migration, CustomerContactDetails PRESENT.
   7. Application: GET /Customers and GET /Customers/{key} load (the Api log has no "Invalid object name").

   ROLLBACK is the block in the header. It is safe only while the table is empty/unused; once agents have saved names or e-mails,
   dropping it DELETES that data - restore from the pre-deploy backup instead, or leave the table (the previous build ignores it).
--------------------------------------------------------------------------------------------- */
