/*
================================================================================
 Rollback_TigerCS_PROD_CallCenterBacklogDistribution.sql
 Database : TigerCS_Ticketing_Prod          *** NOT EXECUTED - ONLY FOR USE AFTER Execute_... HAS COMMITTED AND ROLLBACK IS APPROVED ***

 Undoes ONLY the work of ONE backlog-distribution batch (@BatchId = the CorrelationId printed by the execution script and stored on every
 AuditEntries / TicketWorkflowEvents row it wrote). The working set is read from the batch's own per-ticket AutoAssign audit rows, which record
 the PREVIOUS owner, PREVIOUS current assignment row and PREVIOUS department of every ticket, then re-verified per ticket.

 Each ticket is classified from its CURRENT state:
   ROLLBACK  - it is STILL exactly as the batch left it:
                 * owner = the agent the batch assigned, department = Call Center, status still Open (a started / resolved ticket was worked)
                 * the batch's assignment row is still the ONE current row, and no later assignment row exists (no reassignment / transfer since)
                 * the previous current assignment row (when there was one) still exists, retired
                 * nobody has worked it since the batch: no first human response, no status-history row (any status change / pending / resolve),
                   no note, no interaction-handler record, no NEW interaction (e.g. a new AI/customer conversation), no NEW approval request and no
                 other workflow event (transfer, handoff, reopen, Genesys event ...) after the batch
   SKIP      - anything else. It is listed with the reason and left EXACTLY as found: a human owns that work now.
               (@IncludeWorked = 1 lifts only the "nobody worked it" test, only with explicit approval; owner / department / assignment checks always apply.)

 For each ROLLBACK ticket the pre-batch state is restored:
   * Tickets.CurrentOwnerEmployeeId -> the PREVIOUS owner (NULL when the ticket was unowned)
   * Tickets.CurrentDepartmentId    -> the department it was in before the batch
   * the batch's TicketAssignments row -> IsCurrent = 0 (retired, kept for history - NEVER deleted)
   * the previous current assignment row (if the ticket had one) -> IsCurrent = 1 again, so the previous owner owns it with its original history
   * + 1 'Transfer' audit row (CC -> original) for a ticket that had been moved, + 1 'AutoAssignRollback' audit row, and + 1 TicketTransferred/SystemRouting
     workflow event (CC -> original, receiving employee = the previous owner) for a ticket that had been moved or had an owner
 Round Robin cursor is restored to its pre-batch value IF no ticket has been dealt from it since (otherwise left alone - a cursor position is harmless).
 NEVER touched: OriginatingDepartmentId, RequestTypeId, category, priority, status, SLA, history, notes, approvals, interactions (incl. AI conversations), notifications.
 The tickets the batch HELD BACK (pending approval / active AI conversation) were never part of the batch, so rollback cannot reach them.
 NEVER deleted or edited: any audit row, workflow event, status history, assignment row (only the IsCurrent flag flips).

 HOW TO RUN: set @BatchId, run with @DryRun = 1 (default; shows what would roll back / be skipped, then rolls back), review, set @DryRun = 0.
================================================================================
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;               -- required by the filtered index on TicketAssignments (SSMS default; sqlcmd needs -I)
SET ANSI_NULLS ON;
SET LOCK_TIMEOUT 20000;

DECLARE @BatchId       uniqueidentifier = NULL;   -- REQUIRED: 'xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx' from the execution output
DECLARE @DryRun        bit              = 1;      -- 1 = report and ROLL BACK (default).  0 = COMMIT.
DECLARE @IncludeWorked bit              = 0;      -- keep 0 unless explicitly approved (see header)
DECLARE @ExpectedDb    sysname          = N'TigerCS_Ticketing_Prod';

DECLARE @msg nvarchar(2000);
IF DB_NAME() <> @ExpectedDb
BEGIN
    SET @msg = N'ABORTED: connected to [' + ISNULL(DB_NAME(), N'?') + N']; this script only runs against [' + @ExpectedDb + N']. Nothing was changed.';
    THROW 50400, @msg, 1;
END;
IF @BatchId IS NULL THROW 50401, 'ABORTED: set @BatchId to the batch id printed by the execution script.', 1;

DECLARE @BatchRow TABLE (AuditEntryId bigint, BeforeValue nvarchar(max), AfterValue nvarchar(max), OccurredAtUtc datetime2, EntityId nvarchar(100));
INSERT @BatchRow SELECT AuditEntryId, BeforeValue, AfterValue, OccurredAtUtc, EntityId
FROM dbo.AuditEntries WHERE CorrelationId = @BatchId AND Action = N'BacklogDistributionBatch';
IF (SELECT COUNT(*) FROM @BatchRow) <> 1 THROW 50402, 'ABORTED: expected exactly one BacklogDistributionBatch audit row for this @BatchId (wrong id?).', 1;
IF EXISTS (SELECT 1 FROM dbo.AuditEntries WHERE Action = N'BacklogDistributionRollbackBatch' AND AfterValue LIKE N'%BatchId=' + LOWER(CONVERT(nvarchar(36), @BatchId)) + N'%')
    THROW 50403, 'ABORTED: this batch has already been rolled back.', 1;

DECLARE @RollbackId uniqueidentifier = NEWID();
DECLARE @BatchAt datetime2 = (SELECT OccurredAtUtc FROM @BatchRow);
DECLARE @BatchAuditId bigint = (SELECT AuditEntryId FROM @BatchRow);
DECLARE @CcId int = TRY_CONVERT(int, (SELECT EntityId FROM @BatchRow));
DECLARE @CursorBefore uniqueidentifier = TRY_CONVERT(uniqueidentifier, (SELECT BeforeValue FROM @BatchRow));
DECLARE @BatchAfter nvarchar(max) = (SELECT AfterValue FROM @BatchRow);
DECLARE @CursorAfter uniqueidentifier = TRY_CONVERT(uniqueidentifier, SUBSTRING(@BatchAfter, 24, 36));   -- 'LastAssignedEmployeeId=' is 23 chars
DECLARE @ExpectedTickets int = TRY_CONVERT(int, SUBSTRING(@BatchAfter, CHARINDEX(N';Tickets=', @BatchAfter) + 9,
                                                         CHARINDEX(N';Agents=', @BatchAfter) - CHARINDEX(N';Tickets=', @BatchAfter) - 9));
DECLARE @ExpectedReassigned int = TRY_CONVERT(int, SUBSTRING(@BatchAfter, CHARINDEX(N';Reassigned=', @BatchAfter) + 12, 20));
DECLARE @ExpectedMoved int = TRY_CONVERT(int, SUBSTRING(@BatchAfter, CHARINDEX(N';Moved=', @BatchAfter) + 7,
                                                       CHARINDEX(N';DepartmentId=', @BatchAfter) - CHARINDEX(N';Moved=', @BatchAfter) - 7));
IF @CcId IS NULL OR @CursorAfter IS NULL OR @ExpectedTickets IS NULL OR @ExpectedMoved IS NULL OR @ExpectedReassigned IS NULL
    THROW 50404, 'ABORTED: could not read the batch row (department / cursor / ticket counts).', 1;
PRINT N'Guard passed: database [' + DB_NAME() + N'], BatchId=' + CONVERT(nvarchar(36), @BatchId) + N', DryRun=' + CAST(@DryRun AS nvarchar(1)) + N', RollbackId=' + CONVERT(nvarchar(36), @RollbackId);

-- Working set = the per-ticket AutoAssign audit rows of THIS batch: ticket id from EntityId, agent from AfterValue, and from BeforeValue
-- 'PreviousOwnerId=<guid|none>;PreviousAssignmentId=<id|none>;PreviousDepartmentId=<id>' the state to restore.
CREATE TABLE #Work (TicketId bigint NOT NULL PRIMARY KEY, AgentId uniqueidentifier NOT NULL, FromDepartmentId int NULL, PrevOwnerId uniqueidentifier NULL, PrevAssignmentId bigint NULL,
                    ParseOk bit NOT NULL DEFAULT 0, Decision varchar(60) NULL, Reason nvarchar(300) NULL);
INSERT #Work (TicketId, AgentId, FromDepartmentId, PrevOwnerId, PrevAssignmentId, ParseOk)
SELECT x.TicketId, x.AgentId, x.PrevDept,
       TRY_CONVERT(uniqueidentifier, NULLIF(x.PrevOwnerTxt, N'none')), TRY_CONVERT(bigint, NULLIF(x.PrevAsgTxt, N'none')),
       CASE WHEN x.TicketId IS NOT NULL AND x.AgentId IS NOT NULL AND x.PrevDept IS NOT NULL
             AND (x.PrevOwnerTxt = N'none' OR TRY_CONVERT(uniqueidentifier, x.PrevOwnerTxt) IS NOT NULL)
             AND (x.PrevAsgTxt = N'none' OR TRY_CONVERT(bigint, x.PrevAsgTxt) IS NOT NULL) THEN 1 ELSE 0 END
FROM (SELECT TRY_CONVERT(bigint, a.EntityId) AS TicketId,
             TRY_CONVERT(uniqueidentifier, SUBSTRING(a.AfterValue, 20, 36)) AS AgentId,                                  -- 'AssignedEmployeeId=' is 19 chars
             SUBSTRING(a.BeforeValue, 17, CHARINDEX(N';PreviousAssignmentId=', a.BeforeValue) - 17) AS PrevOwnerTxt,       -- 'PreviousOwnerId=' is 16 chars
             SUBSTRING(a.BeforeValue, CHARINDEX(N';PreviousAssignmentId=', a.BeforeValue) + 22,
                       CHARINDEX(N';PreviousDepartmentId=', a.BeforeValue) - CHARINDEX(N';PreviousAssignmentId=', a.BeforeValue) - 22) AS PrevAsgTxt,
             TRY_CONVERT(int, SUBSTRING(a.BeforeValue, CHARINDEX(N';PreviousDepartmentId=', a.BeforeValue) + 22, 20)) AS PrevDept
      FROM dbo.AuditEntries AS a
      WHERE a.AuditEntryId >= @BatchAuditId - 10000000 AND a.AuditEntryId < @BatchAuditId
        AND a.CorrelationId = @BatchId AND a.Action = N'AutoAssign' AND a.EntityType = N'Ticket'
        AND a.AfterValue LIKE N'AssignedEmployeeId=%Trigger=BacklogDistribution%'
        AND a.BeforeValue LIKE N'PreviousOwnerId=%;PreviousAssignmentId=%;PreviousDepartmentId=%') x;
IF EXISTS (SELECT 1 FROM #Work w WHERE w.ParseOk = 0) OR (SELECT COUNT(*) FROM #Work) <> @ExpectedTickets
    THROW 50405, 'ABORTED: the per-ticket audit rows found do not match the ticket count recorded by the batch row (or could not be parsed).', 1;
IF (SELECT COUNT(*) FROM #Work WHERE FromDepartmentId <> @CcId) <> @ExpectedMoved OR (SELECT COUNT(*) FROM #Work WHERE PrevOwnerId IS NOT NULL) <> @ExpectedReassigned
    THROW 50406, 'ABORTED: the previous departments / owners recorded per ticket do not match the moved / reassigned counts recorded by the batch row.', 1;

BEGIN TRY
    BEGIN TRANSACTION;
    DECLARE @lock int;
    EXEC @lock = sp_getapplock @Resource = N'TigerCS.CallCenterBacklogDistribution', @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 0;
    IF @lock < 0 THROW 50410, 'ABORTED: a backlog distribution / rollback run holds the application lock.', 1;

    -- take the same cursor lock the live engine takes, then lock the batch tickets
    DECLARE @CursorNow uniqueidentifier;
    SELECT @CursorNow = LastAssignedEmployeeId FROM dbo.DepartmentAssignmentRotations WITH (UPDLOCK, ROWLOCK) WHERE DepartmentId = @CcId;

    -- classify every ticket of the batch (locks them)
    UPDATE w SET
        Decision = CASE
            WHEN t.TicketId IS NULL                                  THEN 'SKIP'
            WHEN t.CurrentDepartmentId <> @CcId                      THEN 'SKIP'
            WHEN t.CurrentOwnerEmployeeId IS NULL                    THEN 'SKIP'
            WHEN t.CurrentOwnerEmployeeId <> w.AgentId               THEN 'SKIP'
            WHEN t.TicketStatus <> 1                                 THEN 'SKIP'
            WHEN cur.n <> 1 OR cur.RowOk = 0                         THEN 'SKIP'
            WHEN later.n > 0                                         THEN 'SKIP'
            WHEN w.PrevAssignmentId IS NOT NULL AND prev.n = 0       THEN 'SKIP'
            WHEN @IncludeWorked = 0 AND worked.n > 0                 THEN 'SKIP'
            ELSE 'ROLLBACK' END,
        Reason = CASE
            WHEN t.TicketId IS NULL                                  THEN N'ticket not found'
            WHEN t.CurrentDepartmentId <> @CcId                      THEN N'transferred out of Call Center since the batch'
            WHEN t.CurrentOwnerEmployeeId IS NULL                    THEN N'no owner now (already released)'
            WHEN t.CurrentOwnerEmployeeId <> w.AgentId               THEN N'owner changed since the batch (reassigned) - human ownership preserved'
            WHEN t.TicketStatus <> 1                                 THEN N'no longer Open (work started / resolved / closed since the batch)'
            WHEN cur.n <> 1 OR cur.RowOk = 0                         THEN N'current assignment is not the batch row'
            WHEN later.n > 0                                         THEN N'a later assignment row exists'
            WHEN w.PrevAssignmentId IS NOT NULL AND prev.n = 0       THEN N'the previous owner''s assignment row is missing or current again - cannot restore it safely'
            WHEN @IncludeWorked = 0 AND worked.n > 0                 THEN N'worked since the batch (' + CONVERT(nvarchar(10), worked.n) + N' activity records) - not reverted'
            ELSE N'still exactly as the batch left it' END
    FROM #Work AS w
    LEFT JOIN dbo.Tickets AS t WITH (UPDLOCK, ROWLOCK) ON t.TicketId = w.TicketId
    OUTER APPLY (SELECT (SELECT COUNT(*) FROM dbo.TicketAssignments a WHERE a.TicketId = w.TicketId AND a.IsCurrent = 1) AS n,
                        CASE WHEN EXISTS (SELECT 1 FROM dbo.TicketAssignments a                              -- any current row that is NOT the batch's own row
                                          WHERE a.TicketId = w.TicketId AND a.IsCurrent = 1
                                            AND NOT (a.AssignedEmployeeId = w.AgentId AND a.AssigningActorEmployeeId IS NULL
                                                     AND a.AssignedAtUtc = @BatchAt AND a.AssignedDepartmentId = @CcId))
                             THEN 0 ELSE 1 END AS RowOk) AS cur
    OUTER APPLY (SELECT COUNT(*) AS n FROM dbo.TicketAssignments a WHERE a.TicketId = w.TicketId AND a.AssignedAtUtc > @BatchAt) AS later
    OUTER APPLY (SELECT COUNT(*) AS n FROM dbo.TicketAssignments a WHERE a.TicketAssignmentId = w.PrevAssignmentId AND a.TicketId = w.TicketId AND a.IsCurrent = 0) AS prev
    OUTER APPLY (SELECT (SELECT COUNT(*) FROM dbo.TicketStatusHistory h WHERE h.TicketId = w.TicketId AND h.OccurredAtUtc > @BatchAt)
                      + (SELECT COUNT(*) FROM dbo.TicketNotes n WHERE n.TicketId = w.TicketId AND n.CreatedAtUtc > @BatchAt)
                      + (SELECT COUNT(*) FROM dbo.TicketInteractionHandlers ih WHERE ih.TicketId = w.TicketId AND ih.RecordedAtUtc > @BatchAt)
                      + (SELECT COUNT(*) FROM dbo.TicketApprovals ap WHERE ap.TicketId = w.TicketId AND ap.RequestedAtUtc > @BatchAt)
                      + (SELECT COUNT(*) FROM dbo.TicketInteractions ix WHERE ix.TicketId = w.TicketId AND ix.CreatedAtUtc > @BatchAt)
                      + (SELECT COUNT(*) FROM dbo.TicketWorkflowEvents ev WHERE ev.TicketId = w.TicketId AND ev.OccurredAtUtc > @BatchAt AND ev.CorrelationId <> @BatchId)
                      + CASE WHEN t.FirstHumanResponseAtUtc > @BatchAt THEN 1 ELSE 0 END AS n) AS worked;

    DECLARE @ToRollback int = (SELECT COUNT(*) FROM #Work WHERE Decision = 'ROLLBACK');
    DECLARE @ToRollbackMoved int = (SELECT COUNT(*) FROM #Work WHERE Decision = 'ROLLBACK' AND FromDepartmentId <> @CcId);
    DECLARE @ToRollbackOwned int = (SELECT COUNT(*) FROM #Work WHERE Decision = 'ROLLBACK' AND PrevOwnerId IS NOT NULL);

    IF @ToRollback > 0
    BEGIN
        DECLARE @Now datetime2 = SYSUTCDATETIME(), @n int;

        UPDATE t SET CurrentOwnerEmployeeId = w.PrevOwnerId, CurrentDepartmentId = w.FromDepartmentId
        FROM dbo.Tickets t JOIN #Work w ON w.TicketId = t.TicketId AND w.Decision = 'ROLLBACK'
        WHERE t.CurrentOwnerEmployeeId = w.AgentId AND t.CurrentDepartmentId = @CcId;
        SET @n = @@ROWCOUNT;
        IF @n <> @ToRollback THROW 50420, 'ABORTED: ticket row count mismatch while restoring owner and department.', 1;

        -- keep the history row, just retire it (same as TicketAssignment.MarkSuperseded)
        UPDATE a SET IsCurrent = 0
        FROM dbo.TicketAssignments a JOIN #Work w ON w.TicketId = a.TicketId AND w.Decision = 'ROLLBACK'
        WHERE a.IsCurrent = 1 AND a.AssignedEmployeeId = w.AgentId AND a.AssignedAtUtc = @BatchAt AND a.AssigningActorEmployeeId IS NULL;
        SET @n = @@ROWCOUNT;
        IF @n <> @ToRollback THROW 50421, 'ABORTED: assignment row count mismatch while retiring batch assignments.', 1;

        -- the previous owner's assignment row becomes current again (flag only; the row itself is untouched)
        UPDATE a SET IsCurrent = 1
        FROM dbo.TicketAssignments a JOIN #Work w ON w.TicketId = a.TicketId AND w.Decision = 'ROLLBACK' AND a.TicketAssignmentId = w.PrevAssignmentId
        WHERE a.IsCurrent = 0;
        SET @n = @@ROWCOUNT;
        IF @n <> (SELECT COUNT(*) FROM #Work WHERE Decision = 'ROLLBACK' AND PrevAssignmentId IS NOT NULL)
            THROW 50422, 'ABORTED: assignment row count mismatch while restoring the previous owner''s assignment rows.', 1;

        -- Audit: the department move back (moved tickets only), then the release of the assignment (every rolled-back ticket)
        INSERT dbo.AuditEntries (ActorEmployeeId, Action, EntityType, EntityId, BeforeValue, AfterValue, CorrelationId, OccurredAtUtc)
        SELECT NULL, N'Transfer', N'Ticket', CONVERT(nvarchar(100), w.TicketId),
               N'DepartmentId=' + CONVERT(nvarchar(20), @CcId) + N';AssignedEmployeeId=' + LOWER(CONVERT(nvarchar(36), w.AgentId)),
               N'DepartmentId=' + CONVERT(nvarchar(20), w.FromDepartmentId) + N';Reason=Rollback of one-time backlog redistribution;Trigger=BacklogDistributionRollback;RolledBackBatchId='
                 + LOWER(CONVERT(nvarchar(36), @BatchId)),
               @RollbackId, @Now
        FROM #Work w WHERE w.Decision = 'ROLLBACK' AND w.FromDepartmentId <> @CcId;

        INSERT dbo.AuditEntries (ActorEmployeeId, Action, EntityType, EntityId, BeforeValue, AfterValue, CorrelationId, OccurredAtUtc)
        SELECT NULL, N'AutoAssignRollback', N'Ticket', CONVERT(nvarchar(100), w.TicketId), LOWER(CONVERT(nvarchar(36), w.AgentId)),
               CASE WHEN w.PrevOwnerId IS NULL THEN N'DepartmentQueue' ELSE N'RestoredOwnerId=' + LOWER(CONVERT(nvarchar(36), w.PrevOwnerId)) END
                 + N';DepartmentId=' + CONVERT(nvarchar(20), w.FromDepartmentId) + N';Reason=Rollback of backlog distribution;RolledBackBatchId='
                 + LOWER(CONVERT(nvarchar(36), @BatchId)),
               @RollbackId, @Now
        FROM #Work w WHERE w.Decision = 'ROLLBACK';

        INSERT dbo.TicketWorkflowEvents (TicketId, EventType, OccurredAtUtc, ActorEmployeeId, TicketApprovalId, Note, CorrelationId,
                                         FromDepartmentId, ToDepartmentId, ToEmployeeId, TransferReasonCode)
        SELECT w.TicketId, 17, @Now, NULL, NULL, N'Rollback of one-time backlog redistribution', @RollbackId, @CcId, w.FromDepartmentId, w.PrevOwnerId, 5
        FROM #Work w WHERE w.Decision = 'ROLLBACK' AND (w.FromDepartmentId <> @CcId OR w.PrevOwnerId IS NOT NULL);
    END;

    -- cursor: restore only if no ticket has been dealt since the batch (otherwise leave it; a position in the cycle is harmless)
    DECLARE @CursorAction nvarchar(200) = N'left unchanged';
    IF @ToRollback > 0 AND (@CursorNow = @CursorAfter OR (@CursorNow IS NULL AND @CursorAfter IS NULL))
    BEGIN
        UPDATE dbo.DepartmentAssignmentRotations SET LastAssignedEmployeeId = @CursorBefore, UpdatedAtUtc = SYSUTCDATETIME() WHERE DepartmentId = @CcId;
        SET @CursorAction = N'restored to pre-batch value';
    END
    ELSE IF @CursorNow <> @CursorAfter
        SET @CursorAction = N'NOT restored - live intake advanced it after the batch (harmless)';

    INSERT dbo.AuditEntries (ActorEmployeeId, Action, EntityType, EntityId, BeforeValue, AfterValue, CorrelationId, OccurredAtUtc)
    VALUES (NULL, N'BacklogDistributionRollbackBatch', N'DepartmentAssignmentRotation', CONVERT(nvarchar(100), @CcId),
            LOWER(CONVERT(nvarchar(36), @CursorNow)),
            N'RolledBack=' + CONVERT(nvarchar(20), @ToRollback) + N';DepartmentRestored=' + CONVERT(nvarchar(20), @ToRollbackMoved) + N';PreviousOwnerRestored=' + CONVERT(nvarchar(20), @ToRollbackOwned)
            + N';Skipped=' + CONVERT(nvarchar(20), (SELECT COUNT(*) FROM #Work WHERE Decision = 'SKIP'))
            + N';Cursor=' + @CursorAction + N';BatchId=' + LOWER(CONVERT(nvarchar(36), @BatchId)),
            @RollbackId, SYSUTCDATETIME());

    -- validation: every rolled-back ticket has its previous owner, previous department and exactly the previous current assignment (none if it had none)
    IF EXISTS (SELECT 1 FROM #Work w JOIN dbo.Tickets t ON t.TicketId = w.TicketId
               WHERE w.Decision = 'ROLLBACK' AND ((t.CurrentOwnerEmployeeId IS NULL AND w.PrevOwnerId IS NOT NULL) OR (t.CurrentOwnerEmployeeId IS NOT NULL AND (w.PrevOwnerId IS NULL OR t.CurrentOwnerEmployeeId <> w.PrevOwnerId))))
        THROW 50430, 'VALIDATION FAILED: a rolled-back ticket does not carry its previous owner.', 1;
    IF EXISTS (SELECT 1 FROM #Work w WHERE w.Decision = 'ROLLBACK'
               AND ((w.PrevAssignmentId IS NULL AND EXISTS (SELECT 1 FROM dbo.TicketAssignments a WHERE a.TicketId = w.TicketId AND a.IsCurrent = 1))
                 OR (w.PrevAssignmentId IS NOT NULL AND (SELECT COUNT(*) FROM dbo.TicketAssignments a WHERE a.TicketId = w.TicketId AND a.IsCurrent = 1) <> 1)
                 OR (w.PrevAssignmentId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM dbo.TicketAssignments a WHERE a.TicketAssignmentId = w.PrevAssignmentId AND a.IsCurrent = 1))))
        THROW 50431, 'VALIDATION FAILED: a rolled-back ticket does not have exactly its previous current assignment (or none, if it had none).', 1;
    IF EXISTS (SELECT 1 FROM #Work w JOIN dbo.Tickets t ON t.TicketId = w.TicketId
               WHERE w.Decision = 'ROLLBACK' AND t.CurrentDepartmentId <> w.FromDepartmentId)
        THROW 50432, 'VALIDATION FAILED: a rolled-back ticket is not back in its original department.', 1;
    IF EXISTS (SELECT 1 FROM dbo.TicketAssignments a WHERE a.IsCurrent = 1 GROUP BY a.TicketId HAVING COUNT(*) > 1)
        THROW 50433, 'VALIDATION FAILED: a ticket has more than one current assignment.', 1;

    -- report
    SELECT CASE WHEN @DryRun = 1 THEN 'DRY RUN - will be ROLLED BACK' ELSE 'COMMITTING' END AS Mode, @BatchId AS BatchId, @RollbackId AS RollbackId,
           (SELECT COUNT(*) FROM #Work) AS TicketsInBatch, @ToRollback AS ToRollBack, @ToRollbackMoved AS DepartmentRestoredFor, @ToRollbackOwned AS PreviousOwnerRestoredFor,
           (SELECT COUNT(*) FROM #Work WHERE Decision = 'SKIP') AS SkippedKeepOwner, @CursorAction AS RotationCursor;
    SELECT Decision, Reason, COUNT(*) AS Tickets FROM #Work GROUP BY Decision, Reason ORDER BY Decision, COUNT(*) DESC;
    SELECT e.DisplayName AS Agent, SUM(CASE WHEN w.Decision = 'ROLLBACK' THEN 1 ELSE 0 END) AS RolledBack, SUM(CASE WHEN w.Decision = 'SKIP' THEN 1 ELSE 0 END) AS SkippedKept
    FROM #Work w JOIN dbo.Employees e ON e.EmployeeId = w.AgentId GROUP BY e.DisplayName ORDER BY e.DisplayName;
    SELECT w.TicketId, t.TicketNumber, w.Decision, w.Reason FROM #Work w LEFT JOIN dbo.Tickets t ON t.TicketId = w.TicketId WHERE w.Decision = 'SKIP' ORDER BY w.TicketId;

    IF @DryRun = 1
    BEGIN
        ROLLBACK TRANSACTION;
        PRINT N'DRY RUN complete - nothing changed.';
    END
    ELSE
    BEGIN
        COMMIT TRANSACTION;
        PRINT N'ROLLBACK COMMITTED. Released: ' + CONVERT(nvarchar(20), @ToRollback) + N' (department restored for ' + CONVERT(nvarchar(20), @ToRollbackMoved)
            + N'). RollbackId=' + CONVERT(nvarchar(36), @RollbackId);
    END;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    PRINT N'FAILED - transaction rolled back, nothing changed. ' + ERROR_MESSAGE();
    THROW;
END CATCH;

DROP TABLE IF EXISTS #Work;
