-- ############################################################################################
-- ## TigerCS PRODUCTION ONLY.
-- ## Server   : 10.10.10.124
-- ## Database : TigerCS_Ticketing_Prod   (any other database name aborts before any change)
-- ## NOT EXECUTED by Claude. Review, then run yourself in SSMS (SQLCMD mode OFF).
-- ############################################################################################
-- Migration : 20261119081103_AddCustomerProfileAuditAndProperties (EF Core 10.0.11)
--             Taken from src/TigerCS.Infrastructure/Persistence/Migrations/
--             20261119081103_AddCustomerProfileAuditAndProperties.cs.
-- Depends on: 20261118120000_AddCustomerProfileOverrides (dbo.CustomerProfileOverrides must exist and be recorded in
--             __EFMigrationsHistory - apply Deploy_TigerCS_PROD_AddCustomerProfileOverrides.sql first if it is not).
-- Effect    :  1. dbo.CustomerProfileOverrides gains 10 columns: CustomerProfileId (the customer's stable identifier, NOT NULL, unique)
--                 and nine NULL profile-detail columns (FullName, Email, LandlineNumber, AdditionalPhones, Nationality, Country,
--                 Emirate, Location, PreferredLanguage). Every EXISTING row is given its own new CustomerProfileId (NEWID()).
--              2. creates dbo.CustomerProfileChanges - the append-only customer change history (PK, 4 indexes incl. the unique
--                 ChangeKey that makes retries idempotent) - with a trigger that rejects UPDATE and DELETE.
--              3. creates dbo.CustomerPropertyAssociations - properties added to a customer by hand (PK, unique index on
--                 CustomerProfileId + ProjectName + UnitNumber, index on CustomerKey).
--              4. records the migration in dbo.__EFMigrationsHistory.
--             No existing row is changed except CustomerProfileOverrides.CustomerProfileId; no table, column, index or row is dropped.
--             No ticket, customer, CRM or permission data is touched. The application then also asks for the new permission
--             Customers.ViewHistory, which needs no SQL (it is a code-defined permission: System Administrator and CS Manager hold it).
-- Idempotent: re-running after success changes nothing (everything is checked before it is created; the history row is only verified).
--             A partially applied state is completed, never recreated. A pre-existing object of a different shape aborts untouched.
--
-- HOW TO RUN (SSMS connected to 10.10.10.124, database TigerCS_Ticketing_Prod):
--   1. Leave @DryRun = 1 and run. Everything is applied inside a transaction, verified, then ROLLED BACK.
--   2. Read Messages + result sets: guard passed, "DRY RUN SUCCESS". A dry run changes nothing.
--   3. Set @DryRun = 0 and run again to COMMIT. The same checks run before the commit, then post-commit verification result
--      sets (section 6) show the committed state.
-- Run as ONE batch. There is intentionally no GO in this script.
--
-- ROLLBACK (only if ever needed, and only while the new tables hold no data you want to keep; dropping the table drops its trigger):
--   BEGIN TRAN;
--     DROP TABLE dbo.CustomerPropertyAssociations;
--     DROP TABLE dbo.CustomerProfileChanges;
--     DROP INDEX IX_CustomerProfileOverrides_CustomerProfileId ON dbo.CustomerProfileOverrides;
--     -- the column default has a system-generated name; drop it first
--     DECLARE @df sysname = (SELECT dc.name FROM sys.default_constraints dc JOIN sys.columns c ON c.object_id = dc.parent_object_id AND c.column_id = dc.parent_column_id
--                            WHERE dc.parent_object_id = OBJECT_ID(N'dbo.CustomerProfileOverrides') AND c.name = N'CustomerProfileId');
--     EXEC(N'ALTER TABLE dbo.CustomerProfileOverrides DROP CONSTRAINT ' + @df);
--     ALTER TABLE dbo.CustomerProfileOverrides DROP COLUMN AdditionalPhones, Country, CustomerProfileId, Email, Emirate, FullName,
--                                                             LandlineNumber, Location, Nationality, PreferredLanguage;
--     DELETE FROM dbo.__EFMigrationsHistory WHERE MigrationId = N'20261119081103_AddCustomerProfileAuditAndProperties';
--   COMMIT;
-- ############################################################################################

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @DryRun bit = 1;   -- 1 = apply, verify, ROLL BACK (default).  0 = apply, verify, COMMIT.

DECLARE @Committed bit = 0;  -- set to 1 only after COMMIT TRANSACTION succeeds

DECLARE @MigrationId    nvarchar(150) = N'20261119081103_AddCustomerProfileAuditAndProperties';
DECLARE @Previous       nvarchar(150) = N'20261118120000_AddCustomerProfileOverrides';
DECLARE @ProductVersion nvarchar(32)  = N'10.0.11';
DECLARE @ExpectedDb     sysname       = N'TigerCS_Ticketing_Prod';
DECLARE @msg nvarchar(2000);

-- ---------------------------------------------------------------------------------------------
-- 1. Database-name protection (first statement that can fail; nothing has been touched yet)
-- ---------------------------------------------------------------------------------------------
IF DB_NAME() IS NULL OR DB_NAME() <> @ExpectedDb
BEGIN
    SET @msg = N'ABORTED: connected to database [' + ISNULL(DB_NAME(), N'?') + N'] on server [' + CAST(@@SERVERNAME AS nvarchar(128))
             + N']; this Production script only runs against [' + @ExpectedDb + N']. Nothing was changed.';
    THROW 50300, @msg, 1;
END;
SET @msg = N'Guard passed: server [' + CAST(@@SERVERNAME AS nvarchar(128)) + N'], database [' + DB_NAME()
         + N'], DryRun=' + CAST(@DryRun AS nvarchar(1)) + N'. (Expected host 10.10.10.124 - confirm in the SSMS connection bar.)';
PRINT @msg;

-- ---------------------------------------------------------------------------------------------
-- 2. Dependency / schema pre-checks (read-only)
-- ---------------------------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.__EFMigrationsHistory', N'U') IS NULL
    THROW 50301, 'ABORTED: dbo.__EFMigrationsHistory is missing - wrong database?', 1;

IF OBJECT_ID(N'dbo.CustomerProfileOverrides', N'U') IS NULL
    THROW 50302, 'ABORTED: dbo.CustomerProfileOverrides does not exist. Apply Deploy_TigerCS_PROD_AddCustomerProfileOverrides.sql (migration 20261118120000_AddCustomerProfileOverrides) first.', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId = @Previous)
    THROW 50303, 'ABORTED: 20261118120000_AddCustomerProfileOverrides is not recorded in __EFMigrationsHistory; apply it first.', 1;

-- Anything already present must have exactly the EF shape; otherwise stop and let a human look.
DECLARE @Expected TABLE (TableName sysname, Col sysname, TypeName sysname, MaxLen smallint, Nullable bit, IsIdentity bit);
INSERT @Expected VALUES
    (N'CustomerProfileOverrides',     N'AdditionalPhones',            N'nvarchar',         800, 1, 0),
    (N'CustomerProfileOverrides',     N'Country',                     N'nvarchar',         200, 1, 0),
    (N'CustomerProfileOverrides',     N'CustomerProfileId',           N'uniqueidentifier', 16,  0, 0),
    (N'CustomerProfileOverrides',     N'Email',                       N'nvarchar',         512, 1, 0),
    (N'CustomerProfileOverrides',     N'Emirate',                     N'nvarchar',         200, 1, 0),
    (N'CustomerProfileOverrides',     N'FullName',                    N'nvarchar',         400, 1, 0),
    (N'CustomerProfileOverrides',     N'LandlineNumber',              N'nvarchar',         64,  1, 0),
    (N'CustomerProfileOverrides',     N'Location',                    N'nvarchar',         400, 1, 0),
    (N'CustomerProfileOverrides',     N'Nationality',                 N'nvarchar',         200, 1, 0),
    (N'CustomerProfileOverrides',     N'PreferredLanguage',           N'nvarchar',         200, 1, 0),
    (N'CustomerProfileChanges',       N'CustomerProfileChangeId',     N'bigint',           8,   0, 1),
    (N'CustomerProfileChanges',       N'CustomerProfileId',           N'uniqueidentifier', 16,  0, 0),
    (N'CustomerProfileChanges',       N'CustomerKey',                 N'nvarchar',         600, 0, 0),
    (N'CustomerProfileChanges',       N'FieldName',                   N'nvarchar',         200, 0, 0),
    (N'CustomerProfileChanges',       N'OldValue',                    N'nvarchar',         2000,1, 0),
    (N'CustomerProfileChanges',       N'NewValue',                    N'nvarchar',         2000,1, 0),
    (N'CustomerProfileChanges',       N'ModifiedByEmployeeId',        N'uniqueidentifier', 16,  0, 0),
    (N'CustomerProfileChanges',       N'ModifiedByName',              N'nvarchar',         400, 0, 0),
    (N'CustomerProfileChanges',       N'ModifiedAtUtc',               N'datetime2',        8,   0, 0),
    (N'CustomerProfileChanges',       N'Source',                      N'nvarchar',         100, 0, 0),
    (N'CustomerProfileChanges',       N'ChangeReason',                N'nvarchar',         1000,1, 0),
    (N'CustomerProfileChanges',       N'RelatedTicketId',             N'bigint',           8,   1, 0),
    (N'CustomerProfileChanges',       N'CorrelationId',               N'uniqueidentifier', 16,  0, 0),
    (N'CustomerProfileChanges',       N'ChangeKey',                   N'nchar',            128, 0, 0),
    (N'CustomerPropertyAssociations', N'CustomerPropertyAssociationId', N'bigint',         8,   0, 1),
    (N'CustomerPropertyAssociations', N'CustomerKey',                 N'nvarchar',         600, 0, 0),
    (N'CustomerPropertyAssociations', N'CustomerProfileId',           N'uniqueidentifier', 16,  0, 0),
    (N'CustomerPropertyAssociations', N'ProjectName',                 N'nvarchar',         400, 0, 0),
    (N'CustomerPropertyAssociations', N'UnitNumber',                  N'nvarchar',         200, 0, 0),
    (N'CustomerPropertyAssociations', N'Relationship',                N'tinyint',          1,   0, 0),
    (N'CustomerPropertyAssociations', N'CreatedAtUtc',                N'datetime2',        8,   0, 0),
    (N'CustomerPropertyAssociations', N'CreatedByEmployeeId',         N'uniqueidentifier', 16,  0, 0),
    (N'CustomerPropertyAssociations', N'UpdatedAtUtc',                N'datetime2',        8,   0, 0),
    (N'CustomerPropertyAssociations', N'UpdatedByEmployeeId',         N'uniqueidentifier', 16,  0, 0),
    (N'CustomerPropertyAssociations', N'RowVersion',                  N'timestamp',        8,   0, 0);

DECLARE @bad nvarchar(max) = N'';
-- a column that EXISTS but has the wrong shape
SELECT @bad = @bad + e.TableName + N'.' + e.Col + N' ' FROM @Expected e
JOIN sys.columns c ON c.object_id = OBJECT_ID(N'dbo.' + e.TableName) AND c.name = e.Col
JOIN sys.types t ON t.user_type_id = c.user_type_id
WHERE NOT (t.name = e.TypeName AND c.max_length = e.MaxLen AND c.is_nullable = e.Nullable AND c.is_identity = e.IsIdentity);
-- a table that exists but is missing a column (a partial / foreign table)
SELECT @bad = @bad + e.TableName + N'.' + e.Col + N'(missing) ' FROM @Expected e
WHERE e.TableName <> N'CustomerProfileOverrides' AND OBJECT_ID(N'dbo.' + e.TableName, N'U') IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM sys.columns c WHERE c.object_id = OBJECT_ID(N'dbo.' + e.TableName) AND c.name = e.Col);
IF LEN(@bad) > 0
BEGIN
    SET @msg = N'ABORTED: existing objects have a different shape than the migration expects (check: ' + @bad + N'). Nothing was touched.';
    THROW 50304, @msg, 1;
END;

-- ---------------------------------------------------------------------------------------------
-- 3. Before-snapshot of existing data (row counts of tables that must stay untouched)
-- ---------------------------------------------------------------------------------------------
DECLARE @Snapshot TABLE (TableName sysname PRIMARY KEY, RowsBefore bigint NOT NULL, RowsAfter bigint NULL);
DECLARE @t sysname, @sql nvarchar(max), @n bigint;
DECLARE watched CURSOR LOCAL FAST_FORWARD FOR
    SELECT v.n FROM (VALUES (N'Employees'), (N'Tickets'), (N'TicketAssignments'), (N'TicketInteractions'), (N'IntakeRecords'),
        (N'CustomerContactDetails'), (N'CustomerProfileOverrides'), (N'AuditEntries'), (N'AspNetUsers'), (N'AspNetRoles'),
        (N'AspNetUserRoles'), (N'UserPermissionOverrides'), (N'__EFMigrationsHistory')) v(n)
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
DECLARE @OverridesBefore bigint = (SELECT COUNT_BIG(*) FROM dbo.CustomerProfileOverrides);
PRINT N'Pre-state: migration recorded=' + CAST(@AlreadyRecorded AS nvarchar(1)) + N', history rows=' + CAST(@HistoryBefore AS nvarchar(20))
    + N', CustomerProfileOverrides rows=' + CAST(@OverridesBefore AS nvarchar(20)) + N'.';

-- ---------------------------------------------------------------------------------------------
-- 4. Apply (single transaction, additive only)
-- ---------------------------------------------------------------------------------------------
BEGIN TRY
    BEGIN TRANSACTION;

    -- 4.1 New columns on CustomerProfileOverrides (each only when missing).
    IF COL_LENGTH(N'dbo.CustomerProfileOverrides', N'AdditionalPhones') IS NULL ALTER TABLE dbo.CustomerProfileOverrides ADD AdditionalPhones nvarchar(400) NULL;
    IF COL_LENGTH(N'dbo.CustomerProfileOverrides', N'Country')          IS NULL ALTER TABLE dbo.CustomerProfileOverrides ADD Country nvarchar(100) NULL;
    IF COL_LENGTH(N'dbo.CustomerProfileOverrides', N'CustomerProfileId') IS NULL ALTER TABLE dbo.CustomerProfileOverrides ADD CustomerProfileId uniqueidentifier NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000';
    IF COL_LENGTH(N'dbo.CustomerProfileOverrides', N'Email')            IS NULL ALTER TABLE dbo.CustomerProfileOverrides ADD Email nvarchar(256) NULL;
    IF COL_LENGTH(N'dbo.CustomerProfileOverrides', N'Emirate')          IS NULL ALTER TABLE dbo.CustomerProfileOverrides ADD Emirate nvarchar(100) NULL;
    IF COL_LENGTH(N'dbo.CustomerProfileOverrides', N'FullName')         IS NULL ALTER TABLE dbo.CustomerProfileOverrides ADD FullName nvarchar(200) NULL;
    IF COL_LENGTH(N'dbo.CustomerProfileOverrides', N'LandlineNumber')   IS NULL ALTER TABLE dbo.CustomerProfileOverrides ADD LandlineNumber nvarchar(32) NULL;
    IF COL_LENGTH(N'dbo.CustomerProfileOverrides', N'Location')         IS NULL ALTER TABLE dbo.CustomerProfileOverrides ADD Location nvarchar(200) NULL;
    IF COL_LENGTH(N'dbo.CustomerProfileOverrides', N'Nationality')      IS NULL ALTER TABLE dbo.CustomerProfileOverrides ADD Nationality nvarchar(100) NULL;
    IF COL_LENGTH(N'dbo.CustomerProfileOverrides', N'PreferredLanguage') IS NULL ALTER TABLE dbo.CustomerProfileOverrides ADD PreferredLanguage nvarchar(100) NULL;

    -- 4.2 Every existing row gets its own stable identifier BEFORE the unique index is built (otherwise every row would hold the
    --     all-zero default). EXEC defers compilation: the column was added a moment ago in this same batch.
    EXEC(N'UPDATE dbo.CustomerProfileOverrides SET CustomerProfileId = NEWID() WHERE CustomerProfileId = ''00000000-0000-0000-0000-000000000000'';');

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.CustomerProfileOverrides') AND name = N'IX_CustomerProfileOverrides_CustomerProfileId')
        EXEC(N'CREATE UNIQUE INDEX IX_CustomerProfileOverrides_CustomerProfileId ON dbo.CustomerProfileOverrides (CustomerProfileId);');

    -- 4.3 The append-only change history.
    IF OBJECT_ID(N'dbo.CustomerProfileChanges', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.CustomerProfileChanges (
            CustomerProfileChangeId bigint NOT NULL IDENTITY,
            CustomerProfileId uniqueidentifier NOT NULL,
            CustomerKey nvarchar(300) NOT NULL,
            FieldName nvarchar(100) NOT NULL,
            OldValue nvarchar(1000) NULL,
            NewValue nvarchar(1000) NULL,
            ModifiedByEmployeeId uniqueidentifier NOT NULL,
            ModifiedByName nvarchar(200) NOT NULL,
            ModifiedAtUtc datetime2 NOT NULL,
            Source nvarchar(50) NOT NULL,
            ChangeReason nvarchar(500) NULL,
            RelatedTicketId bigint NULL,
            CorrelationId uniqueidentifier NOT NULL,
            ChangeKey nchar(64) NOT NULL,
            CONSTRAINT PK_CustomerProfileChanges PRIMARY KEY (CustomerProfileChangeId)
        );
        PRINT 'Created table dbo.CustomerProfileChanges.';
    END
    ELSE PRINT 'Table dbo.CustomerProfileChanges already exists with the expected columns - not recreated.';

    IF NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE parent_object_id = OBJECT_ID(N'dbo.CustomerProfileChanges') AND type = 'PK')
        ALTER TABLE dbo.CustomerProfileChanges ADD CONSTRAINT PK_CustomerProfileChanges PRIMARY KEY (CustomerProfileChangeId);
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.CustomerProfileChanges') AND name = N'IX_CustomerProfileChanges_ChangeKey')
        CREATE UNIQUE INDEX IX_CustomerProfileChanges_ChangeKey ON dbo.CustomerProfileChanges (ChangeKey);
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.CustomerProfileChanges') AND name = N'IX_CustomerProfileChanges_CorrelationId')
        CREATE INDEX IX_CustomerProfileChanges_CorrelationId ON dbo.CustomerProfileChanges (CorrelationId);
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.CustomerProfileChanges') AND name = N'IX_CustomerProfileChanges_CustomerKey')
        CREATE INDEX IX_CustomerProfileChanges_CustomerKey ON dbo.CustomerProfileChanges (CustomerKey);
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.CustomerProfileChanges') AND name = N'IX_CustomerProfileChanges_CustomerProfileId_ModifiedAtUtc')
        CREATE INDEX IX_CustomerProfileChanges_CustomerProfileId_ModifiedAtUtc ON dbo.CustomerProfileChanges (CustomerProfileId, ModifiedAtUtc);

    -- 4.4 History is append-only: the database itself refuses to change or remove an audit row, whoever asks. (CREATE TRIGGER must
    --     be the first statement of its batch, hence EXEC.)
    IF NOT EXISTS (SELECT 1 FROM sys.triggers WHERE name = N'TR_CustomerProfileChanges_AppendOnly' AND parent_id = OBJECT_ID(N'dbo.CustomerProfileChanges'))
        EXEC(N'CREATE TRIGGER [TR_CustomerProfileChanges_AppendOnly] ON [CustomerProfileChanges] INSTEAD OF UPDATE, DELETE AS BEGIN SET NOCOUNT ON; THROW 51000, ''CustomerProfileChanges is append-only: audit rows cannot be updated or deleted.'', 1; END');

    -- 4.5 Properties added by hand.
    IF OBJECT_ID(N'dbo.CustomerPropertyAssociations', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.CustomerPropertyAssociations (
            CustomerPropertyAssociationId bigint NOT NULL IDENTITY,
            CustomerKey nvarchar(300) NOT NULL,
            CustomerProfileId uniqueidentifier NOT NULL,
            ProjectName nvarchar(200) NOT NULL,
            UnitNumber nvarchar(100) NOT NULL,
            Relationship tinyint NOT NULL,
            CreatedAtUtc datetime2 NOT NULL,
            CreatedByEmployeeId uniqueidentifier NOT NULL,
            UpdatedAtUtc datetime2 NOT NULL,
            UpdatedByEmployeeId uniqueidentifier NOT NULL,
            RowVersion rowversion NOT NULL,
            CONSTRAINT PK_CustomerPropertyAssociations PRIMARY KEY (CustomerPropertyAssociationId)
        );
        PRINT 'Created table dbo.CustomerPropertyAssociations.';
    END
    ELSE PRINT 'Table dbo.CustomerPropertyAssociations already exists with the expected columns - not recreated.';

    IF NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE parent_object_id = OBJECT_ID(N'dbo.CustomerPropertyAssociations') AND type = 'PK')
        ALTER TABLE dbo.CustomerPropertyAssociations ADD CONSTRAINT PK_CustomerPropertyAssociations PRIMARY KEY (CustomerPropertyAssociationId);
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.CustomerPropertyAssociations') AND name = N'IX_CustomerPropertyAssociations_CustomerKey')
        CREATE INDEX IX_CustomerPropertyAssociations_CustomerKey ON dbo.CustomerPropertyAssociations (CustomerKey);
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.CustomerPropertyAssociations') AND name = N'IX_CustomerPropertyAssociations_CustomerProfileId_ProjectName_UnitNumber')
        CREATE UNIQUE INDEX IX_CustomerPropertyAssociations_CustomerProfileId_ProjectName_UnitNumber ON dbo.CustomerPropertyAssociations (CustomerProfileId, ProjectName, UnitNumber);

    -- 4.6 Migration history registration.
    IF NOT EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId = @MigrationId)
    BEGIN
        INSERT INTO dbo.__EFMigrationsHistory (MigrationId, ProductVersion) VALUES (@MigrationId, @ProductVersion);
        PRINT 'Recorded migration in __EFMigrationsHistory.';
    END
    ELSE PRINT 'Migration already recorded in __EFMigrationsHistory - not inserted again.';

    -- -----------------------------------------------------------------------------------------
    -- 5. Validation (inside the transaction, so any failure rolls everything back)
    -- -----------------------------------------------------------------------------------------
    -- 5a. Every expected column exists with the exact type / length / nullability / identity.
    DECLARE @badCols nvarchar(max) = N'';
    SELECT @badCols = @badCols + e.TableName + N'.' + e.Col + N' ' FROM @Expected e
    WHERE NOT EXISTS (
        SELECT 1 FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
        WHERE c.object_id = OBJECT_ID(N'dbo.' + e.TableName) AND c.name = e.Col
          AND t.name = e.TypeName AND c.max_length = e.MaxLen AND c.is_nullable = e.Nullable AND c.is_identity = e.IsIdentity);
    IF LEN(@badCols) > 0
    BEGIN
        SET @msg = N'VALIDATION FAILED: column shape mismatch (' + @badCols + N').';
        THROW 50310, @msg, 1;
    END;
    IF (SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.CustomerProfileChanges')) <> 14
        THROW 50311, 'VALIDATION FAILED: dbo.CustomerProfileChanges must have exactly 14 columns.', 1;
    IF (SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.CustomerPropertyAssociations')) <> 11
        THROW 50312, 'VALIDATION FAILED: dbo.CustomerPropertyAssociations must have exactly 11 columns.', 1;
    IF (SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.CustomerProfileOverrides')) <> 25
        THROW 50313, 'VALIDATION FAILED: dbo.CustomerProfileOverrides must have exactly 25 columns (15 + the 10 added here).', 1;

    -- 5b. Primary keys.
    IF (SELECT COUNT(*) FROM sys.key_constraints WHERE type = 'PK' AND name IN (N'PK_CustomerProfileChanges', N'PK_CustomerPropertyAssociations')) <> 2
        THROW 50314, 'VALIDATION FAILED: PK_CustomerProfileChanges / PK_CustomerPropertyAssociations missing.', 1;

    -- 5c. Indexes: name, uniqueness and key columns (in order).
    DECLARE @idx TABLE (TableName sysname, IndexName sysname, IsUnique bit, KeyColumns nvarchar(400));
    INSERT @idx VALUES
        (N'CustomerProfileOverrides',     N'IX_CustomerProfileOverrides_CustomerProfileId',                          1, N'CustomerProfileId'),
        (N'CustomerProfileChanges',       N'IX_CustomerProfileChanges_ChangeKey',                                    1, N'ChangeKey'),
        (N'CustomerProfileChanges',       N'IX_CustomerProfileChanges_CorrelationId',                                0, N'CorrelationId'),
        (N'CustomerProfileChanges',       N'IX_CustomerProfileChanges_CustomerKey',                                  0, N'CustomerKey'),
        (N'CustomerProfileChanges',       N'IX_CustomerProfileChanges_CustomerProfileId_ModifiedAtUtc',              0, N'CustomerProfileId, ModifiedAtUtc'),
        (N'CustomerPropertyAssociations', N'IX_CustomerPropertyAssociations_CustomerKey',                            0, N'CustomerKey'),
        (N'CustomerPropertyAssociations', N'IX_CustomerPropertyAssociations_CustomerProfileId_ProjectName_UnitNumber', 1, N'CustomerProfileId, ProjectName, UnitNumber');
    DECLARE @badIdx nvarchar(max) = N'';
    SELECT @badIdx = @badIdx + x.IndexName + N' ' FROM @idx x
    WHERE NOT EXISTS (
        SELECT 1 FROM sys.indexes i
        WHERE i.object_id = OBJECT_ID(N'dbo.' + x.TableName) AND i.name = x.IndexName AND i.is_unique = x.IsUnique AND i.is_disabled = 0
          AND STUFF((SELECT N', ' + c.name FROM sys.index_columns ic JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                     WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0 ORDER BY ic.key_ordinal
                     FOR XML PATH(N'')), 1, 2, N'') = x.KeyColumns);
    IF LEN(@badIdx) > 0
    BEGIN
        SET @msg = N'VALIDATION FAILED: index missing or wrong (' + @badIdx + N').';
        THROW 50315, @msg, 1;
    END;

    -- 5d. The append-only trigger exists, is enabled and covers UPDATE and DELETE.
    IF NOT EXISTS (SELECT 1 FROM sys.triggers t WHERE t.name = N'TR_CustomerProfileChanges_AppendOnly' AND t.parent_id = OBJECT_ID(N'dbo.CustomerProfileChanges')
                   AND t.is_disabled = 0 AND t.is_instead_of_trigger = 1
                   AND EXISTS (SELECT 1 FROM sys.trigger_events te WHERE te.object_id = t.object_id AND te.type_desc = N'UPDATE')
                   AND EXISTS (SELECT 1 FROM sys.trigger_events te WHERE te.object_id = t.object_id AND te.type_desc = N'DELETE'))
        THROW 50316, 'VALIDATION FAILED: the append-only trigger TR_CustomerProfileChanges_AppendOnly is missing, disabled or incomplete.', 1;

    -- 5e. Stable identifiers: none left at the all-zero default, and all distinct (the unique index guarantees the latter).
    DECLARE @zero bigint;
    EXEC sp_executesql N'SELECT @z = COUNT_BIG(*) FROM dbo.CustomerProfileOverrides WHERE CustomerProfileId = ''00000000-0000-0000-0000-000000000000''', N'@z bigint OUTPUT', @z = @zero OUTPUT;
    IF @zero <> 0
        THROW 50317, 'VALIDATION FAILED: some CustomerProfileOverrides rows still have the all-zero CustomerProfileId.', 1;

    -- 5f. Migration history: previous still there, new one recorded exactly once with the right version.
    IF NOT EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId = @Previous)
        THROW 50318, 'VALIDATION FAILED: previous migration row disappeared.', 1;
    IF (SELECT COUNT(*) FROM dbo.__EFMigrationsHistory WHERE MigrationId = @MigrationId AND ProductVersion = @ProductVersion) <> 1
        THROW 50319, 'VALIDATION FAILED: migration not recorded exactly once with the expected ProductVersion.', 1;
    IF (SELECT COUNT_BIG(*) FROM dbo.__EFMigrationsHistory) <> @HistoryBefore + CASE WHEN @AlreadyRecorded = 1 THEN 0 ELSE 1 END
        THROW 50320, 'VALIDATION FAILED: unexpected change in __EFMigrationsHistory row count.', 1;

    -- 5g. Existing data untouched (the history table grows by one row on a first run only).
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
        THROW 50321, 'VALIDATION FAILED: an existing table changed its row count.', 1;

    SELECT TableName, RowsBefore, RowsAfter,
           CASE WHEN RowsBefore = RowsAfter THEN 'unchanged'
                WHEN TableName = N'__EFMigrationsHistory' AND RowsAfter = RowsBefore + 1 THEN '+1 (this migration)'
                ELSE 'CHANGED' END AS Result
    FROM @Snapshot ORDER BY TableName;

    IF @DryRun = 1
    BEGIN
        ROLLBACK TRANSACTION;
        PRINT 'DRY RUN SUCCESS - schema, indexes, trigger, stable-identifier backfill, migration-history and existing-data checks all passed inside the transaction, which was ROLLED BACK. Nothing was changed. Set @DryRun = 0 to commit.';
    END
    ELSE
    BEGIN
        COMMIT TRANSACTION;
        SET @Committed = 1;
        PRINT 'COMMITTED. Migration 20261119081103_AddCustomerProfileAuditAndProperties applied to TigerCS_Ticketing_Prod.';
    END;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    PRINT 'FAILED - transaction rolled back, nothing was changed.';
    THROW;
END CATCH;

-- ---------------------------------------------------------------------------------------------
-- 6. Post-commit verification (read-only). Runs ONLY when @DryRun = 0 AND the commit succeeded.
--    On a dry run nothing is queried here (the objects were rolled back and do not exist), so there is no Msg 208.
-- ---------------------------------------------------------------------------------------------
IF @DryRun = 0 AND @Committed = 1
BEGIN
PRINT 'Post-commit verification results follow.';

-- 6.1 Migration history (expect the previous migration and this one)
SELECT MigrationId, ProductVersion FROM dbo.__EFMigrationsHistory
WHERE MigrationId IN (N'20261118120000_AddCustomerProfileOverrides', N'20261119081103_AddCustomerProfileAuditAndProperties') ORDER BY MigrationId;

-- 6.2 The new tables and the trigger (expect 2 object ids and 1 trigger, enabled)
SELECT OBJECT_ID(N'dbo.CustomerProfileChanges', N'U') AS CustomerProfileChanges, OBJECT_ID(N'dbo.CustomerPropertyAssociations', N'U') AS CustomerPropertyAssociations;
SELECT t.name AS TriggerName, t.is_disabled, t.is_instead_of_trigger FROM sys.triggers t WHERE t.parent_id = OBJECT_ID(N'dbo.CustomerProfileChanges');

-- 6.3 Columns of the three tables (expect 25 / 14 / 11)
SELECT OBJECT_NAME(c.object_id) AS TableName, COUNT(*) AS Columns FROM sys.columns c
WHERE c.object_id IN (OBJECT_ID(N'dbo.CustomerProfileOverrides'), OBJECT_ID(N'dbo.CustomerProfileChanges'), OBJECT_ID(N'dbo.CustomerPropertyAssociations'))
GROUP BY c.object_id ORDER BY TableName;

-- 6.4 The new tables start empty; every existing override has its own CustomerProfileId (expect 0, 0, then equal numbers)
SELECT (SELECT COUNT_BIG(*) FROM dbo.CustomerProfileChanges) AS ChangeRows, (SELECT COUNT_BIG(*) FROM dbo.CustomerPropertyAssociations) AS PropertyRows;
EXEC sp_executesql N'SELECT COUNT_BIG(*) AS OverrideRows, COUNT_BIG(DISTINCT CustomerProfileId) AS DistinctProfileIds FROM dbo.CustomerProfileOverrides';

-- 6.5 Append-only proof (expect error 51000 inside the TRY, nothing changed): a rolled-back attempt to delete from the history.
BEGIN TRY
    BEGIN TRANSACTION;
    DELETE FROM dbo.CustomerProfileChanges;
    ROLLBACK TRANSACTION;
    SELECT 'WARNING: the delete was NOT blocked' AS AppendOnlyCheck;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    SELECT ERROR_NUMBER() AS AppendOnlyCheck_ErrorNumber, ERROR_MESSAGE() AS AppendOnlyCheck_Message;   -- expect 51000
END CATCH;
END;
