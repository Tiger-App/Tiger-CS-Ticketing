/*
================================================================================
 Deploy_TigerCS_UAT_Migrations.sql
 TigerCS Ticketing - UAT deployment of three EF Core migrations
--------------------------------------------------------------------------------
 Applies, in this order, ONLY the migrations that are not yet recorded in
 dbo.__EFMigrationsHistory:

   1. 20261030061255_AddTicketInteractionHandlers
        new table dbo.TicketInteractionHandlers (+ 3 indexes, 3 foreign keys),
        and a copy of the existing single handler of each Genesys interaction
        (Source = 9 / Legacy).
   2. 20261030062642_AddTicketTransferTracking
        4 nullable columns + 1 index on dbo.TicketWorkflowEvents.
   3. 20261030073857_AddTicketCreatedBy
        dbo.Tickets.CreatedByEmployeeId (+ index) and its backfill from the
        first "Create" audit entry of each ticket.

 Safe to run more than once:
   * a migration already in the history table is skipped entirely;
   * for a migration NOT in the history table every object is created only if
     it is missing (a half-applied earlier attempt is completed, not repeated);
   * the data copy / backfill only touch rows that still need it;
   * one application lock stops two runs from overlapping.

 Everything runs in ONE transaction with SET XACT_ABORT ON inside TRY/CATCH:
 any error, failed pre-check or failed verification rolls the whole run back.
 There is deliberately no GO in this file - run it as ONE batch (SSMS Execute,
 or sqlcmd -i with -f 65001).

 BEFORE YOU RUN
   1. Take a backup / snapshot of the UAT database.
   2. Stop (or quiesce) the UAT API and Web while it runs: ALTER TABLE and
      CREATE INDEX on dbo.Tickets / dbo.TicketWorkflowEvents take schema locks.
   3. Set @ExpectedDatabase below to the UAT database name (strongly advised).
   4. Optional: set @DryRun = 1 first. It does all the work, runs every check,
      then ROLLS BACK, so nothing is kept.

 Not run against any database by the author. Do NOT run against Production
 from this file.

 ROLLBACK instructions are at the END of this file (commented out).
================================================================================
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET LOCK_TIMEOUT 60000;

-------------------------------------------------------------------------------
-- Settings
-------------------------------------------------------------------------------
DECLARE @ExpectedDatabase sysname = NULL;   -- e.g. N'TigerCS_Ticketing_UAT'. NULL = no name check, but a database whose name contains 'prod' is refused.
DECLARE @DryRun bit = 0;                    -- 1 = apply, verify, then ROLL BACK

-------------------------------------------------------------------------------
-- Constants
-------------------------------------------------------------------------------
DECLARE @M1 nvarchar(150) = N'20261030061255_AddTicketInteractionHandlers';
DECLARE @M2 nvarchar(150) = N'20261030062642_AddTicketTransferTracking';
DECLARE @M3 nvarchar(150) = N'20261030073857_AddTicketCreatedBy';
DECLARE @ProductVersion nvarchar(32) = N'10.0.11';

DECLARE @sql nvarchar(max);
DECLARE @n bigint;
DECLARE @list nvarchar(max);
DECLARE @msg nvarchar(2000);
DECLARE @committed bit = 0;
DECLARE @lockResult int;

BEGIN TRY

    ---------------------------------------------------------------------------
    -- 0. Target database guard
    ---------------------------------------------------------------------------
    PRINT N'Target: server ' + @@SERVERNAME + N', database ' + DB_NAME() + N', started ' + CONVERT(nvarchar(30), SYSUTCDATETIME(), 126) + N' UTC';

    IF @ExpectedDatabase IS NOT NULL AND DB_NAME() <> @ExpectedDatabase
    BEGIN
        SET @msg = N'Wrong database: connected to [' + DB_NAME() + N'] but @ExpectedDatabase is [' + @ExpectedDatabase + N']. Nothing was changed.';
        THROW 50001, @msg, 1;
    END;

    IF @ExpectedDatabase IS NULL AND DB_NAME() LIKE N'%prod%'
    BEGIN
        SET @msg = N'Refusing to run: database name [' + DB_NAME() + N'] looks like Production. This script is for UAT. Nothing was changed.';
        THROW 50002, @msg, 1;
    END;

    ---------------------------------------------------------------------------
    -- 1. PRE-DEPLOYMENT VALIDATION (read-only)
    ---------------------------------------------------------------------------
    PRINT N'--- Pre-deployment validation';

    IF OBJECT_ID(N'dbo.__EFMigrationsHistory', N'U') IS NULL
        THROW 50003, N'dbo.__EFMigrationsHistory does not exist: this is not an EF-managed TigerCS database. Nothing was changed.', 1;

    -- 1a. Every earlier migration shipped with the application must already be applied.
    DECLARE @prior TABLE (MigrationId nvarchar(150) NOT NULL PRIMARY KEY);
    INSERT INTO @prior (MigrationId) VALUES
    (N'20260819072347_InitialIdentityAndAccess'),
    (N'20260820072044_AddCustomerVerification'),
    (N'20260820140000_AddTicketing'),
    (N'20260821090000_AddTicketOperations'),
    (N'20260821130000_AddSlaAndEscalation'),
    (N'20260822090000_AddNotificationsAndOutbox'),
    (N'20260825053743_AddIntakeRecordPhoneNumber'),
    (N'20260825061341_AddDepartmentCustomerLookupSource'),
    (N'20260825090047_AddDepartmentCustomerLookupSources'),
    (N'20260827090146_AddCrmBuyerLookupToTickets'),
    (N'20260901103309_AddExternalCustomerVerificationToTickets'),
    (N'20260903164723_AddWorkflowConfiguration'),
    (N'20260904075902_AddWorkflowAutomation'),
    (N'20260904082241_ReplaceInteractionContextWithTicketInteractions'),
    (N'20260904091143_AddApprovalWorkflow'),
    (N'20260908054944_AddWorkflowVersioning'),
    (N'20260909084839_AddChannels'),
    (N'20260910080219_AddGenesysIntegration'),
    (N'20260911105154_AddGenesysAgentMappingAndInteractionOwnership'),
    (N'20260921133507_AddAgentHandoffTriggerAndConcurrency'),
    (N'20260928132618_AddGenesysScreenPopLaunches'),
    (N'20261005072511_AddCollectionsReminders'),
    (N'20261007093644_AddChatbotInactivityAndCrmDocumentCopies'),
    (N'20261007103153_AddResolutionClosedForCustomerInactivity'),
    (N'20261007180009_AddCustomerOtpVerification'),
    (N'20261008184722_AddSlaPauseAndPriorityDowngrade'),
    (N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'20261009074521_AddDispatchSuppressionAndVoiceEligibility'),
    (N'20261009102002_AddSmsChannelToCustomerOtp'),
    (N'20261012124810_AddAgentDeskDocumentDelivery'),
    (N'20261020090000_AddGenesysCampaignToDispatchBatch'),
    (N'20261020105026_AddLastCustomerInteractionToTicketInteractions'),
    (N'20261021090000_AddTowerToReviewRecords'),
    (N'20261021120000_AddAdminEmailToCollectionsDispatch'),
    (N'20261022081031_AddCustomerSatisfactionReviews'),
    (N'20261022090000_AddGenesysServiceRequestToTicketInteractions');

    SELECT @list = STUFF((SELECT N', ' + p.MigrationId
        FROM @prior AS p
        WHERE NOT EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory AS h WHERE h.MigrationId = p.MigrationId)
        FOR XML PATH(N''), TYPE).value(N'.', N'nvarchar(max)'), 1, 2, N'');

    IF @list IS NOT NULL
    BEGIN
        SET @msg = N'Earlier migrations are missing from UAT and must be applied first (this script only adds the three newest): ' + LEFT(@list, 1500);
        THROW 50004, @msg, 1;
    END;

    -- 1b. Tables and columns the three migrations build on.
    DECLARE @req TABLE (TableName sysname NOT NULL, ColumnName sysname NOT NULL);
    INSERT INTO @req (TableName, ColumnName) VALUES
        (N'Tickets', N'TicketId'), (N'Tickets', N'CurrentDepartmentId'),
        (N'TicketInteractions', N'TicketInteractionId'), (N'TicketInteractions', N'TicketId'),
        (N'TicketInteractions', N'HandledByUserId'), (N'TicketInteractions', N'GenesysAgentUserId'),
        (N'TicketInteractions', N'CreatedAtUtc'), (N'TicketInteractions', N'IsOriginatingInteraction'),
        (N'TicketInteractions', N'GenesysConversationId'),
        (N'TicketWorkflowEvents', N'TicketId'), (N'TicketWorkflowEvents', N'EventType'),
        (N'AuditEntries', N'AuditEntryId'), (N'AuditEntries', N'EntityType'), (N'AuditEntries', N'Action'),
        (N'AuditEntries', N'EntityId'), (N'AuditEntries', N'ActorEmployeeId'),
        (N'AspNetUsers', N'Id');

    SELECT @list = STUFF((SELECT N', ' + r.TableName + N'.' + r.ColumnName
        FROM @req AS r
        WHERE COL_LENGTH(N'dbo.' + r.TableName, r.ColumnName) IS NULL
        FOR XML PATH(N''), TYPE).value(N'.', N'nvarchar(max)'), 1, 2, N'');

    IF @list IS NOT NULL
    BEGIN
        SET @msg = N'Required schema is missing: ' + @list;
        THROW 50005, @msg, 1;
    END;

    -- 1c. Report the state found (informational) so a half-applied earlier attempt is visible in the output.
    PRINT N'  history: ' + CASE WHEN EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId = @M1) THEN N'M1 recorded' ELSE N'M1 pending' END
        + N' | ' + CASE WHEN EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId = @M2) THEN N'M2 recorded' ELSE N'M2 pending' END
        + N' | ' + CASE WHEN EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId = @M3) THEN N'M3 recorded' ELSE N'M3 pending' END;
    PRINT N'  objects: TicketInteractionHandlers=' + CASE WHEN OBJECT_ID(N'dbo.TicketInteractionHandlers', N'U') IS NULL THEN N'absent' ELSE N'PRESENT' END
        + N' | Tickets.CreatedByEmployeeId=' + CASE WHEN COL_LENGTH(N'dbo.Tickets', N'CreatedByEmployeeId') IS NULL THEN N'absent' ELSE N'PRESENT' END
        + N' | TicketWorkflowEvents.TransferReasonCode=' + CASE WHEN COL_LENGTH(N'dbo.TicketWorkflowEvents', N'TransferReasonCode') IS NULL THEN N'absent' ELSE N'PRESENT' END;

    SELECT @n = COUNT_BIG(*) FROM dbo.Tickets;
    PRINT N'  Tickets rows: ' + CONVERT(nvarchar(20), @n);
    SELECT @n = COUNT_BIG(*) FROM dbo.AuditEntries;
    PRINT N'  AuditEntries rows: ' + CONVERT(nvarchar(20), @n) + N'  (the CreatedBy backfill scans this table once per ticket - AuditEntries has no index on EntityType/Action/EntityId)';

    ---------------------------------------------------------------------------
    -- 2. DEPLOYMENT (single transaction)
    ---------------------------------------------------------------------------
    BEGIN TRANSACTION;

    EXEC @lockResult = sp_getapplock
        @Resource = N'TigerCS_Deploy_UAT_Migrations', @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 0;
    IF @lockResult < 0
        THROW 50006, N'Another run of this script holds the deployment lock. Nothing was changed.', 1;

    ---------------------------------------------------------------------------
    -- Migration 1 of 3: AddTicketInteractionHandlers
    ---------------------------------------------------------------------------
    IF EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId = @M1)
        PRINT N'[1/3] ' + @M1 + N' - already recorded, skipped';
    ELSE
    BEGIN
        PRINT N'[1/3] ' + @M1 + N' - applying';

        IF OBJECT_ID(N'dbo.TicketInteractionHandlers', N'U') IS NULL
        BEGIN
            EXEC (N'
            CREATE TABLE dbo.TicketInteractionHandlers (
                TicketInteractionHandlerId bigint NOT NULL IDENTITY,
                TicketInteractionId bigint NOT NULL,
                TicketId bigint NOT NULL,
                HandledByUserId uniqueidentifier NOT NULL,
                GenesysAgentUserId nvarchar(64) NOT NULL,
                Source tinyint NOT NULL,
                ConnectedAtUtc datetime2 NULL,
                RecordedAtUtc datetime2 NOT NULL,
                CONSTRAINT PK_TicketInteractionHandlers PRIMARY KEY (TicketInteractionHandlerId)
            );');
        END
        ELSE
            PRINT N'      table dbo.TicketInteractionHandlers already exists - completing missing parts only';

        IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_TicketInteractionHandlers_AspNetUsers_HandledByUserId' AND parent_object_id = OBJECT_ID(N'dbo.TicketInteractionHandlers'))
            EXEC (N'ALTER TABLE dbo.TicketInteractionHandlers ADD CONSTRAINT FK_TicketInteractionHandlers_AspNetUsers_HandledByUserId
                    FOREIGN KEY (HandledByUserId) REFERENCES dbo.AspNetUsers (Id) ON DELETE NO ACTION;');

        IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_TicketInteractionHandlers_TicketInteractions_TicketInteractionId' AND parent_object_id = OBJECT_ID(N'dbo.TicketInteractionHandlers'))
            EXEC (N'ALTER TABLE dbo.TicketInteractionHandlers ADD CONSTRAINT FK_TicketInteractionHandlers_TicketInteractions_TicketInteractionId
                    FOREIGN KEY (TicketInteractionId) REFERENCES dbo.TicketInteractions (TicketInteractionId) ON DELETE CASCADE;');

        IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_TicketInteractionHandlers_Tickets_TicketId' AND parent_object_id = OBJECT_ID(N'dbo.TicketInteractionHandlers'))
            EXEC (N'ALTER TABLE dbo.TicketInteractionHandlers ADD CONSTRAINT FK_TicketInteractionHandlers_Tickets_TicketId
                    FOREIGN KEY (TicketId) REFERENCES dbo.Tickets (TicketId) ON DELETE NO ACTION;');

        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_TicketInteractionHandlers_HandledByUserId_TicketId' AND object_id = OBJECT_ID(N'dbo.TicketInteractionHandlers'))
            EXEC (N'CREATE INDEX IX_TicketInteractionHandlers_HandledByUserId_TicketId ON dbo.TicketInteractionHandlers (HandledByUserId, TicketId);');

        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_TicketInteractionHandlers_TicketId' AND object_id = OBJECT_ID(N'dbo.TicketInteractionHandlers'))
            EXEC (N'CREATE INDEX IX_TicketInteractionHandlers_TicketId ON dbo.TicketInteractionHandlers (TicketId);');

        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_TicketInteractionHandlers_InteractionId_UserId' AND object_id = OBJECT_ID(N'dbo.TicketInteractionHandlers'))
            EXEC (N'CREATE UNIQUE INDEX UX_TicketInteractionHandlers_InteractionId_UserId ON dbo.TicketInteractionHandlers (TicketInteractionId, HandledByUserId);');

        -- Carry over each interaction's existing single handler (Source 9 = Legacy). Re-runnable: skips pairs already copied.
        SET @sql = N'
            INSERT INTO dbo.TicketInteractionHandlers
                (TicketInteractionId, TicketId, HandledByUserId, GenesysAgentUserId, Source, ConnectedAtUtc, RecordedAtUtc)
            SELECT i.TicketInteractionId, i.TicketId, i.HandledByUserId, i.GenesysAgentUserId, 9, NULL, i.CreatedAtUtc
            FROM dbo.TicketInteractions AS i
            WHERE i.HandledByUserId IS NOT NULL AND i.GenesysAgentUserId IS NOT NULL
              AND NOT EXISTS (SELECT 1 FROM dbo.TicketInteractionHandlers AS h
                              WHERE h.TicketInteractionId = i.TicketInteractionId AND h.HandledByUserId = i.HandledByUserId);
            SET @rc = @@ROWCOUNT;';
        EXEC sp_executesql @sql, N'@rc bigint OUTPUT', @rc = @n OUTPUT;
        PRINT N'      legacy handlers copied: ' + CONVERT(nvarchar(20), @n);

        INSERT INTO dbo.__EFMigrationsHistory (MigrationId, ProductVersion) VALUES (@M1, @ProductVersion);
    END;

    ---------------------------------------------------------------------------
    -- Migration 2 of 3: AddTicketTransferTracking
    ---------------------------------------------------------------------------
    IF EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId = @M2)
        PRINT N'[2/3] ' + @M2 + N' - already recorded, skipped';
    ELSE
    BEGIN
        PRINT N'[2/3] ' + @M2 + N' - applying';

        IF COL_LENGTH(N'dbo.TicketWorkflowEvents', N'FromDepartmentId') IS NULL
            EXEC (N'ALTER TABLE dbo.TicketWorkflowEvents ADD FromDepartmentId int NULL;');
        IF COL_LENGTH(N'dbo.TicketWorkflowEvents', N'ToDepartmentId') IS NULL
            EXEC (N'ALTER TABLE dbo.TicketWorkflowEvents ADD ToDepartmentId int NULL;');
        IF COL_LENGTH(N'dbo.TicketWorkflowEvents', N'ToEmployeeId') IS NULL
            EXEC (N'ALTER TABLE dbo.TicketWorkflowEvents ADD ToEmployeeId uniqueidentifier NULL;');
        IF COL_LENGTH(N'dbo.TicketWorkflowEvents', N'TransferReasonCode') IS NULL
            EXEC (N'ALTER TABLE dbo.TicketWorkflowEvents ADD TransferReasonCode tinyint NULL;');

        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_TicketWorkflowEvents_EventType_TransferReasonCode' AND object_id = OBJECT_ID(N'dbo.TicketWorkflowEvents'))
            EXEC (N'CREATE INDEX IX_TicketWorkflowEvents_EventType_TransferReasonCode ON dbo.TicketWorkflowEvents (EventType, TransferReasonCode);');

        INSERT INTO dbo.__EFMigrationsHistory (MigrationId, ProductVersion) VALUES (@M2, @ProductVersion);
    END;

    ---------------------------------------------------------------------------
    -- Migration 3 of 3: AddTicketCreatedBy
    ---------------------------------------------------------------------------
    IF EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId = @M3)
        PRINT N'[3/3] ' + @M3 + N' - already recorded, skipped';
    ELSE
    BEGIN
        PRINT N'[3/3] ' + @M3 + N' - applying';

        IF COL_LENGTH(N'dbo.Tickets', N'CreatedByEmployeeId') IS NULL
            EXEC (N'ALTER TABLE dbo.Tickets ADD CreatedByEmployeeId uniqueidentifier NULL;');

        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Tickets_CreatedByEmployeeId' AND object_id = OBJECT_ID(N'dbo.Tickets'))
            EXEC (N'CREATE INDEX IX_Tickets_CreatedByEmployeeId ON dbo.Tickets (CreatedByEmployeeId);');

        -- Backfill: the creator is the actor of the ticket's FIRST non-system "Create" audit entry.
        -- This is exactly the statement shipped with the migration (AddTicketCreatedBy.BackfillSql, covered by the
        -- application's SQL Server test). ticket creation writes ("Create", "Ticket", TicketId) with the calling
        -- employee as actor; for a Genesys inquiry that caller is the Genesys integration account. Tickets with no
        -- such entry (system-created, or never audited) stay NULL. Only NULL rows are touched, so re-running is safe.
        EXEC (N'
            UPDATE [Tickets]
            SET [CreatedByEmployeeId] = (
                SELECT a.[ActorEmployeeId] FROM [AuditEntries] AS a
                WHERE a.[AuditEntryId] = (
                    SELECT MIN(a2.[AuditEntryId]) FROM [AuditEntries] AS a2
                    WHERE a2.[EntityType] = ''Ticket'' AND a2.[Action] = ''Create'' AND a2.[ActorEmployeeId] IS NOT NULL
                      AND a2.[EntityId] = CAST([Tickets].[TicketId] AS nvarchar(64))))
            WHERE [CreatedByEmployeeId] IS NULL;');

        INSERT INTO dbo.__EFMigrationsHistory (MigrationId, ProductVersion) VALUES (@M3, @ProductVersion);
    END;

    ---------------------------------------------------------------------------
    -- 3. VERIFICATION INSIDE THE TRANSACTION (any failure rolls everything back)
    ---------------------------------------------------------------------------
    PRINT N'--- Verification';

    -- 3a. History rows.
    SELECT @list = STUFF((SELECT N', ' + m.MigrationId
        FROM (VALUES (@M1), (@M2), (@M3)) AS m(MigrationId)
        WHERE NOT EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory AS h WHERE h.MigrationId = m.MigrationId)
        FOR XML PATH(N''), TYPE).value(N'.', N'nvarchar(max)'), 1, 2, N'');
    IF @list IS NOT NULL
    BEGIN
        SET @msg = N'Verification failed - history rows missing: ' + @list;
        THROW 50010, @msg, 1;
    END;

    -- 3b. Columns exist with the exact type, size and nullability the model expects.
    DECLARE @col TABLE (TableName sysname, ColumnName sysname, TypeName sysname, MaxLength smallint, IsNullable bit);
    INSERT INTO @col VALUES
        (N'Tickets',                    N'CreatedByEmployeeId',       N'uniqueidentifier', 16,  1),
        (N'TicketWorkflowEvents',       N'FromDepartmentId',          N'int',              4,   1),
        (N'TicketWorkflowEvents',       N'ToDepartmentId',            N'int',              4,   1),
        (N'TicketWorkflowEvents',       N'ToEmployeeId',              N'uniqueidentifier', 16,  1),
        (N'TicketWorkflowEvents',       N'TransferReasonCode',        N'tinyint',          1,   1),
        (N'TicketInteractionHandlers',  N'TicketInteractionHandlerId',N'bigint',           8,   0),
        (N'TicketInteractionHandlers',  N'TicketInteractionId',       N'bigint',           8,   0),
        (N'TicketInteractionHandlers',  N'TicketId',                  N'bigint',           8,   0),
        (N'TicketInteractionHandlers',  N'HandledByUserId',           N'uniqueidentifier', 16,  0),
        (N'TicketInteractionHandlers',  N'GenesysAgentUserId',        N'nvarchar',         128, 0),
        (N'TicketInteractionHandlers',  N'Source',                    N'tinyint',          1,   0),
        (N'TicketInteractionHandlers',  N'ConnectedAtUtc',            N'datetime2',        8,   1),
        (N'TicketInteractionHandlers',  N'RecordedAtUtc',             N'datetime2',        8,   0);

    SELECT @list = STUFF((SELECT N', ' + e.TableName + N'.' + e.ColumnName
        FROM @col AS e
        WHERE NOT EXISTS (
        SELECT 1 FROM sys.columns AS c
        JOIN sys.types AS t ON t.user_type_id = c.user_type_id
        WHERE c.object_id = OBJECT_ID(N'dbo.' + e.TableName) AND c.name = e.ColumnName
          AND t.name = e.TypeName AND c.max_length = e.MaxLength AND c.is_nullable = e.IsNullable)
        FOR XML PATH(N''), TYPE).value(N'.', N'nvarchar(max)'), 1, 2, N'');
    IF @list IS NOT NULL
    BEGIN
        SET @msg = N'Verification failed - column missing or with an unexpected type/size/nullability: ' + @list;
        THROW 50011, @msg, 1;
    END;

    -- 3c. Indexes, primary key and foreign keys.
    DECLARE @idx TABLE (TableName sysname, IndexName sysname, IsUnique bit);
    INSERT INTO @idx VALUES
        (N'TicketInteractionHandlers', N'IX_TicketInteractionHandlers_HandledByUserId_TicketId', 0),
        (N'TicketInteractionHandlers', N'IX_TicketInteractionHandlers_TicketId', 0),
        (N'TicketInteractionHandlers', N'UX_TicketInteractionHandlers_InteractionId_UserId', 1),
        (N'TicketWorkflowEvents',      N'IX_TicketWorkflowEvents_EventType_TransferReasonCode', 0),
        (N'Tickets',                   N'IX_Tickets_CreatedByEmployeeId', 0);

    SELECT @list = STUFF((SELECT N', ' + i.IndexName
        FROM @idx AS i
        WHERE NOT EXISTS (SELECT 1 FROM sys.indexes AS x WHERE x.object_id = OBJECT_ID(N'dbo.' + i.TableName) AND x.name = i.IndexName AND x.is_unique = i.IsUnique)
        FOR XML PATH(N''), TYPE).value(N'.', N'nvarchar(max)'), 1, 2, N'');
    IF @list IS NOT NULL
    BEGIN
        SET @msg = N'Verification failed - index missing: ' + @list;
        THROW 50012, @msg, 1;
    END;

    IF NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE name = N'PK_TicketInteractionHandlers' AND parent_object_id = OBJECT_ID(N'dbo.TicketInteractionHandlers'))
        THROW 50013, N'Verification failed - PK_TicketInteractionHandlers is missing.', 1;

    SELECT @list = STUFF((SELECT N', ' + f.FkName
        FROM (VALUES (N'FK_TicketInteractionHandlers_AspNetUsers_HandledByUserId'),
                 (N'FK_TicketInteractionHandlers_TicketInteractions_TicketInteractionId'),
                 (N'FK_TicketInteractionHandlers_Tickets_TicketId')) AS f(FkName)
        WHERE NOT EXISTS (SELECT 1 FROM sys.foreign_keys AS k WHERE k.name = f.FkName AND k.parent_object_id = OBJECT_ID(N'dbo.TicketInteractionHandlers'))
        FOR XML PATH(N''), TYPE).value(N'.', N'nvarchar(max)'), 1, 2, N'');
    IF @list IS NOT NULL
    BEGIN
        SET @msg = N'Verification failed - foreign key missing: ' + @list;
        THROW 50014, @msg, 1;
    END;

    -- 3d. Data: every legacy handler was copied, and no ticket with a usable "Create" audit entry was left without a creator.
    SET @sql = N'SELECT @c = COUNT_BIG(*) FROM dbo.TicketInteractions AS i
                 WHERE i.HandledByUserId IS NOT NULL AND i.GenesysAgentUserId IS NOT NULL
                   AND NOT EXISTS (SELECT 1 FROM dbo.TicketInteractionHandlers AS h WHERE h.TicketInteractionId = i.TicketInteractionId AND h.HandledByUserId = i.HandledByUserId);';
    EXEC sp_executesql @sql, N'@c bigint OUTPUT', @c = @n OUTPUT;
    IF @n > 0
    BEGIN
        SET @msg = N'Verification failed - ' + CONVERT(nvarchar(20), @n) + N' interaction handler(s) were not copied to TicketInteractionHandlers.';
        THROW 50015, @msg, 1;
    END;

    SET @sql = N'SELECT @c = COUNT_BIG(*) FROM dbo.Tickets AS t
                 WHERE t.CreatedByEmployeeId IS NULL
                   AND t.TicketId IN (SELECT TRY_CAST(a.EntityId AS bigint) FROM dbo.AuditEntries AS a
                                      WHERE a.EntityType = N''Ticket'' AND a.Action = N''Create'' AND a.ActorEmployeeId IS NOT NULL);';
    EXEC sp_executesql @sql, N'@c bigint OUTPUT', @c = @n OUTPUT;
    IF @n > 0
    BEGIN
        SET @msg = N'Verification failed - ' + CONVERT(nvarchar(20), @n) + N' ticket(s) with a "Create" audit actor still have no CreatedByEmployeeId.';
        THROW 50016, @msg, 1;
    END;

    PRINT N'  all checks passed';

    ---------------------------------------------------------------------------
    -- 4. COMMIT (or roll back for a dry run)
    ---------------------------------------------------------------------------
    IF @DryRun = 1
    BEGIN
        ROLLBACK TRANSACTION;
        PRINT N'DRY RUN: every step and check succeeded and the transaction was ROLLED BACK. Nothing was kept. Set @DryRun = 0 to deploy.';
    END
    ELSE
    BEGIN
        COMMIT TRANSACTION;
        SET @committed = 1;
        PRINT N'COMMITTED. Migrations are recorded in dbo.__EFMigrationsHistory.';
    END;

    ---------------------------------------------------------------------------
    -- 5. POST-DEPLOYMENT REPORT (read-only result sets; skipped for a dry run)
    ---------------------------------------------------------------------------
    IF @committed = 1
    BEGIN
        -- R1: migration history
        SELECT MigrationId, ProductVersion FROM dbo.__EFMigrationsHistory
        WHERE MigrationId IN (@M1, @M2, @M3) ORDER BY MigrationId;

        -- R2: counts
        EXEC (N'
            SELECT (SELECT COUNT_BIG(*) FROM dbo.Tickets) AS Tickets,
                   (SELECT COUNT_BIG(*) FROM dbo.Tickets WHERE CreatedByEmployeeId IS NOT NULL) AS TicketsWithCreator,
                   (SELECT COUNT_BIG(*) FROM dbo.Tickets WHERE CreatedByEmployeeId IS NULL) AS TicketsWithoutCreator,
                   (SELECT COUNT_BIG(*) FROM dbo.TicketInteractionHandlers) AS HandlerRows,
                   (SELECT COUNT_BIG(*) FROM dbo.TicketInteractionHandlers WHERE Source = 9) AS LegacyHandlerRows;');

        -- R3: how Genesys-originated tickets (an originating interaction that carries a Genesys conversation id) were attributed.
        -- Expect ONE dominant creator: the genesys.integration account.
        EXEC (N'
            SELECT t.CreatedByEmployeeId, u.UserName AS CreatorUserName, COUNT_BIG(*) AS GenesysOriginatedTickets
            FROM dbo.Tickets AS t
            JOIN dbo.TicketInteractions AS i ON i.TicketId = t.TicketId AND i.IsOriginatingInteraction = 1 AND i.GenesysConversationId IS NOT NULL
            LEFT JOIN dbo.AspNetUsers AS u ON u.Id = t.CreatedByEmployeeId
            GROUP BY t.CreatedByEmployeeId, u.UserName
            ORDER BY COUNT_BIG(*) DESC;');

        -- R4: Genesys-originated tickets with NO creator: genesys.integration would NOT see these. Expect 0 in UAT.
        EXEC (N'
            SELECT COUNT_BIG(*) AS GenesysOriginatedWithoutCreator
            FROM dbo.Tickets AS t
            WHERE t.CreatedByEmployeeId IS NULL
              AND EXISTS (SELECT 1 FROM dbo.TicketInteractions AS i WHERE i.TicketId = t.TicketId AND i.IsOriginatingInteraction = 1 AND i.GenesysConversationId IS NOT NULL);');

        -- R5: the integration account itself: tickets it created, split into Genesys-originated and manual.
        -- ManualCreatedByIntegration should be 0; anything else is exposed to genesys.integration without being Genesys-originated.
        EXEC (N'
            SELECT u.Id AS IntegrationEmployeeId, u.UserName,
                   (SELECT COUNT_BIG(*) FROM dbo.Tickets t WHERE t.CreatedByEmployeeId = u.Id) AS CreatedByIntegration,
                   (SELECT COUNT_BIG(*) FROM dbo.Tickets t WHERE t.CreatedByEmployeeId = u.Id
                       AND EXISTS (SELECT 1 FROM dbo.TicketInteractions i WHERE i.TicketId = t.TicketId AND i.IsOriginatingInteraction = 1 AND i.GenesysConversationId IS NOT NULL)) AS GenesysOriginated,
                   (SELECT COUNT_BIG(*) FROM dbo.Tickets t WHERE t.CreatedByEmployeeId = u.Id
                       AND NOT EXISTS (SELECT 1 FROM dbo.TicketInteractions i WHERE i.TicketId = t.TicketId AND i.IsOriginatingInteraction = 1 AND i.GenesysConversationId IS NOT NULL)) AS ManualCreatedByIntegration
            FROM dbo.AspNetUsers AS u WHERE u.NormalizedUserName = N''GENESYS.INTEGRATION'';');
    END;

END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;

    IF @committed = 1
        PRINT N'The migrations were COMMITTED, but a read-only post-deployment report query failed: ' + ERROR_MESSAGE();
    ELSE
        PRINT N'FAILED - the transaction was rolled back and nothing was changed: [' + CONVERT(nvarchar(20), ERROR_NUMBER()) + N'] ' + ERROR_MESSAGE();

    THROW;
END CATCH;

/*
================================================================================
 POST-DEPLOYMENT ONE-LINERS (run separately if you want to re-check later)
--------------------------------------------------------------------------------
 SELECT MigrationId FROM dbo.__EFMigrationsHistory ORDER BY MigrationId DESC;   -- newest must be ...20261030073857_AddTicketCreatedBy
 SELECT TOP 1 TicketId, CreatedByEmployeeId FROM dbo.Tickets;                   -- the column GET /api/tickets reads
================================================================================

================================================================================
 ROLLBACK INSTRUCTIONS  (commented out on purpose - read before using)
--------------------------------------------------------------------------------
 Preferred rollback: restore the backup / snapshot taken before deployment.

 Schema-only rollback (below) is DESTRUCTIVE: it permanently deletes
   * CreatedByEmployeeId for every ticket (including those created since),
   * every TicketInteractionHandlers row,
   * the transfer-tracking values written to TicketWorkflowEvents.
 Do it only after the application build is rolled back to one that does NOT
 reference these objects (the current build reads Tickets.CreatedByEmployeeId on
 every ticket list, so the API fails without the column). Stop the API first.
 Order is the reverse of deployment. To roll back only some migrations, run only
 their sections, newest first.

 SET XACT_ABORT ON;
 BEGIN TRANSACTION;

 -- Undo 3: AddTicketCreatedBy
 IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Tickets_CreatedByEmployeeId' AND object_id = OBJECT_ID(N'dbo.Tickets'))
     DROP INDEX IX_Tickets_CreatedByEmployeeId ON dbo.Tickets;
 IF COL_LENGTH(N'dbo.Tickets', N'CreatedByEmployeeId') IS NOT NULL
     ALTER TABLE dbo.Tickets DROP COLUMN CreatedByEmployeeId;
 DELETE FROM dbo.__EFMigrationsHistory WHERE MigrationId = N'20261030073857_AddTicketCreatedBy';

 -- Undo 2: AddTicketTransferTracking
 IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_TicketWorkflowEvents_EventType_TransferReasonCode' AND object_id = OBJECT_ID(N'dbo.TicketWorkflowEvents'))
     DROP INDEX IX_TicketWorkflowEvents_EventType_TransferReasonCode ON dbo.TicketWorkflowEvents;
 IF COL_LENGTH(N'dbo.TicketWorkflowEvents', N'FromDepartmentId') IS NOT NULL ALTER TABLE dbo.TicketWorkflowEvents DROP COLUMN FromDepartmentId;
 IF COL_LENGTH(N'dbo.TicketWorkflowEvents', N'ToDepartmentId') IS NOT NULL ALTER TABLE dbo.TicketWorkflowEvents DROP COLUMN ToDepartmentId;
 IF COL_LENGTH(N'dbo.TicketWorkflowEvents', N'ToEmployeeId') IS NOT NULL ALTER TABLE dbo.TicketWorkflowEvents DROP COLUMN ToEmployeeId;
 IF COL_LENGTH(N'dbo.TicketWorkflowEvents', N'TransferReasonCode') IS NOT NULL ALTER TABLE dbo.TicketWorkflowEvents DROP COLUMN TransferReasonCode;
 DELETE FROM dbo.__EFMigrationsHistory WHERE MigrationId = N'20261030062642_AddTicketTransferTracking';

 -- Undo 1: AddTicketInteractionHandlers
 IF OBJECT_ID(N'dbo.TicketInteractionHandlers', N'U') IS NOT NULL
     DROP TABLE dbo.TicketInteractionHandlers;
 DELETE FROM dbo.__EFMigrationsHistory WHERE MigrationId = N'20261030061255_AddTicketInteractionHandlers';

 -- Check, then COMMIT (or ROLLBACK to abandon the rollback):
 SELECT MigrationId FROM dbo.__EFMigrationsHistory ORDER BY MigrationId DESC;
 COMMIT TRANSACTION;
================================================================================
*/
