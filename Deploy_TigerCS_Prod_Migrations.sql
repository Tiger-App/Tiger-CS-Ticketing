/*
================================================================================
 Deploy_TigerCS_Prod_Migrations.sql
 TigerCS Ticketing - PRODUCTION deployment of EF Core migrations (four new + one conditional older one)
 Target: server 10.10.10.124, database TigerCS_Ticketing_Prod
--------------------------------------------------------------------------------
 Production's newest recorded migration is
   20261022090000_AddGenesysServiceRequestToTicketInteractions
 This script applies, in this order, ONLY the migrations of the current repository
 that are not yet recorded in dbo.__EFMigrationsHistory (`has-pending-model-changes` = no drift).
 The repository has 41 migrations. Four are certainly newer than Production's latest; a fifth, OLDER one
 (0. below) is applied only if Production's history lacks it - EF would apply it too, because it applies any
 unrecorded migration regardless of order:

   0. 20260912110119_AddExternalCustomerSnapshotToTickets   (conditional)
        dbo.Tickets.ExternalCustomerEmail nvarchar(256) NULL, ExternalCustomerName nvarchar(200) NULL.

   1. 20261030061255_AddTicketInteractionHandlers
        new table dbo.TicketInteractionHandlers (+ 3 indexes, 3 foreign keys),
        and a copy of the existing single handler of each Genesys interaction
        (Source = 9 / Legacy).
   2. 20261030062642_AddTicketTransferTracking
        4 nullable columns + 1 index on dbo.TicketWorkflowEvents.
   3. 20261030073857_AddTicketCreatedBy
        dbo.Tickets.CreatedByEmployeeId (+ index) and its backfill from the
        first "Create" audit entry of each ticket.
   4. 20261031094748_AddDepartmentAssignmentRotation
        new table dbo.DepartmentAssignmentRotations (Round Robin cursor, FK to
        dbo.Departments, ON DELETE CASCADE). No data is copied.

 Production is NOT assumed to match a fresh database. Objects may already exist
 from manual scripts (AddTicketInteractionHandlers.sql, AddTicketTransferTracking.sql,
 AddTicketCreatedBy.sql, AddDepartmentAssignmentRotation.sql ...):
   * a migration already in the history table is skipped entirely;
   * for a migration NOT in the history table every object is created only if it
     is missing, and any object that already exists is VERIFIED (type, size,
     nullability, index keys, foreign-key targets and delete rules) before the
     history row is written - a table or column that exists with a different
     shape aborts and rolls back the whole run instead of being trusted;
   * the data copy / backfill only touch rows that still need it;
   * one application lock stops two runs from overlapping.

 Everything runs in ONE transaction with SET XACT_ABORT ON inside TRY/CATCH:
 any error, failed pre-check or failed verification rolls the whole run back.
 There is deliberately no GO in this file - run it as ONE batch (SSMS Execute,
 or sqlcmd -i with -f 65001).

 @DryRun DEFAULTS TO 1: it does all the work, runs every check, then ROLLS BACK.
 Review the output, then set @DryRun = 0 for the real run.

 BEFORE YOU RUN
   1. Take a full backup / snapshot of TigerCS_Ticketing_Prod and confirm it restores.
   2. Run the read-only PREFLIGHT block at the end of this file and review it.
   3. Stop (or quiesce) the Production API, Web and background workers while it runs:
      ALTER TABLE / CREATE INDEX on dbo.Tickets and dbo.TicketWorkflowEvents take
      schema locks, and the CreatedBy backfill reads all of dbo.AuditEntries.
   4. Deploy the application build only AFTER this script has committed (the new
      build reads Tickets.CreatedByEmployeeId and the new tables).

 Not run against any database by the author.

 ROLLBACK instructions are at the END of this file (commented out).
================================================================================
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET LOCK_TIMEOUT 60000;

-------------------------------------------------------------------------------
-- Settings
-------------------------------------------------------------------------------
DECLARE @ExpectedDatabase sysname = N'TigerCS_Ticketing_Prod';   -- REQUIRED: the run aborts on any other database.
DECLARE @DryRun bit = 1;                    -- 1 = apply, verify, then ROLL BACK (default). Set to 0 for the real run.

-------------------------------------------------------------------------------
-- Constants
-------------------------------------------------------------------------------
DECLARE @M0 nvarchar(150) = N'20260912110119_AddExternalCustomerSnapshotToTickets';
DECLARE @M1 nvarchar(150) = N'20261030061255_AddTicketInteractionHandlers';
DECLARE @M2 nvarchar(150) = N'20261030062642_AddTicketTransferTracking';
DECLARE @M3 nvarchar(150) = N'20261030073857_AddTicketCreatedBy';
DECLARE @M4 nvarchar(150) = N'20261031094748_AddDepartmentAssignmentRotation';
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

    IF @ExpectedDatabase IS NULL OR DB_NAME() <> @ExpectedDatabase
    BEGIN
        SET @msg = N'Wrong database: connected to [' + DB_NAME() + N'] but @ExpectedDatabase is [' + ISNULL(@ExpectedDatabase, N'(not set)') + N']. Nothing was changed.';
        THROW 50001, @msg, 1;
    END;

    IF @DryRun = 1
        PRINT N'*** DRY RUN: everything is applied and verified, then ROLLED BACK. ***';
    ELSE
        PRINT N'*** REAL RUN: the transaction will be COMMITTED if every check passes. ***';

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
        SET @msg = N'Earlier migrations are missing from Production and must be applied first (this script only adds the newest ones): ' + LEFT(@list, 1500);
        THROW 50004, @msg, 1;
    END;

    -- 1b. Tables and columns the migrations build on.
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
        (N'AspNetUsers', N'Id'),
        (N'Departments', N'DepartmentId');

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
    --     (PRINT cannot contain a subquery, so the state is gathered into variables first.)
    DECLARE @h0 nvarchar(10), @h1 nvarchar(10), @h2 nvarchar(10), @h3 nvarchar(10), @h4 nvarchar(10);
    SELECT @h0 = CASE WHEN EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId = @M0) THEN N'recorded' ELSE N'pending' END,
           @h1 = CASE WHEN EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId = @M1) THEN N'recorded' ELSE N'pending' END,
           @h2 = CASE WHEN EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId = @M2) THEN N'recorded' ELSE N'pending' END,
           @h3 = CASE WHEN EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId = @M3) THEN N'recorded' ELSE N'pending' END,
           @h4 = CASE WHEN EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId = @M4) THEN N'recorded' ELSE N'pending' END;
    PRINT N'  history: M0 ' + @h0 + N' | M1 ' + @h1 + N' | M2 ' + @h2 + N' | M3 ' + @h3 + N' | M4 ' + @h4;

    -- History rows this repository does not know about (a newer build was deployed, or a manual row was inserted): informational.
    SELECT @list = STUFF((SELECT N', ' + h.MigrationId FROM dbo.__EFMigrationsHistory AS h
        WHERE h.MigrationId NOT IN (SELECT MigrationId FROM @prior) AND h.MigrationId NOT IN (@M0, @M1, @M2, @M3, @M4)
        FOR XML PATH(N''), TYPE).value(N'.', N'nvarchar(max)'), 1, 2, N'');
    IF @list IS NOT NULL PRINT N'  NOTE - history contains migrations unknown to this repository: ' + LEFT(@list, 1500);

    DECLARE @o1 nvarchar(10), @o2 nvarchar(10), @o3 nvarchar(10), @o4 nvarchar(10);
    SET @o1 = CASE WHEN OBJECT_ID(N'dbo.TicketInteractionHandlers', N'U') IS NULL THEN N'absent' ELSE N'PRESENT' END;
    SET @o2 = CASE WHEN COL_LENGTH(N'dbo.Tickets', N'CreatedByEmployeeId') IS NULL THEN N'absent' ELSE N'PRESENT' END;
    SET @o3 = CASE WHEN COL_LENGTH(N'dbo.TicketWorkflowEvents', N'TransferReasonCode') IS NULL THEN N'absent' ELSE N'PRESENT' END;
    SET @o4 = CASE WHEN OBJECT_ID(N'dbo.DepartmentAssignmentRotations', N'U') IS NULL THEN N'absent' ELSE N'PRESENT' END;
    PRINT N'  objects: TicketInteractionHandlers=' + @o1 + N' | Tickets.CreatedByEmployeeId=' + @o2 + N' | TicketWorkflowEvents.TransferReasonCode=' + @o3 + N' | DepartmentAssignmentRotations=' + @o4;
    PRINT N'  (an object marked PRESENT whose migration is still pending came from a manual script: it is verified, not recreated)';

    -- Migration 1 copies TicketInteractions.HandledByUserId into a table with an FK to AspNetUsers: an orphaned id would fail the run mid-way.
    IF NOT EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId = @M1)
    BEGIN
        SELECT @n = COUNT_BIG(*) FROM dbo.TicketInteractions AS i
        WHERE i.HandledByUserId IS NOT NULL AND i.GenesysAgentUserId IS NOT NULL
          AND NOT EXISTS (SELECT 1 FROM dbo.AspNetUsers AS u WHERE u.Id = i.HandledByUserId);
        IF @n > 0
        BEGIN
            SET @msg = CONVERT(nvarchar(20), @n) + N' TicketInteractions row(s) name a HandledByUserId that is not in AspNetUsers; the legacy-handler copy (and its foreign key) would fail. Resolve them first. Nothing was changed.';
            THROW 50007, @msg, 1;
        END;
    END;

    SELECT @n = COUNT_BIG(*) FROM dbo.Tickets;
    PRINT N'  Tickets rows: ' + CONVERT(nvarchar(20), @n);
    SELECT @n = COUNT_BIG(*) FROM dbo.AuditEntries;
    PRINT N'  AuditEntries rows: ' + CONVERT(nvarchar(20), @n) + N'  (the CreatedBy backfill scans this table once per ticket - AuditEntries has no index on EntityType/Action/EntityId)';

    ---------------------------------------------------------------------------
    -- 2. DEPLOYMENT (single transaction)
    ---------------------------------------------------------------------------
    BEGIN TRANSACTION;

    EXEC @lockResult = sp_getapplock
        @Resource = N'TigerCS_Deploy_Prod_Migrations', @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 0;
    IF @lockResult < 0
        THROW 50006, N'Another run of this script holds the deployment lock. Nothing was changed.', 1;

    ---------------------------------------------------------------------------
    -- Migration 1 of 5 (conditional, OLDER than Production's newest): AddExternalCustomerSnapshotToTickets
    ---------------------------------------------------------------------------
    IF EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId = @M0)
        PRINT N'[1/5] ' + @M0 + N' - already recorded, skipped';
    ELSE
    BEGIN
        PRINT N'[1/5] ' + @M0 + N' - NOT recorded in Production: applying';

        IF COL_LENGTH(N'dbo.Tickets', N'ExternalCustomerEmail') IS NULL
            EXEC (N'ALTER TABLE dbo.Tickets ADD ExternalCustomerEmail nvarchar(256) NULL;');
        IF COL_LENGTH(N'dbo.Tickets', N'ExternalCustomerName') IS NULL
            EXEC (N'ALTER TABLE dbo.Tickets ADD ExternalCustomerName nvarchar(200) NULL;');

        INSERT INTO dbo.__EFMigrationsHistory (MigrationId, ProductVersion) VALUES (@M0, @ProductVersion);
    END;

    ---------------------------------------------------------------------------
    -- Migration 2 of 5: AddTicketInteractionHandlers
    ---------------------------------------------------------------------------
    IF EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId = @M1)
        PRINT N'[2/5] ' + @M1 + N' - already recorded, skipped';
    ELSE
    BEGIN
        PRINT N'[2/5] ' + @M1 + N' - applying';

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
    -- Migration 3 of 5: AddTicketTransferTracking
    ---------------------------------------------------------------------------
    IF EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId = @M2)
        PRINT N'[3/5] ' + @M2 + N' - already recorded, skipped';
    ELSE
    BEGIN
        PRINT N'[3/5] ' + @M2 + N' - applying';

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
    -- Migration 4 of 5: AddTicketCreatedBy
    ---------------------------------------------------------------------------
    IF EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId = @M3)
        PRINT N'[4/5] ' + @M3 + N' - already recorded, skipped';
    ELSE
    BEGIN
        PRINT N'[4/5] ' + @M3 + N' - applying';

        IF COL_LENGTH(N'dbo.Tickets', N'CreatedByEmployeeId') IS NULL
            EXEC (N'ALTER TABLE dbo.Tickets ADD CreatedByEmployeeId uniqueidentifier NULL;');

        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Tickets_CreatedByEmployeeId' AND object_id = OBJECT_ID(N'dbo.Tickets'))
            EXEC (N'CREATE INDEX IX_Tickets_CreatedByEmployeeId ON dbo.Tickets (CreatedByEmployeeId);');

        -- Backfill: the creator is the actor of the ticket's FIRST non-system "Create" audit entry.
        -- This is exactly the statement shipped with the migration (AddTicketCreatedBy.BackfillSql, covered by the
        -- application's SQL Server test). ticket creation writes ("Create", "Ticket", TicketId) with the calling
        -- employee as actor; for a Genesys inquiry that caller is the Genesys integration account. Tickets with no
        -- such entry (system-created, or never audited) stay NULL. Only NULL rows are touched, so re-running is safe.
        -- Production-sized variant: the SAME rule as AddTicketCreatedBy.BackfillSql (first non-system "Create" entry per ticket), written as one
        -- grouped pass over AuditEntries instead of one correlated scan per ticket (AuditEntries has no index on EntityType/Action/EntityId).
        SET @sql = N'
            ;WITH FirstCreate AS (
                SELECT a.[EntityId], MIN(a.[AuditEntryId]) AS [AuditEntryId]
                FROM [AuditEntries] AS a
                WHERE a.[EntityType] = ''Ticket'' AND a.[Action] = ''Create'' AND a.[ActorEmployeeId] IS NOT NULL
                GROUP BY a.[EntityId])
            UPDATE t SET t.[CreatedByEmployeeId] = src.[ActorEmployeeId]
            FROM [Tickets] AS t
            JOIN FirstCreate AS f ON f.[EntityId] = CAST(t.[TicketId] AS nvarchar(64))
            JOIN [AuditEntries] AS src ON src.[AuditEntryId] = f.[AuditEntryId]
            WHERE t.[CreatedByEmployeeId] IS NULL;
            SET @rc = @@ROWCOUNT;';
        EXEC sp_executesql @sql, N'@rc bigint OUTPUT', @rc = @n OUTPUT;
        PRINT N'      creators backfilled: ' + CONVERT(nvarchar(20), @n);

        INSERT INTO dbo.__EFMigrationsHistory (MigrationId, ProductVersion) VALUES (@M3, @ProductVersion);
    END;

    ---------------------------------------------------------------------------
    -- Migration 5 of 5: AddDepartmentAssignmentRotation
    ---------------------------------------------------------------------------
    IF EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId = @M4)
        PRINT N'[5/5] ' + @M4 + N' - already recorded, skipped';
    ELSE
    BEGIN
        PRINT N'[5/5] ' + @M4 + N' - applying';

        IF OBJECT_ID(N'dbo.DepartmentAssignmentRotations', N'U') IS NULL
        BEGIN
            EXEC (N'
            CREATE TABLE dbo.DepartmentAssignmentRotations (
                DepartmentId int NOT NULL,
                LastAssignedEmployeeId uniqueidentifier NULL,
                UpdatedAtUtc datetime2 NOT NULL,
                RowVersion rowversion NOT NULL,
                CONSTRAINT PK_DepartmentAssignmentRotations PRIMARY KEY (DepartmentId),
                CONSTRAINT FK_DepartmentAssignmentRotations_Departments_DepartmentId FOREIGN KEY (DepartmentId) REFERENCES dbo.Departments (DepartmentId) ON DELETE CASCADE
            );');
        END
        ELSE
            PRINT N'      table dbo.DepartmentAssignmentRotations already exists - it will be verified below, not recreated';

        INSERT INTO dbo.__EFMigrationsHistory (MigrationId, ProductVersion) VALUES (@M4, @ProductVersion);
    END;

    ---------------------------------------------------------------------------
    -- 3. VERIFICATION INSIDE THE TRANSACTION (any failure rolls everything back)
    ---------------------------------------------------------------------------
    PRINT N'--- Verification';

    -- 3a. History rows.
    SELECT @list = STUFF((SELECT N', ' + m.MigrationId
        FROM (VALUES (@M0), (@M1), (@M2), (@M3), (@M4)) AS m(MigrationId)
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
        (N'Tickets',                    N'ExternalCustomerEmail',     N'nvarchar',         512, 1),
        (N'Tickets',                    N'ExternalCustomerName',      N'nvarchar',         400, 1),
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
        (N'TicketInteractionHandlers',  N'RecordedAtUtc',             N'datetime2',        8,   0),
        (N'DepartmentAssignmentRotations', N'DepartmentId',           N'int',              4,   0),
        (N'DepartmentAssignmentRotations', N'LastAssignedEmployeeId', N'uniqueidentifier', 16,  1),
        (N'DepartmentAssignmentRotations', N'UpdatedAtUtc',           N'datetime2',        8,   0),
        (N'DepartmentAssignmentRotations', N'RowVersion',             N'timestamp',        8,   0);

    SELECT @list = STUFF((SELECT N', ' + e.TableName + N'.' + e.ColumnName
        FROM @col AS e
        WHERE NOT EXISTS (
        SELECT 1 FROM sys.columns AS c
        JOIN sys.types AS t ON t.user_type_id = c.user_type_id
        WHERE c.object_id = OBJECT_ID(N'dbo.' + e.TableName) AND c.name COLLATE DATABASE_DEFAULT = e.ColumnName
          AND t.name COLLATE DATABASE_DEFAULT = e.TypeName AND c.max_length = e.MaxLength AND c.is_nullable = e.IsNullable)
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

    -- 3c-i. Index KEY columns, in order, for the indexes whose shape matters (a same-named index on other columns is not accepted).
    DECLARE @keys TABLE (TableName sysname, IndexName sysname, KeyList nvarchar(400));
    INSERT INTO @keys VALUES
        (N'TicketInteractionHandlers', N'IX_TicketInteractionHandlers_HandledByUserId_TicketId', N'HandledByUserId,TicketId'),
        (N'TicketInteractionHandlers', N'IX_TicketInteractionHandlers_TicketId', N'TicketId'),
        (N'TicketInteractionHandlers', N'UX_TicketInteractionHandlers_InteractionId_UserId', N'TicketInteractionId,HandledByUserId'),
        (N'TicketWorkflowEvents', N'IX_TicketWorkflowEvents_EventType_TransferReasonCode', N'EventType,TransferReasonCode'),
        (N'Tickets', N'IX_Tickets_CreatedByEmployeeId', N'CreatedByEmployeeId'),
        (N'DepartmentAssignmentRotations', N'PK_DepartmentAssignmentRotations', N'DepartmentId');

    SELECT @list = STUFF((SELECT N', ' + k.IndexName
        FROM @keys AS k
        WHERE ISNULL((SELECT STUFF((SELECT N',' + c.name FROM sys.indexes AS x
                          JOIN sys.index_columns AS ic ON ic.object_id = x.object_id AND ic.index_id = x.index_id AND ic.is_included_column = 0
                          JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                          WHERE x.object_id = OBJECT_ID(N'dbo.' + k.TableName) AND x.name COLLATE DATABASE_DEFAULT = k.IndexName
                          ORDER BY ic.key_ordinal FOR XML PATH(N''), TYPE).value(N'.', N'nvarchar(max)'), 1, 1, N'')), N'') COLLATE DATABASE_DEFAULT <> k.KeyList
        FOR XML PATH(N''), TYPE).value(N'.', N'nvarchar(max)'), 1, 2, N'');
    IF @list IS NOT NULL
    BEGIN
        SET @msg = N'Verification failed - index exists with different key columns (or is missing): ' + @list;
        THROW 50017, @msg, 1;
    END;

    SELECT @list = STUFF((SELECT N', ' + i.IndexName
        FROM @idx AS i
        WHERE NOT EXISTS (SELECT 1 FROM sys.indexes AS x WHERE x.object_id = OBJECT_ID(N'dbo.' + i.TableName) AND x.name COLLATE DATABASE_DEFAULT = i.IndexName AND x.is_unique = i.IsUnique)
        FOR XML PATH(N''), TYPE).value(N'.', N'nvarchar(max)'), 1, 2, N'');
    IF @list IS NOT NULL
    BEGIN
        SET @msg = N'Verification failed - index missing: ' + @list;
        THROW 50012, @msg, 1;
    END;

    IF NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE name = N'PK_TicketInteractionHandlers' AND parent_object_id = OBJECT_ID(N'dbo.TicketInteractionHandlers'))
        THROW 50013, N'Verification failed - PK_TicketInteractionHandlers is missing.', 1;

    -- Foreign keys: parent table, referenced table and delete rule must all match (an existing same-named FK with another target is refused).
    DECLARE @fk TABLE (FkName sysname, ParentTable sysname, RefTable sysname, DeleteRule nvarchar(60));
    INSERT INTO @fk VALUES
        (N'FK_TicketInteractionHandlers_AspNetUsers_HandledByUserId', N'TicketInteractionHandlers', N'AspNetUsers', N'NO_ACTION'),
        (N'FK_TicketInteractionHandlers_TicketInteractions_TicketInteractionId', N'TicketInteractionHandlers', N'TicketInteractions', N'CASCADE'),
        (N'FK_TicketInteractionHandlers_Tickets_TicketId', N'TicketInteractionHandlers', N'Tickets', N'NO_ACTION'),
        (N'FK_DepartmentAssignmentRotations_Departments_DepartmentId', N'DepartmentAssignmentRotations', N'Departments', N'CASCADE');

    SELECT @list = STUFF((SELECT N', ' + f.FkName
        FROM @fk AS f
        WHERE NOT EXISTS (SELECT 1 FROM sys.foreign_keys AS k
                          WHERE k.name COLLATE DATABASE_DEFAULT = f.FkName AND k.parent_object_id = OBJECT_ID(N'dbo.' + f.ParentTable)
                            AND k.referenced_object_id = OBJECT_ID(N'dbo.' + f.RefTable) AND k.delete_referential_action_desc COLLATE DATABASE_DEFAULT = f.DeleteRule)
        FOR XML PATH(N''), TYPE).value(N'.', N'nvarchar(max)'), 1, 2, N'');
    IF @list IS NOT NULL
    BEGIN
        SET @msg = N'Verification failed - foreign key missing or with a different target/delete rule: ' + @list;
        THROW 50014, @msg, 1;
    END;

    IF NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE name = N'PK_DepartmentAssignmentRotations' AND parent_object_id = OBJECT_ID(N'dbo.DepartmentAssignmentRotations'))
        THROW 50018, N'Verification failed - PK_DepartmentAssignmentRotations is missing.', 1;

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
        WHERE MigrationId IN (@M0, @M1, @M2, @M3, @M4) ORDER BY MigrationId;

        -- R2: counts
        EXEC (N'
            SELECT (SELECT COUNT_BIG(*) FROM dbo.Tickets) AS Tickets,
                   (SELECT COUNT_BIG(*) FROM dbo.Tickets WHERE CreatedByEmployeeId IS NOT NULL) AS TicketsWithCreator,
                   (SELECT COUNT_BIG(*) FROM dbo.Tickets WHERE CreatedByEmployeeId IS NULL) AS TicketsWithoutCreator,
                   (SELECT COUNT_BIG(*) FROM dbo.TicketInteractionHandlers) AS HandlerRows,
                   (SELECT COUNT_BIG(*) FROM dbo.TicketInteractionHandlers WHERE Source = 9) AS LegacyHandlerRows,
                   (SELECT COUNT_BIG(*) FROM dbo.DepartmentAssignmentRotations) AS RotationRows;');

        -- R3: how Genesys-originated tickets (an originating interaction that carries a Genesys conversation id) were attributed.
        -- Expect ONE dominant creator: the genesys.integration account.
        EXEC (N'
            SELECT t.CreatedByEmployeeId, u.UserName AS CreatorUserName, COUNT_BIG(*) AS GenesysOriginatedTickets
            FROM dbo.Tickets AS t
            JOIN dbo.TicketInteractions AS i ON i.TicketId = t.TicketId AND i.IsOriginatingInteraction = 1 AND i.GenesysConversationId IS NOT NULL
            LEFT JOIN dbo.AspNetUsers AS u ON u.Id = t.CreatedByEmployeeId
            GROUP BY t.CreatedByEmployeeId, u.UserName
            ORDER BY COUNT_BIG(*) DESC;');

        -- R4: Genesys-originated tickets with NO creator: genesys.integration would NOT see these. Expect 0 (or a known, explained number) in Production.
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
 PREFLIGHT  (READ-ONLY - run these separately BEFORE the deployment, review, keep the output)
--------------------------------------------------------------------------------
 -- P1. Compare Production's history with the repository's 41 migrations.
 --     Expect: MissingInProduction = the four newest migrations (plus 20260912110119_AddExternalCustomerSnapshotToTickets if Production lacks it); UnknownInProduction = none.
 ;WITH Repo(MigrationId) AS (SELECT v FROM (VALUES
   (N'20260819072347_InitialIdentityAndAccess'),(N'20260820072044_AddCustomerVerification'),(N'20260820140000_AddTicketing'),
   (N'20260821090000_AddTicketOperations'),(N'20260821130000_AddSlaAndEscalation'),(N'20260822090000_AddNotificationsAndOutbox'),
   (N'20260825053743_AddIntakeRecordPhoneNumber'),(N'20260825061341_AddDepartmentCustomerLookupSource'),(N'20260825090047_AddDepartmentCustomerLookupSources'),
   (N'20260827090146_AddCrmBuyerLookupToTickets'),(N'20260901103309_AddExternalCustomerVerificationToTickets'),(N'20260903164723_AddWorkflowConfiguration'),
   (N'20260904075902_AddWorkflowAutomation'),(N'20260904082241_ReplaceInteractionContextWithTicketInteractions'),(N'20260904091143_AddApprovalWorkflow'),
   (N'20260908054944_AddWorkflowVersioning'),(N'20260909084839_AddChannels'),(N'20260910080219_AddGenesysIntegration'),
   (N'20260911105154_AddGenesysAgentMappingAndInteractionOwnership'),(N'20260912110119_AddExternalCustomerSnapshotToTickets'),(N'20260921133507_AddAgentHandoffTriggerAndConcurrency'),(N'20260928132618_AddGenesysScreenPopLaunches'),
   (N'20261005072511_AddCollectionsReminders'),(N'20261007093644_AddChatbotInactivityAndCrmDocumentCopies'),(N'20261007103153_AddResolutionClosedForCustomerInactivity'),
   (N'20261007180009_AddCustomerOtpVerification'),(N'20261008184722_AddSlaPauseAndPriorityDowngrade'),(N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
   (N'20261009074521_AddDispatchSuppressionAndVoiceEligibility'),(N'20261009102002_AddSmsChannelToCustomerOtp'),(N'20261012124810_AddAgentDeskDocumentDelivery'),
   (N'20261020090000_AddGenesysCampaignToDispatchBatch'),(N'20261020105026_AddLastCustomerInteractionToTicketInteractions'),(N'20261021090000_AddTowerToReviewRecords'),
   (N'20261021120000_AddAdminEmailToCollectionsDispatch'),(N'20261022081031_AddCustomerSatisfactionReviews'),(N'20261022090000_AddGenesysServiceRequestToTicketInteractions'),
   (N'20261030061255_AddTicketInteractionHandlers'),(N'20261030062642_AddTicketTransferTracking'),(N'20261030073857_AddTicketCreatedBy'),
   (N'20261031094748_AddDepartmentAssignmentRotation')) AS x(v))
 SELECT 'MissingInProduction' AS Kind, r.MigrationId FROM Repo r WHERE NOT EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory h WHERE h.MigrationId = r.MigrationId)
 UNION ALL
 SELECT 'UnknownInProduction', h.MigrationId FROM dbo.__EFMigrationsHistory h WHERE NOT EXISTS (SELECT 1 FROM Repo r WHERE r.MigrationId = h.MigrationId)
 ORDER BY 1, 2;

 -- P2. Which of this script's objects ALREADY exist (manual deployments)? PRESENT + migration missing = will be verified, not recreated.
 SELECT 'Table' AS Kind, N'TicketInteractionHandlers' AS Name, CASE WHEN OBJECT_ID(N'dbo.TicketInteractionHandlers', N'U') IS NULL THEN 'absent' ELSE 'PRESENT' END AS State
 UNION ALL SELECT 'Table', N'DepartmentAssignmentRotations', CASE WHEN OBJECT_ID(N'dbo.DepartmentAssignmentRotations', N'U') IS NULL THEN 'absent' ELSE 'PRESENT' END
 UNION ALL SELECT 'Column', N'Tickets.CreatedByEmployeeId', CASE WHEN COL_LENGTH(N'dbo.Tickets', N'CreatedByEmployeeId') IS NULL THEN 'absent' ELSE 'PRESENT' END
 UNION ALL SELECT 'Column', N'Tickets.ExternalCustomerEmail', CASE WHEN COL_LENGTH(N'dbo.Tickets', N'ExternalCustomerEmail') IS NULL THEN 'absent' ELSE 'PRESENT' END
 UNION ALL SELECT 'Column', N'Tickets.ExternalCustomerName', CASE WHEN COL_LENGTH(N'dbo.Tickets', N'ExternalCustomerName') IS NULL THEN 'absent' ELSE 'PRESENT' END
 UNION ALL SELECT 'Column', N'TicketWorkflowEvents.FromDepartmentId', CASE WHEN COL_LENGTH(N'dbo.TicketWorkflowEvents', N'FromDepartmentId') IS NULL THEN 'absent' ELSE 'PRESENT' END
 UNION ALL SELECT 'Column', N'TicketWorkflowEvents.ToDepartmentId', CASE WHEN COL_LENGTH(N'dbo.TicketWorkflowEvents', N'ToDepartmentId') IS NULL THEN 'absent' ELSE 'PRESENT' END
 UNION ALL SELECT 'Column', N'TicketWorkflowEvents.ToEmployeeId', CASE WHEN COL_LENGTH(N'dbo.TicketWorkflowEvents', N'ToEmployeeId') IS NULL THEN 'absent' ELSE 'PRESENT' END
 UNION ALL SELECT 'Column', N'TicketWorkflowEvents.TransferReasonCode', CASE WHEN COL_LENGTH(N'dbo.TicketWorkflowEvents', N'TransferReasonCode') IS NULL THEN 'absent' ELSE 'PRESENT' END
 UNION ALL SELECT 'Index', name, 'PRESENT' FROM sys.indexes
   WHERE name IN (N'IX_Tickets_CreatedByEmployeeId', N'IX_TicketWorkflowEvents_EventType_TransferReasonCode', N'IX_TicketInteractionHandlers_HandledByUserId_TicketId',
                  N'IX_TicketInteractionHandlers_TicketId', N'UX_TicketInteractionHandlers_InteractionId_UserId');

 -- P3. Size: drives the lock/backfill window.
 SELECT (SELECT COUNT_BIG(*) FROM dbo.Tickets) AS Tickets, (SELECT COUNT_BIG(*) FROM dbo.AuditEntries) AS AuditEntries,
        (SELECT COUNT_BIG(*) FROM dbo.TicketInteractions) AS TicketInteractions, (SELECT COUNT_BIG(*) FROM dbo.TicketWorkflowEvents) AS TicketWorkflowEvents;

 -- P4. Legacy handler copy: orphaned HandledByUserId (must be 0) and rows that would be copied.
 SELECT (SELECT COUNT_BIG(*) FROM dbo.TicketInteractions i WHERE i.HandledByUserId IS NOT NULL AND i.GenesysAgentUserId IS NOT NULL) AS WouldCopy,
        (SELECT COUNT_BIG(*) FROM dbo.TicketInteractions i WHERE i.HandledByUserId IS NOT NULL AND i.GenesysAgentUserId IS NOT NULL
            AND NOT EXISTS (SELECT 1 FROM dbo.AspNetUsers u WHERE u.Id = i.HandledByUserId)) AS Orphans;

 -- P5. Anything running against the database right now (quiesce before the real run).
 SELECT s.session_id, s.login_name, s.host_name, s.program_name, r.status, r.command, r.blocking_session_id
 FROM sys.dm_exec_sessions s LEFT JOIN sys.dm_exec_requests r ON r.session_id = s.session_id
 WHERE s.database_id = DB_ID() AND s.is_user_process = 1 AND s.session_id <> @@SPID;

 -- P6. Round Robin / Call Center prerequisites the new build relies on (data, not schema): an ACTIVE department with Code = 'CC'.
 SELECT DepartmentId, Name, Code, IsActive FROM dbo.Departments WHERE Code = N'CC';
================================================================================

================================================================================
 POST-DEPLOYMENT VERIFICATION  (READ-ONLY - run after a real run, and keep the output)
--------------------------------------------------------------------------------
 -- V1. The migrations are recorded and the newest is the rotation one.
 SELECT TOP 7 MigrationId, ProductVersion FROM dbo.__EFMigrationsHistory ORDER BY MigrationId DESC;

 -- V2. Columns (see the script's @col list). Type/size/nullability:
 SELECT o.name AS TableName, c.name AS ColumnName, t.name AS TypeName, c.max_length, c.is_nullable
 FROM sys.columns c JOIN sys.objects o ON o.object_id = c.object_id AND o.schema_id = SCHEMA_ID(N'dbo') JOIN sys.types t ON t.user_type_id = c.user_type_id
 WHERE (o.name = N'DepartmentAssignmentRotations')
    OR (o.name = N'TicketInteractionHandlers')
    OR (o.name = N'Tickets' AND c.name IN (N'CreatedByEmployeeId', N'ExternalCustomerEmail', N'ExternalCustomerName'))
    OR (o.name = N'TicketWorkflowEvents' AND c.name IN (N'FromDepartmentId', N'ToDepartmentId', N'ToEmployeeId', N'TransferReasonCode'))
 ORDER BY o.name, c.column_id;

 -- V3. Indexes, keys and foreign keys (expect 4 FKs: 3 on TicketInteractionHandlers, 1 on DepartmentAssignmentRotations; PK on both new tables).
 SELECT OBJECT_NAME(object_id) AS TableName, name, is_unique, is_primary_key FROM sys.indexes
 WHERE name IN (N'PK_TicketInteractionHandlers', N'PK_DepartmentAssignmentRotations', N'IX_Tickets_CreatedByEmployeeId', N'IX_TicketWorkflowEvents_EventType_TransferReasonCode',
                N'IX_TicketInteractionHandlers_HandledByUserId_TicketId', N'IX_TicketInteractionHandlers_TicketId', N'UX_TicketInteractionHandlers_InteractionId_UserId');
 SELECT name, OBJECT_NAME(parent_object_id) AS ParentTable, OBJECT_NAME(referenced_object_id) AS RefTable, delete_referential_action_desc, is_disabled, is_not_trusted
 FROM sys.foreign_keys WHERE name LIKE N'FK_TicketInteractionHandlers_%' OR name LIKE N'FK_DepartmentAssignmentRotations_%';

 -- V4. Data: legacy handlers fully copied (MissingCopies = 0); creators backfilled (WithAuditCreateButNoCreator = 0).
 SELECT (SELECT COUNT_BIG(*) FROM dbo.TicketInteractionHandlers) AS HandlerRows,
        (SELECT COUNT_BIG(*) FROM dbo.TicketInteractions i WHERE i.HandledByUserId IS NOT NULL AND i.GenesysAgentUserId IS NOT NULL
            AND NOT EXISTS (SELECT 1 FROM dbo.TicketInteractionHandlers h WHERE h.TicketInteractionId = i.TicketInteractionId AND h.HandledByUserId = i.HandledByUserId)) AS MissingCopies,
        (SELECT COUNT_BIG(*) FROM dbo.Tickets WHERE CreatedByEmployeeId IS NOT NULL) AS TicketsWithCreator,
        (SELECT COUNT_BIG(*) FROM dbo.Tickets WHERE CreatedByEmployeeId IS NULL) AS TicketsWithoutCreator,
        (SELECT COUNT_BIG(*) FROM dbo.Tickets t WHERE t.CreatedByEmployeeId IS NULL
            AND t.TicketId IN (SELECT TRY_CAST(a.EntityId AS bigint) FROM dbo.AuditEntries a WHERE a.EntityType = N'Ticket' AND a.Action = N'Create' AND a.ActorEmployeeId IS NOT NULL)) AS WithAuditCreateButNoCreator,
        (SELECT COUNT_BIG(*) FROM dbo.DepartmentAssignmentRotations) AS RotationRows;   -- 0 right after deployment; rows appear as tickets are round-robined

 -- V5. Exactly one CURRENT assignment per ticket (the application relies on this unique index; must return no rows).
 SELECT TicketId, COUNT_BIG(*) AS CurrentAssignments FROM dbo.TicketAssignments WHERE IsCurrent = 1 GROUP BY TicketId HAVING COUNT_BIG(*) > 1;
 SELECT name, filter_definition FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.TicketAssignments') AND is_unique = 1 AND filter_definition IS NOT NULL;

 -- V6. Genesys-originated tickets and their creator (the integration account's ticket visibility rests on CreatedByEmployeeId).
 SELECT t.CreatedByEmployeeId, u.UserName, COUNT_BIG(*) AS GenesysOriginatedTickets
 FROM dbo.Tickets t JOIN dbo.TicketInteractions i ON i.TicketId = t.TicketId AND i.IsOriginatingInteraction = 1 AND i.GenesysConversationId IS NOT NULL
 LEFT JOIN dbo.AspNetUsers u ON u.Id = t.CreatedByEmployeeId GROUP BY t.CreatedByEmployeeId, u.UserName ORDER BY COUNT_BIG(*) DESC;

 -- V7. Application smoke checks after the new build starts: GET /api/tickets returns 200; assign a test ticket and confirm a
 --     DepartmentAssignmentRotations row appears for its department; transfer a ticket and confirm a TicketWorkflowEvents row with ToDepartmentId set.
================================================================================

================================================================================
 ROLLBACK INSTRUCTIONS  (commented out on purpose - read before using)
--------------------------------------------------------------------------------
 Preferred rollback: restore the backup / snapshot taken before deployment.

 Schema-only rollback (below) is DESTRUCTIVE: it permanently deletes CreatedByEmployeeId for every ticket, every
 TicketInteractionHandlers row, the transfer-tracking values and the Round Robin cursors. Do it only after the application
 build is rolled back to one that does NOT reference these objects, with the API stopped. Reverse order; to roll back only
 some migrations run only their sections, newest first. Objects that existed BEFORE this deployment (manual scripts) are
 dropped too - back them up first if they hold data you need.

 SET XACT_ABORT ON;
 BEGIN TRANSACTION;

 -- Undo 4: AddDepartmentAssignmentRotation
 IF OBJECT_ID(N'dbo.DepartmentAssignmentRotations', N'U') IS NOT NULL DROP TABLE dbo.DepartmentAssignmentRotations;
 DELETE FROM dbo.__EFMigrationsHistory WHERE MigrationId = N'20261031094748_AddDepartmentAssignmentRotation';

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
 IF OBJECT_ID(N'dbo.TicketInteractionHandlers', N'U') IS NOT NULL DROP TABLE dbo.TicketInteractionHandlers;
 DELETE FROM dbo.__EFMigrationsHistory WHERE MigrationId = N'20261030061255_AddTicketInteractionHandlers';

 -- Check, then COMMIT (or ROLLBACK to abandon the rollback):
 SELECT MigrationId FROM dbo.__EFMigrationsHistory ORDER BY MigrationId DESC;
 COMMIT TRANSACTION;
================================================================================
*/
