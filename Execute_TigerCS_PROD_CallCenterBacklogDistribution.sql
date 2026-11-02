/*
================================================================================
 Execute_TigerCS_PROD_CallCenterBacklogDistribution.sql
 Database : TigerCS_Ticketing_Prod          *** NOT EXECUTED - REQUIRES WRITTEN APPROVAL BEFORE @DryRun = 0 ***

 ONE-TIME controlled distribution of the existing unowned Call Center backlog to eligible Call Center agents, using the Round Robin
 rules and cursor of the existing engine (see Preview_..._ReadOnly.sql for the full description of the rules reproduced here).

 WHAT IT WRITES (all in ONE transaction, all-or-nothing):
   * dbo.Tickets                        CurrentOwnerEmployeeId  (only on in-scope tickets; status / SLA / department / priority untouched)
   * dbo.TicketAssignments              +1 current row per ticket, AssigningActorEmployeeId = NULL (system), exactly like AutoAssign
   * dbo.AuditEntries                   +1 'AutoAssign' row per ticket (same format as the engine, Trigger=BacklogDistribution, BatchId=...)
                                        +1 batch row (Action='BacklogDistributionBatch') holding the rotation cursor before/after
   * dbo.DepartmentAssignmentRotations  cursor of the CC department advanced to the last agent dealt (as the engine does)
 WHAT IT NEVER TOUCHES: ticket status/history, SLA instances/pauses, interactions, notes, resolutions, approvals, notifications/outbox,
   any ticket that already has an owner or an active assignment, Resolved/Closed tickets, any other department.
 No schema change. No notification is emitted (an assignment never emitted one in the app either; this avoids a bulk burst).

 SAFETY:
   * DB-name guard; hard abort on any schema difference it depends on (incl. the unique filtered index "one current assignment per ticket").
   * @DryRun = 1 (DEFAULT): performs every write, runs every validation, prints the result sets, then ROLLS BACK.
   * Commit gates (all required for @DryRun = 0):  @ApprovedTicketCount and @ApprovedAgentCount must equal what the run computes
     (copy from the preview), @ServiceIdsConfirmed = 1 with the service-account ids filled in, and no earlier batch present.
   * Concurrency: sp_getapplock (blocks a second concurrent run); the CC rotation row is locked UPDLOCK/ROWLOCK exactly like the live app, so live
     intake serializes behind this run; candidate tickets are locked UPDLOCK/ROWLOCK, re-checked in the UPDATE (owner IS NULL AND RowVersion unchanged)
     and the run aborts if even one row differs; the filtered unique index makes a duplicate current assignment impossible.
   * Idempotent: a second run finds no unowned tickets (they now have owners) and an earlier batch blocks it anyway.
   * Rollback of ONLY these assignments: Rollback_TigerCS_PROD_CallCenterBacklogDistribution.sql with this run's @BatchId (printed + in AuditEntries).

 HOW TO RUN (SSMS, ONE batch, no GO):  1) Take a full backup / snapshot.  2) Run with defaults (@DryRun = 1) on a restored copy / UAT first.
   3) After sign-off fill the gates below, set @DryRun = 0 in the approved window.
================================================================================
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET LOCK_TIMEOUT 20000;                 -- fail fast (20 s) instead of hanging behind a blocker

-- ------------------------------------------------ PARAMETERS ----------------------------------------------------------------
DECLARE @DryRun              bit              = 1;      -- 1 = do everything, then ROLL BACK (default).  0 = COMMIT.
DECLARE @ApprovedTicketCount int              = NULL;   -- commit gate: from the preview ("ApprovedTicketCount_for_execution_script")
DECLARE @ApprovedAgentCount  int              = NULL;   -- commit gate: from the preview ("ApprovedAgentCount_for_execution_script")
DECLARE @ServiceIdsConfirmed bit              = 0;      -- commit gate: set 1 only after listing ALL service-account ids below
DECLARE @AllowSecondBatch    bit              = 0;      -- keep 0: this is a ONE-TIME operation
DECLARE @BatchId             uniqueidentifier = NEWID();-- correlation id of every row this run writes; needed by the rollback script
DECLARE @ExpectedDb          sysname          = N'TigerCS_Ticketing_Prod';

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
    (N'Tickets', N'TicketId'), (N'Tickets', N'TicketNumber'), (N'Tickets', N'CurrentDepartmentId'), (N'Tickets', N'CurrentOwnerEmployeeId'),
    (N'Tickets', N'TicketStatus'), (N'Tickets', N'CreatedAtUtc'), (N'Tickets', N'RowVersion'),
    (N'TicketAssignments', N'TicketId'), (N'TicketAssignments', N'AssignedEmployeeId'), (N'TicketAssignments', N'AssignedDepartmentId'),
    (N'TicketAssignments', N'AssignedAtUtc'), (N'TicketAssignments', N'AssigningActorEmployeeId'), (N'TicketAssignments', N'IsCurrent'),
    (N'AuditEntries', N'ActorEmployeeId'), (N'AuditEntries', N'Action'), (N'AuditEntries', N'EntityType'), (N'AuditEntries', N'EntityId'),
    (N'AuditEntries', N'BeforeValue'), (N'AuditEntries', N'AfterValue'), (N'AuditEntries', N'CorrelationId'), (N'AuditEntries', N'OccurredAtUtc'),
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

IF @AllowSecondBatch = 0 AND EXISTS (SELECT 1 FROM dbo.AuditEntries WHERE Action = N'BacklogDistributionBatch')
    THROW 50306, 'ABORTED: a BacklogDistributionBatch audit row already exists. This is a one-time operation (set @AllowSecondBatch = 1 only with explicit approval).', 1;

IF @DryRun = 0 AND (ISNULL(@ServiceIdsConfirmed, 0) <> 1 OR @ApprovedTicketCount IS NULL OR @ApprovedAgentCount IS NULL)
    THROW 50307, 'ABORTED: commit gates not satisfied - set @ServiceIdsConfirmed = 1 (service-account ids filled in) and @ApprovedTicketCount / @ApprovedAgentCount from the approved preview.', 1;

-- ---------------------------------------------------- before-snapshot of tables that must stay untouched ----------------------
DECLARE @Snapshot TABLE (TableName sysname PRIMARY KEY, RowsBefore bigint NOT NULL, RowsAfter bigint NULL, Expected nvarchar(40) NOT NULL);
DECLARE @t sysname, @sql nvarchar(max), @n bigint;
DECLARE watched CURSOR LOCAL FAST_FORWARD FOR
    SELECT v.n FROM (VALUES (N'Tickets'), (N'TicketAssignments'), (N'AuditEntries'), (N'DepartmentAssignmentRotations'), (N'TicketStatusHistory'),
        (N'TicketSlaInstances'), (N'TicketSlaPausePeriods'), (N'TicketInteractions'), (N'TicketInteractionMessages'), (N'TicketNotes'),
        (N'TicketResolutions'), (N'TicketPendingRecords'), (N'TicketApprovals'), (N'TicketEscalations'), (N'TicketWorkflowEvents'),
        (N'Notifications'), (N'OutboxMessages'), (N'Employees'), (N'AspNetUsers'), (N'AspNetUserRoles')) v(n)
    WHERE OBJECT_ID(N'dbo.' + v.n, N'U') IS NOT NULL;
OPEN watched; FETCH NEXT FROM watched INTO @t;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @sql = N'SELECT @n = COUNT_BIG(*) FROM dbo.' + QUOTENAME(@t) + N' WITH (NOLOCK);';
    EXEC sp_executesql @sql, N'@n bigint OUTPUT', @n = @n OUTPUT;
    INSERT @Snapshot (TableName, RowsBefore, Expected) VALUES (@t, @n, CASE WHEN @t IN (N'Tickets', N'TicketAssignments', N'AuditEntries', N'DepartmentAssignmentRotations') THEN N'written by this script' ELSE N'must not be written by this script' END);
    FETCH NEXT FROM watched INTO @t;
END;
CLOSE watched; DEALLOCATE watched;

CREATE TABLE #Agents (
    Rn int NOT NULL PRIMARY KEY, EmployeeId uniqueidentifier NOT NULL, DisplayName nvarchar(200) NULL, UserName nvarchar(256) NULL,
    SortKey char(36) COLLATE Latin1_General_BIN2 NOT NULL);
CREATE TABLE #Cand (Seq int NOT NULL PRIMARY KEY, TicketId bigint NOT NULL, TicketNumber nvarchar(40) NULL, TicketStatus tinyint NOT NULL,
    CreatedAtUtc datetime2 NOT NULL, RowVer binary(8) NOT NULL);
CREATE TABLE #Plan (Seq int NOT NULL PRIMARY KEY, TicketId bigint NOT NULL, AgentRn int NOT NULL, EmployeeId uniqueidentifier NOT NULL, RowVer binary(8) NOT NULL);

DECLARE @Now datetime2 = SYSUTCDATETIME();
DECLARE @Cursor uniqueidentifier, @Start int = 1, @N int, @Cand int, @Updated int, @Inserted int, @LastAgent uniqueidentifier, @MaxAuditIdBefore bigint;

BEGIN TRY
    BEGIN TRANSACTION;

    -- (a) one run at a time -----------------------------------------------------------------------------------------------
    DECLARE @lock int;
    EXEC @lock = sp_getapplock @Resource = N'TigerCS.CallCenterBacklogDistribution', @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 0;
    IF @lock < 0 THROW 50310, 'ABORTED: another backlog-distribution run holds the application lock.', 1;

    SET @MaxAuditIdBefore = ISNULL((SELECT MAX(AuditEntryId) FROM dbo.AuditEntries), 0);

    -- (b) lock the CC rotation cursor exactly like AssignmentRotationRepository.GetForUpdateAsync (live intake now waits behind this run)
    IF NOT EXISTS (SELECT 1 FROM dbo.DepartmentAssignmentRotations WITH (UPDLOCK, HOLDLOCK) WHERE DepartmentId = @CcId)
        INSERT INTO dbo.DepartmentAssignmentRotations (DepartmentId, LastAssignedEmployeeId, UpdatedAtUtc) VALUES (@CcId, NULL, @Now);
    SELECT @Cursor = LastAssignedEmployeeId FROM dbo.DepartmentAssignmentRotations WITH (UPDLOCK, ROWLOCK) WHERE DepartmentId = @CcId;

    -- (c) eligible agents, Round Robin order (same rules as FilterEligibleAsync) ------------------------------------------------
    INSERT #Agents (Rn, EmployeeId, DisplayName, UserName, SortKey)
    SELECT ROW_NUMBER() OVER (ORDER BY CONVERT(char(36), e.EmployeeId) COLLATE Latin1_General_BIN2),
           e.EmployeeId, e.DisplayName, u.UserName, CONVERT(char(36), e.EmployeeId) COLLATE Latin1_General_BIN2
    FROM dbo.Employees AS e
    JOIN dbo.AspNetUsers AS u ON u.Id = e.EmployeeId
    WHERE e.DeactivatedAtUtc IS NULL
      AND EXISTS (SELECT 1 FROM dbo.UserDepartmentAssignments a WHERE a.EmployeeId = e.EmployeeId AND a.DepartmentId = @CcId)
      AND NOT (u.LockoutEnabled = 1 AND u.LockoutEnd IS NOT NULL AND u.LockoutEnd > SYSDATETIMEOFFSET())
      AND NOT EXISTS (SELECT 1 FROM @ServiceIdentity s WHERE s.EmployeeId = e.EmployeeId)
      AND EXISTS (SELECT 1 FROM dbo.AspNetUserRoles ur JOIN dbo.AspNetRoles r ON r.Id = ur.RoleId
                  WHERE ur.UserId = e.EmployeeId AND r.Name IN (N'CS Agent', N'Department Employee', N'Department Head'));
    SET @N = (SELECT COUNT(*) FROM #Agents);
    IF @N = 0 THROW 50311, 'ABORTED: no eligible Call Center agent.', 1;
    IF @DryRun = 0 AND @N <> @ApprovedAgentCount
    BEGIN
        SET @msg = N'ABORTED: eligible agents = ' + CONVERT(nvarchar(10), @N) + N' but @ApprovedAgentCount = ' + CONVERT(nvarchar(10), @ApprovedAgentCount) + N'. Re-run the preview and re-approve.';
        THROW 50312, @msg, 1;
    END;

    -- (d) candidate tickets, LOCKED for this transaction -----------------------------------------------------------------------
    INSERT #Cand (Seq, TicketId, TicketNumber, TicketStatus, CreatedAtUtc, RowVer)
    SELECT ROW_NUMBER() OVER (ORDER BY t.CreatedAtUtc, t.TicketId), t.TicketId, t.TicketNumber, t.TicketStatus, t.CreatedAtUtc, t.RowVersion
    FROM dbo.Tickets AS t WITH (UPDLOCK, ROWLOCK)
    WHERE t.CurrentDepartmentId = @CcId
      AND t.TicketStatus IN (1, 2, 3, 4)
      AND t.CurrentOwnerEmployeeId IS NULL
      AND NOT EXISTS (SELECT 1 FROM dbo.TicketAssignments a WHERE a.TicketId = t.TicketId AND a.IsCurrent = 1);
    SET @Cand = (SELECT COUNT(*) FROM #Cand);
    IF @Cand = 0 THROW 50313, 'NOTHING TO DO: no eligible unowned tickets. Nothing was changed.', 1;
    IF @DryRun = 0 AND @Cand <> @ApprovedTicketCount
    BEGIN
        SET @msg = N'ABORTED: eligible tickets = ' + CONVERT(nvarchar(10), @Cand) + N' but @ApprovedTicketCount = ' + CONVERT(nvarchar(10), @ApprovedTicketCount) + N'. The backlog changed since the preview - re-run the preview and re-approve.';
        THROW 50314, @msg, 1;
    END;

    -- (e) the deal: next eligible after the cursor, wrapping (DepartmentAssignmentRotation.PickNext) --------------------------
    IF @Cursor IS NOT NULL
        SELECT @Start = ISNULL(MIN(Rn), 1) FROM #Agents WHERE SortKey > CONVERT(char(36), @Cursor) COLLATE Latin1_General_BIN2;
    INSERT #Plan (Seq, TicketId, AgentRn, EmployeeId, RowVer)
    SELECT c.Seq, c.TicketId, a.Rn, a.EmployeeId, c.RowVer
    FROM #Cand c JOIN #Agents a ON a.Rn = ((@Start - 1 + c.Seq - 1) % @N) + 1;
    IF (SELECT COUNT(*) FROM #Plan) <> @Cand THROW 50315, 'ABORTED: internal check failed - plan size differs from candidate count.', 1;
    SELECT TOP (1) @LastAgent = EmployeeId FROM #Plan ORDER BY Seq DESC;

    -- (f) writes ------------------------------------------------------------------------------------------------------------------
    -- Ownership: guarded by owner IS NULL + unchanged RowVersion; ALL rows must match or the whole run aborts.
    UPDATE t SET CurrentOwnerEmployeeId = p.EmployeeId
    FROM dbo.Tickets AS t
    JOIN #Plan AS p ON p.TicketId = t.TicketId AND t.RowVersion = p.RowVer
    WHERE t.CurrentOwnerEmployeeId IS NULL AND t.CurrentDepartmentId = @CcId AND t.TicketStatus IN (1, 2, 3, 4);
    SET @Updated = @@ROWCOUNT;
    IF @Updated <> @Cand
    BEGIN
        SET @msg = N'ABORTED: ' + CONVERT(nvarchar(10), @Cand - @Updated) + N' ticket(s) changed while the run was in progress (owner/status/rowversion). Whole run rolled back.';
        THROW 50316, @msg, 1;
    END;

    -- Current assignment rows (system actor = NULL, like AutoAssign). The filtered unique index rejects a second current row.
    INSERT dbo.TicketAssignments (TicketId, AssignedEmployeeId, AssignedDepartmentId, AssignedAtUtc, AssigningActorEmployeeId, IsCurrent)
    SELECT p.TicketId, p.EmployeeId, @CcId, @Now, NULL, 1 FROM #Plan p;
    SET @Inserted = @@ROWCOUNT;

    -- Audit: one AutoAssign row per ticket (engine format), correlated by @BatchId
    INSERT dbo.AuditEntries (ActorEmployeeId, Action, EntityType, EntityId, BeforeValue, AfterValue, CorrelationId, OccurredAtUtc)
    SELECT NULL, N'AutoAssign', N'Ticket', CONVERT(nvarchar(100), p.TicketId), NULL,
           N'AssignedEmployeeId=' + LOWER(CONVERT(nvarchar(36), p.EmployeeId)) + N';DepartmentId=' + CONVERT(nvarchar(20), @CcId)
           + N';Mode=RoundRobin;Trigger=BacklogDistribution;BatchId=' + LOWER(CONVERT(nvarchar(36), @BatchId)),
           @BatchId, @Now
    FROM #Plan p;

    -- Cursor + one batch audit row (holds the cursor pre-image the rollback needs)
    UPDATE dbo.DepartmentAssignmentRotations SET LastAssignedEmployeeId = @LastAgent, UpdatedAtUtc = @Now WHERE DepartmentId = @CcId;
    INSERT dbo.AuditEntries (ActorEmployeeId, Action, EntityType, EntityId, BeforeValue, AfterValue, CorrelationId, OccurredAtUtc)
    VALUES (NULL, N'BacklogDistributionBatch', N'DepartmentAssignmentRotation', CONVERT(nvarchar(100), @CcId),
            LOWER(CONVERT(nvarchar(36), @Cursor)),
            N'LastAssignedEmployeeId=' + LOWER(CONVERT(nvarchar(36), @LastAgent)) + N';Tickets=' + CONVERT(nvarchar(20), @Cand)
            + N';Agents=' + CONVERT(nvarchar(20), @N) + N';DepartmentId=' + CONVERT(nvarchar(20), @CcId) + N';BatchId=' + LOWER(CONVERT(nvarchar(36), @BatchId)),
            @BatchId, @Now);

    -- (g) validation INSIDE the transaction: any failure rolls everything back ------------------------------------------------------
    IF @Inserted <> @Cand THROW 50320, 'VALIDATION FAILED: assignment rows inserted <> tickets.', 1;
    IF EXISTS (SELECT 1 FROM #Plan p JOIN dbo.Tickets t ON t.TicketId = p.TicketId WHERE t.CurrentOwnerEmployeeId IS NULL OR t.CurrentOwnerEmployeeId <> p.EmployeeId)
        THROW 50321, 'VALIDATION FAILED: a planned ticket does not carry its planned owner.', 1;
    IF EXISTS (SELECT 1 FROM dbo.TicketAssignments a WHERE a.IsCurrent = 1 GROUP BY a.TicketId HAVING COUNT(*) > 1)
        THROW 50322, 'VALIDATION FAILED: a ticket has more than one current assignment.', 1;
    IF EXISTS (SELECT 1 FROM #Plan p WHERE (SELECT COUNT(*) FROM dbo.TicketAssignments a WHERE a.TicketId = p.TicketId AND a.IsCurrent = 1) <> 1
                                         OR NOT EXISTS (SELECT 1 FROM dbo.TicketAssignments a WHERE a.TicketId = p.TicketId AND a.IsCurrent = 1 AND a.AssignedEmployeeId = p.EmployeeId))
        THROW 50323, 'VALIDATION FAILED: a planned ticket does not have exactly one current assignment matching its owner.', 1;
    IF EXISTS (SELECT 1 FROM #Plan p JOIN dbo.Tickets t ON t.TicketId = p.TicketId WHERE t.TicketStatus NOT IN (1, 2, 3, 4) OR t.CurrentDepartmentId <> @CcId)
        THROW 50324, 'VALIDATION FAILED: a planned ticket is not an active Call Center ticket.', 1;
    IF EXISTS (SELECT 1 FROM #Plan p JOIN #Agents a ON a.Rn = p.AgentRn JOIN dbo.Employees e ON e.EmployeeId = a.EmployeeId WHERE e.DeactivatedAtUtc IS NOT NULL)
        THROW 50325, 'VALIDATION FAILED: a ticket was assigned to a deactivated employee.', 1;
    IF EXISTS (SELECT 1 FROM #Plan p JOIN @ServiceIdentity s ON s.EmployeeId = p.EmployeeId)
        THROW 50326, 'VALIDATION FAILED: a ticket was assigned to a service account.', 1;
    IF (SELECT MAX(c) - MIN(c) FROM (SELECT a.Rn, COUNT(p.TicketId) AS c FROM #Agents a LEFT JOIN #Plan p ON p.AgentRn = a.Rn GROUP BY a.Rn) x) > 1
        THROW 50327, 'VALIDATION FAILED: distribution is not even (spread > 1).', 1;

    -- Batch-scoped assertions (live traffic keeps adding rows to other tables during the run, so global row-count deltas are NOT a valid check):
    IF (SELECT COUNT(*) FROM dbo.AuditEntries WHERE AuditEntryId > @MaxAuditIdBefore AND CorrelationId = @BatchId) <> @Cand + 1
        THROW 50328, 'VALIDATION FAILED: audit rows written by this batch <> tickets + 1 batch row.', 1;
    IF (SELECT COUNT(*) FROM dbo.TicketAssignments a JOIN #Plan p ON p.TicketId = a.TicketId WHERE a.IsCurrent = 1 AND a.AssignedAtUtc = @Now) <> @Cand
        THROW 50329, 'VALIDATION FAILED: current assignment rows written by this batch <> tickets.', 1;
    IF EXISTS (SELECT 1 FROM dbo.TicketAssignments a JOIN #Plan p ON p.TicketId = a.TicketId WHERE a.IsCurrent = 0 AND a.AssignedAtUtc = @Now)
        THROW 50330, 'VALIDATION FAILED: an existing assignment row was superseded - this script must never supersede one.', 1;

    -- Informational only: row counts of tables this script must not write to (NOLOCK; may move with live traffic, a large jump is worth a look).
    DECLARE watched2 CURSOR LOCAL FAST_FORWARD FOR SELECT TableName FROM @Snapshot;
    OPEN watched2; FETCH NEXT FROM watched2 INTO @t;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        SET @sql = N'SELECT @n = COUNT_BIG(*) FROM dbo.' + QUOTENAME(@t) + N' WITH (NOLOCK);';
        EXEC sp_executesql @sql, N'@n bigint OUTPUT', @n = @n OUTPUT;
        UPDATE @Snapshot SET RowsAfter = @n WHERE TableName = @t;
        FETCH NEXT FROM watched2 INTO @t;
    END;
    CLOSE watched2; DEALLOCATE watched2;

    -- (h) report (inside the transaction so a dry run still shows what WOULD be committed) ------------------------------------------
    SELECT CASE WHEN @DryRun = 1 THEN 'DRY RUN - everything below will be ROLLED BACK' ELSE 'COMMITTING' END AS Mode,
           @BatchId AS BatchId_KeepForRollback, @CcId AS CallCenterDepartmentId, @N AS Agents, @Cand AS TicketsAssigned,
           @Cursor AS CursorBefore, @LastAgent AS CursorAfter;

    SELECT a.Rn AS RotationPosition, a.DisplayName, a.UserName, COUNT(p.TicketId) AS TicketsAssigned,
           SUM(CASE WHEN c.TicketStatus = 1 THEN 1 ELSE 0 END) AS Open_, SUM(CASE WHEN c.TicketStatus = 2 THEN 1 ELSE 0 END) AS InProgress_,
           SUM(CASE WHEN c.TicketStatus IN (3, 4) THEN 1 ELSE 0 END) AS Pending_,
           (SELECT COUNT(*) FROM dbo.Tickets t WHERE t.CurrentOwnerEmployeeId = a.EmployeeId AND t.TicketStatus IN (1, 2, 3, 4)) AS ActiveOwnedNow
    FROM #Agents a LEFT JOIN #Plan p ON p.AgentRn = a.Rn LEFT JOIN #Cand c ON c.TicketId = p.TicketId
    GROUP BY a.Rn, a.DisplayName, a.UserName, a.EmployeeId ORDER BY a.Rn;

    SELECT TableName, RowsBefore, RowsAfter, RowsAfter - RowsBefore AS Delta_includes_live_traffic, Expected FROM @Snapshot ORDER BY TableName;

    IF @DryRun = 1
    BEGIN
        ROLLBACK TRANSACTION;
        PRINT N'DRY RUN complete - transaction ROLLED BACK. No data was changed. Tickets that would have been assigned: ' + CONVERT(nvarchar(20), @Cand);
    END
    ELSE
    BEGIN
        COMMIT TRANSACTION;
        PRINT N'COMMITTED. Tickets assigned: ' + CONVERT(nvarchar(20), @Cand) + N'. KEEP THIS BatchId FOR ROLLBACK: ' + CONVERT(nvarchar(36), @BatchId);
    END;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    PRINT N'FAILED - transaction rolled back, nothing changed. ' + ERROR_MESSAGE();
    DECLARE @em nvarchar(2000) = ERROR_MESSAGE();
    THROW 50399, @em, 1;
END CATCH;

DROP TABLE IF EXISTS #Plan; DROP TABLE IF EXISTS #Cand; DROP TABLE IF EXISTS #Agents;
