/*
================================================================================
 Backfill_TigerCS_PROD_ClosedTickets_ClearOwner.sql
 Database : TigerCS_Ticketing_Prod          *** NOT EXECUTED - REQUIRES WRITTEN APPROVAL BEFORE @DryRun = 0 ***

 PURPOSE
   The application now clears the owner whenever a ticket becomes Closed (manual close, Genesys document-delivery / AI-completion close, chatbot
   inactivity auto-close). Tickets closed BEFORE that change still carry Tickets.CurrentOwnerEmployeeId. This one-time backfill brings them in line.

 WHAT IT WRITES (all in ONE transaction, all-or-nothing), ONLY for tickets with TicketStatus = Closed (6) AND CurrentOwnerEmployeeId IS NOT NULL:
   * dbo.Tickets            CurrentOwnerEmployeeId -> NULL            (nothing else on the ticket changes)
   * dbo.TicketAssignments  the CURRENT row (IsCurrent = 1) -> IsCurrent = 0   (the row is KEPT; it is never deleted or otherwise edited)
   * dbo.AuditEntries       +1 'ClearAssignmentOnClosure' row per ticket; BeforeValue preserves the previous owner
                            ("CurrentOwnerEmployeeId=<guid>;AssignmentId=<id>"), AfterValue = "CurrentOwnerEmployeeId=NULL;Backfill=<BatchId>".
 NEVER TOUCHED: Open / InProgress / PendingCustomer / Resolved tickets, departments, classifications, SLA rows, status history, interactions, notes,
   resolutions, approvals, outbox/notifications, and every assignment row except the IsCurrent flag of the one current row of a targeted ticket.
   No schema change. No notification is emitted.

 SAFETY
   * @DryRun = 1 (DEFAULT): performs every write, runs every validation, prints the result sets, then ROLLS BACK. Nothing is persisted.
   * DB-name guard; aborts if a column it depends on is missing.
   * COMMIT GATE: @DryRun = 0 additionally requires @ApprovedTicketCount to equal the count this run computes (copy it from a dry run).
   * Concurrency: candidates are locked (UPDLOCK, ROWLOCK) and re-checked in the UPDATE (still Closed, same owner); the run aborts if even one row
     differs from the plan. sp_getapplock blocks a second concurrent run. @MaxTickets (default 5000) bounds one run; run again for the rest.
   * Idempotent: a ticket that no longer has an owner is not a candidate, so a second run finds nothing to do.
   * Rollback of ONLY this batch: the audit rows carry the previous owner and the batch id, so each owner can be restored from
     AuditEntries WHERE Action = 'ClearAssignmentOnClosure' AND AfterValue LIKE '%Backfill=<BatchId>%' (and the superseded row's IsCurrent re-set).
   * Names: the owner's full name is resolved by the application from Employees / AspNetUsers; this script does not need or change employee data.

 HOW TO RUN (SSMS, ONE batch, no GO):  1) Full backup.  2) Run with defaults (@DryRun = 1) - on a restored copy first, then Production.
   3) After sign-off set @ApprovedTicketCount to the dry-run's count and @DryRun = 0 in the approved window.
================================================================================
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;               -- required by the filtered index on TicketAssignments (sqlcmd needs -I)
SET ANSI_NULLS ON;
SET LOCK_TIMEOUT 20000;

-- ------------------------------------------------ PARAMETERS ------------------------------------------------
DECLARE @DryRun               bit              = 1;     -- 1 = do everything, then ROLL BACK (default).  0 = COMMIT.
DECLARE @ApprovedTicketCount  int              = NULL;  -- commit gate: the number of tickets a dry run reported
DECLARE @MaxTickets           int              = 5000;  -- upper bound for one run
DECLARE @BatchId              uniqueidentifier = NEWID();
DECLARE @ExpectedDb           sysname          = N'TigerCS_Ticketing_Prod';
DECLARE @ClosedStatus         tinyint          = 6;     -- TicketStatus.Closed
-- ------------------------------------------------------------------------------------------------------------

DECLARE @msg nvarchar(2000);
IF DB_NAME() <> @ExpectedDb
BEGIN
    SET @msg = N'ABORTED: connected to [' + ISNULL(DB_NAME(), N'?') + N'] on [' + CAST(@@SERVERNAME AS nvarchar(128)) + N']; this script only runs against [' + @ExpectedDb + N']. Nothing was changed.';
    THROW 50400, @msg, 1;
END;
PRINT N'Guard passed: server [' + CAST(@@SERVERNAME AS nvarchar(128)) + N'], database [' + DB_NAME() + N'], DryRun=' + CAST(@DryRun AS nvarchar(1)) + N', BatchId=' + CONVERT(nvarchar(36), @BatchId);

DECLARE @missing nvarchar(max) = N'';
SELECT @missing += v.Tbl + N'.' + v.Col + N' ' FROM (VALUES
    (N'Tickets', N'TicketId'), (N'Tickets', N'TicketStatus'), (N'Tickets', N'CurrentOwnerEmployeeId'),
    (N'TicketAssignments', N'TicketAssignmentId'), (N'TicketAssignments', N'TicketId'), (N'TicketAssignments', N'AssignedEmployeeId'), (N'TicketAssignments', N'IsCurrent'),
    (N'AuditEntries', N'ActorEmployeeId'), (N'AuditEntries', N'Action'), (N'AuditEntries', N'EntityType'), (N'AuditEntries', N'EntityId'),
    (N'AuditEntries', N'BeforeValue'), (N'AuditEntries', N'AfterValue'), (N'AuditEntries', N'CorrelationId'), (N'AuditEntries', N'OccurredAtUtc')
) v(Tbl, Col)
WHERE COL_LENGTH(N'dbo.' + v.Tbl, v.Col) IS NULL;
IF @missing <> N'' THROW 50401, 'ABORTED: missing column(s) this script depends on (apply Deploy_TigerCS_Prod_Migrations.sql first).', 1;

DECLARE @lock int;
EXEC @lock = sp_getapplock @Resource = N'TigerCS_ClosedTickets_ClearOwner', @LockMode = N'Exclusive', @LockOwner = N'Session', @LockTimeout = 0;
IF @lock < 0 THROW 50402, 'ABORTED: another run of this script holds the lock. Nothing was changed.', 1;

DECLARE @Now datetime2(7) = SYSUTCDATETIME();
DECLARE @Plan TABLE (
    TicketId bigint NOT NULL PRIMARY KEY, PreviousOwnerId uniqueidentifier NOT NULL, CurrentAssignmentId bigint NULL, RowVersionAtPlan varbinary(8) NULL);

-- ---- read-only plan (what a dry run reports) ----
INSERT @Plan (TicketId, PreviousOwnerId, CurrentAssignmentId)
SELECT TOP (@MaxTickets) t.TicketId, t.CurrentOwnerEmployeeId,
       (SELECT a.TicketAssignmentId FROM dbo.TicketAssignments a WHERE a.TicketId = t.TicketId AND a.IsCurrent = 1)
FROM dbo.Tickets t
WHERE t.TicketStatus = @ClosedStatus AND t.CurrentOwnerEmployeeId IS NOT NULL
ORDER BY t.TicketId;

DECLARE @PlannedCount int = (SELECT COUNT(*) FROM @Plan);
DECLARE @WithCurrentRow int = (SELECT COUNT(*) FROM @Plan WHERE CurrentAssignmentId IS NOT NULL);
DECLARE @OwnerMissing int = (SELECT COUNT(*) FROM @Plan p WHERE NOT EXISTS (SELECT 1 FROM dbo.Employees e WHERE e.EmployeeId = p.PreviousOwnerId));

SELECT N'PLAN' AS [Section], @PlannedCount AS ApprovedTicketCount, @WithCurrentRow AS TicketsWithACurrentAssignmentRow,
       @OwnerMissing AS PreviousOwnersWithoutAnEmployeeRecord_ReportedOnly, @MaxTickets AS MaxTickets;
SELECT TOP (50) N'SAMPLE' AS [Section], p.TicketId, p.PreviousOwnerId, e.DisplayName AS PreviousOwnerName, p.CurrentAssignmentId
FROM @Plan p LEFT JOIN dbo.Employees e ON e.EmployeeId = p.PreviousOwnerId ORDER BY p.TicketId;
IF @OwnerMissing > 0
    SELECT N'DATA INCONSISTENCY (owner has no Employees row; ownership is still cleared, the id is preserved in the audit entry)' AS [Section], p.TicketId, p.PreviousOwnerId
    FROM @Plan p WHERE NOT EXISTS (SELECT 1 FROM dbo.Employees e WHERE e.EmployeeId = p.PreviousOwnerId);

IF @DryRun = 0 AND (@ApprovedTicketCount IS NULL OR @ApprovedTicketCount <> @PlannedCount)
BEGIN
    SET @msg = N'ABORTED: commit gate - @ApprovedTicketCount (' + ISNULL(CAST(@ApprovedTicketCount AS nvarchar(20)), N'NULL') + N') must equal the planned count (' + CAST(@PlannedCount AS nvarchar(20)) + N'). Nothing was changed.';
    THROW 50403, @msg, 1;
END;

BEGIN TRANSACTION;
BEGIN TRY
    -- lock + re-check the candidates; the plan must still be exactly true
    DECLARE @Locked int;
    SELECT @Locked = COUNT(*) FROM dbo.Tickets t WITH (UPDLOCK, ROWLOCK)
    JOIN @Plan p ON p.TicketId = t.TicketId
    WHERE t.TicketStatus = @ClosedStatus AND t.CurrentOwnerEmployeeId = p.PreviousOwnerId;
    IF @Locked <> @PlannedCount THROW 50404, 'ABORTED: a planned ticket changed (no longer Closed, or its owner changed) since the plan was read. Rolled back.', 1;

    -- 1. audit first: the previous owner is preserved before it is cleared
    INSERT dbo.AuditEntries (ActorEmployeeId, Action, EntityType, EntityId, BeforeValue, AfterValue, CorrelationId, OccurredAtUtc)
    SELECT NULL, N'ClearAssignmentOnClosure', N'Ticket', CONVERT(nvarchar(100), p.TicketId),
           N'CurrentOwnerEmployeeId=' + CONVERT(nvarchar(36), p.PreviousOwnerId)
             + ISNULL(N';AssignmentId=' + CONVERT(nvarchar(30), p.CurrentAssignmentId), N''),
           N'CurrentOwnerEmployeeId=NULL;Backfill=' + CONVERT(nvarchar(36), @BatchId),
           @BatchId, @Now
    FROM @Plan p;

    -- 2. end the current assignment row (kept, never deleted)
    UPDATE a SET IsCurrent = 0
    FROM dbo.TicketAssignments a JOIN @Plan p ON p.CurrentAssignmentId = a.TicketAssignmentId
    WHERE a.IsCurrent = 1;
    DECLARE @RowsEnded int = @@ROWCOUNT;
    IF @RowsEnded <> @WithCurrentRow THROW 50405, 'ABORTED: the number of assignment rows ended differs from the plan. Rolled back.', 1;

    -- 3. clear the owner
    UPDATE t SET CurrentOwnerEmployeeId = NULL
    FROM dbo.Tickets t JOIN @Plan p ON p.TicketId = t.TicketId
    WHERE t.TicketStatus = @ClosedStatus AND t.CurrentOwnerEmployeeId = p.PreviousOwnerId;
    IF @@ROWCOUNT <> @PlannedCount THROW 50406, 'ABORTED: the number of tickets updated differs from the plan. Rolled back.', 1;

    -- validation: no open-state ticket was touched, nothing is left to do, no current row remains on a targeted ticket
    IF EXISTS (SELECT 1 FROM dbo.Tickets t JOIN @Plan p ON p.TicketId = t.TicketId WHERE t.TicketStatus <> @ClosedStatus)
        THROW 50407, 'ABORTED: a targeted ticket is not Closed. Rolled back.', 1;
    IF EXISTS (SELECT 1 FROM dbo.TicketAssignments a JOIN @Plan p ON p.TicketId = a.TicketId WHERE a.IsCurrent = 1)
        THROW 50408, 'ABORTED: a targeted ticket still has a current assignment row. Rolled back.', 1;

    SELECT N'RESULT' AS [Section], @PlannedCount AS TicketsOwnerCleared, @RowsEnded AS AssignmentRowsEnded, @PlannedCount AS AuditRowsWritten, @BatchId AS BatchId,
           CASE WHEN @DryRun = 1 THEN N'DRY RUN - rolling back, nothing persisted' ELSE N'COMMITTING' END AS Mode;

    IF @DryRun = 1
    BEGIN
        ROLLBACK TRANSACTION;
        PRINT N'DRY RUN complete: everything above was rolled back. Set @ApprovedTicketCount = ' + CAST(@PlannedCount AS nvarchar(20)) + N' and @DryRun = 0 (after approval) to commit.';
    END
    ELSE
    BEGIN
        COMMIT TRANSACTION;
        PRINT N'COMMITTED. BatchId = ' + CONVERT(nvarchar(36), @BatchId);
    END;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    EXEC sp_releaseapplock @Resource = N'TigerCS_ClosedTickets_ClearOwner', @LockOwner = N'Session';
    THROW;
END CATCH;

EXEC sp_releaseapplock @Resource = N'TigerCS_ClosedTickets_ClearOwner', @LockOwner = N'Session';
