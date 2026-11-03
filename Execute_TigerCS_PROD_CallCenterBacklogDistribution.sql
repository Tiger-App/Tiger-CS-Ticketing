/*
================================================================================
 Execute_TigerCS_PROD_CallCenterBacklogDistribution.sql
 Database : TigerCS_Ticketing_Prod          *** NOT EXECUTED - REQUIRES WRITTEN APPROVAL BEFORE @DryRun = 0 ***

 ONE-TIME redistribution (explicitly approved scope - replaces the narrower scope of PR #143/#144):
   EVERY ticket with Status = OPEN from EVERY department - unowned OR currently owned by another employee - is moved into the Call Center (CC) and given
   to exactly ONE eligible Call Center agent, using the Round Robin rules and persisted cursor of the existing engine (see Preview_..._ReadOnly.sql).
   Plain Round Robin: no workload balancing. Future tickets are unaffected: no application code, configuration or workflow is changed.
   The previous owner is REPLACED, never erased: the previous current TicketAssignments row is kept (IsCurrent = 0, exactly like TicketAssignmentAppService
   does on a reassignment), and the previous owner / assignment id / department are recorded in AuditEntries and in the TicketTransferred event.
   NOT TOUCHED: InProgress / Pending / Resolved / Closed tickets.
   HELD BACK and never touched: tickets with a PENDING APPROVAL (TicketApprovals.Status = 1), tickets with a RECORDED HUMAN HANDLER (see below) and tickets
   with an ACTIVE AI CONVERSATION (a live Genesys interaction nobody has taken and that needs no human) - listed by the preview; their counts are commit gates.
   RECORDED HUMAN HANDLER = the actual human who received / accepted / answered the customer interaction is on record: a dbo.TicketInteractionHandlers row,
   a TicketInteractions.HandledByUserId, or a HumanAgent transcript line (TicketInteractionMessages.Sender = 2). BUSINESS RULE: that person owns the ticket.
   Round Robin must never hand such a ticket to somebody else, so these tickets are not moved or assigned (no switch lifts this). Precedence when a ticket
   qualifies for several holds: pending approval, then recorded human handler, then active AI. Use Verify_TigerCS_PROD_BacklogBatch_OwnershipAudit_ReadOnly.sql to review them.

 PER ELIGIBLE TICKET IT WRITES (all in ONE transaction, all-or-nothing):
   * dbo.Tickets                        CurrentDepartmentId -> CC (only if it was elsewhere) and CurrentOwnerEmployeeId -> the dealt agent, in ONE UPDATE
   * dbo.TicketAssignments              previous current row -> IsCurrent = 0 (kept; never deleted or edited otherwise) and +1 current row
                                           (AssignedDepartmentId = CC, AssigningActorEmployeeId = NULL = system), exactly like AutoAssign
   * dbo.AuditEntries                   +1 'Transfer' row for a ticket that changes department (system actor) and +1 'AutoAssign' row for EVERY ticket
                                           (engine format + Trigger=BacklogDistribution;BatchId=...; BeforeValue = PreviousOwnerId / PreviousAssignmentId / PreviousDepartmentId)
   * dbo.TicketWorkflowEvents           +1 'TicketTransferred' event (reason SystemRouting, system actor, From -> CC, ToEmployeeId = agent, note names the previous owner)
                                           for a ticket that changes department OR had an owner (a reassignment). SystemRouting is the app's own "system moved it"
                                           reason: not an agent transfer, not an escalation, does not affect a Genesys handler's rights.
   and once per batch:
   * dbo.AuditEntries                   +1 'BacklogDistributionBatch' row (rotation cursor before/after, counts) - the rollback anchor
   * dbo.DepartmentAssignmentRotations  CC cursor advanced to the last agent dealt (as the engine does)
 NEVER CHANGED (proved by an in-transaction before/after comparison): OriginatingDepartmentId, RequestTypeId, CategoryId, PriorityId, TicketStatus,
   SlaState, EscalationLevel, VerificationStatus, CreatedAtUtc, and every pre-existing assignment row except its IsCurrent flag. Not written at all: status history,
   SLA instances/pauses, interactions, notes, resolutions, approvals, notifications/outbox, any non-Open ticket.
 No schema change. No notification is emitted (an assignment never emitted one in the app; avoids a bulk burst to agents).

 SAFETY:
   * DB-name guard; hard abort on any schema difference it depends on (incl. the unique filtered index "one current assignment per ticket").
   * @DryRun = 1 (DEFAULT): performs every write, runs every validation, prints the result sets, then ROLLS BACK.
   * COMMIT GATES (all required for @DryRun = 0): @ApprovedTicketCount, @ApprovedTransferCount, @ApprovedReassignedCount, @ApprovedAgentCount,
     @ApprovedHeldBackPendingApprovalCount and @ApprovedHeldBackActiveAiCount must equal what the run computes (copy the six numbers from the preview);
     service-account exclusions confirmed (ids listed AND @ServiceIdsConfirmed = 1, OR empty list AND @NoServiceAccountsExist = 1; never both) and every
     listed id must be a real employee; no earlier batch present.
   * Concurrency: sp_getapplock (blocks a second concurrent run); the CC rotation row is locked UPDLOCK/ROWLOCK exactly like the live app, so live
     intake serializes behind this run; candidate tickets are locked UPDLOCK/ROWLOCK, re-checked in the UPDATE (still Open, same owner, same department,
     same RowVersion) and the run aborts if even one row differs - so a person's concurrent reassignment either completes first (counts then differ and the
     run is refused) or fails on the RowVersion; the filtered unique index makes a duplicate current assignment impossible; the superseded assignment rows
     must be exactly the rows read at selection. The candidate and agent rows stay locked to commit, and AFTER the writes the run re-checks that no planned
     ticket gained a pending approval or an active AI conversation and that every new owner is STILL an eligible agent - any change aborts the run.
   * Idempotent: a second run is refused by the earlier batch's audit row.
   * Rollback of ONLY this batch: Rollback_TigerCS_PROD_CallCenterBacklogDistribution.sql with this run's @BatchId (printed + stored in AuditEntries);
     it restores the previous owner, previous department and the previous current assignment row of every ticket nobody has touched since.

 HOW TO RUN (SSMS, ONE batch, no GO):  1) Full backup.  2) Run with defaults (@DryRun = 1) on a restored copy first.
   3) After sign-off fill the gates below and set @DryRun = 0 in the approved window.
================================================================================
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;               -- required by the filtered index on TicketAssignments (SSMS default; sqlcmd needs -I)
SET ANSI_NULLS ON;
SET LOCK_TIMEOUT 20000;                 -- fail fast (20 s) instead of hanging behind a blocker

-- ------------------------------------------------ PARAMETERS ----------------------------------------------------------------
DECLARE @DryRun                  bit              = 1;     -- 1 = do everything, then ROLL BACK (default).  0 = COMMIT.
DECLARE @ApprovedTicketCount     int              = NULL;  -- commit gate: preview "ApprovedTicketCount"
DECLARE @ApprovedTransferCount   int             = NULL;  -- commit gate: preview "ApprovedTransferCount" (tickets that change department)
DECLARE @ApprovedReassignedCount int             = NULL;  -- commit gate: preview "ApprovedReassignedCount" (eligible tickets that currently have an owner - the owner is replaced)
DECLARE @ApprovedAgentCount      int              = NULL;  -- commit gate: preview "ApprovedAgentCount"
DECLARE @ApprovedHeldBackPendingApprovalCount int = NULL;  -- commit gate: preview "ApprovedHeldBackPendingApprovalCount" (tickets left alone: pending approval)
DECLARE @ApprovedHeldBackActiveAiCount        int = NULL;  -- commit gate: preview "ApprovedHeldBackActiveAiCount" (tickets left alone: active AI conversation only)
DECLARE @ApprovedHeldBackHumanHandledCount    int = NULL;  -- commit gate: preview "ApprovedHeldBackHumanHandledCount" (tickets left alone: a human handler is on record)
DECLARE @ServiceIdsConfirmed     bit              = 0;     -- commit gate: set 1 only after listing ALL service-account ids below
DECLARE @NoServiceAccountsExist  bit              = 0;     -- commit gate alternative: 1 ONLY if confirmed that no service/integration accounts exist (list stays empty)
DECLARE @IncludeLiveAiConversations bit          = 0;     -- keep 0 unless explicitly approved: 0 = tickets with an ACTIVE AI conversation are held back. MUST equal the Preview's value
DECLARE @CsAgentRoleOnly         bit              = 1;     -- 1 = only holders of the CS Agent role receive tickets (can Transfer as owner). MUST equal the Preview's value
DECLARE @ReassignTicketsAlreadyOwnedByEligibleCcAgent bit = 1;  -- MUST equal the Preview's value. 1 = literal rule (every Open ticket is dealt again). 0 = leave a CC ticket an eligible CC agent already owns
DECLARE @StaleAiAfterHours       int              = NULL;  -- MUST equal the Preview's value. NULL = off (default): "active AI" tickets stay held back. e.g. 24 = silent > 24 h with no human evidence is treated as ended
DECLARE @TreatHumanTouchedAiAsHistorical bit      = 0;     -- MUST equal the Preview's value. 0 = off (default)
DECLARE @ExcludeAgentsWithTicketDenials bit      = 1;     -- MUST equal the Preview's value. 1 = an agent with an explicit DENY of Tickets.View / Resolve / Close is not dealt tickets
DECLARE @AllowSecondBatch        bit              = 0;     -- keep 0: this is a ONE-TIME operation
DECLARE @BatchId                 uniqueidentifier = NEWID();-- correlation id of every row this run writes; needed by the rollback script
DECLARE @ExpectedDb              sysname          = N'TigerCS_Ticketing_Prod';

DECLARE @ServiceIdentity TABLE (EmployeeId uniqueidentifier PRIMARY KEY);
-- Authorization:ServiceIdentity:EmployeeIds  and  Collections:Authorization:IntegrationEmployeeIds  (one INSERT per id):
-- INSERT INTO @ServiceIdentity VALUES ('00000000-0000-0000-0000-000000000000');
-- ----------------------------------------------------------------------------------------------------------------------------

DECLARE @msg nvarchar(2000);
IF DB_NAME() <> @ExpectedDb
BEGIN
    SET @msg = N'ABORTED: connected to [' + ISNULL(DB_NAME(), N'?') + N'] on [' + CAST(@@SERVERNAME AS nvarchar(128)) + N']; this script only runs against [' + @ExpectedDb + N']. Nothing was changed.';
    THROW 50300, @msg, 1;
END;
PRINT N'Guard passed: server [' + CAST(@@SERVERNAME AS nvarchar(128)) + N'], database [' + DB_NAME() + N'], DryRun=' + CAST(@DryRun AS nvarchar(1)) + N', BatchId=' + CONVERT(nvarchar(36), @BatchId);

-- ---------------------------------------------------- schema pre-checks (read-only) -------------------------------------------
IF OBJECT_ID(N'dbo.DepartmentAssignmentRotations', N'U') IS NULL THROW 50301, 'ABORTED: dbo.DepartmentAssignmentRotations is missing (apply Deploy_TigerCS_Prod_Migrations.sql first).', 1;
IF NOT EXISTS (SELECT 1 FROM sys.indexes i
               WHERE i.object_id = OBJECT_ID(N'dbo.TicketAssignments') AND i.is_unique = 1 AND i.has_filter = 1
                 AND i.filter_definition LIKE N'%IsCurrent%=%1%'
                 AND (SELECT COUNT(*) FROM sys.index_columns ic WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0) = 1
                 AND EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                             WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND c.name = N'TicketId'))
    THROW 50302, 'ABORTED: the unique filtered index on dbo.TicketAssignments(TicketId) WHERE IsCurrent = 1 is missing - the duplicate-assignment guard this script relies on.', 1;

DECLARE @missing nvarchar(max) = N'';
SELECT @missing += v.Tbl + N'.' + v.Col + N' ' FROM (VALUES
    (N'Tickets', N'TicketId'), (N'Tickets', N'TicketNumber'), (N'Tickets', N'OriginatingDepartmentId'), (N'Tickets', N'CurrentDepartmentId'),
    (N'Tickets', N'CurrentOwnerEmployeeId'), (N'Tickets', N'TicketStatus'), (N'Tickets', N'CreatedAtUtc'), (N'Tickets', N'RowVersion'),
    (N'Tickets', N'RequestTypeId'), (N'Tickets', N'CategoryId'), (N'Tickets', N'PriorityId'), (N'Tickets', N'SlaState'),
    (N'Tickets', N'EscalationLevel'), (N'Tickets', N'VerificationStatus'),
    (N'TicketAssignments', N'TicketAssignmentId'), (N'TicketAssignments', N'TicketId'), (N'TicketAssignments', N'AssignedEmployeeId'), (N'TicketAssignments', N'AssignedDepartmentId'),
    (N'TicketAssignments', N'AssignedAtUtc'), (N'TicketAssignments', N'AssigningActorEmployeeId'), (N'TicketAssignments', N'IsCurrent'),
    (N'AuditEntries', N'ActorEmployeeId'), (N'AuditEntries', N'Action'), (N'AuditEntries', N'EntityType'), (N'AuditEntries', N'EntityId'),
    (N'AuditEntries', N'BeforeValue'), (N'AuditEntries', N'AfterValue'), (N'AuditEntries', N'CorrelationId'), (N'AuditEntries', N'OccurredAtUtc'),
    (N'TicketWorkflowEvents', N'TicketId'), (N'TicketWorkflowEvents', N'EventType'), (N'TicketWorkflowEvents', N'OccurredAtUtc'),
    (N'TicketWorkflowEvents', N'ActorEmployeeId'), (N'TicketWorkflowEvents', N'TicketApprovalId'), (N'TicketWorkflowEvents', N'Note'),
    (N'TicketWorkflowEvents', N'CorrelationId'), (N'TicketWorkflowEvents', N'FromDepartmentId'), (N'TicketWorkflowEvents', N'ToDepartmentId'),
    (N'TicketWorkflowEvents', N'ToEmployeeId'), (N'TicketWorkflowEvents', N'TransferReasonCode'),
    (N'TicketApprovals', N'TicketId'), (N'TicketApprovals', N'Status'), (N'TicketAgentHandoffs', N'TicketId'), (N'TicketAgentHandoffs', N'Status'),
    (N'TicketInteractions', N'TicketId'), (N'TicketInteractions', N'Source'), (N'TicketInteractions', N'EndedAtUtc'), (N'TicketInteractions', N'InactivityClosedAtUtc'),
    (N'TicketInteractions', N'RequiresHumanFollowUp'), (N'TicketInteractions', N'HandledByUserId'), (N'TicketInteractions', N'TicketInteractionId'),
    (N'TicketInteractionHandlers', N'TicketId'), (N'TicketInteractionMessages', N'TicketInteractionId'), (N'TicketInteractionMessages', N'Sender'),
    (N'DepartmentAssignmentRotations', N'DepartmentId'), (N'DepartmentAssignmentRotations', N'LastAssignedEmployeeId'),
    (N'DepartmentAssignmentRotations', N'UpdatedAtUtc'), (N'Employees', N'DeactivatedAtUtc'), (N'Employees', N'DisplayName'),
    (N'UserDepartmentAssignments', N'EmployeeId'), (N'UserDepartmentAssignments', N'DepartmentId'),
    (N'AspNetUsers', N'LockoutEnabled'), (N'AspNetUsers', N'LockoutEnd'), (N'AspNetUserRoles', N'UserId'), (N'AspNetRoles', N'Name'),
    (N'Departments', N'Code'), (N'Departments', N'IsActive'), (N'DepartmentWorkflowSettings', N'AllowAssignment')) v(Tbl, Col)
WHERE NOT EXISTS (SELECT 1 FROM sys.columns c WHERE c.object_id = OBJECT_ID(N'dbo.' + v.Tbl) AND c.name = v.Col);
IF LEN(@missing) > 0
BEGIN
    SET @msg = N'ABORTED: expected columns not found: ' + @missing;
    THROW 50303, @msg, 1;
END;

DECLARE @CcCount int = (SELECT COUNT(*) FROM dbo.Departments WHERE Code = N'CC' AND IsActive = 1);
IF @CcCount <> 1 THROW 50304, 'ABORTED: expected exactly one ACTIVE department with Code = CC.', 1;
DECLARE @CcId int = (SELECT DepartmentId FROM dbo.Departments WHERE Code = N'CC' AND IsActive = 1);
IF EXISTS (SELECT 1 FROM dbo.DepartmentWorkflowSettings WHERE DepartmentId = @CcId AND AllowAssignment = 0)
    THROW 50305, 'ABORTED: Call Center workflow settings disable assignment; the engine would leave tickets unassigned, so this script does too.', 1;

-- Service-account gate (IDENTICAL rule in the Preview script):
--   list NOT empty : @ServiceIdsConfirmed = 1 required, @NoServiceAccountsExist must be 0, every id must be a real employee.
--   list EMPTY     : the ONLY accepted state is @NoServiceAccountsExist = 1 (verified: no service/integration accounts in appsettings).
--                    @ServiceIdsConfirmed is not required then - there is no list to confirm.
DECLARE @ServiceListCount int = (SELECT COUNT(*) FROM @ServiceIdentity);
IF @DryRun = 0
BEGIN
    IF @ApprovedTicketCount IS NULL OR @ApprovedTransferCount IS NULL OR @ApprovedReassignedCount IS NULL OR @ApprovedAgentCount IS NULL
       OR @ApprovedHeldBackPendingApprovalCount IS NULL OR @ApprovedHeldBackActiveAiCount IS NULL OR @ApprovedHeldBackHumanHandledCount IS NULL
        THROW 50307, 'ABORTED: commit gates not satisfied - set the seven approved counts (tickets, moved, reassigned, agents, held-back pending approvals, held-back active AI, held-back human-handled) from the approved preview.', 1;
    IF @ServiceListCount > 0 AND ISNULL(@NoServiceAccountsExist, 0) = 1
        THROW 50350, 'ABORTED: contradictory service-account gates - ids are listed but @NoServiceAccountsExist = 1. Keep only one.', 1;
    IF @ServiceListCount > 0 AND ISNULL(@ServiceIdsConfirmed, 0) <> 1
        THROW 50307, 'ABORTED: commit gates not satisfied - service-account ids are listed but @ServiceIdsConfirmed <> 1.', 1;
    IF @ServiceListCount = 0 AND ISNULL(@NoServiceAccountsExist, 0) <> 1
        THROW 50308, 'ABORTED: the service-account list is empty. List every service/integration account id, or set @NoServiceAccountsExist = 1 if you have confirmed there are none.', 1;
    IF EXISTS (SELECT 1 FROM @ServiceIdentity s WHERE NOT EXISTS (SELECT 1 FROM dbo.Employees e WHERE e.EmployeeId = s.EmployeeId))
        THROW 50309, 'ABORTED: a listed service-account id is not an employee (typo?). Fix the list.', 1;
END
ELSE
BEGIN
    IF @ServiceListCount = 0 AND ISNULL(@NoServiceAccountsExist, 0) <> 1 PRINT N'WARNING: service-account list is empty and @NoServiceAccountsExist = 0 - a real run would be refused.';
    IF @ServiceListCount > 0 AND ISNULL(@ServiceIdsConfirmed, 0) <> 1 PRINT N'WARNING: ids are listed but @ServiceIdsConfirmed = 0 - a real run would be refused.';
    IF @ServiceListCount > 0 AND ISNULL(@NoServiceAccountsExist, 0) = 1 PRINT N'WARNING: ids are listed AND @NoServiceAccountsExist = 1 (contradiction) - a real run would be refused.';
END;

-- ---------------------------------------------------- before-snapshot of tables (informational) -----------------------------------
DECLARE @Snapshot TABLE (TableName sysname PRIMARY KEY, RowsBefore bigint NOT NULL, RowsAfter bigint NULL, Expected nvarchar(60) NOT NULL);
DECLARE @t sysname, @sql nvarchar(max), @rowCnt bigint;
DECLARE watched CURSOR LOCAL FAST_FORWARD FOR
    SELECT v.n FROM (VALUES (N'Tickets'), (N'TicketAssignments'), (N'AuditEntries'), (N'TicketWorkflowEvents'), (N'DepartmentAssignmentRotations'), (N'TicketStatusHistory'),
        (N'TicketSlaInstances'), (N'TicketSlaPausePeriods'), (N'TicketInteractions'), (N'TicketInteractionMessages'), (N'TicketNotes'),
        (N'TicketResolutions'), (N'TicketPendingRecords'), (N'TicketApprovals'), (N'TicketAgentHandoffs'), (N'TicketEscalations'),
        (N'Notifications'), (N'OutboxMessages'), (N'Employees'), (N'AspNetUsers'), (N'AspNetUserRoles')) v(n)
    WHERE OBJECT_ID(N'dbo.' + v.n, N'U') IS NOT NULL;
OPEN watched; FETCH NEXT FROM watched INTO @t;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @sql = N'SELECT @rowCnt = COUNT_BIG(*) FROM dbo.' + QUOTENAME(@t) + N' WITH (NOLOCK);';
    EXEC sp_executesql @sql, N'@rowCnt bigint OUTPUT', @rowCnt = @rowCnt OUTPUT;
    INSERT @Snapshot (TableName, RowsBefore, Expected)
    VALUES (@t, @rowCnt, CASE WHEN @t IN (N'Tickets', N'TicketAssignments', N'AuditEntries', N'TicketWorkflowEvents', N'DepartmentAssignmentRotations')
                         THEN N'written by this script' ELSE N'must not be written by this script' END);
    FETCH NEXT FROM watched INTO @t;
END;
CLOSE watched; DEALLOCATE watched;

CREATE TABLE #Agents (
    Rn int NOT NULL PRIMARY KEY, EmployeeId uniqueidentifier NOT NULL, DisplayName nvarchar(200) NULL, UserName nvarchar(256) NULL,
    SortKey char(36) COLLATE Latin1_General_BIN2 NOT NULL);
CREATE TABLE #StaleAi (TicketId bigint NOT NULL PRIMARY KEY, Reason varchar(20) NOT NULL, LastActivityUtc datetime2 NOT NULL);
CREATE TABLE #Deny (EmployeeId uniqueidentifier NOT NULL, PermissionKey nvarchar(100) NOT NULL);
CREATE TABLE #AsgBefore (TicketAssignmentId bigint NOT NULL PRIMARY KEY, TicketId bigint NOT NULL, AssignedEmployeeId uniqueidentifier NOT NULL, AssignedDepartmentId int NOT NULL,
    AssignedAtUtc datetime2 NOT NULL, AssigningActorEmployeeId uniqueidentifier NULL, IsCurrent bit NOT NULL);
CREATE TABLE #Cand (Seq int NOT NULL PRIMARY KEY, TicketId bigint NOT NULL, TicketNumber nvarchar(80) NULL, TicketStatus tinyint NOT NULL,
    CreatedAtUtc datetime2 NOT NULL, FromDepartmentId int NOT NULL, OriginatingDepartmentId int NOT NULL, IsMove bit NOT NULL,
    PrevOwnerId uniqueidentifier NULL, PrevAssignmentId bigint NULL,
    RequestTypeId int NULL, CategoryId int NULL, PriorityId tinyint NULL, SlaState tinyint NOT NULL, EscalationLevel tinyint NOT NULL,
    VerificationStatus tinyint NOT NULL, RowVer binary(8) NOT NULL);
CREATE TABLE #Plan (Seq int NOT NULL PRIMARY KEY, TicketId bigint NOT NULL, AgentRn int NOT NULL, EmployeeId uniqueidentifier NOT NULL,
    FromDepartmentId int NOT NULL, IsMove bit NOT NULL, RowVer binary(8) NOT NULL, PrevOwnerId uniqueidentifier NULL, PrevAssignmentId bigint NULL);

DECLARE @Now datetime2 = SYSUTCDATETIME();
DECLARE @RunStartUtc datetime2 = SYSUTCDATETIME();   -- an interaction created after this moment on a "stale" ticket is a NEW conversation and aborts the run (Msg 50341)
DECLARE @Cursor uniqueidentifier, @Start int = 1, @N int, @Cand int, @Moved int, @Reassigned int, @Events int, @Superseded int, @HeldApproval int, @HeldAiOnly int, @HeldHuman int, @Updated int, @Inserted int, @EventsInserted int, @LastAgent uniqueidentifier,
        @MaxAuditIdBefore bigint, @MaxEventIdBefore bigint;

BEGIN TRY
    BEGIN TRANSACTION;

    -- (a) one run at a time -----------------------------------------------------------------------------------------------
    DECLARE @lock int;
    EXEC @lock = sp_getapplock @Resource = N'TigerCS.CallCenterBacklogDistribution', @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 0;
    IF @lock < 0 THROW 50310, 'ABORTED: another backlog-distribution run holds the application lock.', 1;

    -- one-time guard: checked only AFTER the application lock, so a second concurrent run fails immediately on the lock instead of
    -- blocking on the first run's uncommitted audit rows
    IF @AllowSecondBatch = 0 AND EXISTS (SELECT 1 FROM dbo.AuditEntries WHERE Action = N'BacklogDistributionBatch')
        THROW 50306, 'ABORTED: a BacklogDistributionBatch audit row already exists. This is a one-time operation (set @AllowSecondBatch = 1 only with explicit approval).', 1;

    SET @MaxAuditIdBefore = ISNULL((SELECT MAX(AuditEntryId) FROM dbo.AuditEntries), 0);
    SET @MaxEventIdBefore = ISNULL((SELECT MAX(TicketWorkflowEventId) FROM dbo.TicketWorkflowEvents), 0);

    -- (b) lock the CC rotation cursor exactly like AssignmentRotationRepository.GetForUpdateAsync (live intake now waits behind this run)
    IF NOT EXISTS (SELECT 1 FROM dbo.DepartmentAssignmentRotations WITH (UPDLOCK, HOLDLOCK) WHERE DepartmentId = @CcId)
        INSERT INTO dbo.DepartmentAssignmentRotations (DepartmentId, LastAssignedEmployeeId, UpdatedAtUtc) VALUES (@CcId, NULL, @Now);
    SELECT @Cursor = LastAssignedEmployeeId FROM dbo.DepartmentAssignmentRotations WITH (UPDLOCK, ROWLOCK) WHERE DepartmentId = @CcId;

    -- explicit permission DENIALS (dbo.UserPermissionOverrides, Effect 2) of View / Resolve / Close - the table exists only after AddUserPermissionOverrides
    IF OBJECT_ID(N'dbo.UserPermissionOverrides', N'U') IS NOT NULL
        EXEC sp_executesql N'INSERT #Deny (EmployeeId, PermissionKey) SELECT EmployeeId, PermissionKey FROM dbo.UserPermissionOverrides
                             WHERE Effect = 2 AND PermissionKey IN (N''Tickets.View'', N''Tickets.Resolve'', N''Tickets.Close'')';

    -- stale / human-touched "active AI" conversations (proposed correction, in memory; both switches default OFF) - same SQL as the Preview
    IF @StaleAiAfterHours IS NOT NULL OR @TreatHumanTouchedAiAsHistorical = 1
        INSERT #StaleAi (TicketId, Reason, LastActivityUtc)
        SELECT x.TicketId, CASE WHEN x.Human = 1 THEN 'HUMAN_TOUCHED' ELSE 'STALE' END, x.LastActivityUtc
        FROM (SELECT t.TicketId,
                     CASE WHEN EXISTS (SELECT 1 FROM dbo.TicketInteractions i JOIN dbo.TicketInteractionMessages mm ON mm.TicketInteractionId = i.TicketInteractionId WHERE i.TicketId = t.TicketId AND mm.Sender = 2)
                            OR EXISTS (SELECT 1 FROM dbo.TicketInteractionHandlers h WHERE h.TicketId = t.TicketId)
                            OR t.FirstHumanResponseAtUtc IS NOT NULL
                            OR EXISTS (SELECT 1 FROM dbo.TicketAgentHandoffs h WHERE h.TicketId = t.TicketId)
                            OR EXISTS (SELECT 1 FROM dbo.TicketWorkflowEvents e WHERE e.TicketId = t.TicketId AND e.EventType IN (11, 12, 18, 19)) THEN 1 ELSE 0 END AS Human,
                     (SELECT MAX(v) FROM (VALUES
                         ((SELECT MAX(z.v) FROM dbo.TicketInteractions i CROSS APPLY (VALUES (i.CreatedAtUtc), (i.InteractionStartedAtUtc), (i.LastCustomerInteractionAtUtc), (i.AwaitingCustomerReplySinceUtc)) z(v) WHERE i.TicketId = t.TicketId)),
                         ((SELECT MAX(mm.SentAtUtc) FROM dbo.TicketInteractions i JOIN dbo.TicketInteractionMessages mm ON mm.TicketInteractionId = i.TicketInteractionId WHERE i.TicketId = t.TicketId)),
                         ((SELECT MAX(e.OccurredAtUtc) FROM dbo.TicketWorkflowEvents e WHERE e.TicketId = t.TicketId AND e.EventType BETWEEN 10 AND 19)),
                         (t.CreatedAtUtc)) a(v)) AS LastActivityUtc
              FROM dbo.Tickets t
              WHERE t.TicketStatus = 1
                AND (EXISTS (SELECT 1 FROM dbo.TicketInteractions i WHERE i.TicketId = t.TicketId AND i.Source = 2 AND i.EndedAtUtc IS NULL
                             AND i.InactivityClosedAtUtc IS NULL AND i.RequiresHumanFollowUp = 0 AND i.HandledByUserId IS NULL)
                     AND NOT EXISTS (SELECT 1 FROM dbo.TicketInteractions i2 WHERE i2.TicketId = t.TicketId AND i2.RequiresHumanFollowUp = 1)
                     AND NOT EXISTS (SELECT 1 FROM dbo.TicketAgentHandoffs h WHERE h.TicketId = t.TicketId AND h.Status IN (2, 3, 4)))) x
        WHERE (x.Human = 1 AND @TreatHumanTouchedAiAsHistorical = 1)
           OR (x.Human = 0 AND @StaleAiAfterHours IS NOT NULL AND x.LastActivityUtc < DATEADD(hour, -@StaleAiAfterHours, SYSUTCDATETIME()));


    -- (c) eligible agents, Round Robin order (same rules as FilterEligibleAsync + explicit denials) ----------------------------
    INSERT #Agents (Rn, EmployeeId, DisplayName, UserName, SortKey)
    SELECT ROW_NUMBER() OVER (ORDER BY CONVERT(char(36), e.EmployeeId) COLLATE Latin1_General_BIN2),
           e.EmployeeId, e.DisplayName, u.UserName, CONVERT(char(36), e.EmployeeId) COLLATE Latin1_General_BIN2
    FROM dbo.Employees AS e WITH (UPDLOCK, ROWLOCK)      -- an agent cannot be deactivated while the batch runs
    JOIN dbo.AspNetUsers AS u ON u.Id = e.EmployeeId
    WHERE e.DeactivatedAtUtc IS NULL
      AND EXISTS (SELECT 1 FROM dbo.UserDepartmentAssignments a WHERE a.EmployeeId = e.EmployeeId AND a.DepartmentId = @CcId)
      AND NOT (u.LockoutEnabled = 1 AND u.LockoutEnd IS NOT NULL AND u.LockoutEnd > SYSDATETIMEOFFSET())
      AND NOT EXISTS (SELECT 1 FROM @ServiceIdentity s WHERE s.EmployeeId = e.EmployeeId)
      AND (@ExcludeAgentsWithTicketDenials = 0 OR NOT EXISTS (SELECT 1 FROM #Deny d WHERE d.EmployeeId = e.EmployeeId))
      AND EXISTS (SELECT 1 FROM dbo.AspNetUserRoles ur JOIN dbo.AspNetRoles r ON r.Id = ur.RoleId
                  WHERE ur.UserId = e.EmployeeId AND r.Name IN (N'CS Agent', N'Department Employee', N'Department Head'))
      AND (@CsAgentRoleOnly = 0 OR EXISTS (SELECT 1 FROM dbo.AspNetUserRoles ur JOIN dbo.AspNetRoles r ON r.Id = ur.RoleId
                                           WHERE ur.UserId = e.EmployeeId AND r.Name = N'CS Agent'));
    SET @N = (SELECT COUNT(*) FROM #Agents);
    IF @N = 0 THROW 50311, 'ABORTED: no eligible Call Center agent.', 1;
    IF @DryRun = 0 AND @N <> @ApprovedAgentCount
    BEGIN
        SET @msg = N'ABORTED: eligible agents = ' + CONVERT(nvarchar(10), @N) + N' but @ApprovedAgentCount = ' + CONVERT(nvarchar(10), @ApprovedAgentCount) + N'. Re-run the preview and re-approve.';
        THROW 50312, @msg, 1;
    END;

    -- (d) eligible tickets from EVERY department, LOCKED for this transaction: Open only, owned or not. Held back (never selected, never locked, never touched):
    --     a PENDING APPROVAL, or an ACTIVE AI CONVERSATION (live Genesys interaction nobody took, no human needed, no open handoff).
    INSERT #Cand (Seq, TicketId, TicketNumber, TicketStatus, CreatedAtUtc, FromDepartmentId, OriginatingDepartmentId, IsMove,
                  PrevOwnerId, PrevAssignmentId, RequestTypeId, CategoryId, PriorityId, SlaState, EscalationLevel, VerificationStatus, RowVer)
    SELECT ROW_NUMBER() OVER (ORDER BY t.CreatedAtUtc, t.TicketId), t.TicketId, t.TicketNumber, t.TicketStatus, t.CreatedAtUtc,
           t.CurrentDepartmentId, t.OriginatingDepartmentId, CASE WHEN t.CurrentDepartmentId <> @CcId THEN 1 ELSE 0 END,
           t.CurrentOwnerEmployeeId, (SELECT TOP (1) a.TicketAssignmentId FROM dbo.TicketAssignments a WHERE a.TicketId = t.TicketId AND a.IsCurrent = 1),
           t.RequestTypeId, t.CategoryId, t.PriorityId, t.SlaState, t.EscalationLevel, t.VerificationStatus, t.RowVersion
    FROM dbo.Tickets AS t WITH (UPDLOCK, ROWLOCK)
    WHERE t.TicketStatus = 1                                                                      -- OPEN only
      AND NOT EXISTS (SELECT 1 FROM dbo.TicketApprovals ap WHERE ap.TicketId = t.TicketId AND ap.Status = 1)
      AND NOT (EXISTS (SELECT 1 FROM dbo.TicketInteractionHandlers hh WHERE hh.TicketId = t.TicketId)
       OR EXISTS (SELECT 1 FROM dbo.TicketInteractions hi WHERE hi.TicketId = t.TicketId AND hi.HandledByUserId IS NOT NULL)
       OR EXISTS (SELECT 1 FROM dbo.TicketInteractions hj JOIN dbo.TicketInteractionMessages hm ON hm.TicketInteractionId = hj.TicketInteractionId
                  WHERE hj.TicketId = t.TicketId AND hm.Sender = 2))   -- a recorded human handler owns it: never re-dealt
      AND (@IncludeLiveAiConversations = 1 OR NOT (EXISTS (SELECT 1 FROM dbo.TicketInteractions i WHERE i.TicketId = t.TicketId AND i.Source = 2 AND i.EndedAtUtc IS NULL
                     AND i.InactivityClosedAtUtc IS NULL AND i.RequiresHumanFollowUp = 0 AND i.HandledByUserId IS NULL)
       AND NOT EXISTS (SELECT 1 FROM dbo.TicketInteractions i2 WHERE i2.TicketId = t.TicketId AND i2.RequiresHumanFollowUp = 1)
       AND NOT EXISTS (SELECT 1 FROM dbo.TicketAgentHandoffs h WHERE h.TicketId = t.TicketId AND h.Status IN (2, 3, 4))
       AND NOT EXISTS (SELECT 1 FROM #StaleAi sa WHERE sa.TicketId = t.TicketId)))
      AND NOT (@ReassignTicketsAlreadyOwnedByEligibleCcAgent = 0 AND t.CurrentDepartmentId = @CcId
               AND EXISTS (SELECT 1 FROM #Agents ag WHERE ag.EmployeeId = t.CurrentOwnerEmployeeId));
    SET @Cand = (SELECT COUNT(*) FROM #Cand);
    SET @Moved = (SELECT COUNT(*) FROM #Cand WHERE IsMove = 1);
    SET @Reassigned = (SELECT COUNT(*) FROM #Cand WHERE PrevOwnerId IS NOT NULL);
    SET @Events = (SELECT COUNT(*) FROM #Cand WHERE IsMove = 1 OR PrevOwnerId IS NOT NULL);
    -- pre-image of EVERY assignment row of the candidates (the "nothing was deleted or edited except IsCurrent" proof below)
    INSERT #AsgBefore SELECT a.TicketAssignmentId, a.TicketId, a.AssignedEmployeeId, a.AssignedDepartmentId, a.AssignedAtUtc, a.AssigningActorEmployeeId, a.IsCurrent
    FROM dbo.TicketAssignments a JOIN #Cand c ON c.TicketId = a.TicketId;
    -- held-back counts (read only, not locked): Open tickets with a pending approval (with or without an AI conversation) and AI-only Open tickets
    SELECT @HeldApproval = COUNT(*) FROM dbo.Tickets AS t
    WHERE t.TicketStatus = 1 AND EXISTS (SELECT 1 FROM dbo.TicketApprovals ap WHERE ap.TicketId = t.TicketId AND ap.Status = 1);
    SELECT @HeldHuman = COUNT(*) FROM dbo.Tickets AS t
    WHERE t.TicketStatus = 1 AND NOT EXISTS (SELECT 1 FROM dbo.TicketApprovals ap WHERE ap.TicketId = t.TicketId AND ap.Status = 1)
      AND (EXISTS (SELECT 1 FROM dbo.TicketInteractionHandlers hh WHERE hh.TicketId = t.TicketId)
       OR EXISTS (SELECT 1 FROM dbo.TicketInteractions hi WHERE hi.TicketId = t.TicketId AND hi.HandledByUserId IS NOT NULL)
       OR EXISTS (SELECT 1 FROM dbo.TicketInteractions hj JOIN dbo.TicketInteractionMessages hm ON hm.TicketInteractionId = hj.TicketInteractionId
                  WHERE hj.TicketId = t.TicketId AND hm.Sender = 2));
    SELECT @HeldAiOnly = COUNT(*) FROM dbo.Tickets AS t
    WHERE t.TicketStatus = 1 AND NOT EXISTS (SELECT 1 FROM dbo.TicketApprovals ap WHERE ap.TicketId = t.TicketId AND ap.Status = 1)
      AND NOT (EXISTS (SELECT 1 FROM dbo.TicketInteractionHandlers hh WHERE hh.TicketId = t.TicketId)
       OR EXISTS (SELECT 1 FROM dbo.TicketInteractions hi WHERE hi.TicketId = t.TicketId AND hi.HandledByUserId IS NOT NULL)
       OR EXISTS (SELECT 1 FROM dbo.TicketInteractions hj JOIN dbo.TicketInteractionMessages hm ON hm.TicketInteractionId = hj.TicketInteractionId
                  WHERE hj.TicketId = t.TicketId AND hm.Sender = 2)) AND @IncludeLiveAiConversations = 0 AND (EXISTS (SELECT 1 FROM dbo.TicketInteractions i WHERE i.TicketId = t.TicketId AND i.Source = 2 AND i.EndedAtUtc IS NULL
                     AND i.InactivityClosedAtUtc IS NULL AND i.RequiresHumanFollowUp = 0 AND i.HandledByUserId IS NULL)
       AND NOT EXISTS (SELECT 1 FROM dbo.TicketInteractions i2 WHERE i2.TicketId = t.TicketId AND i2.RequiresHumanFollowUp = 1)
       AND NOT EXISTS (SELECT 1 FROM dbo.TicketAgentHandoffs h WHERE h.TicketId = t.TicketId AND h.Status IN (2, 3, 4))
       AND NOT EXISTS (SELECT 1 FROM #StaleAi sa WHERE sa.TicketId = t.TicketId));
    IF @Cand = 0 THROW 50313, 'NOTHING TO DO: no eligible Open tickets. Nothing was changed.', 1;
    IF @DryRun = 0 AND (@Cand <> @ApprovedTicketCount OR @Moved <> @ApprovedTransferCount OR @Reassigned <> @ApprovedReassignedCount
                        OR @HeldApproval <> @ApprovedHeldBackPendingApprovalCount OR @HeldAiOnly <> @ApprovedHeldBackActiveAiCount OR @HeldHuman <> @ApprovedHeldBackHumanHandledCount)
    BEGIN
        SET @msg = N'ABORTED: eligible tickets = ' + CONVERT(nvarchar(10), @Cand) + N' (moved ' + CONVERT(nvarchar(10), @Moved) + N', owner replaced ' + CONVERT(nvarchar(10), @Reassigned)
                 + N', held back: approvals ' + CONVERT(nvarchar(10), @HeldApproval) + N', AI ' + CONVERT(nvarchar(10), @HeldAiOnly) + N', human-handled ' + CONVERT(nvarchar(10), @HeldHuman) + N') but approved = '
                 + CONVERT(nvarchar(10), @ApprovedTicketCount) + N' (moved ' + CONVERT(nvarchar(10), @ApprovedTransferCount) + N', owner replaced ' + CONVERT(nvarchar(10), @ApprovedReassignedCount)
                 + N', held back: approvals ' + CONVERT(nvarchar(10), @ApprovedHeldBackPendingApprovalCount) + N', AI ' + CONVERT(nvarchar(10), @ApprovedHeldBackActiveAiCount) + N', human-handled ' + CONVERT(nvarchar(10), @ApprovedHeldBackHumanHandledCount)
                 + N'). The backlog changed since the preview - re-run the preview and re-approve.';
        THROW 50314, @msg, 1;
    END;

    -- (e) the deal: next eligible after the cursor, wrapping (DepartmentAssignmentRotation.PickNext) --------------------------
    IF @Cursor IS NOT NULL
        SELECT @Start = ISNULL(MIN(Rn), 1) FROM #Agents WHERE SortKey > CONVERT(char(36), @Cursor) COLLATE Latin1_General_BIN2;
    INSERT #Plan (Seq, TicketId, AgentRn, EmployeeId, FromDepartmentId, IsMove, RowVer, PrevOwnerId, PrevAssignmentId)
    SELECT c.Seq, c.TicketId, a.Rn, a.EmployeeId, c.FromDepartmentId, c.IsMove, c.RowVer, c.PrevOwnerId, c.PrevAssignmentId
    FROM #Cand c JOIN #Agents a ON a.Rn = ((@Start - 1 + c.Seq - 1) % @N) + 1;
    IF (SELECT COUNT(*) FROM #Plan) <> @Cand THROW 50315, 'ABORTED: internal check failed - plan size differs from candidate count.', 1;
    SELECT TOP (1) @LastAgent = EmployeeId FROM #Plan ORDER BY Seq DESC;

    -- (f) writes ------------------------------------------------------------------------------------------------------------------
    -- Department move + ownership in ONE statement. Guarded by: still Open, still owned by the same person (or still unowned), still in the department we
    -- planned from, RowVersion unchanged. ALL rows must match or the whole run aborts.
    UPDATE t SET CurrentDepartmentId = @CcId, CurrentOwnerEmployeeId = p.EmployeeId
    FROM dbo.Tickets AS t
    JOIN #Plan AS p ON p.TicketId = t.TicketId AND t.RowVersion = p.RowVer
    WHERE t.TicketStatus = 1 AND t.CurrentDepartmentId = p.FromDepartmentId
      AND ((t.CurrentOwnerEmployeeId IS NULL AND p.PrevOwnerId IS NULL) OR t.CurrentOwnerEmployeeId = p.PrevOwnerId);
    SET @Updated = @@ROWCOUNT;
    IF @Updated <> @Cand
    BEGIN
        SET @msg = N'ABORTED: ' + CONVERT(nvarchar(10), @Cand - @Updated) + N' ticket(s) changed while the run was in progress (status/owner/department/rowversion). Whole run rolled back.';
        THROW 50316, @msg, 1;
    END;

    -- Retire the previous current assignment row (history is KEPT: same as TicketAssignment.MarkSuperseded). It must be exactly the row read at selection.
    DECLARE @Sup TABLE (TicketAssignmentId bigint NOT NULL PRIMARY KEY);
    UPDATE a SET IsCurrent = 0 OUTPUT inserted.TicketAssignmentId INTO @Sup
    FROM dbo.TicketAssignments a JOIN #Plan p ON p.TicketId = a.TicketId WHERE a.IsCurrent = 1;
    SET @Superseded = @@ROWCOUNT;
    IF EXISTS (SELECT 1 FROM @Sup s WHERE NOT EXISTS (SELECT 1 FROM #Plan p WHERE p.PrevAssignmentId = s.TicketAssignmentId))
       OR EXISTS (SELECT 1 FROM #Plan p WHERE p.PrevAssignmentId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM @Sup s WHERE s.TicketAssignmentId = p.PrevAssignmentId))
        THROW 50317, 'ABORTED: the assignment rows superseded are not exactly the rows read at selection (a concurrent assignment). Whole run rolled back.', 1;

    -- New current assignment rows in CC (system actor = NULL, like AutoAssign). The filtered unique index rejects a second current row.
    INSERT dbo.TicketAssignments (TicketId, AssignedEmployeeId, AssignedDepartmentId, AssignedAtUtc, AssigningActorEmployeeId, IsCurrent)
    SELECT p.TicketId, p.EmployeeId, @CcId, @Now, NULL, 1 FROM #Plan p;
    SET @Inserted = @@ROWCOUNT;

    -- Audit 1/2: the department move (only tickets that changed department) - same shape as the app's Call-Center-first "Transfer" entry
    INSERT dbo.AuditEntries (ActorEmployeeId, Action, EntityType, EntityId, BeforeValue, AfterValue, CorrelationId, OccurredAtUtc)
    SELECT NULL, N'Transfer', N'Ticket', CONVERT(nvarchar(100), p.TicketId),
           N'DepartmentId=' + CONVERT(nvarchar(20), p.FromDepartmentId) + N';AssignedEmployeeId=' + ISNULL(LOWER(CONVERT(nvarchar(36), p.PrevOwnerId)), N'DepartmentQueue'),
           N'DepartmentId=' + CONVERT(nvarchar(20), @CcId) + N';Reason=One-time backlog redistribution to Call Center;OriginatingDepartmentId='
             + CONVERT(nvarchar(20), c.OriginatingDepartmentId) + N' (unchanged);Trigger=BacklogDistribution;BatchId=' + LOWER(CONVERT(nvarchar(36), @BatchId)),
           @BatchId, @Now
    FROM #Plan p JOIN #Cand c ON c.TicketId = p.TicketId WHERE p.IsMove = 1;

    -- Audit 2/2: the assignment, one per ticket (engine format)
    INSERT dbo.AuditEntries (ActorEmployeeId, Action, EntityType, EntityId, BeforeValue, AfterValue, CorrelationId, OccurredAtUtc)
    SELECT NULL, N'AutoAssign', N'Ticket', CONVERT(nvarchar(100), p.TicketId),
           N'PreviousOwnerId=' + ISNULL(LOWER(CONVERT(nvarchar(36), p.PrevOwnerId)), N'none') + N';PreviousAssignmentId=' + ISNULL(CONVERT(nvarchar(20), p.PrevAssignmentId), N'none')
           + N';PreviousDepartmentId=' + CONVERT(nvarchar(20), p.FromDepartmentId),
           N'AssignedEmployeeId=' + LOWER(CONVERT(nvarchar(36), p.EmployeeId)) + N';DepartmentId=' + CONVERT(nvarchar(20), @CcId)
           + N';Mode=RoundRobin;Trigger=BacklogDistribution;BatchId=' + LOWER(CONVERT(nvarchar(36), @BatchId))
           + CASE WHEN p.PrevOwnerId IS NULL THEN N';Reassigned=0' ELSE N';Reassigned=1' END,
           @BatchId, @Now
    FROM #Plan p;

    -- Workflow event: TicketTransferred / SystemRouting (5), system actor, From -> CC, receiving employee (TicketWorkflowEvent.ForTransfer shape).
    -- Written when the ticket changes department OR already had an owner (a reassignment); a first assignment out of the CC queue needs none (as in the app).
    INSERT dbo.TicketWorkflowEvents (TicketId, EventType, OccurredAtUtc, ActorEmployeeId, TicketApprovalId, Note, CorrelationId,
                                     FromDepartmentId, ToDepartmentId, ToEmployeeId, TransferReasonCode)
    SELECT p.TicketId, 17, @Now, NULL, NULL,
           N'One-time backlog redistribution to Call Center' + CASE WHEN p.PrevOwnerId IS NULL THEN N'' ELSE N'; previous owner ' + LOWER(CONVERT(nvarchar(36), p.PrevOwnerId)) END,
           @BatchId, p.FromDepartmentId, @CcId, p.EmployeeId, 5
    FROM #Plan p WHERE p.IsMove = 1 OR p.PrevOwnerId IS NOT NULL;
    SET @EventsInserted = @@ROWCOUNT;

    -- Cursor + one batch audit row (holds the cursor pre-image the rollback needs)
    UPDATE dbo.DepartmentAssignmentRotations SET LastAssignedEmployeeId = @LastAgent, UpdatedAtUtc = @Now WHERE DepartmentId = @CcId;
    INSERT dbo.AuditEntries (ActorEmployeeId, Action, EntityType, EntityId, BeforeValue, AfterValue, CorrelationId, OccurredAtUtc)
    VALUES (NULL, N'BacklogDistributionBatch', N'DepartmentAssignmentRotation', CONVERT(nvarchar(100), @CcId),
            LOWER(CONVERT(nvarchar(36), @Cursor)),
            N'LastAssignedEmployeeId=' + LOWER(CONVERT(nvarchar(36), @LastAgent)) + N';Tickets=' + CONVERT(nvarchar(20), @Cand)
            + N';Agents=' + CONVERT(nvarchar(20), @N) + N';Moved=' + CONVERT(nvarchar(20), @Moved)
            + N';DepartmentId=' + CONVERT(nvarchar(20), @CcId) + N';BatchId=' + LOWER(CONVERT(nvarchar(36), @BatchId))
            + N';HeldBackPendingApproval=' + CONVERT(nvarchar(20), @HeldApproval) + N';HeldBackActiveAi=' + CONVERT(nvarchar(20), @HeldAiOnly) + N';HeldBackHumanHandled=' + CONVERT(nvarchar(20), @HeldHuman)
            + N';Reassigned=' + CONVERT(nvarchar(20), @Reassigned),
            @BatchId, @Now);

    -- (g0) RE-CHECKS after the writes. The planned ticket rows are now exclusively locked by this transaction, so any competing writer that read a
    -- ticket before us has either already committed (and is seen here - READ COMMITTED waits for an uncommitted competing row) or must wait for our commit.
    -- Anything that appeared since selection aborts the WHOLE run (re-preview, re-approve): a held-back ticket must never be moved.
    IF EXISTS (SELECT 1 FROM #Plan p JOIN dbo.Tickets t ON t.TicketId = p.TicketId WHERE EXISTS (SELECT 1 FROM dbo.TicketApprovals ap WHERE ap.TicketId = t.TicketId AND ap.Status = 1))
        THROW 50340, 'ABORTED: a planned ticket gained a PENDING APPROVAL while the run was in progress. Whole run rolled back - re-run the preview.', 1;
    IF EXISTS (SELECT 1 FROM #Plan p JOIN dbo.Tickets t ON t.TicketId = p.TicketId WHERE (EXISTS (SELECT 1 FROM dbo.TicketInteractionHandlers hh WHERE hh.TicketId = t.TicketId)
       OR EXISTS (SELECT 1 FROM dbo.TicketInteractions hi WHERE hi.TicketId = t.TicketId AND hi.HandledByUserId IS NOT NULL)
       OR EXISTS (SELECT 1 FROM dbo.TicketInteractions hj JOIN dbo.TicketInteractionMessages hm ON hm.TicketInteractionId = hj.TicketInteractionId
                  WHERE hj.TicketId = t.TicketId AND hm.Sender = 2)))
        THROW 50343, 'ABORTED: a planned ticket gained a RECORDED HUMAN HANDLER while the run was in progress. Whole run rolled back - re-run the preview.', 1;
    IF @IncludeLiveAiConversations = 0 AND EXISTS (SELECT 1 FROM #Plan p JOIN dbo.Tickets t ON t.TicketId = p.TicketId WHERE (EXISTS (SELECT 1 FROM dbo.TicketInteractions i WHERE i.TicketId = t.TicketId AND i.Source = 2 AND i.EndedAtUtc IS NULL
                     AND i.InactivityClosedAtUtc IS NULL AND i.RequiresHumanFollowUp = 0 AND i.HandledByUserId IS NULL)
       AND NOT EXISTS (SELECT 1 FROM dbo.TicketInteractions i2 WHERE i2.TicketId = t.TicketId AND i2.RequiresHumanFollowUp = 1)
       AND NOT EXISTS (SELECT 1 FROM dbo.TicketAgentHandoffs h WHERE h.TicketId = t.TicketId AND h.Status IN (2, 3, 4))
       AND (NOT EXISTS (SELECT 1 FROM #StaleAi sa WHERE sa.TicketId = t.TicketId)
            OR EXISTS (SELECT 1 FROM dbo.TicketInteractions ni WHERE ni.TicketId = t.TicketId AND ni.CreatedAtUtc > @RunStartUtc))))
        THROW 50341, 'ABORTED: a planned ticket gained an ACTIVE AI CONVERSATION while the run was in progress. Whole run rolled back - re-run the preview.', 1;
    IF EXISTS (SELECT 1 FROM #Plan p
               WHERE NOT EXISTS (
                   SELECT 1 FROM dbo.Employees e JOIN dbo.AspNetUsers u ON u.Id = e.EmployeeId
                   WHERE e.EmployeeId = p.EmployeeId AND e.DeactivatedAtUtc IS NULL
                     AND NOT (u.LockoutEnabled = 1 AND u.LockoutEnd IS NOT NULL AND u.LockoutEnd > SYSDATETIMEOFFSET())
                     AND NOT EXISTS (SELECT 1 FROM @ServiceIdentity s WHERE s.EmployeeId = e.EmployeeId)
                     AND (@ExcludeAgentsWithTicketDenials = 0 OR NOT EXISTS (SELECT 1 FROM #Deny d WHERE d.EmployeeId = e.EmployeeId))
                     AND EXISTS (SELECT 1 FROM dbo.UserDepartmentAssignments a WHERE a.EmployeeId = e.EmployeeId AND a.DepartmentId = @CcId)
                     AND EXISTS (SELECT 1 FROM dbo.AspNetUserRoles ur JOIN dbo.AspNetRoles r ON r.Id = ur.RoleId
                                 WHERE ur.UserId = e.EmployeeId AND r.Name IN (N'CS Agent', N'Department Employee', N'Department Head'))
                     AND (@CsAgentRoleOnly = 0 OR EXISTS (SELECT 1 FROM dbo.AspNetUserRoles ur JOIN dbo.AspNetRoles r ON r.Id = ur.RoleId
                                                          WHERE ur.UserId = e.EmployeeId AND r.Name = N'CS Agent'))))
        THROW 50342, 'ABORTED: a new owner is no longer an eligible Call Center agent (deactivated / locked / removed from CC / lost role) - whole run rolled back.', 1;

    -- (g) validation INSIDE the transaction: any failure rolls everything back ------------------------------------------------------
    IF @Inserted <> @Cand THROW 50320, 'VALIDATION FAILED: assignment rows inserted <> tickets.', 1;
    IF EXISTS (SELECT 1 FROM #Plan p JOIN dbo.Tickets t ON t.TicketId = p.TicketId WHERE t.CurrentOwnerEmployeeId IS NULL OR t.CurrentOwnerEmployeeId <> p.EmployeeId)
        THROW 50321, 'VALIDATION FAILED: a planned ticket does not carry its planned owner.', 1;
    IF EXISTS (SELECT 1 FROM dbo.TicketAssignments a WHERE a.IsCurrent = 1 GROUP BY a.TicketId HAVING COUNT(*) > 1)
        THROW 50322, 'VALIDATION FAILED: a ticket has more than one current assignment.', 1;
    IF EXISTS (SELECT 1 FROM #Plan p WHERE (SELECT COUNT(*) FROM dbo.TicketAssignments a WHERE a.TicketId = p.TicketId AND a.IsCurrent = 1) <> 1
                                         OR NOT EXISTS (SELECT 1 FROM dbo.TicketAssignments a WHERE a.TicketId = p.TicketId AND a.IsCurrent = 1
                                                        AND a.AssignedEmployeeId = p.EmployeeId AND a.AssignedDepartmentId = @CcId))
        THROW 50323, 'VALIDATION FAILED: a planned ticket does not have exactly one current assignment matching its owner and Call Center.', 1;
    IF EXISTS (SELECT 1 FROM #Plan p JOIN dbo.Tickets t ON t.TicketId = p.TicketId WHERE t.TicketStatus <> 1 OR t.CurrentDepartmentId <> @CcId)
        THROW 50324, 'VALIDATION FAILED: a planned ticket is not an Open Call Center ticket.', 1;
    IF EXISTS (SELECT 1 FROM #Plan p JOIN #Agents a ON a.Rn = p.AgentRn JOIN dbo.Employees e ON e.EmployeeId = a.EmployeeId WHERE e.DeactivatedAtUtc IS NOT NULL)
        THROW 50325, 'VALIDATION FAILED: a ticket was assigned to a deactivated employee.', 1;
    IF EXISTS (SELECT 1 FROM #Plan p JOIN @ServiceIdentity s ON s.EmployeeId = p.EmployeeId)
        THROW 50326, 'VALIDATION FAILED: a ticket was assigned to a service account.', 1;
    IF (SELECT MAX(c) - MIN(c) FROM (SELECT a.Rn, COUNT(p.TicketId) AS c FROM #Agents a LEFT JOIN #Plan p ON p.AgentRn = a.Rn GROUP BY a.Rn) x) > 1
        THROW 50327, 'VALIDATION FAILED: distribution is not even (spread > 1).', 1;

    -- Batch-scoped assertions (live traffic keeps adding rows to other tables during the run, so global row-count deltas are NOT a valid check):
    IF (SELECT COUNT(*) FROM dbo.AuditEntries WHERE AuditEntryId > @MaxAuditIdBefore AND CorrelationId = @BatchId) <> @Moved + @Cand + 1
        THROW 50328, 'VALIDATION FAILED: audit rows written by this batch <> moves + assignments + 1 batch row.', 1;
    IF (SELECT COUNT(*) FROM dbo.TicketAssignments a JOIN #Plan p ON p.TicketId = a.TicketId WHERE a.IsCurrent = 1 AND a.AssignedAtUtc = @Now) <> @Cand
        THROW 50329, 'VALIDATION FAILED: current assignment rows written by this batch <> tickets.', 1;
    -- history kept: every pre-existing assignment row of a planned ticket still exists, unchanged except IsCurrent, and none is current any more
    IF EXISTS (SELECT b.TicketAssignmentId, b.TicketId, b.AssignedEmployeeId, b.AssignedDepartmentId, b.AssignedAtUtc, b.AssigningActorEmployeeId FROM #AsgBefore b
               EXCEPT
               SELECT a.TicketAssignmentId, a.TicketId, a.AssignedEmployeeId, a.AssignedDepartmentId, a.AssignedAtUtc, a.AssigningActorEmployeeId FROM dbo.TicketAssignments a)
       OR EXISTS (SELECT 1 FROM #AsgBefore b JOIN dbo.TicketAssignments a ON a.TicketAssignmentId = b.TicketAssignmentId WHERE a.IsCurrent = 1)
       OR (SELECT COUNT(*) FROM dbo.TicketAssignments a JOIN #Plan p ON p.TicketId = a.TicketId) <> (SELECT COUNT(*) FROM #AsgBefore) + @Cand
        THROW 50330, 'VALIDATION FAILED: a previous assignment row was deleted, edited (other than IsCurrent) or is still current.', 1;
    IF (SELECT COUNT(*) FROM dbo.AuditEntries WHERE AuditEntryId > @MaxAuditIdBefore AND CorrelationId = @BatchId AND Action = N'AutoAssign' AND BeforeValue LIKE N'PreviousOwnerId=%PreviousAssignmentId=%PreviousDepartmentId=%') <> @Cand
        THROW 50334, 'VALIDATION FAILED: a per-ticket AutoAssign audit row does not record the previous owner / assignment / department.', 1;
    IF @EventsInserted <> @Events
       OR (SELECT COUNT(*) FROM dbo.TicketWorkflowEvents WHERE TicketWorkflowEventId > @MaxEventIdBefore AND CorrelationId = @BatchId) <> @Events
        THROW 50331, 'VALIDATION FAILED: workflow events written by this batch <> tickets moved or reassigned.', 1;
    IF EXISTS (SELECT 1 FROM #Plan p WHERE (p.IsMove = 1 OR p.PrevOwnerId IS NOT NULL) AND NOT EXISTS (
                   SELECT 1 FROM dbo.TicketWorkflowEvents e WHERE e.TicketId = p.TicketId AND e.CorrelationId = @BatchId AND e.EventType = 17
                     AND e.TransferReasonCode = 5 AND e.FromDepartmentId = p.FromDepartmentId AND e.ToDepartmentId = @CcId AND e.ToEmployeeId = p.EmployeeId))
        THROW 50332, 'VALIDATION FAILED: a moved or reassigned ticket lacks its TicketTransferred workflow event.', 1;

    -- Preserved fields: everything that must not change is compared with the pre-write values captured in (d). EXCEPT is NULL-safe.
    IF EXISTS (
        SELECT c.TicketId, c.OriginatingDepartmentId, c.RequestTypeId, c.CategoryId, c.PriorityId, c.TicketStatus, c.SlaState, c.EscalationLevel, c.VerificationStatus, c.CreatedAtUtc, c.TicketNumber FROM #Cand c
        EXCEPT
        SELECT t.TicketId, t.OriginatingDepartmentId, t.RequestTypeId, t.CategoryId, t.PriorityId, t.TicketStatus, t.SlaState, t.EscalationLevel, t.VerificationStatus, t.CreatedAtUtc, t.TicketNumber
        FROM dbo.Tickets t JOIN #Plan p ON p.TicketId = t.TicketId)
        THROW 50333, 'VALIDATION FAILED: a preserved field (originating department, request type, category, priority, status, SLA state, escalation, verification, created time) changed.', 1;

    -- Informational only: row counts of the watched tables (NOLOCK; may move with live traffic, a large jump in a "must not be written" table is worth a look).
    DECLARE watched2 CURSOR LOCAL FAST_FORWARD FOR SELECT TableName FROM @Snapshot;
    OPEN watched2; FETCH NEXT FROM watched2 INTO @t;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        SET @sql = N'SELECT @rowCnt = COUNT_BIG(*) FROM dbo.' + QUOTENAME(@t) + N' WITH (NOLOCK);';
        EXEC sp_executesql @sql, N'@rowCnt bigint OUTPUT', @rowCnt = @rowCnt OUTPUT;
        UPDATE @Snapshot SET RowsAfter = @rowCnt WHERE TableName = @t;
        FETCH NEXT FROM watched2 INTO @t;
    END;
    CLOSE watched2; DEALLOCATE watched2;

    -- (h) report (inside the transaction so a dry run still shows what WOULD be committed) ------------------------------------------
    SELECT CASE WHEN @DryRun = 1 THEN 'DRY RUN - everything below will be ROLLED BACK' ELSE 'COMMITTING' END AS Mode,
           @BatchId AS BatchId_KeepForRollback, @CcId AS CallCenterDepartmentId, @N AS Agents, @Cand AS TicketsAssigned,
           @Moved AS TicketsMovedIntoCC, @Cand - @Moved AS TicketsAlreadyInCC, @Reassigned AS OwnerReplaced, @Cand - @Reassigned AS WereUnowned, @Cursor AS CursorBefore, @LastAgent AS CursorAfter;

    SELECT @HeldApproval AS HeldBack_PendingApproval_Untouched, @HeldAiOnly AS HeldBack_ActiveAi_Untouched, @HeldHuman AS HeldBack_HumanHandled_Untouched;

    SELECT d.Code AS FromDepartment, COUNT(*) AS Tickets, SUM(CASE WHEN p.IsMove = 1 THEN 1 ELSE 0 END) AS Moved, SUM(CASE WHEN p.PrevOwnerId IS NOT NULL THEN 1 ELSE 0 END) AS OwnerReplaced
    FROM #Plan p JOIN dbo.Departments d ON d.DepartmentId = p.FromDepartmentId GROUP BY d.Code ORDER BY COUNT(*) DESC;

    SELECT a.Rn AS RotationPosition, a.DisplayName, a.UserName, COUNT(p.TicketId) AS TicketsAssigned,
           SUM(CASE WHEN p.IsMove = 1 THEN 1 ELSE 0 END) AS FromOtherDepartments,
           SUM(CASE WHEN p.PrevOwnerId IS NOT NULL THEN 1 ELSE 0 END) AS TakenFromAnotherOwner,
           (SELECT COUNT(*) FROM dbo.Tickets t WHERE t.CurrentOwnerEmployeeId = a.EmployeeId AND t.TicketStatus IN (1, 2, 3, 4)) AS ActiveOwnedNow
    FROM #Agents a LEFT JOIN #Plan p ON p.AgentRn = a.Rn LEFT JOIN #Cand c ON c.TicketId = p.TicketId
    GROUP BY a.Rn, a.DisplayName, a.UserName, a.EmployeeId ORDER BY a.Rn;

    SELECT TableName, RowsBefore, RowsAfter, RowsAfter - RowsBefore AS Delta_includes_live_traffic, Expected FROM @Snapshot ORDER BY TableName;

    IF @DryRun = 1
    BEGIN
        ROLLBACK TRANSACTION;
        PRINT N'DRY RUN complete - transaction ROLLED BACK. No data was changed. Tickets that would have been assigned: ' + CONVERT(nvarchar(20), @Cand)
            + N' (moved into CC: ' + CONVERT(nvarchar(20), @Moved) + N').';
    END
    ELSE
    BEGIN
        COMMIT TRANSACTION;
        PRINT N'COMMITTED. Tickets assigned: ' + CONVERT(nvarchar(20), @Cand) + N' (moved into CC: ' + CONVERT(nvarchar(20), @Moved)
            + N'). KEEP THIS BatchId FOR ROLLBACK: ' + CONVERT(nvarchar(36), @BatchId);
    END;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    PRINT N'FAILED - transaction rolled back, nothing changed. ' + ERROR_MESSAGE();
    THROW;
END CATCH;

DROP TABLE IF EXISTS #Plan; DROP TABLE IF EXISTS #Cand; DROP TABLE IF EXISTS #Agents; DROP TABLE IF EXISTS #AsgBefore; DROP TABLE IF EXISTS #Deny; DROP TABLE IF EXISTS #StaleAi;
