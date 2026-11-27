-- ############################################################################################
-- ## TigerCS PRODUCTION ONLY - HISTORICAL DATA CORRECTION (start time only).
-- ## Server   : TGAESHJDBP02
-- ## Database : TigerCS_Ticketing_Prod   (any other server or database aborts before any read or change)
-- ## NOT EXECUTED by Claude. Needs explicit approval. Run only after Diagnose_TigerCS_PROD_GenesysHistoricalTimestamps_ReadOnly.sql
-- ## has been reviewed, the application build containing PR #292 is deployed, and a fresh full backup exists.
-- ############################################################################################
-- WHAT IT FIXES
--   Before PR #292 an offset-bearing UTC timestamp from Genesys ("...+00:00") was read into the application host's zone and stored as UAE wall-clock time in a "...Utc"
--   column: exactly +4 hours. On Production the proven damage is on the conversation START only (InteractionStartedAtUtc).
--
-- WHAT IT DOES (and refuses to do)
--   * It re-validates EVERY Genesys conversation against the CURRENT Production data on each run (the evidence block below is identical to the read-only diagnostic) and
--     corrects ONLY conversations classified PROVEN_START_OFFSET: start later than the ticket's own CreatedAtUtc (server clock) AND, minus 4h, it lands just before the
--     conversation's own first transcript line; end and lines are plausible. A 4h difference alone is never proof. Everything else is counted and left untouched:
--     SUSPECT, CORRECT, and PROVEN_FULL_OFFSET (reported; this script does not move end/transcript/connect times - that needs its own approval).
--   * It changes exactly ONE column: TicketInteractions.InteractionStartedAtUtc. It never writes EndedAtUtc, transcript SentAtUtc, ConnectedAtUtc, Tickets (creation date,
--     First Human Response, status, owner...), SLA rows, assignments, status history, resolutions, or any existing audit row. This is PROVEN inside the transaction
--     (checksums over every one of those, plus every other interaction's start) and the run aborts and rolls back if anything protected moved.
--   * ORIGINAL VALUES ARE PRESERVED, durably: one NEW append-only audit row per corrected conversation (action GenesysTimestampCorrected) holds the original and the new
--     start, the TicketInteractionId, the conversation id and the proof. Every run (dry or real) also returns the exact ROLLBACK STATEMENTS. @RollbackRunId restores from the
--     audit rows alone (below), even if the SSMS result sets were lost.
--   * IDEMPOTENT: (1) a corrected start no longer matches the proof, so a re-run finds nothing; (2) independently, a conversation that already has a GenesysTimestampCorrected
--     audit row is never planned again.
--   * SLA is NOT touched or recalculated (a start-only correction moves no transcript line, so First Human Response is unaffected).
--
-- HOW TO RUN (SSMS, connected to TGAESHJDBP02, database TigerCS_Ticketing_Prod). Run as ONE batch. There is intentionally no GO in this script.
--   1. DRY RUN (default, @DryRun = 1): NOTHING is written (not even a transaction). Check the "REVALIDATION" result set against the approved diagnostic, review
--      "PLAN: conversations" and the ROLLBACK STATEMENTS, and save the result sets (the before-image).
--   2. PILOT ONE TICKET: set @TicketId to one ticket from the PLAN, run with @DryRun = 1, then with @DryRun = 0. Check the Ticket Details page and the POST result sets.
--   3. REST: clear @TicketId (NULL) and run with @DryRun = 0; each run corrects at most @MaxConversations (oldest first). Repeat until it prints "Nothing to correct.".
--      A run for ALL tickets aborts if more conversations are PROVEN than @ApprovedProvenStart (the number approved from the diagnostic).
--
-- ROLLBACK (undo one committed run): set @RollbackRunId to the run id printed by that run (also the CorrelationId of its audit rows). Leave @DryRun = 1 to list what would be
--   restored, then @DryRun = 0 to restore, optimistically (only rows still holding the corrected value) and with an audit row per restored conversation. A rolled-back
--   conversation keeps its GenesysTimestampCorrected audit row, so it is NOT corrected again automatically.
-- ############################################################################################

SET NOCOUNT ON;
SET XACT_ABORT ON;
SET LOCK_TIMEOUT 15000;

DECLARE @DryRun bit = 1;                       -- 1 = show the plan, write NOTHING (default).  0 = apply, validate, COMMIT.
DECLARE @TicketId bigint = NULL;               -- NULL = every PROVEN conversation; a ticket id = pilot on that ticket only
DECLARE @MaxConversations int = 200;           -- cap per run (oldest first), so a large backlog is corrected in reviewable batches
DECLARE @RollbackRunId uniqueidentifier = NULL;-- NULL = normal run. A run id = restore the ORIGINAL start of that run's conversations (from its audit rows)
DECLARE @ToleranceMin int = 5;
DECLARE @ShiftHours int = 4;                   -- Asia/Dubai: UTC+4, no daylight saving
DECLARE @MaxStartToFirstLineMin int = 15;      -- the real gap between a conversation's start and its first message can be at most this long (same as the diagnostic)
DECLARE @OnlyCreatedBeforeUtc datetime2 = NULL;-- set to the moment the build with PR #292 went live: later tickets cannot be shifted and are never considered

-- the production diagnostic that was approved (reference for the REVALIDATION result set); @ApprovedProvenStart is also a CEILING for a real run over all tickets
DECLARE @ApprovedTotal int = 2531, @ApprovedProvenStart int = 1693, @ApprovedSuspect int = 549, @ApprovedCorrect int = 289, @ApprovedPriorCorrections int = 0;

DECLARE @ExpectedServer sysname = N'TGAESHJDBP02';
DECLARE @ExpectedDb     sysname = N'TigerCS_Ticketing_Prod';
DECLARE @msg nvarchar(2000);

-- ---------------------------------------------------------------------------------------------
-- 1. Server + database protection (nothing has been read or touched yet)
-- ---------------------------------------------------------------------------------------------
DECLARE @ServerName sysname = CAST(@@SERVERNAME AS sysname);
IF DB_NAME() IS NULL OR DB_NAME() <> @ExpectedDb
   OR @ServerName IS NULL OR (UPPER(@ServerName) <> UPPER(@ExpectedServer) AND UPPER(@ServerName) NOT LIKE UPPER(@ExpectedServer) + N'\%')
BEGIN
    SET @msg = N'ABORTED: connected to database [' + ISNULL(DB_NAME(), N'?') + N'] on server [' + ISNULL(@ServerName, N'?')
             + N']; this Production script only runs against [' + @ExpectedDb + N'] on [' + @ExpectedServer + N']. Nothing was changed.';
    THROW 50300, @msg, 1;
END;
PRINT N'Guard passed: server [' + @ServerName + N'], database [' + DB_NAME() + N'], DryRun=' + CAST(@DryRun AS nvarchar(1))
      + N', TicketId=' + ISNULL(CAST(@TicketId AS nvarchar(20)), N'(all)') + N', RollbackRunId=' + ISNULL(CAST(@RollbackRunId AS nvarchar(40)), N'(none)') + N'.';

IF OBJECT_ID(N'dbo.TicketInteractions', N'U') IS NULL OR OBJECT_ID(N'dbo.TicketInteractionMessages', N'U') IS NULL
   OR OBJECT_ID(N'dbo.TicketInteractionHandlers', N'U') IS NULL OR OBJECT_ID(N'dbo.AuditEntries', N'U') IS NULL
    THROW 50301, 'ABORTED: expected Ticketing tables are missing - wrong database?', 1;

-- every table/column this script reads or protects must exist (i.e. the required migrations are applied); a missing one aborts BEFORE anything is read for the plan
DECLARE @Missing nvarchar(2000) = N'';
SELECT @Missing = @Missing + N' ' + r.Obj
FROM (VALUES
    (N'Tickets', N'TicketId'), (N'Tickets', N'TicketNumber'), (N'Tickets', N'TicketStatus'), (N'Tickets', N'CreatedAtUtc'), (N'Tickets', N'FirstHumanResponseAtUtc'),
    (N'Tickets', N'AcknowledgementSentAtUtc'), (N'Tickets', N'CurrentOwnerEmployeeId'), (N'Tickets', N'AiAgentOwnerEmployeeId'), (N'Tickets', N'CurrentDepartmentId'),
    (N'Tickets', N'SlaState'), (N'Tickets', N'ResolutionOutcome'), (N'Tickets', N'ReopenCount'),
    (N'TicketInteractions', N'TicketInteractionId'), (N'TicketInteractions', N'TicketId'), (N'TicketInteractions', N'GenesysConversationId'), (N'TicketInteractions', N'CreatedAtUtc'),
    (N'TicketInteractions', N'InteractionStartedAtUtc'), (N'TicketInteractions', N'EndedAtUtc'), (N'TicketInteractions', N'EndReceivedAtUtc'), (N'TicketInteractions', N'EndReason'),
    (N'TicketInteractions', N'HandledByUserId'), (N'TicketInteractions', N'CustomerPhone'),
    (N'TicketInteractionMessages', N'TicketInteractionMessageId'), (N'TicketInteractionMessages', N'TicketInteractionId'), (N'TicketInteractionMessages', N'Sequence'), (N'TicketInteractionMessages', N'SentAtUtc'),
    (N'TicketInteractionHandlers', N'TicketInteractionHandlerId'), (N'TicketInteractionHandlers', N'TicketInteractionId'), (N'TicketInteractionHandlers', N'ConnectedAtUtc'), (N'TicketInteractionHandlers', N'RecordedAtUtc'),
    (N'TicketAssignments', N'TicketAssignmentId'), (N'TicketStatusHistory', N'TicketId'), (N'TicketResolutions', N'ResolvedAtUtc'), (N'TicketSlaInstances', N'FirstResponseDueAtUtc'),
    (N'TicketWorkflowEvents', N'TicketId'),
    (N'AuditEntries', N'AuditEntryId'), (N'AuditEntries', N'Action'), (N'AuditEntries', N'EntityType'), (N'AuditEntries', N'EntityId'), (N'AuditEntries', N'BeforeValue'),
    (N'AuditEntries', N'AfterValue'), (N'AuditEntries', N'CorrelationId'), (N'AuditEntries', N'OccurredAtUtc')
) v (T, C)
CROSS APPLY (SELECT N'dbo.' + v.T + N'.' + v.C AS Obj) r
WHERE COL_LENGTH(N'dbo.' + v.T, v.C) IS NULL;
IF LEN(@Missing) > 0
BEGIN
    SET @msg = N'ABORTED: required column(s) missing - apply the pending migrations first:' + @Missing + N'. Nothing was changed.';
    THROW 50302, @msg, 1;
END;

-- ---------------------------------------------------------------------------------------------
-- 1b. ROLLBACK MODE (only when @RollbackRunId is set): restore the ORIGINAL start recorded in that run's audit rows. DryRun applies here too.
-- ---------------------------------------------------------------------------------------------
IF @RollbackRunId IS NOT NULL
BEGIN
    DECLARE @Rb TABLE (CorrectedAuditEntryId bigint PRIMARY KEY, TicketId bigint NOT NULL, TicketInteractionId bigint NOT NULL, OriginalStartUtc datetime2 NOT NULL, CorrectedStartUtc datetime2 NOT NULL);
    DECLARE @RbAuditRows int = (SELECT COUNT(*) FROM dbo.AuditEntries WHERE CorrelationId = @RollbackRunId AND Action = N'GenesysTimestampCorrected');

    INSERT @Rb (CorrectedAuditEntryId, TicketId, TicketInteractionId, OriginalStartUtc, CorrectedStartUtc)
    SELECT a.AuditEntryId, TRY_CAST(a.EntityId AS bigint),
           TRY_CAST(SUBSTRING(a.BeforeValue, p.PId, CHARINDEX(N';', a.BeforeValue + N';', p.PId) - p.PId) AS bigint),
           TRY_CONVERT(datetime2, SUBSTRING(a.BeforeValue, p.POld, CHARINDEX(N';', a.BeforeValue + N';', p.POld) - p.POld), 126),
           TRY_CONVERT(datetime2, SUBSTRING(a.AfterValue,  p.PNew, CHARINDEX(N';', a.AfterValue  + N';', p.PNew) - p.PNew), 126)
    FROM dbo.AuditEntries a
    CROSS APPLY (SELECT CHARINDEX(N'TicketInteractionId=', a.BeforeValue)    + LEN(N'TicketInteractionId=')    AS PId,
                        CHARINDEX(N'InteractionStartedAtUtc=', a.BeforeValue) + LEN(N'InteractionStartedAtUtc=') AS POld,
                        CHARINDEX(N'InteractionStartedAtUtc=', a.AfterValue)  + LEN(N'InteractionStartedAtUtc=') AS PNew) p
    WHERE a.CorrelationId = @RollbackRunId AND a.Action = N'GenesysTimestampCorrected' AND a.EntityType = N'Ticket'
      AND CHARINDEX(N'TicketInteractionId=', a.BeforeValue) > 0 AND CHARINDEX(N'InteractionStartedAtUtc=', a.BeforeValue) > 0 AND CHARINDEX(N'InteractionStartedAtUtc=', a.AfterValue) > 0
      AND (@TicketId IS NULL OR TRY_CAST(a.EntityId AS bigint) = @TicketId);

    DECLARE @RbCount int = (SELECT COUNT(*) FROM @Rb);
    IF @RbAuditRows = 0
        THROW 50340, 'ABORTED: no GenesysTimestampCorrected audit rows exist for that @RollbackRunId. Nothing was changed.', 1;
    IF @TicketId IS NULL AND @RbCount <> @RbAuditRows
        THROW 50341, 'ABORTED: an audit row of that run could not be read back (unexpected format). Nothing was changed.', 1;

    SELECT 'ROLLBACK PLAN' AS ResultSet, r.TicketId, r.TicketInteractionId, i.InteractionStartedAtUtc AS StartNow, r.CorrectedStartUtc AS StartWrittenByThatRun, r.OriginalStartUtc AS StartWillBeRestoredTo,
           CASE WHEN i.InteractionStartedAtUtc = r.CorrectedStartUtc THEN 'WILL RESTORE' ELSE 'BLOCKED: current value is not the corrected value (changed since)' END AS Decision
    FROM @Rb r LEFT JOIN dbo.TicketInteractions i ON i.TicketInteractionId = r.TicketInteractionId ORDER BY r.TicketId;

    IF @DryRun = 1
    BEGIN
        PRINT N'ROLLBACK DRY RUN - nothing was written. Set @DryRun = 0 to restore the rows marked WILL RESTORE (any BLOCKED row aborts the whole rollback).';
        RETURN;
    END;

    BEGIN TRY
        BEGIN TRANSACTION;

        UPDATE i SET i.InteractionStartedAtUtc = r.OriginalStartUtc
        FROM dbo.TicketInteractions i JOIN @Rb r ON r.TicketInteractionId = i.TicketInteractionId
        WHERE i.InteractionStartedAtUtc = r.CorrectedStartUtc;                                   -- optimistic: only if still exactly the corrected value
        IF @@ROWCOUNT <> @RbCount
            THROW 50342, 'ABORTED: a conversation no longer holds the corrected start (changed since), or an audit row was not matched. Rolled back.', 1;

        INSERT dbo.AuditEntries (ActorEmployeeId, Action, EntityType, EntityId, BeforeValue, AfterValue, CorrelationId, OccurredAtUtc)
        SELECT NULL, N'GenesysTimestampCorrectionRolledBack', N'Ticket', CAST(r.TicketId AS nvarchar(100)),
               N'CorrectedAuditEntryId=' + CAST(r.CorrectedAuditEntryId AS nvarchar(20)) + N';TicketInteractionId=' + CAST(r.TicketInteractionId AS nvarchar(20))
                 + N';InteractionStartedAtUtc=' + CONVERT(nvarchar(33), r.CorrectedStartUtc, 126),
               N'TicketInteractionId=' + CAST(r.TicketInteractionId AS nvarchar(20)) + N';InteractionStartedAtUtc=' + CONVERT(nvarchar(33), r.OriginalStartUtc, 126)
                 + N';RollbackOfRun=' + CAST(@RollbackRunId AS nvarchar(40)),
               NEWID(), SYSUTCDATETIME()
        FROM @Rb r;

        COMMIT TRANSACTION;
        PRINT N'ROLLED BACK: ' + CAST(@RbCount AS nvarchar(10)) + N' conversation start(s) restored to the original value from run ' + CAST(@RollbackRunId AS nvarchar(40)) + N'.';
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
        PRINT N'ROLLBACK FAILED - transaction rolled back, nothing was changed.';
        THROW;
    END CATCH;
    RETURN;
END;

-- ---------------------------------------------------------------------------------------------
-- 2. The evidence (read-only so far): the block below is IDENTICAL to the read-only diagnostic and is re-evaluated on the CURRENT data on every run.
-- ---------------------------------------------------------------------------------------------
DECLARE @All TABLE (
    TicketInteractionId bigint PRIMARY KEY, TicketId bigint NOT NULL, GenesysConversationId nvarchar(64) NOT NULL,
    CreatedAtUtc datetime2 NOT NULL, StartStored datetime2 NOT NULL, EndStored datetime2 NULL, EndReceivedAtUtc datetime2 NULL,
    LineCount int NOT NULL, FirstLineUtc datetime2 NULL, LastLineUtc datetime2 NULL,
    StartAfterCreated bit NOT NULL, StartShapeCreated bit NOT NULL, StartAfterFirstLine bit NOT NULL, StartAnchorFirstLine bit NOT NULL,
    EndBeforeStart bit NOT NULL, EndAfterReceipt bit NOT NULL, EndShapeReceipt bit NOT NULL,
    LinesAfterReceipt int NOT NULL, LinesShapeReceipt int NOT NULL, LinesBeyondShape int NOT NULL, LinesOutside int NOT NULL, ConnectImpossible int NOT NULL,
    CorrectedStartAfterEnd bit NOT NULL,
    Classification varchar(10) NOT NULL, SubType varchar(40) NOT NULL, ReasonCode varchar(60) NOT NULL, ReasonText nvarchar(400) NOT NULL);

;WITH EndReceipt AS (
    -- when TigerCS RECEIVED the end event of a conversation (server clock), per ticket
    SELECT TRY_CAST(SUBSTRING(a.AfterValue, 10, CHARINDEX(';', a.AfterValue + ';') - 10) AS bigint) AS TicketId,
           a.EntityId AS ConversationId, MIN(a.OccurredAtUtc) AS EndReceivedAtUtc
    FROM dbo.AuditEntries a
    WHERE a.Action = 'GenesysConversationEnded' AND a.EntityType = 'GenesysConversation' AND a.AfterValue LIKE 'TicketId=%'
    GROUP BY TRY_CAST(SUBSTRING(a.AfterValue, 10, CHARINDEX(';', a.AfterValue + ';') - 10) AS bigint), a.EntityId),
Conv AS (
    SELECT i.TicketInteractionId, i.TicketId, i.GenesysConversationId, i.CreatedAtUtc, i.InteractionStartedAtUtc, i.EndedAtUtc, r.EndReceivedAtUtc
    FROM dbo.TicketInteractions i
    LEFT JOIN EndReceipt r ON r.TicketId = i.TicketId AND r.ConversationId = i.GenesysConversationId
    WHERE i.GenesysConversationId IS NOT NULL AND i.InteractionStartedAtUtc IS NOT NULL
      AND (@TicketId IS NULL OR i.TicketId = @TicketId)
      AND (@OnlyCreatedBeforeUtc IS NULL OR i.CreatedAtUtc < @OnlyCreatedBeforeUtc)),
Facts AS (
    SELECT c.*, l.LineCount, l.FirstLineUtc, l.LastLineUtc,
        CASE WHEN c.InteractionStartedAtUtc > DATEADD(MINUTE, @ToleranceMin, c.CreatedAtUtc) THEN 1 ELSE 0 END AS StartAfterCreated,
        CASE WHEN c.InteractionStartedAtUtc > DATEADD(MINUTE, @ToleranceMin, c.CreatedAtUtc)
              AND c.InteractionStartedAtUtc <= DATEADD(MINUTE, @ToleranceMin, DATEADD(HOUR, @ShiftHours, c.CreatedAtUtc)) THEN 1 ELSE 0 END AS StartShapeCreated,
        CASE WHEN l.LineCount > 0 AND c.InteractionStartedAtUtc > DATEADD(MINUTE, @ToleranceMin, l.FirstLineUtc) THEN 1 ELSE 0 END AS StartAfterFirstLine,
        -- the corrected start lands just before the conversation's own first message, and that first message is not in the future of the end receipt
        CASE WHEN l.LineCount > 0 AND (c.EndReceivedAtUtc IS NULL OR l.FirstLineUtc <= DATEADD(MINUTE, @ToleranceMin, c.EndReceivedAtUtc))
              AND DATEADD(HOUR, -@ShiftHours, c.InteractionStartedAtUtc) >= DATEADD(MINUTE, -@MaxStartToFirstLineMin, l.FirstLineUtc)
              AND DATEADD(HOUR, -@ShiftHours, c.InteractionStartedAtUtc) <= DATEADD(MINUTE, @ToleranceMin, l.FirstLineUtc) THEN 1 ELSE 0 END AS StartAnchorFirstLine,
        CASE WHEN c.EndedAtUtc IS NOT NULL AND c.EndedAtUtc < c.InteractionStartedAtUtc THEN 1 ELSE 0 END AS EndBeforeStart,
        CASE WHEN c.EndedAtUtc IS NOT NULL AND c.EndReceivedAtUtc IS NOT NULL AND c.EndedAtUtc > DATEADD(MINUTE, @ToleranceMin, c.EndReceivedAtUtc) THEN 1 ELSE 0 END AS EndAfterReceipt,
        CASE WHEN c.EndedAtUtc IS NOT NULL AND c.EndReceivedAtUtc IS NOT NULL
              AND c.EndedAtUtc > DATEADD(MINUTE, @ToleranceMin, c.EndReceivedAtUtc)
              AND c.EndedAtUtc <= DATEADD(MINUTE, @ToleranceMin, DATEADD(HOUR, @ShiftHours, c.EndReceivedAtUtc)) THEN 1 ELSE 0 END AS EndShapeReceipt,
        CASE WHEN c.EndReceivedAtUtc IS NULL THEN 0 ELSE l.LinesAfterReceipt END AS LinesAfterReceipt,
        CASE WHEN c.EndReceivedAtUtc IS NULL THEN 0 ELSE l.LinesShapeReceipt END AS LinesShapeReceipt,
        CASE WHEN c.EndReceivedAtUtc IS NULL THEN 0 ELSE l.LinesBeyondShape END AS LinesBeyondShape,
        (SELECT COUNT(*) FROM dbo.TicketInteractionMessages m
          WHERE m.TicketInteractionId = c.TicketInteractionId
            AND (m.SentAtUtc < DATEADD(MINUTE, -@ToleranceMin, c.InteractionStartedAtUtc)
              OR (c.EndedAtUtc IS NOT NULL AND m.SentAtUtc > DATEADD(MINUTE, @ToleranceMin, c.EndedAtUtc)))) AS LinesOutside,
        (SELECT COUNT(*) FROM dbo.TicketInteractionHandlers h
          WHERE h.TicketInteractionId = c.TicketInteractionId AND h.ConnectedAtUtc IS NOT NULL
            AND h.ConnectedAtUtc > DATEADD(MINUTE, @ToleranceMin, h.RecordedAtUtc)) AS ConnectImpossible,
        CASE WHEN c.EndedAtUtc IS NOT NULL AND DATEADD(HOUR, -@ShiftHours, c.InteractionStartedAtUtc) > DATEADD(MINUTE, @ToleranceMin, c.EndedAtUtc) THEN 1 ELSE 0 END AS CorrectedStartAfterEnd
    FROM Conv c
    OUTER APPLY (SELECT COUNT(*) AS LineCount, MIN(x.SentAtUtc) AS FirstLineUtc, MAX(x.SentAtUtc) AS LastLineUtc,
                        COALESCE(SUM(CASE WHEN x.SentAtUtc > DATEADD(MINUTE, @ToleranceMin, x.ReceiptUtc) THEN 1 ELSE 0 END), 0) AS LinesAfterReceipt,
                        COALESCE(SUM(CASE WHEN x.SentAtUtc > DATEADD(MINUTE, @ToleranceMin, x.ReceiptUtc)
                                           AND x.SentAtUtc <= DATEADD(MINUTE, @ToleranceMin, DATEADD(HOUR, @ShiftHours, x.ReceiptUtc)) THEN 1 ELSE 0 END), 0) AS LinesShapeReceipt,
                        COALESCE(SUM(CASE WHEN x.SentAtUtc > DATEADD(MINUTE, @ToleranceMin, DATEADD(HOUR, @ShiftHours, x.ReceiptUtc)) THEN 1 ELSE 0 END), 0) AS LinesBeyondShape
                 FROM (SELECT m.SentAtUtc, c.EndReceivedAtUtc AS ReceiptUtc FROM dbo.TicketInteractionMessages m WHERE m.TicketInteractionId = c.TicketInteractionId) x) l),
Kind AS (
    SELECT f.*,
        -- PROVEN_START_OFFSET: start impossible by the server clock, anchored by the first transcript line; end and lines are the correct values
        CASE WHEN f.StartShapeCreated = 1 AND f.StartAnchorFirstLine = 1 AND f.EndAfterReceipt = 0 AND f.LinesAfterReceipt = 0 AND f.CorrectedStartAfterEnd = 0 THEN 1 ELSE 0 END AS IsProvenStart,
        -- PROVEN_FULL_OFFSET: start impossible by the server clock AND end/line impossible by the end receipt; the result is internally consistent
        CASE WHEN f.StartShapeCreated = 1 AND (f.EndShapeReceipt = 1 OR f.LinesShapeReceipt > 0) AND f.LinesBeyondShape = 0 AND f.EndBeforeStart = 0 AND f.LinesOutside = 0 THEN 1 ELSE 0 END AS IsProvenFull
    FROM Facts f)
INSERT @All
SELECT k.TicketInteractionId, k.TicketId, k.GenesysConversationId, k.CreatedAtUtc, k.InteractionStartedAtUtc, k.EndedAtUtc, k.EndReceivedAtUtc,
       k.LineCount, k.FirstLineUtc, k.LastLineUtc,
       k.StartAfterCreated, k.StartShapeCreated, k.StartAfterFirstLine, k.StartAnchorFirstLine, k.EndBeforeStart, k.EndAfterReceipt, k.EndShapeReceipt,
       k.LinesAfterReceipt, k.LinesShapeReceipt, k.LinesBeyondShape, k.LinesOutside, k.ConnectImpossible, k.CorrectedStartAfterEnd,
       x.Classification, x.SubType, x.ReasonCode, x.ReasonText
FROM Kind k
CROSS APPLY (SELECT CASE
        WHEN k.IsProvenStart = 1 THEN 'PROVEN' WHEN k.IsProvenFull = 1 THEN 'PROVEN'
        WHEN k.StartAfterCreated = 1 OR k.StartAfterFirstLine = 1 OR k.EndBeforeStart = 1 OR k.EndAfterReceipt = 1 OR k.LinesAfterReceipt > 0
             OR k.LinesOutside > 0 OR k.ConnectImpossible > 0 THEN 'SUSPECT'
        ELSE 'CORRECT' END AS Classification) cl
CROSS APPLY (SELECT
    CASE WHEN k.IsProvenStart = 1 THEN 'PROVEN_START_OFFSET' WHEN k.IsProvenFull = 1 THEN 'PROVEN_FULL_OFFSET'
         WHEN cl.Classification = 'SUSPECT' THEN 'SUSPECT' ELSE 'CORRECT' END AS SubType,
    CASE
        WHEN k.IsProvenStart = 1 THEN 'START_LATER_THAN_TICKET_AND_FIRST_LINE'
        WHEN k.IsProvenFull = 1 THEN 'START_LATER_THAN_TICKET_AND_END_LATER_THAN_RECEIPT'
        WHEN cl.Classification = 'CORRECT' THEN 'NO_INCONSISTENCY_FOUND'
        WHEN k.StartAfterCreated = 1 AND k.StartShapeCreated = 0 THEN 'START_NOT_4H_SHAPED'
        WHEN k.StartShapeCreated = 1 AND k.LineCount = 0 AND k.EndReceivedAtUtc IS NULL THEN 'START_IMPOSSIBLE_NO_TRANSCRIPT_NO_END_RECEIPT'
        WHEN k.StartShapeCreated = 1 AND k.LineCount = 0 THEN 'START_IMPOSSIBLE_NO_TRANSCRIPT_END_PLAUSIBLE'
        WHEN k.StartShapeCreated = 1 AND k.StartAnchorFirstLine = 0 AND k.EndShapeReceipt = 0 AND k.LinesShapeReceipt = 0 THEN 'START_IMPOSSIBLE_FIRST_LINE_DOES_NOT_CORROBORATE'
        WHEN k.StartShapeCreated = 1 AND k.CorrectedStartAfterEnd = 1 THEN 'START_IMPOSSIBLE_CORRECTED_START_AFTER_END'
        WHEN k.StartShapeCreated = 1 AND k.StartAnchorFirstLine = 1 THEN 'START_PROVEN_BUT_END_OR_LINES_ALSO_IMPOSSIBLE'
        WHEN k.StartShapeCreated = 1 THEN 'START_IMPOSSIBLE_RESULT_NOT_CONSISTENT'
        WHEN k.StartAfterFirstLine = 1 THEN 'START_AFTER_FIRST_LINE_SINGLE_FACT'
        WHEN k.EndAfterReceipt = 1 OR k.LinesAfterReceipt > 0 THEN 'END_OR_LINE_IMPOSSIBLE_START_PLAUSIBLE'
        WHEN k.EndBeforeStart = 1 THEN 'END_BEFORE_START'
        WHEN k.LinesOutside > 0 THEN 'LINES_OUTSIDE_CONVERSATION'
        ELSE 'CONNECT_TIME_BEFORE_RECORDED' END AS ReasonCode) rc
CROSS APPLY (SELECT CASE rc.ReasonCode
        WHEN 'START_LATER_THAN_TICKET_AND_FIRST_LINE' THEN N'PROVEN: the start is later than the ticket it created (server clock) and, minus 4h, lands just before the first transcript line; end and lines are plausible. Only the start is corrected.'
        WHEN 'START_LATER_THAN_TICKET_AND_END_LATER_THAN_RECEIPT' THEN N'PROVEN: the start is later than the ticket it created and the end/transcript is later than the recorded receipt of the end event, each by at most 4h; the result is consistent. Start, end and lines are corrected.'
        WHEN 'NO_INCONSISTENCY_FOUND' THEN N'No impossible or inconsistent timestamp found.'
        WHEN 'START_NOT_4H_SHAPED' THEN N'EXCLUDED: the start is later than the ticket by MORE than 4h - a 4h shift cannot explain it; cause unknown.'
        WHEN 'START_IMPOSSIBLE_NO_TRANSCRIPT_NO_END_RECEIPT' THEN N'EXCLUDED: the start is impossible (later than its ticket) but there is no transcript and no end receipt - no independent evidence to prove the correct value.'
        WHEN 'START_IMPOSSIBLE_NO_TRANSCRIPT_END_PLAUSIBLE' THEN N'EXCLUDED: the start is impossible but there is no transcript to anchor the corrected start and the end is plausible - only one fact.'
        WHEN 'START_IMPOSSIBLE_FIRST_LINE_DOES_NOT_CORROBORATE' THEN N'EXCLUDED: the start is impossible but minus 4h it does not land just before the first transcript line (or the first line is itself in the future) - a 4h shift is not proven.'
        WHEN 'START_IMPOSSIBLE_CORRECTED_START_AFTER_END' THEN N'EXCLUDED: even minus 4h the start would be after the conversation end - contradictory evidence.'
        WHEN 'START_PROVEN_BUT_END_OR_LINES_ALSO_IMPOSSIBLE' THEN N'EXCLUDED: the start is anchored by the first line but the end/lines are also impossible in a way the full-shift rule does not confirm - mixed evidence.'
        WHEN 'START_IMPOSSIBLE_RESULT_NOT_CONSISTENT' THEN N'EXCLUDED: the start is impossible and the end/lines are impossible, but the corrected conversation would not be internally consistent.'
        WHEN 'START_AFTER_FIRST_LINE_SINGLE_FACT' THEN N'EXCLUDED: the start is after its own first transcript line but not later than its ticket - one fact only.'
        WHEN 'END_OR_LINE_IMPOSSIBLE_START_PLAUSIBLE' THEN N'EXCLUDED: the end/a transcript line is later than the recorded receipt of the end event but the start is plausible - mixed conversation, one fact only.'
        WHEN 'END_BEFORE_START' THEN N'EXCLUDED: the end precedes the start; no offset-shaped evidence.'
        WHEN 'LINES_OUTSIDE_CONVERSATION' THEN N'EXCLUDED: transcript lines fall outside the conversation window; no offset-shaped evidence.'
        ELSE N'EXCLUDED: a connect time is later than the moment TigerCS recorded it; not part of a proven conversation.' END AS ReasonText) rt
CROSS APPLY (SELECT cl.Classification AS Classification, rc.SubType AS SubType, rc.ReasonCode AS ReasonCode, rt.ReasonText AS ReasonText) x;

-- the ONE guard that is not part of the shared evidence block (idempotency, independent of the data): a conversation that already has a GenesysTimestampCorrected audit row is never planned again
DECLARE @Already TABLE (TicketInteractionId bigint PRIMARY KEY);
INSERT @Already
SELECT a.TicketInteractionId FROM @All a
WHERE EXISTS (SELECT 1 FROM dbo.AuditEntries c
              WHERE c.Action = N'GenesysTimestampCorrected' AND c.EntityType = N'Ticket' AND c.EntityId = CAST(a.TicketId AS nvarchar(100))
                AND c.BeforeValue LIKE N'TicketInteractionId=' + CAST(a.TicketInteractionId AS nvarchar(20)) + N';%');

DECLARE @nTotal int = (SELECT COUNT(*) FROM @All),
        @nProvenStart int = (SELECT COUNT(*) FROM @All WHERE SubType = 'PROVEN_START_OFFSET'),
        @nProvenFull int = (SELECT COUNT(*) FROM @All WHERE SubType = 'PROVEN_FULL_OFFSET'),
        @Suspects int = (SELECT COUNT(*) FROM @All WHERE Classification = 'SUSPECT'),
        @nCorrect int = (SELECT COUNT(*) FROM @All WHERE Classification = 'CORRECT'),
        @nAlready int = (SELECT COUNT(*) FROM @All a WHERE a.SubType = 'PROVEN_START_OFFSET' AND a.TicketInteractionId IN (SELECT TicketInteractionId FROM @Already)),
        @nPriorAudit int = (SELECT COUNT(*) FROM dbo.AuditEntries WHERE Action = N'GenesysTimestampCorrected');

-- REVALIDATION: the live numbers, on the current Production data, next to the numbers of the approved diagnostic. A difference is not an error by itself (new conversations keep arriving), but read it.
SELECT 'REVALIDATION vs approved diagnostic' AS ResultSet, v.Metric, v.Approved, v.Live,
       CASE WHEN @TicketId IS NOT NULL THEN 'n/a (single-ticket run)' WHEN v.Approved = v.Live THEN 'MATCH' ELSE 'DIFFERS - review before @DryRun = 0' END AS Status
FROM (VALUES
    (1, N'Total Genesys conversations', @ApprovedTotal, @nTotal),
    (2, N'PROVEN_START_OFFSET (the only class this script corrects)', @ApprovedProvenStart, @nProvenStart),
    (3, N'SUSPECT (never touched)', @ApprovedSuspect, @Suspects),
    (4, N'CORRECT (never touched)', @ApprovedCorrect, @nCorrect),
    (5, N'PROVEN_FULL_OFFSET (reported; never touched by this script)', 0, @nProvenFull),
    (6, N'Previous GenesysTimestampCorrected audit records', @ApprovedPriorCorrections, @nPriorAudit)
) v (Ord, Metric, Approved, Live) ORDER BY v.Ord;

-- the PLAN: PROVEN_START_OFFSET only, not already corrected, oldest first, capped per run. Only the start is ever proposed; there is no end / transcript / connect-time column in the plan.
DECLARE @Plan TABLE (
    TicketInteractionId bigint PRIMARY KEY, TicketId bigint NOT NULL, GenesysConversationId nvarchar(64) NOT NULL, CreatedAtUtc datetime2 NOT NULL, SubType varchar(40) NOT NULL,
    StartStored datetime2 NOT NULL, StartProposed datetime2 NOT NULL, FirstLineUtc datetime2 NULL, ReasonText nvarchar(400) NOT NULL);
INSERT @Plan
SELECT TOP (@MaxConversations) a.TicketInteractionId, a.TicketId, a.GenesysConversationId, a.CreatedAtUtc, a.SubType,
       a.StartStored, DATEADD(HOUR, -@ShiftHours, a.StartStored), a.FirstLineUtc, a.ReasonText
FROM @All a
WHERE a.SubType = 'PROVEN_START_OFFSET' AND a.TicketInteractionId NOT IN (SELECT TicketInteractionId FROM @Already)
ORDER BY a.CreatedAtUtc, a.TicketInteractionId;

DECLARE @nConv int = (SELECT COUNT(*) FROM @Plan);
SET @msg = N'Plan: ' + CAST(@nConv AS nvarchar(10)) + N' PROVEN_START_OFFSET conversation(s) in this run (start only; ' + CAST(@nAlready AS nvarchar(10)) + N' already corrected and skipped); '
         + CAST(@nProvenFull AS nvarchar(10)) + N' PROVEN_FULL_OFFSET and ' + CAST(@Suspects AS nvarchar(10)) + N' SUSPECT conversation(s) will NOT be touched.';
PRINT @msg;
IF @TicketId IS NULL AND @nProvenStart > @ApprovedProvenStart
    PRINT N'WARNING: ' + CAST(@nProvenStart AS nvarchar(10)) + N' conversations are PROVEN_START_OFFSET now but ' + CAST(@ApprovedProvenStart AS nvarchar(10))
        + N' were approved; a real run over all tickets will ABORT (50330) until the difference is reviewed and @ApprovedProvenStart is updated.';

-- ---------------------------------------------------------------------------------------------
-- 3. Show old -> proposed BEFORE anything can change (result sets are the before-image: save them)
-- ---------------------------------------------------------------------------------------------
SELECT 'PLAN: conversations' AS ResultSet, p.TicketId, t.TicketNumber, t.TicketStatus, p.GenesysConversationId, p.CreatedAtUtc AS TicketCreatedAtUtc_ServerClock,
       p.StartStored, p.StartProposed, p.FirstLineUtc AS FirstTranscriptLineUtc,
       DATEDIFF(MINUTE, p.CreatedAtUtc, p.StartStored) AS StartMinusTicketCreated_Min, DATEDIFF(SECOND, p.FirstLineUtc, p.StartProposed) AS ProposedStartMinusFirstLine_Sec, p.SubType, p.ReasonText AS Reason
FROM @Plan p JOIN dbo.Tickets t ON t.TicketId = p.TicketId ORDER BY p.CreatedAtUtc, p.TicketInteractionId;
-- the same plan as one row per timestamp VALUE: Ticket ID, Conversation ID, affected column, old UTC, proposed UTC, reason (always exactly one column: InteractionStartedAtUtc)
SELECT 'PLAN: column preview' AS ResultSet, p.TicketId, p.GenesysConversationId AS ConversationId, N'TicketInteractions.InteractionStartedAtUtc' AS AffectedColumn, p.TicketInteractionId AS RowId,
       p.StartStored AS OldUtc, p.StartProposed AS ProposedUtc, p.ReasonText AS Reason
FROM @Plan p ORDER BY p.TicketId, p.TicketInteractionId;

-- ROLLBACK STATEMENTS: restore the exact old start (only where the row still holds the corrected value). Generated on every run; the same values are in the audit rows a real run writes.
SELECT 'ROLLBACK STATEMENTS' AS ResultSet,
       N'UPDATE dbo.TicketInteractions SET InteractionStartedAtUtc = ''' + CONVERT(nvarchar(33), p.StartStored, 126) + N''' WHERE TicketInteractionId = ' + CAST(p.TicketInteractionId AS nvarchar(20))
     + N' AND InteractionStartedAtUtc = ''' + CONVERT(nvarchar(33), p.StartProposed, 126) + N''';' AS Stmt
FROM @Plan p ORDER BY p.TicketInteractionId;

IF @DryRun = 1
BEGIN
    PRINT N'DRY RUN - nothing was written (no transaction was opened). Review the result sets, then set @DryRun = 0 to apply exactly this plan.';
    RETURN;
END;

IF @nConv = 0
BEGIN
    PRINT N'Nothing to correct.';
    RETURN;
END;

IF @TicketId IS NULL AND @nProvenStart > @ApprovedProvenStart
BEGIN
    SET @msg = N'ABORTED: ' + CAST(@nProvenStart AS nvarchar(10)) + N' conversations are PROVEN_START_OFFSET on the current data but only ' + CAST(@ApprovedProvenStart AS nvarchar(10))
             + N' were approved from the diagnostic. Review the difference, then raise @ApprovedProvenStart. Nothing was changed.';
    THROW 50330, @msg, 1;
END;

-- ---------------------------------------------------------------------------------------------
-- 4. Apply (ONE transaction): optimistic update of ONE column, protected-state proof, append-only audit
-- ---------------------------------------------------------------------------------------------
DECLARE @Protected TABLE (K nvarchar(40) PRIMARY KEY, V bigint NOT NULL);
DECLARE @After TABLE (K nvarchar(40) PRIMARY KEY, V bigint NOT NULL);
DECLARE @Run uniqueidentifier = NEWID();
DECLARE @rc int;

BEGIN TRY
    BEGIN TRANSACTION;

    -- the state that must NOT move: ticket creation / First Human Response / status / owners / departments / closure, every interaction column except the planned starts (end, receipt, reason...),
    -- every OTHER interaction's start, transcript lines, connect times, assignments, status history, resolutions, SLA rows, workflow events, and every EXISTING audit row
    INSERT @Protected SELECT N'Tickets',            COALESCE(CHECKSUM_AGG(CHECKSUM(TicketId, TicketStatus, CurrentOwnerEmployeeId, AiAgentOwnerEmployeeId, CurrentDepartmentId, SlaState, ResolutionOutcome, ReopenCount, CreatedAtUtc, FirstHumanResponseAtUtc, AcknowledgementSentAtUtc)), 0) FROM dbo.Tickets;
    INSERT @Protected SELECT N'TicketInteractionsOther', COALESCE(CHECKSUM_AGG(CHECKSUM(TicketInteractionId, TicketId, CreatedAtUtc, GenesysConversationId, EndedAtUtc, EndReceivedAtUtc, EndReason, HandledByUserId, CustomerPhone)), 0) + COUNT_BIG(*) FROM dbo.TicketInteractions;
    INSERT @Protected SELECT N'StartsOutsidePlan',  COALESCE(CHECKSUM_AGG(CHECKSUM(TicketInteractionId, InteractionStartedAtUtc)), 0) + COUNT_BIG(*) FROM dbo.TicketInteractions WHERE TicketInteractionId NOT IN (SELECT TicketInteractionId FROM @Plan);
    INSERT @Protected SELECT N'TicketInteractionMessages', COALESCE(CHECKSUM_AGG(CHECKSUM(TicketInteractionMessageId, TicketInteractionId, Sequence, SentAtUtc)), 0) + COUNT_BIG(*) FROM dbo.TicketInteractionMessages;
    INSERT @Protected SELECT N'TicketInteractionHandlers', COALESCE(CHECKSUM_AGG(CHECKSUM(TicketInteractionHandlerId, TicketInteractionId, ConnectedAtUtc, RecordedAtUtc)), 0) + COUNT_BIG(*) FROM dbo.TicketInteractionHandlers;
    INSERT @Protected SELECT N'TicketAssignments',  COALESCE(CHECKSUM_AGG(CHECKSUM(TicketAssignmentId, AssignedEmployeeId, AssignedDepartmentId, AssignedAtUtc, IsCurrent)), 0) FROM dbo.TicketAssignments;
    INSERT @Protected SELECT N'TicketStatusHistory',COALESCE(CHECKSUM_AGG(CHECKSUM(TicketId, Dimension, OldValue, NewValue, OccurredAtUtc)), 0) + COUNT_BIG(*) FROM dbo.TicketStatusHistory;
    INSERT @Protected SELECT N'TicketResolutions',  COALESCE(CHECKSUM_AGG(CHECKSUM(TicketResolutionId, ResolutionOutcome, ResolvedAtUtc)), 0) FROM dbo.TicketResolutions;
    INSERT @Protected SELECT N'TicketSlaInstances', COALESCE(CHECKSUM_AGG(CHECKSUM(TicketId, PeriodStartAtUtc, FirstResponseDueAtUtc, FirstResponseBreached)), 0) + COUNT_BIG(*) FROM dbo.TicketSlaInstances;
    INSERT @Protected SELECT N'TicketWorkflowEvents', COUNT_BIG(*) FROM dbo.TicketWorkflowEvents;
    INSERT @Protected SELECT N'AuditEntriesBefore', COALESCE(CHECKSUM_AGG(CHECKSUM(AuditEntryId, Action, EntityId, OccurredAtUtc)), 0) + COUNT_BIG(*) FROM dbo.AuditEntries;
    DECLARE @MaxAuditId bigint = (SELECT COALESCE(MAX(AuditEntryId), 0) FROM dbo.AuditEntries);

    -- the ONLY write to the interaction table: one column, and only where the row still holds exactly the value the proof was computed on
    UPDATE i SET i.InteractionStartedAtUtc = p.StartProposed
    FROM dbo.TicketInteractions i JOIN @Plan p ON p.TicketInteractionId = i.TicketInteractionId
    WHERE i.InteractionStartedAtUtc = p.StartStored;
    SET @rc = @@ROWCOUNT;
    IF @rc <> @nConv THROW 50310, 'ABORTED: a planned conversation changed since the plan was read (or was updated twice). Rolled back.', 1;

    -- append-only trace and durable before-image: one NEW audit row per corrected conversation with the ORIGINAL and the new start (no existing audit row is touched)
    INSERT dbo.AuditEntries (ActorEmployeeId, Action, EntityType, EntityId, BeforeValue, AfterValue, CorrelationId, OccurredAtUtc)
    SELECT NULL, N'GenesysTimestampCorrected', N'Ticket', CAST(p.TicketId AS nvarchar(100)),
           N'TicketInteractionId=' + CAST(p.TicketInteractionId AS nvarchar(20)) + N';ConversationId=' + p.GenesysConversationId
             + N';InteractionStartedAtUtc=' + CONVERT(nvarchar(33), p.StartStored, 126),
           N'TicketInteractionId=' + CAST(p.TicketInteractionId AS nvarchar(20)) + N';ConversationId=' + p.GenesysConversationId
             + N';InteractionStartedAtUtc=' + CONVERT(nvarchar(33), p.StartProposed, 126)
             + N';Column=TicketInteractions.InteractionStartedAtUtc;ShiftHours=-' + CAST(@ShiftHours AS nvarchar(3)) + N';Proof=' + p.SubType
             + N';Reason=Genesys UTC offset read in the host zone before PR #292 (+4h); ' + p.ReasonText,
           @Run, SYSUTCDATETIME()
    FROM @Plan p;

    -- validation: nothing protected moved; the only change to the audit trail is the appended rows
    INSERT @After SELECT N'Tickets',            COALESCE(CHECKSUM_AGG(CHECKSUM(TicketId, TicketStatus, CurrentOwnerEmployeeId, AiAgentOwnerEmployeeId, CurrentDepartmentId, SlaState, ResolutionOutcome, ReopenCount, CreatedAtUtc, FirstHumanResponseAtUtc, AcknowledgementSentAtUtc)), 0) FROM dbo.Tickets;
    INSERT @After SELECT N'TicketInteractionsOther', COALESCE(CHECKSUM_AGG(CHECKSUM(TicketInteractionId, TicketId, CreatedAtUtc, GenesysConversationId, EndedAtUtc, EndReceivedAtUtc, EndReason, HandledByUserId, CustomerPhone)), 0) + COUNT_BIG(*) FROM dbo.TicketInteractions;
    INSERT @After SELECT N'StartsOutsidePlan',  COALESCE(CHECKSUM_AGG(CHECKSUM(TicketInteractionId, InteractionStartedAtUtc)), 0) + COUNT_BIG(*) FROM dbo.TicketInteractions WHERE TicketInteractionId NOT IN (SELECT TicketInteractionId FROM @Plan);
    INSERT @After SELECT N'TicketInteractionMessages', COALESCE(CHECKSUM_AGG(CHECKSUM(TicketInteractionMessageId, TicketInteractionId, Sequence, SentAtUtc)), 0) + COUNT_BIG(*) FROM dbo.TicketInteractionMessages;
    INSERT @After SELECT N'TicketInteractionHandlers', COALESCE(CHECKSUM_AGG(CHECKSUM(TicketInteractionHandlerId, TicketInteractionId, ConnectedAtUtc, RecordedAtUtc)), 0) + COUNT_BIG(*) FROM dbo.TicketInteractionHandlers;
    INSERT @After SELECT N'TicketAssignments',  COALESCE(CHECKSUM_AGG(CHECKSUM(TicketAssignmentId, AssignedEmployeeId, AssignedDepartmentId, AssignedAtUtc, IsCurrent)), 0) FROM dbo.TicketAssignments;
    INSERT @After SELECT N'TicketStatusHistory',COALESCE(CHECKSUM_AGG(CHECKSUM(TicketId, Dimension, OldValue, NewValue, OccurredAtUtc)), 0) + COUNT_BIG(*) FROM dbo.TicketStatusHistory;
    INSERT @After SELECT N'TicketResolutions',  COALESCE(CHECKSUM_AGG(CHECKSUM(TicketResolutionId, ResolutionOutcome, ResolvedAtUtc)), 0) FROM dbo.TicketResolutions;
    INSERT @After SELECT N'TicketSlaInstances', COALESCE(CHECKSUM_AGG(CHECKSUM(TicketId, PeriodStartAtUtc, FirstResponseDueAtUtc, FirstResponseBreached)), 0) + COUNT_BIG(*) FROM dbo.TicketSlaInstances;
    INSERT @After SELECT N'TicketWorkflowEvents', COUNT_BIG(*) FROM dbo.TicketWorkflowEvents;
    INSERT @After SELECT N'AuditEntriesBefore', COALESCE(CHECKSUM_AGG(CHECKSUM(AuditEntryId, Action, EntityId, OccurredAtUtc)), 0) + COUNT_BIG(*) FROM dbo.AuditEntries WHERE AuditEntryId <= @MaxAuditId;

    IF EXISTS (SELECT 1 FROM @Protected b JOIN @After a ON a.K = b.K WHERE a.V <> b.V)
        THROW 50320, 'VALIDATION FAILED: a protected value (ticket dates / status / ownership / closure, interaction end, other starts, transcript lines, connect times, assignments, status history, SLA, workflow events, existing audit rows) changed. Rolled back.', 1;
    IF (SELECT COUNT(*) FROM dbo.AuditEntries WHERE AuditEntryId > @MaxAuditId) <> @nConv
        THROW 50321, 'VALIDATION FAILED: the number of appended audit rows is not the number of corrected conversations. Rolled back.', 1;
    IF EXISTS (SELECT 1 FROM dbo.TicketInteractions i JOIN @Plan p ON p.TicketInteractionId = i.TicketInteractionId
               WHERE i.InteractionStartedAtUtc > DATEADD(MINUTE, @ToleranceMin, p.CreatedAtUtc))
        THROW 50322, 'VALIDATION FAILED: a corrected start is still later than its ticket creation. Rolled back.', 1;
    IF EXISTS (SELECT 1 FROM dbo.TicketInteractions i JOIN @Plan p ON p.TicketInteractionId = i.TicketInteractionId WHERE i.InteractionStartedAtUtc <> p.StartProposed)
        THROW 50323, 'VALIDATION FAILED: a corrected start is not the proposed value. Rolled back.', 1;

    COMMIT TRANSACTION;
    PRINT N'COMMITTED: ' + CAST(@nConv AS nvarchar(10)) + N' conversation start(s) corrected (InteractionStartedAtUtc only); run id ' + CAST(@Run AS nvarchar(40))
        + N' (keep it: it is the @RollbackRunId for this run). Re-running finds nothing left to correct for these conversations.';
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    PRINT N'FAILED - transaction rolled back, nothing was changed.';
    THROW;
END CATCH;

-- ---------------------------------------------------------------------------------------------
-- 5. Post-commit verification (read-only)
-- ---------------------------------------------------------------------------------------------
SELECT 'POST: corrected conversations' AS ResultSet, p.TicketId, p.GenesysConversationId, i.InteractionStartedAtUtc AS StartNow, p.StartStored AS StartWas, i.EndedAtUtc AS EndUnchanged
FROM @Plan p JOIN dbo.TicketInteractions i ON i.TicketInteractionId = p.TicketInteractionId ORDER BY p.TicketId;
SELECT 'POST: audit rows written' AS ResultSet, @Run AS RunId, AuditEntryId, EntityId AS TicketId, BeforeValue, AfterValue FROM dbo.AuditEntries WHERE CorrelationId = @Run ORDER BY AuditEntryId;
