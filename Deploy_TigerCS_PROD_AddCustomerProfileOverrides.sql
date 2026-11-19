-- ############################################################################################
-- ## TigerCS PRODUCTION ONLY.
-- ## Server   : 10.10.10.124
-- ## Database : TigerCS_Ticketing_Prod   (any other database name aborts before any change)
-- ## NOT EXECUTED by Claude. Review, then run yourself in SSMS (SQLCMD mode OFF).
-- ############################################################################################
-- Migration : 20261118120000_AddCustomerProfileOverrides (EF Core 10.0.11)
--             Taken from src/TigerCS.Infrastructure/Persistence/Migrations/
--             20261118120000_AddCustomerProfileOverrides.cs (CreateTable + CreateIndex), introduced in PR #285.
-- Fixes     : SqlException 208 "Invalid object name 'CustomerProfileOverrides'" (Customers directory edit / profile).
-- Depends on: nothing (no foreign keys). Standalone create of ONE new table.
-- Effect    : creates dbo.CustomerProfileOverrides (PK, unique index on CustomerKey) and records the
--             migration in dbo.__EFMigrationsHistory. No existing table, column, row or index is touched.
-- Idempotent: re-running after success changes nothing. A pre-existing table with a different shape aborts untouched.
--
-- HOW TO RUN: leave @DryRun = 1 first (applies, verifies, ROLLS BACK), read the Messages, then set @DryRun = 0 to COMMIT.
-- Run as ONE batch (no GO).
--
-- ROLLBACK (only while the table is still empty/unused):
--   BEGIN TRAN;
--     DROP TABLE dbo.CustomerProfileOverrides;
--     DELETE FROM dbo.__EFMigrationsHistory WHERE MigrationId = N'20261118120000_AddCustomerProfileOverrides';
--   COMMIT;
-- ############################################################################################
SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @DryRun bit = 1;   -- 1 = apply, verify, ROLL BACK (default).  0 = apply, verify, COMMIT.
DECLARE @Committed bit = 0;
DECLARE @MigrationId    nvarchar(150) = N'20261118120000_AddCustomerProfileOverrides';
DECLARE @Previous       nvarchar(150) = N'20261117101035_AddQueueingIntegration';
DECLARE @ProductVersion nvarchar(32)  = N'10.0.11';
DECLARE @ExpectedDb     sysname       = N'TigerCS_Ticketing_Prod';
DECLARE @msg nvarchar(2000);

-- 1. Database-name protection
IF DB_NAME() IS NULL OR DB_NAME() <> @ExpectedDb
BEGIN
    SET @msg = N'ABORTED: connected to [' + ISNULL(DB_NAME(), N'?') + N'] on [' + CAST(@@SERVERNAME AS nvarchar(128))
             + N']; this script only runs against [' + @ExpectedDb + N']. Nothing was changed.';
    THROW 50200, @msg, 1;
END;
PRINT N'Guard passed: server [' + CAST(@@SERVERNAME AS nvarchar(128)) + N'], database [' + DB_NAME() + N'], DryRun=' + CAST(@DryRun AS nvarchar(1));

-- 2. Pre-checks (read-only)
IF OBJECT_ID(N'dbo.__EFMigrationsHistory', N'U') IS NULL
    THROW 50201, 'ABORTED: dbo.__EFMigrationsHistory is missing - wrong database?', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId = @Previous)
    PRINT N'WARNING: ' + @Previous + N' is not recorded. This table has no dependency on it, so the script continues - but run Verify_TigerCS_PROD_AllMigrations_ReadOnly.sql to find other missing migrations.';

IF OBJECT_ID(N'dbo.CustomerProfileOverrides', N'U') IS NOT NULL
BEGIN
    DECLARE @bad nvarchar(max) = N'';
    SELECT @bad = @bad + e.Col + N' ' FROM (VALUES
        (N'CustomerProfileOverrideId', N'bigint',           8,   0, 1),
        (N'CustomerKey',               N'nvarchar',         600, 0, 0),   -- nvarchar(300)
        (N'IdentityKind',              N'int',              4,   0, 0),
        (N'CrmBuyerCustomerId',        N'int',              4,   1, 0),
        (N'ExternalSource',            N'nvarchar',         400, 1, 0),   -- nvarchar(200)
        (N'ExternalCustomerId',        N'nvarchar',         400, 1, 0),
        (N'IdentityPhone',             N'nvarchar',         64,  1, 0),   -- nvarchar(32)
        (N'PhoneNumber',               N'nvarchar',         64,  1, 0),
        (N'PhoneLocation',             N'tinyint',          1,   1, 0),
        (N'ProjectName',               N'nvarchar',         400, 1, 0),
        (N'UnitNumber',                N'nvarchar',         200, 1, 0),   -- nvarchar(100)
        (N'CreatedAtUtc',              N'datetime2',        8,   0, 0),
        (N'UpdatedAtUtc',              N'datetime2',        8,   0, 0),
        (N'UpdatedByEmployeeId',       N'uniqueidentifier', 16,  0, 0),
        (N'RowVersion',                N'timestamp',        8,   0, 0)) AS e(Col, TypeName, MaxLen, Nullable, IsIdentity)
      WHERE NOT EXISTS (
        SELECT 1 FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
        WHERE c.object_id = OBJECT_ID(N'dbo.CustomerProfileOverrides') AND c.name = e.Col
          AND t.name = e.TypeName AND c.max_length = e.MaxLen AND c.is_nullable = e.Nullable AND c.is_identity = e.IsIdentity);
    IF LEN(@bad) > 0 OR (SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.CustomerProfileOverrides')) <> 15
    BEGIN
        SET @msg = N'ABORTED: dbo.CustomerProfileOverrides already exists with a different shape (check: ' + @bad + N'). Not touched.';
        THROW 50202, @msg, 1;
    END;
END;

DECLARE @HistoryBefore bigint = (SELECT COUNT_BIG(*) FROM dbo.__EFMigrationsHistory);
DECLARE @AlreadyRecorded bit = CASE WHEN EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId = @MigrationId) THEN 1 ELSE 0 END;
DECLARE @TableExisted bit = CASE WHEN OBJECT_ID(N'dbo.CustomerProfileOverrides', N'U') IS NOT NULL THEN 1 ELSE 0 END;
PRINT N'Pre-state: table exists=' + CAST(@TableExisted AS nvarchar(1)) + N', migration recorded=' + CAST(@AlreadyRecorded AS nvarchar(1))
    + N', history rows=' + CAST(@HistoryBefore AS nvarchar(20));

-- 3. Apply (single transaction, additive only)
BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.CustomerProfileOverrides', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.CustomerProfileOverrides (
            CustomerProfileOverrideId bigint NOT NULL IDENTITY,
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
        PRINT 'Created table dbo.CustomerProfileOverrides.';
    END
    ELSE PRINT 'Table already exists with the expected columns - not recreated.';

    IF NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE parent_object_id = OBJECT_ID(N'dbo.CustomerProfileOverrides') AND type = 'PK')
        ALTER TABLE dbo.CustomerProfileOverrides ADD CONSTRAINT PK_CustomerProfileOverrides PRIMARY KEY (CustomerProfileOverrideId);

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.CustomerProfileOverrides') AND name = N'IX_CustomerProfileOverrides_CustomerKey')
        CREATE UNIQUE INDEX IX_CustomerProfileOverrides_CustomerKey ON dbo.CustomerProfileOverrides (CustomerKey);

    IF NOT EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId = @MigrationId)
    BEGIN
        INSERT INTO dbo.__EFMigrationsHistory (MigrationId, ProductVersion) VALUES (@MigrationId, @ProductVersion);
        PRINT 'Recorded migration in __EFMigrationsHistory.';
    END
    ELSE PRINT 'Migration already recorded - not inserted again.';

    -- 4. Validation (inside the transaction: any failure rolls everything back)
    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes i
        JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
        JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
        WHERE i.object_id = OBJECT_ID(N'dbo.CustomerProfileOverrides') AND i.is_primary_key = 1
          AND i.name = N'PK_CustomerProfileOverrides' AND c.name = N'CustomerProfileOverrideId'
          AND (SELECT COUNT(*) FROM sys.index_columns x WHERE x.object_id = i.object_id AND x.index_id = i.index_id) = 1)
        THROW 50210, 'VALIDATION FAILED: PK_CustomerProfileOverrides missing or on the wrong column.', 1;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes i
        JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
        JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
        WHERE i.object_id = OBJECT_ID(N'dbo.CustomerProfileOverrides') AND i.name = N'IX_CustomerProfileOverrides_CustomerKey'
          AND i.is_unique = 1 AND i.is_disabled = 0 AND c.name = N'CustomerKey'
          AND (SELECT COUNT(*) FROM sys.index_columns x WHERE x.object_id = i.object_id AND x.index_id = i.index_id) = 1)
        THROW 50211, 'VALIDATION FAILED: unique index IX_CustomerProfileOverrides_CustomerKey missing or wrong.', 1;

    IF (SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.CustomerProfileOverrides')) <> 15
        THROW 50212, 'VALIDATION FAILED: expected 15 columns.', 1;

    IF (SELECT COUNT(*) FROM dbo.__EFMigrationsHistory WHERE MigrationId = @MigrationId AND ProductVersion = @ProductVersion) <> 1
        THROW 50213, 'VALIDATION FAILED: migration not recorded exactly once with ProductVersion 10.0.11.', 1;
    IF (SELECT COUNT_BIG(*) FROM dbo.__EFMigrationsHistory) <> @HistoryBefore + CASE WHEN @AlreadyRecorded = 1 THEN 0 ELSE 1 END
        THROW 50214, 'VALIDATION FAILED: unexpected change in __EFMigrationsHistory row count.', 1;
    IF @TableExisted = 0 AND EXISTS (SELECT 1 FROM dbo.CustomerProfileOverrides)
        THROW 50215, 'VALIDATION FAILED: newly created table is not empty.', 1;

    IF @DryRun = 1
    BEGIN
        ROLLBACK TRANSACTION;
        PRINT 'DRY RUN SUCCESS - all checks passed, transaction ROLLED BACK. Nothing changed. Set @DryRun = 0 to commit.';
    END
    ELSE
    BEGIN
        COMMIT TRANSACTION;
        SET @Committed = 1;
        PRINT 'COMMITTED. Migration 20261118120000_AddCustomerProfileOverrides applied to TigerCS_Ticketing_Prod.';
    END;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    PRINT 'FAILED - transaction rolled back, nothing was changed.';
    THROW;
END CATCH;

-- 5. Post-commit verification (read-only; only after a real commit)
IF @DryRun = 0 AND @Committed = 1
BEGIN
    SELECT MigrationId, ProductVersion FROM dbo.__EFMigrationsHistory WHERE MigrationId = @MigrationId;
    SELECT c.column_id, c.name AS ColumnName, t.name AS TypeName, c.max_length, c.is_nullable, c.is_identity
    FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
    WHERE c.object_id = OBJECT_ID(N'dbo.CustomerProfileOverrides') ORDER BY c.column_id;      -- expect 15
    SELECT name, type_desc, is_primary_key, is_unique FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.CustomerProfileOverrides') ORDER BY index_id;           -- PK + unique IX on CustomerKey
    SELECT COUNT_BIG(*) AS OverrideRows FROM dbo.CustomerProfileOverrides;                    -- expect 0
END;
