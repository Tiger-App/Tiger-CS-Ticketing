/*
================================================================================
 Execute_TigerCS_PROD_CallCenterBacklogDistribution.sql
 Database : TigerCS_Ticketing_Prod          *** NOT EXECUTED - REQUIRES WRITTEN APPROVAL BEFORE @DryRun = 0 ***

 ONE-TIME automatic distribution of the existing OPEN backlog (confirmed business decision):
   EVERY Open ticket from EVERY department - assigned to someone or not - is moved into the Call Center (CC) and given to exactly ONE eligible
   Call Center agent. Plain Round Robin over the existing engine's rules and persisted cursor (see Preview_..._ReadOnly.sql for the rules), EXCEPT
   a ticket whose Genesys interaction was ANSWERED by a mapped, eligible Call Center agent goes directly to that answering agent (as the live
   "the agent who answers owns the ticket" rule does) and does not consume a Round Robin turn.
   NOT touched: tickets that are not Open (InProgress / Pending* / Resolved / Closed), tickets with a PENDING APPROVAL (TicketApprovals.Status = 1)
   and tickets with an ACTIVE AI CONVERSATION (live Genesys interaction nobody has taken and that needs no human). Their counts are commit gates.
   No application code, configuration or workflow is changed here; future tickets are routed by the application itself.

 PER OPEN TICKET IT WRITES (all in ONE transaction, all-or-nothing). A ticket already in CC whose dealt agent is its current owner is KEPT (no write):
   * dbo.Tickets                        CurrentDepartmentId -> CC (only if elsewhere) and CurrentOwnerEmployeeId -> the dealt agent
   * dbo.TicketAssignments              the previous CURRENT row (if any) -> IsCurrent = 0 (retired, kept as history - NEVER deleted), and +1 current row
                                           (AssignedDepartmentId = CC, AssigningActorEmployeeId = NULL = system), exactly like AutoAssign. The previous owner
                                           therefore holds a superseded assignment: the application's "previous owner" rule removes their Resolve / Close /
                                           Transfer rights on the ticket (they are no longer the owner).
   * dbo.AuditEntries                   +1 'Transfer' row for a ticket that changes department and +1 'AutoAssign' row per changed ticket whose BeforeValue
                                           records PreviousOwner / PreviousDepartmentId / PreviousAssignmentId (the rollback's source of truth)
   * dbo.TicketWorkflowEvents           +1 'TicketTransferred' event (reason SystemRouting, system actor, From -> CC, ToEmployeeId = agent) for a ticket that
                                           changes department. SystemRouting is the app's own "system moved it" reason: not counted as an agent transfer,
                                           not an escalation.
   and once per batch:
   * dbo.AuditEntries                   +1 'BacklogDistributionBatch' row (rotation cursor before/after, counts) - the rollback anchor
   * dbo.DepartmentAssignmentRotations  CC cursor advanced to the last agent dealt BY ROUND ROBIN (answering-agent tickets do not move it)
 NEVER CHANGED (proved by an in-transaction before/after comparison): OriginatingDepartmentId, RequestTypeId, CategoryId, PriorityId, TicketStatus (still Open),
   SlaState, EscalationLevel, VerificationStatus, CreatedAtUtc. Not written at all: status history, SLA instances/pauses, interactions, notes, resolutions,
   approvals, notifications/outbox, and any ticket that is not Open. No schema change. No notification is emitted (an assignment never emitted one).

 SAFETY:
   * DB-name guard; hard abort on any schema difference it depends on (incl. the unique filtered index "one current assignment per ticket").
   * @DryRun = 1 (DEFAULT): performs every write, runs every validation, prints the result sets, then ROLLS BACK.
   * COMMIT GATES (all required for @DryRun = 0): @ApprovedTicketCount, @ApprovedTransferCount, @ApprovedAgentCount, @ApprovedHeldBackPendingApprovalCount,
     @ApprovedHeldBackActiveAiCount, @ApprovedReassignedCount (tickets whose current owner is replaced) and @ApprovedPlanFingerprint (a checksum of the exact
     ticket -> agent plan) must equal what the run computes (copy the numbers from the preview); service-account exclusions confirmed
     (@ServiceIdsConfirmed = 1 AND ids listed, or @NoServiceAccountsExist = 1) and every listed id must be a real employee; no earlier batch present.
   * OWNERSHIP PROTECTION: candidate tickets are locked UPDLOCK/ROWLOCK, and the UPDATE only succeeds if the ticket is STILL Open, in the department and with
     the owner that were planned and its RowVersion is unchanged; a single mismatch aborts the whole run. A person assigning/working a ticket in the app
     at the same moment is therefore either seen (and the run aborts) or blocked until the commit (and then fails the app's own row-version check).
   * Concurrency: sp_getapplock (blocks a second concurrent run); the CC rotation row is locked UPDLOCK/ROWLOCK exactly like the live app, so live intake
     serializes behind this run; the filtered unique index makes a duplicate current assignment impossible. AFTER the writes the run re-checks that no planned
     ticket gained a pending approval or an active AI conversation and that every new owner is STILL an eligible agent - any change aborts the run.
   * Idempotent: a second run is blocked by the earlier batch audit row (and would find the tickets already dealt).
   * Rollback of ONLY this batch: Rollback_TigerCS_PROD_CallCenterBacklogDistribution.sql with this run's @BatchId (printed + stored in AuditEntries).

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
DECLARE @ApprovedAgentCount      int              = NULL;  -- commit gate: preview "ApprovedAgentCount"
DECLARE @ApprovedHeldBackPendingApprovalCount int = NULL;  -- commit gate: preview "ApprovedHeldBackPendingApprovalCount" (tickets left alone: pending approval)
DECLARE @ApprovedHeldBackActiveAiCount        int = NULL;  -- commit gate: preview "ApprovedHeldBackActiveAiCount" (tickets left alone: active AI conversation only)
DECLARE @ApprovedReassignedCount              int = NULL;  -- commit gate: preview "ApprovedReassignedCount" (Open tickets whose current owner is replaced)
DECLARE @ApprovedPlanFingerprint              int = NULL;  -- commit gate: preview "ApprovedPlanFingerprint" (checksum of the exact ticket -> agent plan)
DECLARE @ServiceIdsConfirmed     bit              = 0;     -- commit gate: set 1 only after listing ALL service-account ids below
DECLARE @NoServiceAccountsExist  bit              = 0;     -- commit gate alternative: 1 ONLY if confirmed that no service/integration accounts exist (list stays empty)
DECLARE @IncludeLiveAiConversations bit          = 0;     -- keep 0 unless explicitly approved: 0 = tickets with an ACTIVE AI conversation are held back. MUST equal the Preview's value
DECLARE @CsAgentRoleOnly         bit              = 1;     -- 1 = only holders of the CS Agent role receive tickets (can Transfer as owner). MUST equal the Preview's value
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
    (N'TicketInteractions', N'RequiresHumanFollowUp'), (N'TicketInteractions', N'HandledByUserId'), (N'TicketInteractions', N'IsOriginatingInteraction'),
    (N'TicketInteractions', N'TicketInteractionId'),
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

IF @DryRun = 0
BEGIN
    IF ISNULL(@ServiceIdsConfirmed, 0) <> 1 OR @ApprovedTicketCount IS NULL OR @ApprovedTransferCount IS NULL OR @ApprovedAgentCount IS NULL
       OR @ApprovedHeldBackPendingApprovalCount IS NULL OR @ApprovedHeldBackActiveAiCount IS NULL OR @ApprovedReassignedCount IS NULL OR @ApprovedPlanFingerprint IS NULL
        THROW 50307, 'ABORTED: commit gates not satisfied - set @ServiceIdsConfirmed = 1 and the seven approved values (tickets, moved, agents, held-back pending approvals, held-back active AI, reassigned, plan fingerprint) from the approved preview.', 1;
    IF NOT EXISTS (SELECT 1 FROM @ServiceIdentity) AND ISNULL(@NoServiceAccountsExist, 0) <> 1
        THROW 50308, 'ABORTED: the service-account list is empty. List every service/integration account id, or set @NoServiceAccountsExist = 1 if you have confirmed there are none.', 1;
    IF EXISTS (SELECT 1 FROM @ServiceIdentity s WHERE NOT EXISTS (SELECT 1 FROM dbo.Employees e WHERE e.EmployeeId = s.EmployeeId))
        THROW 50309, 'ABORTED: a listed service-account id is not an employee (typo?). Fix the list.', 1;
END
ELSE
BEGIN
    IF NOT EXISTS (SELECT 1 FROM @ServiceIdentity) PRINT N'WARNING: service-account list is empty - a real run would be refused unless @NoServiceAccountsExist = 1.';
    IF ISNULL(@ServiceIdsConfirmed, 0) <> 1 PRINT N'WARNING: @ServiceIdsConfirmed = 0 - a real run would be refused.';
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
-- Every OPEN ticket that is not held back. PrevOwner / CurAssignId describe how it is assigned NOW; PinnedEmployeeId is the person who ANSWERED its originating Genesys interaction.
CREATE TABLE #Cand (Seq int NOT NULL PRIMARY KEY, TicketId bigint NOT NULL, TicketNumber nvarchar(40) NULL, TicketStatus tinyint NOT NULL,
    CreatedAtUtc datetime2 NOT NULL, FromDepartmentId int NOT NULL, OriginatingDepartmentId int NOT NULL, IsMove bit NOT NULL,
    PrevOwner uniqueidentifier NULL, CurAssignId bigint NULL, PinnedEmployeeId uniqueidentifier NULL,
    RequestTypeId int NULL, CategoryId int NULL, PriorityId tinyint NULL, SlaState tinyint NOT NULL, EscalationLevel tinyint NOT NULL,
    VerificationStatus tinyint NOT NULL, RowVer binary(8) NOT NULL);
-- Action: KEEP (already in CC and already owned by the dealt agent - nothing is written), ASSIGN (had no owner), REASSIGN (had a different owner, or another department)
CREATE TABLE #Plan (Seq int NOT NULL PRIMARY KEY, TicketId bigint NOT NULL, AgentRn int NOT NULL, EmployeeId uniqueidentifier NOT NULL,
    FromDepartmentId int NOT NULL, IsMove bit NOT NULL, PrevOwner uniqueidentifier NULL, CurAssignId bigint NULL, IsPinned bit NOT NULL,
    Action varchar(8) NOT NULL, RowVer binary(8) NOT NULL);

DECLARE @Now datetime2 = SYSUTCDATETIME();
DECLARE @Zero uniqueidentifier = '00000000-0000-0000-0000-000000000000';
DECLARE @Cursor uniqueidentifier, @Start int = 1, @N int, @Cand int, @Moved int, @Kept int, @Changed int, @Reassigned int, @Pinned int, @HeldApproval int, @HeldAiOnly int,
        @Updated int, @Superseded int, @Inserted int, @EventsInserted int, @LastAgent uniqueidentifier, @PlanFp int, @PriorRows int,
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

    -- (c) eligible agents, Round Robin order (same rules as FilterEligibleAsync) ------------------------------------------------
    INSERT #Agents (Rn, EmployeeId, DisplayName, UserName, SortKey)
    SELECT ROW_NUMBER() OVER (ORDER BY CONVERT(char(36), e.EmployeeId) COLLATE Latin1_General_BIN2),
           e.EmployeeId, e.DisplayName, u.UserName, CONVERT(char(36), e.EmployeeId) COLLATE Latin1_General_BIN2
    FROM dbo.Employees AS e WITH (UPDLOCK, ROWLOCK)      -- an agent cannot be deactivated while the batch runs
    JOIN dbo.AspNetUsers AS u ON u.Id = e.EmployeeId
    WHERE e.DeactivatedAtUtc IS NULL
      AND EXISTS (SELECT 1 FROM dbo.UserDepartmentAssignments a WHERE a.EmployeeId = e.EmployeeId AND a.DepartmentId = @CcId)
      AND NOT (u.LockoutEnabled = 1 AND u.LockoutEnd IS NOT NULL AND u.LockoutEnd > SYSDATETIMEOFFSET())
      AND NOT EXISTS (SELECT 1 FROM @ServiceIdentity s WHERE s.EmployeeId = e.EmployeeId)
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

    -- (d) every OPEN ticket from EVERY department - assigned or not - LOCKED for this transaction. Held back (never selected, never locked, never touched):
    --     a PENDING APPROVAL, or an ACTIVE AI CONVERSATION (live Genesys interaction nobody took, no human needed, no open handoff).
    INSERT #Cand (Seq, TicketId, TicketNumber, TicketStatus, CreatedAtUtc, FromDepartmentId, OriginatingDepartmentId, IsMove, PrevOwner, CurAssignId, PinnedEmployeeId,
                  RequestTypeId, CategoryId, PriorityId, SlaState, EscalationLevel, VerificationStatus, RowVer)
    SELECT ROW_NUMBER() OVER (ORDER BY t.CreatedAtUtc, t.TicketId), t.TicketId, t.TicketNumber, t.TicketStatus, t.CreatedAtUtc,
           t.CurrentDepartmentId, t.OriginatingDepartmentId, CASE WHEN t.CurrentDepartmentId <> @CcId THEN 1 ELSE 0 END,
           t.CurrentOwnerEmployeeId,
           (SELECT TOP (1) a.TicketAssignmentId FROM dbo.TicketAssignments a WHERE a.TicketId = t.TicketId AND a.IsCurrent = 1),
           (SELECT TOP (1) i.HandledByUserId FROM dbo.TicketInteractions i
             WHERE i.TicketId = t.TicketId AND i.IsOriginatingInteraction = 1 AND i.HandledByUserId IS NOT NULL ORDER BY i.TicketInteractionId),
           t.RequestTypeId, t.CategoryId, t.PriorityId, t.SlaState, t.EscalationLevel, t.VerificationStatus, t.RowVersion
    FROM dbo.Tickets AS t WITH (UPDLOCK, ROWLOCK)
    WHERE t.TicketStatus = 1
      AND NOT EXISTS (SELECT 1 FROM dbo.TicketApprovals ap WHERE ap.TicketId = t.TicketId AND ap.Status = 1)
      AND (@IncludeLiveAiConversations = 1 OR NOT (EXISTS (SELECT 1 FROM dbo.TicketInteractions i WHERE i.TicketId = t.TicketId AND i.Source = 2 AND i.EndedAtUtc IS NULL
                     AND i.InactivityClosedAtUtc IS NULL AND i.RequiresHumanFollowUp = 0 AND i.HandledByUserId IS NULL)
       AND NOT EXISTS (SELECT 1 FROM dbo.TicketInteractions i2 WHERE i2.TicketId = t.TicketId AND i2.RequiresHumanFollowUp = 1)
       AND NOT EXISTS (SELECT 1 FROM dbo.TicketAgentHandoffs h WHERE h.TicketId = t.TicketId AND h.Status IN (2, 3, 4))));
    SET @Cand = (SELECT COUNT(*) FROM #Cand);
    SET @Moved = (SELECT COUNT(*) FROM #Cand WHERE IsMove = 1);
    -- held-back counts (read only, not locked): Open tickets with a pending approval (with or without an AI conversation) and AI-only Open tickets
    SELECT @HeldApproval = COUNT(*) FROM dbo.Tickets AS t
    WHERE t.TicketStatus = 1 AND EXISTS (SELECT 1 FROM dbo.TicketApprovals ap WHERE ap.TicketId = t.TicketId AND ap.Status = 1);
    SELECT @HeldAiOnly = COUNT(*) FROM dbo.Tickets AS t
    WHERE t.TicketStatus = 1 AND NOT EXISTS (SELECT 1 FROM dbo.TicketApprovals ap WHERE ap.TicketId = t.TicketId AND ap.Status = 1) AND @IncludeLiveAiConversations = 0 AND (EXISTS (SELECT 1 FROM dbo.TicketInteractions i WHERE i.TicketId = t.TicketId AND i.Source = 2 AND i.EndedAtUtc IS NULL
                     AND i.InactivityClosedAtUtc IS NULL AND i.RequiresHumanFollowUp = 0 AND i.HandledByUserId IS NULL)
       AND NOT EXISTS (SELECT 1 FROM dbo.TicketInteractions i2 WHERE i2.TicketId = t.TicketId AND i2.RequiresHumanFollowUp = 1)
       AND NOT EXISTS (SELECT 1 FROM dbo.TicketAgentHandoffs h WHERE h.TicketId = t.TicketId AND h.Status IN (2, 3, 4)));
    IF @Cand = 0 THROW 50313, 'NOTHING TO DO: no eligible Open tickets. Nothing was changed.', 1;

    -- (e) the deal. Answered-by-an-eligible-agent tickets go straight to that agent; every other ticket takes the next eligible agent after the cursor, wrapping
    --     (DepartmentAssignmentRotation.PickNext), oldest first.
    IF @Cursor IS NOT NULL
        SELECT @Start = ISNULL(MIN(Rn), 1) FROM #Agents WHERE SortKey > CONVERT(char(36), @Cursor) COLLATE Latin1_General_BIN2;
    INSERT #Plan (Seq, TicketId, AgentRn, EmployeeId, FromDepartmentId, IsMove, PrevOwner, CurAssignId, IsPinned, Action, RowVer)
    SELECT c.Seq, c.TicketId, a.Rn, a.EmployeeId, c.FromDepartmentId, c.IsMove, c.PrevOwner, c.CurAssignId, 1, '', c.RowVer
    FROM #Cand c JOIN #Agents a ON a.EmployeeId = c.PinnedEmployeeId;
    INSERT #Plan (Seq, TicketId, AgentRn, EmployeeId, FromDepartmentId, IsMove, PrevOwner, CurAssignId, IsPinned, Action, RowVer)
    SELECT r.Seq, r.TicketId, a.Rn, a.EmployeeId, r.FromDepartmentId, r.IsMove, r.PrevOwner, r.CurAssignId, 0, '', r.RowVer
    FROM (SELECT c.*, ROW_NUMBER() OVER (ORDER BY c.Seq) AS k FROM #Cand c
          WHERE NOT EXISTS (SELECT 1 FROM #Agents pa WHERE pa.EmployeeId = c.PinnedEmployeeId)) r
    JOIN #Agents a ON a.Rn = ((@Start - 1 + r.k - 1) % @N) + 1;
    IF (SELECT COUNT(*) FROM #Plan) <> @Cand THROW 50315, 'ABORTED: internal check failed - plan size differs from candidate count.', 1;
    UPDATE p SET Action = CASE
            WHEN p.IsMove = 0 AND p.PrevOwner = p.EmployeeId
                 AND EXISTS (SELECT 1 FROM dbo.TicketAssignments a WHERE a.TicketAssignmentId = p.CurAssignId AND a.IsCurrent = 1
                             AND a.AssignedEmployeeId = p.EmployeeId AND a.AssignedDepartmentId = @CcId) THEN 'KEEP'
            WHEN p.PrevOwner IS NULL THEN 'ASSIGN' ELSE 'REASSIGN' END
    FROM #Plan p;
    SELECT @Kept = COUNT(*) FROM #Plan WHERE Action = 'KEEP';
    SELECT @Reassigned = COUNT(*) FROM #Plan WHERE Action = 'REASSIGN';
    SELECT @Pinned = COUNT(*) FROM #Plan WHERE IsPinned = 1;
    SET @Changed = @Cand - @Kept;
    SELECT @PlanFp = CHECKSUM_AGG(CHECKSUM(p.TicketId, p.EmployeeId)) FROM #Plan p;
    SELECT TOP (1) @LastAgent = EmployeeId FROM #Plan WHERE IsPinned = 0 ORDER BY Seq DESC;
    IF @LastAgent IS NULL SET @LastAgent = @Cursor;          -- nobody was dealt by Round Robin: the cursor stays where it is

    IF @DryRun = 0 AND (@Cand <> @ApprovedTicketCount OR @Moved <> @ApprovedTransferCount
                        OR @HeldApproval <> @ApprovedHeldBackPendingApprovalCount OR @HeldAiOnly <> @ApprovedHeldBackActiveAiCount
                        OR @Reassigned <> @ApprovedReassignedCount OR ISNULL(@PlanFp, 0) <> @ApprovedPlanFingerprint)
    BEGIN
        SET @msg = N'ABORTED: eligible tickets = ' + CONVERT(nvarchar(10), @Cand) + N' (moved ' + CONVERT(nvarchar(10), @Moved) + N', reassigned ' + CONVERT(nvarchar(10), @Reassigned)
                 + N', held back: approvals ' + CONVERT(nvarchar(10), @HeldApproval) + N', AI ' + CONVERT(nvarchar(10), @HeldAiOnly) + N', plan fingerprint ' + CONVERT(nvarchar(20), ISNULL(@PlanFp, 0))
                 + N') but approved = ' + CONVERT(nvarchar(10), @ApprovedTicketCount) + N' (moved ' + CONVERT(nvarchar(10), @ApprovedTransferCount) + N', reassigned '
                 + CONVERT(nvarchar(10), @ApprovedReassignedCount) + N', held back: approvals ' + CONVERT(nvarchar(10), @ApprovedHeldBackPendingApprovalCount) + N', AI '
                 + CONVERT(nvarchar(10), @ApprovedHeldBackActiveAiCount) + N', plan fingerprint ' + CONVERT(nvarchar(20), @ApprovedPlanFingerprint)
                 + N'). The backlog changed since the preview - re-run the preview and re-approve.';
        THROW 50314, @msg, 1;
    END;

    -- (f) writes ------------------------------------------------------------------------------------------------------------------
    SET @PriorRows = (SELECT COUNT(*) FROM dbo.TicketAssignments a JOIN #Plan p ON p.TicketId = a.TicketId);

    -- KEEP tickets are not written; prove they are still exactly as planned (locked since selection, so this cannot change under us).
    IF (SELECT COUNT(*) FROM #Plan p JOIN dbo.Tickets t ON t.TicketId = p.TicketId AND t.RowVersion = p.RowVer AND t.CurrentOwnerEmployeeId = p.EmployeeId
         AND t.CurrentDepartmentId = @CcId AND t.TicketStatus = 1 WHERE p.Action = 'KEEP') <> @Kept
        THROW 50317, 'ABORTED: a ticket planned to be KEPT changed while the run was in progress. Whole run rolled back.', 1;

    -- Department move + ownership in ONE statement. Guarded by: still Open, still in the department we planned from, STILL OWNED BY WHOM WE SAW (or unowned),
    -- RowVersion unchanged. ALL changed rows must match or the whole run aborts - a person who touched a ticket meanwhile is never overwritten.
    UPDATE t SET CurrentDepartmentId = @CcId, CurrentOwnerEmployeeId = p.EmployeeId
    FROM dbo.Tickets AS t
    JOIN #Plan AS p ON p.TicketId = t.TicketId AND t.RowVersion = p.RowVer
    WHERE p.Action <> 'KEEP' AND t.TicketStatus = 1 AND t.CurrentDepartmentId = p.FromDepartmentId
      AND ISNULL(t.CurrentOwnerEmployeeId, @Zero) = ISNULL(p.PrevOwner, @Zero);
    SET @Updated = @@ROWCOUNT;
    IF @Updated <> @Changed
    BEGIN
        SET @msg = N'ABORTED: ' + CONVERT(nvarchar(10), @Changed - @Updated) + N' ticket(s) changed while the run was in progress (status/owner/department/rowversion). Whole run rolled back.';
        THROW 50316, @msg, 1;
    END;

    -- The previous CURRENT assignment of every changed ticket is retired (same as TicketAssignment.MarkSuperseded) - history is kept, never deleted. The previous owner
    -- is no longer the owner, and holds a superseded assignment, which is what removes their Resolve / Close / Transfer rights in the application.
    UPDATE a SET IsCurrent = 0
    FROM dbo.TicketAssignments a JOIN #Plan p ON p.TicketId = a.TicketId
    WHERE a.IsCurrent = 1 AND p.Action <> 'KEEP';
    SET @Superseded = @@ROWCOUNT;
    IF @Superseded <> (SELECT COUNT(*) FROM #Plan WHERE Action <> 'KEEP' AND CurAssignId IS NOT NULL)
        THROW 50318, 'ABORTED: the current assignment rows retired differ from those planned (an assignment changed while the run was in progress). Whole run rolled back.', 1;

    -- New current assignment rows in CC (system actor = NULL, like AutoAssign). The filtered unique index rejects a second current row.
    INSERT dbo.TicketAssignments (TicketId, AssignedEmployeeId, AssignedDepartmentId, AssignedAtUtc, AssigningActorEmployeeId, IsCurrent)
    SELECT p.TicketId, p.EmployeeId, @CcId, @Now, NULL, 1 FROM #Plan p WHERE p.Action <> 'KEEP';
    SET @Inserted = @@ROWCOUNT;

    -- Audit 1/2: the department move (only tickets that changed department) - same shape as the app's Call-Center-first "Transfer" entry
    INSERT dbo.AuditEntries (ActorEmployeeId, Action, EntityType, EntityId, BeforeValue, AfterValue, CorrelationId, OccurredAtUtc)
    SELECT NULL, N'Transfer', N'Ticket', CONVERT(nvarchar(100), p.TicketId),
           N'DepartmentId=' + CONVERT(nvarchar(20), p.FromDepartmentId) + N';AssignedEmployeeId=' + ISNULL(LOWER(CONVERT(nvarchar(36), p.PrevOwner)), N'DepartmentQueue'),
           N'DepartmentId=' + CONVERT(nvarchar(20), @CcId) + N';Reason=One-time backlog redistribution to Call Center;OriginatingDepartmentId='
             + CONVERT(nvarchar(20), c.OriginatingDepartmentId) + N' (unchanged);Trigger=BacklogDistribution;BatchId=' + LOWER(CONVERT(nvarchar(36), @BatchId)),
           @BatchId, @Now
    FROM #Plan p JOIN #Cand c ON c.TicketId = p.TicketId WHERE p.IsMove = 1;

    -- Audit 2/2: the assignment, one per CHANGED ticket. BeforeValue records what the ticket looked like (the rollback restores exactly this).
    INSERT dbo.AuditEntries (ActorEmployeeId, Action, EntityType, EntityId, BeforeValue, AfterValue, CorrelationId, OccurredAtUtc)
    SELECT NULL, N'AutoAssign', N'Ticket', CONVERT(nvarchar(100), p.TicketId),
           N'PreviousOwner=' + ISNULL(LOWER(CONVERT(nvarchar(36), p.PrevOwner)), N'none') + N';PreviousDepartmentId=' + CONVERT(nvarchar(20), p.FromDepartmentId)
             + N';PreviousAssignmentId=' + ISNULL(CONVERT(nvarchar(20), p.CurAssignId), N'none'),
           N'AssignedEmployeeId=' + LOWER(CONVERT(nvarchar(36), p.EmployeeId)) + N';DepartmentId=' + CONVERT(nvarchar(20), @CcId)
           + CASE WHEN p.IsPinned = 1 THEN N';Mode=AnsweringAgent' ELSE N';Mode=RoundRobin' END
           + N';Trigger=BacklogDistribution;BatchId=' + LOWER(CONVERT(nvarchar(36), @BatchId)),
           @BatchId, @Now
    FROM #Plan p WHERE p.Action <> 'KEEP';

    -- Workflow event: TicketTransferred / SystemRouting (5), system actor, From -> CC, receiving employee (TicketWorkflowEvent.ForTransfer shape)
    INSERT dbo.TicketWorkflowEvents (TicketId, EventType, OccurredAtUtc, ActorEmployeeId, TicketApprovalId, Note, CorrelationId,
                                     FromDepartmentId, ToDepartmentId, ToEmployeeId, TransferReasonCode)
    SELECT p.TicketId, 17, @Now, NULL, NULL, N'One-time backlog redistribution to Call Center', @BatchId, p.FromDepartmentId, @CcId, p.EmployeeId, 5
    FROM #Plan p WHERE p.IsMove = 1;
    SET @EventsInserted = @@ROWCOUNT;

    -- Cursor + one batch audit row (holds the cursor pre-image the rollback needs). 'LastAssignedEmployeeId=' is always a GUID (all zeros = no cursor).
    UPDATE dbo.DepartmentAssignmentRotations SET LastAssignedEmployeeId = @LastAgent, UpdatedAtUtc = @Now WHERE DepartmentId = @CcId;
    INSERT dbo.AuditEntries (ActorEmployeeId, Action, EntityType, EntityId, BeforeValue, AfterValue, CorrelationId, OccurredAtUtc)
    VALUES (NULL, N'BacklogDistributionBatch', N'DepartmentAssignmentRotation', CONVERT(nvarchar(100), @CcId),
            LOWER(CONVERT(nvarchar(36), @Cursor)),
            N'LastAssignedEmployeeId=' + LOWER(CONVERT(nvarchar(36), ISNULL(@LastAgent, @Zero))) + N';Tickets=' + CONVERT(nvarchar(20), @Changed)
            + N';Agents=' + CONVERT(nvarchar(20), @N) + N';Moved=' + CONVERT(nvarchar(20), @Moved)
            + N';DepartmentId=' + CONVERT(nvarchar(20), @CcId) + N';BatchId=' + LOWER(CONVERT(nvarchar(36), @BatchId))
            + N';HeldBackPendingApproval=' + CONVERT(nvarchar(20), @HeldApproval) + N';HeldBackActiveAi=' + CONVERT(nvarchar(20), @HeldAiOnly)
            + N';Candidates=' + CONVERT(nvarchar(20), @Cand) + N';Kept=' + CONVERT(nvarchar(20), @Kept) + N';Reassigned=' + CONVERT(nvarchar(20), @Reassigned)
            + N';AnsweringAgent=' + CONVERT(nvarchar(20), @Pinned),
            @BatchId, @Now);

    -- (g0) RE-CHECKS after the writes. The planned ticket rows are now exclusively locked by this transaction, so any competing writer that read a
    -- ticket before us has either already committed (and is seen here - READ COMMITTED waits for an uncommitted competing row) or must wait for our commit.
    -- Anything that appeared since selection aborts the WHOLE run (re-preview, re-approve): a held-back ticket must never be moved.
    IF EXISTS (SELECT 1 FROM #Plan p JOIN dbo.Tickets t ON t.TicketId = p.TicketId WHERE EXISTS (SELECT 1 FROM dbo.TicketApprovals ap WHERE ap.TicketId = t.TicketId AND ap.Status = 1))
        THROW 50340, 'ABORTED: a planned ticket gained a PENDING APPROVAL while the run was in progress. Whole run rolled back - re-run the preview.', 1;
    IF @IncludeLiveAiConversations = 0 AND EXISTS (SELECT 1 FROM #Plan p JOIN dbo.Tickets t ON t.TicketId = p.TicketId WHERE (EXISTS (SELECT 1 FROM dbo.TicketInteractions i WHERE i.TicketId = t.TicketId AND i.Source = 2 AND i.EndedAtUtc IS NULL
                     AND i.InactivityClosedAtUtc IS NULL AND i.RequiresHumanFollowUp = 0 AND i.HandledByUserId IS NULL)
       AND NOT EXISTS (SELECT 1 FROM dbo.TicketInteractions i2 WHERE i2.TicketId = t.TicketId AND i2.RequiresHumanFollowUp = 1)
       AND NOT EXISTS (SELECT 1 FROM dbo.TicketAgentHandoffs h WHERE h.TicketId = t.TicketId AND h.Status IN (2, 3, 4))))
        THROW 50341, 'ABORTED: a planned ticket gained an ACTIVE AI CONVERSATION while the run was in progress. Whole run rolled back - re-run the preview.', 1;
    IF EXISTS (SELECT 1 FROM #Plan p
               WHERE NOT EXISTS (
                   SELECT 1 FROM dbo.Employees e JOIN dbo.AspNetUsers u ON u.Id = e.EmployeeId
                   WHERE e.EmployeeId = p.EmployeeId AND e.DeactivatedAtUtc IS NULL
                     AND NOT (u.LockoutEnabled = 1 AND u.LockoutEnd IS NOT NULL AND u.LockoutEnd > SYSDATETIMEOFFSET())
                     AND NOT EXISTS (SELECT 1 FROM @ServiceIdentity s WHERE s.EmployeeId = e.EmployeeId)
                     AND EXISTS (SELECT 1 FROM dbo.UserDepartmentAssignments a WHERE a.EmployeeId = e.EmployeeId AND a.DepartmentId = @CcId)
                     AND EXISTS (SELECT 1 FROM dbo.AspNetUserRoles ur JOIN dbo.AspNetRoles r ON r.Id = ur.RoleId
                                 WHERE ur.UserId = e.EmployeeId AND r.Name IN (N'CS Agent', N'Department Employee', N'Department Head'))
                     AND (@CsAgentRoleOnly = 0 OR EXISTS (SELECT 1 FROM dbo.AspNetUserRoles ur JOIN dbo.AspNetRoles r ON r.Id = ur.RoleId
                                                          WHERE ur.UserId = e.EmployeeId AND r.Name = N'CS Agent'))))
        THROW 50342, 'ABORTED: a new owner is no longer an eligible Call Center agent (deactivated / locked / removed from CC / lost role) - whole run rolled back.', 1;

    -- (g) validation INSIDE the transaction: any failure rolls everything back ------------------------------------------------------
    IF @Inserted <> @Changed THROW 50320, 'VALIDATION FAILED: assignment rows inserted <> changed tickets.', 1;
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
    -- Fairness is a property of the Round Robin deal: the tickets dealt by rotation differ by at most one per agent (answering-agent tickets are outside the rotation).
    IF (SELECT MAX(c) - MIN(c) FROM (SELECT a.Rn, COUNT(p.TicketId) AS c FROM #Agents a LEFT JOIN #Plan p ON p.AgentRn = a.Rn AND p.IsPinned = 0 GROUP BY a.Rn) x) > 1
        THROW 50327, 'VALIDATION FAILED: the Round Robin distribution is not even (spread > 1).', 1;

    -- Batch-scoped assertions (live traffic keeps adding rows to other tables during the run, so global row-count deltas are NOT a valid check):
    IF (SELECT COUNT(*) FROM dbo.AuditEntries WHERE AuditEntryId > @MaxAuditIdBefore AND CorrelationId = @BatchId) <> @Moved + @Changed + 1
        THROW 50328, 'VALIDATION FAILED: audit rows written by this batch <> moves + assignments + 1 batch row.', 1;
    IF (SELECT COUNT(*) FROM dbo.TicketAssignments a JOIN #Plan p ON p.TicketId = a.TicketId WHERE a.IsCurrent = 1 AND a.AssignedAtUtc = @Now) <> @Changed
        THROW 50329, 'VALIDATION FAILED: current assignment rows written by this batch <> changed tickets.', 1;
    -- History only grows: every changed ticket gained exactly one row, nothing was deleted, and every retired row is a previous CURRENT row.
    IF (SELECT COUNT(*) FROM dbo.TicketAssignments a JOIN #Plan p ON p.TicketId = a.TicketId) <> @PriorRows + @Changed
        THROW 50330, 'VALIDATION FAILED: assignment history changed other than by +1 row per changed ticket.', 1;
    IF EXISTS (SELECT 1 FROM #Plan p WHERE p.Action <> 'KEEP' AND p.CurAssignId IS NOT NULL
               AND NOT EXISTS (SELECT 1 FROM dbo.TicketAssignments a WHERE a.TicketAssignmentId = p.CurAssignId AND a.IsCurrent = 0))
        THROW 50334, 'VALIDATION FAILED: a previous assignment row was not retired (history must keep it, superseded).', 1;
    IF @EventsInserted <> @Moved
       OR (SELECT COUNT(*) FROM dbo.TicketWorkflowEvents WHERE TicketWorkflowEventId > @MaxEventIdBefore AND CorrelationId = @BatchId) <> @Moved
        THROW 50331, 'VALIDATION FAILED: workflow events written by this batch <> tickets moved.', 1;
    IF EXISTS (SELECT 1 FROM #Plan p WHERE p.IsMove = 1 AND NOT EXISTS (
                   SELECT 1 FROM dbo.TicketWorkflowEvents e WHERE e.TicketId = p.TicketId AND e.CorrelationId = @BatchId AND e.EventType = 17
                     AND e.TransferReasonCode = 5 AND e.FromDepartmentId = p.FromDepartmentId AND e.ToDepartmentId = @CcId AND e.ToEmployeeId = p.EmployeeId))
        THROW 50332, 'VALIDATION FAILED: a moved ticket lacks its TicketTransferred workflow event.', 1;

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
           @BatchId AS BatchId_KeepForRollback, @CcId AS CallCenterDepartmentId, @N AS Agents, @Cand AS OpenTicketsInPlan, @Changed AS TicketsChanged,
           @Moved AS TicketsMovedIntoCC, @Kept AS TicketsAlreadyCorrect_Kept, @Reassigned AS TicketsReassignedFromAnotherOwner,
           @Pinned AS TicketsToTheAnsweringAgent, @Cursor AS CursorBefore, @LastAgent AS CursorAfter;

    SELECT @HeldApproval AS HeldBack_PendingApproval_Untouched, @HeldAiOnly AS HeldBack_ActiveAi_Untouched;

    SELECT d.Code AS FromDepartment, COUNT(*) AS Tickets, SUM(CASE WHEN p.IsMove = 1 THEN 1 ELSE 0 END) AS Moved,
           SUM(CASE WHEN p.Action = 'REASSIGN' THEN 1 ELSE 0 END) AS Reassigned, SUM(CASE WHEN p.Action = 'KEEP' THEN 1 ELSE 0 END) AS Kept
    FROM #Plan p JOIN dbo.Departments d ON d.DepartmentId = p.FromDepartmentId GROUP BY d.Code ORDER BY COUNT(*) DESC;

    SELECT a.Rn AS RotationPosition, a.DisplayName, a.UserName, COUNT(p.TicketId) AS TicketsAssigned,
           SUM(CASE WHEN p.IsPinned = 1 THEN 1 ELSE 0 END) AS AsAnsweringAgent, SUM(CASE WHEN p.IsPinned = 0 THEN 1 ELSE 0 END) AS ByRoundRobin,
           SUM(CASE WHEN p.IsMove = 1 THEN 1 ELSE 0 END) AS FromOtherDepartments, SUM(CASE WHEN p.Action = 'KEEP' THEN 1 ELSE 0 END) AS Kept,
           (SELECT COUNT(*) FROM dbo.Tickets t WHERE t.CurrentOwnerEmployeeId = a.EmployeeId AND t.TicketStatus IN (1, 2, 3, 4)) AS ActiveOwnedNow
    FROM #Agents a LEFT JOIN #Plan p ON p.AgentRn = a.Rn
    GROUP BY a.Rn, a.DisplayName, a.UserName, a.EmployeeId ORDER BY a.Rn;

    SELECT TableName, RowsBefore, RowsAfter, RowsAfter - RowsBefore AS Delta_includes_live_traffic, Expected FROM @Snapshot ORDER BY TableName;

    IF @DryRun = 1
    BEGIN
        ROLLBACK TRANSACTION;
        PRINT N'DRY RUN complete - transaction ROLLED BACK. No data was changed. Open tickets in the plan: ' + CONVERT(nvarchar(20), @Cand)
            + N' (changed: ' + CONVERT(nvarchar(20), @Changed) + N', moved into CC: ' + CONVERT(nvarchar(20), @Moved) + N', reassigned: ' + CONVERT(nvarchar(20), @Reassigned) + N').';
    END
    ELSE
    BEGIN
        COMMIT TRANSACTION;
        PRINT N'COMMITTED. Open tickets in the plan: ' + CONVERT(nvarchar(20), @Cand) + N' (changed: ' + CONVERT(nvarchar(20), @Changed) + N', moved into CC: ' + CONVERT(nvarchar(20), @Moved)
            + N', reassigned: ' + CONVERT(nvarchar(20), @Reassigned) + N'). KEEP THIS BatchId FOR ROLLBACK: ' + CONVERT(nvarchar(36), @BatchId);
    END;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    PRINT N'FAILED - transaction rolled back, nothing changed. ' + ERROR_MESSAGE();
    THROW;
END CATCH;

DROP TABLE IF EXISTS #Plan; DROP TABLE IF EXISTS #Cand; DROP TABLE IF EXISTS #Agents;
