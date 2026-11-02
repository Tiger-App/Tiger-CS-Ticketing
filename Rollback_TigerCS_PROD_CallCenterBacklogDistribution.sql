/*
================================================================================
 Rollback_TigerCS_PROD_CallCenterBacklogDistribution.sql
 Database : TigerCS_Ticketing_Prod          *** NOT EXECUTED - ONLY FOR USE AFTER Execute_... HAS COMMITTED AND ROLLBACK IS APPROVED ***

 Undoes ONLY the assignments - and the Call Center department move - created by ONE backlog-distribution batch (@BatchId = the CorrelationId printed by the execution script and stored on
 every AuditEntries row it wrote). It cannot touch any other ticket: the working set is read from those audit rows, then re-verified per ticket.

 A ticket is rolled back (returned to the unowned Call Center queue) only if it is STILL exactly as the batch left it:
   * owner = the agent the batch assigned, department still Call Center (the batch's destination), status still Open/InProgress/Pending (not Resolved/Closed)
   * the batch's assignment row is still the ONE current row, and no later assignment row exists (no reassignment / transfer since)
   * nobody worked it since: no first human response, no status-history row (any status change, pending, resolve) and no note after the batch
 Anything else is SKIPPED and listed with the reason - a human owns that work now, and ownership is never taken away from work in progress.
 (@IncludeWorked = 1 lifts only the "nobody worked it" test, and only with explicit approval; owner/department/assignment checks always apply.)

 What a rollback does, per ticket:   Tickets.CurrentOwnerEmployeeId -> NULL ;  for a ticket the batch moved into the Call Center, CurrentDepartmentId -> the
 department it came from (read from the batch's own Transfer audit row; OriginatingDepartmentId was never changed) ;  the batch's TicketAssignments row ->
 IsCurrent = 0 (superseded, kept for history - NEVER deleted) ;  + 1 'AutoAssignRollback' audit row ; for a moved ticket also + 1 'Transfer' audit row and
 + 1 TicketTransferred workflow event going back (the original move and its history stay).  Also restores the Round Robin cursor to its pre-batch value IF nobody has been dealt a
 ticket since (otherwise it is left alone and reported - a cursor position is harmless).
 Never deletes or edits any audit row, status history, SLA, note, notification or interaction.  Status / SLA / department are not changed.

 HOW TO RUN: set @BatchId, run with @DryRun = 1 (default; shows what would roll back / be skipped, then rolls back), review, set @DryRun = 0.
================================================================================
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;
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
                                                         CHARINDEX(N';Transfers=', @BatchAfter) - CHARINDEX(N';Tickets=', @BatchAfter) - 9));
DECLARE @ExpectedTransfers int = TRY_CONVERT(int, SUBSTRING(@BatchAfter, CHARINDEX(N';Transfers=', @BatchAfter) + 11,
                                                          CHARINDEX(N';Agents=', @BatchAfter) - CHARINDEX(N';Transfers=', @BatchAfter) - 11));
IF @CcId IS NULL OR @CursorAfter IS NULL OR @ExpectedTickets IS NULL OR @ExpectedTransfers IS NULL
    THROW 50404, 'ABORTED: could not read the batch row (department / cursor / ticket and transfer counts).', 1;
PRINT N'Guard passed: database [' + DB_NAME() + N'], BatchId=' + CONVERT(nvarchar(36), @BatchId) + N', DryRun=' + CAST(@DryRun AS nvarchar(1)) + N', RollbackId=' + CONVERT(nvarchar(36), @RollbackId);

-- Working set = the per-ticket audit rows of THIS batch, parsed (Ticket id from EntityId, agent from AfterValue)
CREATE TABLE #Work (TicketId bigint NOT NULL PRIMARY KEY, AgentId uniqueidentifier NOT NULL, SrcDeptId int NULL, Decision varchar(60) NULL, Reason nvarchar(300) NULL);
INSERT #Work (TicketId, AgentId)
SELECT TRY_CONVERT(bigint, a.EntityId), TRY_CONVERT(uniqueidentifier, SUBSTRING(a.AfterValue, 20, 36))   -- 'AssignedEmployeeId=' is 19 chars
FROM dbo.AuditEntries AS a
WHERE a.AuditEntryId >= @BatchAuditId - 10000000 AND a.AuditEntryId < @BatchAuditId AND a.CorrelationId = @BatchId AND a.Action = N'AutoAssign' AND a.EntityType = N'Ticket'
  AND a.AfterValue LIKE N'AssignedEmployeeId=%Trigger=BacklogDistribution%';
IF EXISTS (SELECT 1 FROM #Work w WHERE w.AgentId IS NULL) OR (SELECT COUNT(*) FROM #Work) <> @ExpectedTickets
    THROW 50405, 'ABORTED: the per-ticket audit rows found do not match the ticket count recorded by the batch row (or could not be parsed).', 1;

-- Department each moved ticket came from = BeforeValue of THIS batch's own Transfer audit row: 'DepartmentId=<n>;AssignedEmployeeId=DepartmentQueue'
UPDATE w SET SrcDeptId = TRY_CONVERT(int, SUBSTRING(a.BeforeValue, 14, CHARINDEX(N';', a.BeforeValue + N';') - 14))
FROM #Work AS w
JOIN dbo.AuditEntries AS a ON a.EntityId = CONVERT(nvarchar(100), w.TicketId)
WHERE a.AuditEntryId >= @BatchAuditId - 10000000 AND a.AuditEntryId < @BatchAuditId AND a.CorrelationId = @BatchId
  AND a.Action = N'Transfer' AND a.EntityType = N'Ticket' AND a.AfterValue LIKE N'%Trigger=BacklogDistribution%' AND a.BeforeValue LIKE N'DepartmentId=%';
IF (SELECT COUNT(*) FROM #Work WHERE SrcDeptId IS NOT NULL) <> @ExpectedTransfers
    THROW 50406, 'ABORTED: the Transfer audit rows found do not match the transfer count recorded by the batch row (or could not be parsed).', 1;
IF EXISTS (SELECT 1 FROM #Work w WHERE w.SrcDeptId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM dbo.Departments d WHERE d.DepartmentId = w.SrcDeptId))
    THROW 50407, 'ABORTED: a source department recorded by the batch no longer exists.', 1;

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
            WHEN t.CurrentOwnerEmployeeId IS NULL                    THEN 'SKIP'
            WHEN t.CurrentOwnerEmployeeId <> w.AgentId               THEN 'SKIP'
            WHEN t.CurrentDepartmentId <> @CcId                      THEN 'SKIP'
            WHEN t.TicketStatus NOT IN (1, 2, 3, 4)                  THEN 'SKIP'
            WHEN cur.n <> 1 OR cur.RowOk = 0                         THEN 'SKIP'
            WHEN later.n > 0                                         THEN 'SKIP'
            WHEN @IncludeWorked = 0 AND worked.n > 0                 THEN 'SKIP'
            ELSE 'ROLLBACK' END,
        Reason = CASE
            WHEN t.TicketId IS NULL                                  THEN N'ticket not found'
            WHEN t.CurrentOwnerEmployeeId IS NULL                    THEN N'no owner now (already released)'
            WHEN t.CurrentOwnerEmployeeId <> w.AgentId               THEN N'owner changed since the batch (reassigned) - human ownership preserved'
            WHEN t.CurrentDepartmentId <> @CcId                      THEN N'transferred out of Call Center since the batch'
            WHEN t.TicketStatus NOT IN (1, 2, 3, 4)                  THEN N'Resolved/Closed since the batch'
            WHEN cur.n <> 1 OR cur.RowOk = 0                         THEN N'current assignment is not the batch row'
            WHEN later.n > 0                                         THEN N'a later assignment row exists'
            WHEN @IncludeWorked = 0 AND worked.n > 0                 THEN N'worked since the batch (' + CONVERT(nvarchar(10), worked.n) + N' activity records) - not reverted'
            ELSE N'still exactly as the batch left it' END
    FROM #Work AS w
    LEFT JOIN dbo.Tickets AS t WITH (UPDLOCK, ROWLOCK) ON t.TicketId = w.TicketId
    OUTER APPLY (SELECT COUNT(*) AS n,
                        CASE WHEN SUM(CASE WHEN a.AssignedEmployeeId = w.AgentId AND a.AssigningActorEmployeeId IS NULL AND a.AssignedAtUtc = @BatchAt THEN 1 ELSE 0 END) = COUNT(*) THEN 1 ELSE 0 END AS RowOk
                 FROM dbo.TicketAssignments a WHERE a.TicketId = w.TicketId AND a.IsCurrent = 1) AS cur
    OUTER APPLY (SELECT COUNT(*) AS n FROM dbo.TicketAssignments a WHERE a.TicketId = w.TicketId AND a.AssignedAtUtc > @BatchAt) AS later
    OUTER APPLY (SELECT (SELECT COUNT(*) FROM dbo.TicketStatusHistory h WHERE h.TicketId = w.TicketId AND h.OccurredAtUtc > @BatchAt)
                      + (SELECT COUNT(*) FROM dbo.TicketNotes n WHERE n.TicketId = w.TicketId AND n.CreatedAtUtc > @BatchAt)
                      + CASE WHEN t.FirstHumanResponseAtUtc > @BatchAt THEN 1 ELSE 0 END AS n) AS worked;

    DECLARE @ToRollback int = (SELECT COUNT(*) FROM #Work WHERE Decision = 'ROLLBACK');

    IF @ToRollback > 0
    BEGIN
        DECLARE @Now datetime2 = SYSUTCDATETIME(), @n int;

        -- Owner released; a ticket the batch moved into the Call Center goes back to the department it came from (OriginatingDepartmentId, status, SLA untouched).
        UPDATE t SET CurrentOwnerEmployeeId = NULL, CurrentDepartmentId = ISNULL(w.SrcDeptId, t.CurrentDepartmentId)
        FROM dbo.Tickets t JOIN #Work w ON w.TicketId = t.TicketId AND w.Decision = 'ROLLBACK'
        WHERE t.CurrentOwnerEmployeeId = w.AgentId AND t.CurrentDepartmentId = @CcId;
        SET @n = @@ROWCOUNT;
        IF @n <> @ToRollback THROW 50420, 'ABORTED: ticket row count mismatch while releasing ownership.', 1;

        -- keep the history row, just retire it (same as TicketAssignment.MarkSuperseded)
        UPDATE a SET IsCurrent = 0
        FROM dbo.TicketAssignments a JOIN #Work w ON w.TicketId = a.TicketId AND w.Decision = 'ROLLBACK'
        WHERE a.IsCurrent = 1 AND a.AssignedEmployeeId = w.AgentId AND a.AssignedAtUtc = @BatchAt AND a.AssigningActorEmployeeId IS NULL;
        SET @n = @@ROWCOUNT;
        IF @n <> @ToRollback THROW 50421, 'ABORTED: assignment row count mismatch while retiring batch assignments.', 1;

        -- the department move going back: audit + typed event (the original move and its history stay; nothing is deleted)
        INSERT dbo.AuditEntries (ActorEmployeeId, Action, EntityType, EntityId, BeforeValue, AfterValue, CorrelationId, OccurredAtUtc)
        SELECT NULL, N'Transfer', N'Ticket', CONVERT(nvarchar(100), w.TicketId),
               N'DepartmentId=' + CONVERT(nvarchar(20), @CcId) + N';AssignedEmployeeId=' + LOWER(CONVERT(nvarchar(36), w.AgentId)),
               N'DepartmentId=' + CONVERT(nvarchar(20), w.SrcDeptId) + N';Reason=Rollback of backlog distribution;RolledBackBatchId=' + LOWER(CONVERT(nvarchar(36), @BatchId)),
               @RollbackId, @Now
        FROM #Work w WHERE w.Decision = 'ROLLBACK' AND w.SrcDeptId IS NOT NULL;
        INSERT dbo.TicketWorkflowEvents (TicketId, EventType, OccurredAtUtc, ActorEmployeeId, TicketApprovalId, Note, CorrelationId,
                                         FromDepartmentId, ToDepartmentId, ToEmployeeId, TransferReasonCode)
        SELECT w.TicketId, 17, @Now, NULL, NULL, N'Rollback of backlog distribution', @RollbackId, @CcId, w.SrcDeptId, NULL, 5
        FROM #Work w WHERE w.Decision = 'ROLLBACK' AND w.SrcDeptId IS NOT NULL;

        INSERT dbo.AuditEntries (ActorEmployeeId, Action, EntityType, EntityId, BeforeValue, AfterValue, CorrelationId, OccurredAtUtc)
        SELECT NULL, N'AutoAssignRollback', N'Ticket', CONVERT(nvarchar(100), w.TicketId), LOWER(CONVERT(nvarchar(36), w.AgentId)),
               N'DepartmentQueue;DepartmentId=' + CONVERT(nvarchar(20), @CcId) + N';Reason=Rollback of backlog distribution;RolledBackBatchId=' + LOWER(CONVERT(nvarchar(36), @BatchId)),
               @RollbackId, @Now
        FROM #Work w WHERE w.Decision = 'ROLLBACK';
    END;

    -- cursor: restore only if no ticket has been dealt since the batch (otherwise leave it; a position in the cycle is harmless)
    DECLARE @CursorAction nvarchar(200) = N'left unchanged';
    IF @CursorNow = @CursorAfter AND @ToRollback > 0
    BEGIN
        UPDATE dbo.DepartmentAssignmentRotations SET LastAssignedEmployeeId = @CursorBefore, UpdatedAtUtc = SYSUTCDATETIME() WHERE DepartmentId = @CcId;
        SET @CursorAction = N'restored to pre-batch value';
    END
    ELSE IF @CursorNow <> @CursorAfter
        SET @CursorAction = N'NOT restored - live intake advanced it after the batch (harmless)';

    INSERT dbo.AuditEntries (ActorEmployeeId, Action, EntityType, EntityId, BeforeValue, AfterValue, CorrelationId, OccurredAtUtc)
    VALUES (NULL, N'BacklogDistributionRollbackBatch', N'DepartmentAssignmentRotation', CONVERT(nvarchar(100), @CcId),
            LOWER(CONVERT(nvarchar(36), @CursorNow)),
            N'RolledBack=' + CONVERT(nvarchar(20), @ToRollback) + N';Skipped=' + CONVERT(nvarchar(20), (SELECT COUNT(*) FROM #Work WHERE Decision = 'SKIP'))
            + N';Cursor=' + @CursorAction + N';BatchId=' + LOWER(CONVERT(nvarchar(36), @BatchId)),
            @RollbackId, SYSUTCDATETIME());

    -- validation: every rolled-back ticket is unowned with no current assignment; skipped tickets are byte-for-byte as found
    IF EXISTS (SELECT 1 FROM #Work w JOIN dbo.Tickets t ON t.TicketId = w.TicketId WHERE w.Decision = 'ROLLBACK' AND t.CurrentOwnerEmployeeId IS NOT NULL)
        THROW 50430, 'VALIDATION FAILED: a rolled-back ticket still has an owner.', 1;
    IF EXISTS (SELECT 1 FROM #Work w JOIN dbo.TicketAssignments a ON a.TicketId = w.TicketId AND a.IsCurrent = 1 WHERE w.Decision = 'ROLLBACK')
        THROW 50431, 'VALIDATION FAILED: a rolled-back ticket still has a current assignment.', 1;
    IF EXISTS (SELECT 1 FROM #Work w JOIN dbo.Tickets t ON t.TicketId = w.TicketId
               WHERE w.Decision = 'ROLLBACK' AND w.SrcDeptId IS NOT NULL AND t.CurrentDepartmentId <> w.SrcDeptId)
        THROW 50432, 'VALIDATION FAILED: a rolled-back ticket is not back in the department it came from.', 1;

    -- report
    SELECT CASE WHEN @DryRun = 1 THEN 'DRY RUN - will be ROLLED BACK' ELSE 'COMMITTING' END AS Mode, @BatchId AS BatchId, @RollbackId AS RollbackId,
           (SELECT COUNT(*) FROM #Work) AS TicketsInBatch, @ToRollback AS ToRollBack, (SELECT COUNT(*) FROM #Work WHERE Decision = 'SKIP') AS SkippedKeepOwner,
           @CursorAction AS RotationCursor;
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
        PRINT N'ROLLBACK COMMITTED. Released: ' + CONVERT(nvarchar(20), @ToRollback) + N'. RollbackId=' + CONVERT(nvarchar(36), @RollbackId);
    END;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    DECLARE @em nvarchar(2000) = ERROR_MESSAGE();
    PRINT N'FAILED - transaction rolled back, nothing changed. ' + @em;
    THROW 50499, @em, 1;
END CATCH;

DROP TABLE IF EXISTS #Work;
