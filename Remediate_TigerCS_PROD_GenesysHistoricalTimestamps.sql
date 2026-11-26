-- ############################################################################################
-- ## TigerCS PRODUCTION ONLY - HISTORICAL DATA CORRECTION.
-- ## Server   : TGAESHJDBP02
-- ## Database : TigerCS_Ticketing_Prod   (any other server or database aborts before any read or change)
-- ## NOT EXECUTED by Claude. Needs explicit approval. Run only after Diagnose_TigerCS_PROD_GenesysHistoricalTimestamps_ReadOnly.sql
-- ## has been reviewed, the application build containing PR #292 is deployed, and a fresh full backup exists.
-- ############################################################################################
-- WHAT IT FIXES
--   Before PR #292 an offset-bearing UTC timestamp from Genesys ("...+00:00") was read into the application host's zone and stored as UAE wall-clock time in
--   a "...Utc" column: exactly +4 hours, on the conversation start, end, every transcript line and the connect (acceptance) time.
--
-- WHAT IT DOES (and refuses to do)
--   * It does NOT subtract four hours from history, and a +4h difference ALONE is never proof. Only conversations the read-only diagnostic classifies PROVEN are planned:
--       PROVEN_START_OFFSET  start later than the ticket's own CreatedAtUtc (server clock) AND, minus 4h, it lands just before the first transcript line; end and lines are
--                            plausible and are NOT changed. Only InteractionStartedAtUtc moves.
--       PROVEN_FULL_OFFSET   start later than the ticket AND end/transcript later than the audit-recorded receipt of the end event, each by at most 4h + tolerance, and the
--                            result is internally consistent. Start, end, every transcript line move (and a connect time only with its own evidence).
--     Everything else (SUSPECT: missing, partial, mixed or contradictory evidence; CORRECT) is counted and left untouched. If nothing is PROVEN the plan is empty.
--   * It changes ONLY: TicketInteractions.InteractionStartedAtUtc / EndedAtUtc, TicketInteractionMessages.SentAtUtc, TicketInteractionHandlers.ConnectedAtUtc.
--     Ticket status, ownership, department, closure, resolution, assignments, status history, SLA rows, customer data and every existing audit row are NOT touched; this
--     is verified inside the transaction (checksums + counts) and the run aborts and rolls back if anything protected moved.
--   * It writes ONE new audit row per corrected ticket (action GenesysTimestampCorrected: old and new values, proof type, evidence) - append-only, no existing audit row is altered.
--   * SLA is NOT recalculated. Tickets whose First Human Response came from a shifted line are listed SEPARATELY (result set "SLA REVIEW") with the flag as it is
--     and as it would be; correcting SLA records is a separate, separately approved decision.
--
-- HOW TO RUN (SSMS, connected to TGAESHJDBP02, database TigerCS_Ticketing_Prod)
--   1. Leave @DryRun = 1 and run. NOTHING is written (not even a transaction). Read every result set: old -> proposed values, SLA review, suspects, the generated
--      rollback statements. Save the result sets (they are your before-image).
--   2. Pilot: set @TicketId = 1878 (then @DryRun = 0) and run; confirm the page; then clear @TicketId and run for the rest (@MaxConversations caps each run).
--   3. @DryRun = 0 applies the SAME plan in ONE transaction, validates, then commits. Re-running finds nothing left to correct (idempotent).
--   Run as ONE batch. There is intentionally no GO in this script.
--
-- ROLLBACK: the result set "ROLLBACK STATEMENTS" (generated on every run, dry or real) restores the exact old values. Run it, in a transaction, only if the correction
--   must be undone. The audit rows written by a real run record the same old/new values.
-- ############################################################################################

SET NOCOUNT ON;
SET XACT_ABORT ON;
SET LOCK_TIMEOUT 15000;

DECLARE @DryRun bit = 1;                       -- 1 = show the plan, write NOTHING (default).  0 = apply, validate, COMMIT.
DECLARE @TicketId bigint = NULL;               -- NULL = every CONFIRMED conversation; 1878 = pilot on that ticket only
DECLARE @MaxConversations int = 200;           -- cap per run (oldest first), so a large backlog is corrected in reviewable batches
DECLARE @ToleranceMin int = 5;
DECLARE @ShiftHours int = 4;                   -- Asia/Dubai: UTC+4, no daylight saving
DECLARE @MaxStartToFirstLineMin int = 15;      -- the real gap between a conversation's start and its first message can be at most this long (same as the diagnostic)
DECLARE @OnlyCreatedBeforeUtc datetime2 = NULL;-- set to the moment the build with PR #292 went live: later tickets cannot be shifted and are never considered

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
      + N', TicketId=' + ISNULL(CAST(@TicketId AS nvarchar(20)), N'(all)') + N'.';

IF OBJECT_ID(N'dbo.TicketInteractions', N'U') IS NULL OR OBJECT_ID(N'dbo.TicketInteractionMessages', N'U') IS NULL
   OR OBJECT_ID(N'dbo.TicketInteractionHandlers', N'U') IS NULL OR OBJECT_ID(N'dbo.AuditEntries', N'U') IS NULL
    THROW 50301, 'ABORTED: expected Ticketing tables are missing - wrong database?', 1;

-- ---------------------------------------------------------------------------------------------
-- 2. The plan (read-only so far): the evidence block below is IDENTICAL to the read-only diagnostic. Only PROVEN conversations are ever planned.
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


-- the PLAN: PROVEN conversations only, oldest first, capped per run. A start-only conversation keeps its stored end (EndProposed = EndStored); a full-shift conversation moves it.
DECLARE @Plan TABLE (
    TicketInteractionId bigint PRIMARY KEY, TicketId bigint NOT NULL, GenesysConversationId nvarchar(64) NOT NULL, CreatedAtUtc datetime2 NOT NULL, SubType varchar(40) NOT NULL,
    StartStored datetime2 NOT NULL, StartProposed datetime2 NOT NULL, EndStored datetime2 NULL, EndProposed datetime2 NULL, EndReceivedAtUtc datetime2 NULL, FirstLineUtc datetime2 NULL, ReasonText nvarchar(400) NOT NULL);
DECLARE @Suspects int = (SELECT COUNT(*) FROM @All WHERE Classification = 'SUSPECT');
INSERT @Plan
SELECT TOP (@MaxConversations) a.TicketInteractionId, a.TicketId, a.GenesysConversationId, a.CreatedAtUtc, a.SubType,
       a.StartStored, DATEADD(HOUR, -@ShiftHours, a.StartStored), a.EndStored,
       CASE WHEN a.SubType = 'PROVEN_FULL_OFFSET' AND a.EndStored IS NOT NULL THEN DATEADD(HOUR, -@ShiftHours, a.EndStored) ELSE a.EndStored END,
       a.EndReceivedAtUtc, a.FirstLineUtc, a.ReasonText
FROM @All a WHERE a.Classification = 'PROVEN'
ORDER BY a.CreatedAtUtc, a.TicketInteractionId;

-- transcript lines move ONLY in a PROVEN_FULL_OFFSET conversation; in a start-only conversation the lines are the correct values that prove the start wrong
DECLARE @PlanMsg TABLE (TicketInteractionMessageId bigint PRIMARY KEY, TicketInteractionId bigint NOT NULL, TicketId bigint NOT NULL, Sequence int NOT NULL, SentStored datetime2 NOT NULL, SentProposed datetime2 NOT NULL);
INSERT @PlanMsg
SELECT m.TicketInteractionMessageId, m.TicketInteractionId, p.TicketId, m.Sequence, m.SentAtUtc, DATEADD(HOUR, -@ShiftHours, m.SentAtUtc)
FROM @Plan p JOIN dbo.TicketInteractionMessages m ON m.TicketInteractionId = p.TicketInteractionId WHERE p.SubType = 'PROVEN_FULL_OFFSET';

-- a connect time is corrected only with ITS OWN independent evidence: TigerCS recorded the row (RecordedAtUtc, server clock) before the stored connect time (impossible), by at most 4h + tolerance,
-- and the corrected value does not precede the corrected start
DECLARE @PlanHandler TABLE (TicketInteractionHandlerId bigint PRIMARY KEY, TicketInteractionId bigint NOT NULL, TicketId bigint NOT NULL, ConnectedStored datetime2 NOT NULL, ConnectedProposed datetime2 NOT NULL);
INSERT @PlanHandler
SELECT h.TicketInteractionHandlerId, h.TicketInteractionId, p.TicketId, h.ConnectedAtUtc, DATEADD(HOUR, -@ShiftHours, h.ConnectedAtUtc)
FROM @Plan p JOIN dbo.TicketInteractionHandlers h ON h.TicketInteractionId = p.TicketInteractionId
WHERE h.ConnectedAtUtc IS NOT NULL
  AND h.ConnectedAtUtc > DATEADD(MINUTE, @ToleranceMin, h.RecordedAtUtc)
  AND h.ConnectedAtUtc <= DATEADD(MINUTE, @ToleranceMin, DATEADD(HOUR, @ShiftHours, h.RecordedAtUtc))
  AND DATEADD(HOUR, -@ShiftHours, h.ConnectedAtUtc) >= DATEADD(MINUTE, -@ToleranceMin, p.StartProposed);

DECLARE @nConv int = (SELECT COUNT(*) FROM @Plan), @nMsg int = (SELECT COUNT(*) FROM @PlanMsg), @nHandler int = (SELECT COUNT(*) FROM @PlanHandler);
SET @msg = N'Plan: ' + CAST(@nConv AS nvarchar(10)) + N' conversation(s) PROVEN (' + CAST((SELECT COUNT(*) FROM @Plan WHERE SubType = 'PROVEN_START_OFFSET') AS nvarchar(10)) + N' start-only, ' + CAST((SELECT COUNT(*) FROM @Plan WHERE SubType = 'PROVEN_FULL_OFFSET') AS nvarchar(10)) + N' full), ' + CAST(@nMsg AS nvarchar(10)) + N' transcript line(s), '
         + CAST(@nHandler AS nvarchar(10)) + N' connect time(s); ' + CAST(@Suspects AS nvarchar(10)) + N' SUSPECT conversation(s) will NOT be touched.';
PRINT @msg;

-- ---------------------------------------------------------------------------------------------
-- 3. Show old -> proposed BEFORE anything can change (result sets are the before-image: save them)
-- ---------------------------------------------------------------------------------------------
SELECT 'PLAN: conversations' AS ResultSet, p.TicketId, t.TicketNumber, t.TicketStatus, p.GenesysConversationId, p.CreatedAtUtc AS TicketCreatedAtUtc_ServerClock,
       p.StartStored, p.StartProposed, p.EndStored, p.EndProposed, p.EndReceivedAtUtc AS EndReceived_ServerClock,
       (SELECT COUNT(*) FROM @PlanMsg m WHERE m.TicketInteractionId = p.TicketInteractionId) AS Lines,
       (SELECT COUNT(*) FROM @PlanHandler h WHERE h.TicketInteractionId = p.TicketInteractionId) AS ConnectTimes, p.SubType, p.ReasonText AS Reason
FROM @Plan p JOIN dbo.Tickets t ON t.TicketId = p.TicketId ORDER BY p.CreatedAtUtc;
-- the same plan as one row per timestamp VALUE: Ticket ID, Conversation ID, affected column, old UTC, proposed UTC, reason
SELECT 'PLAN: column preview' AS ResultSet, v.TicketId, v.GenesysConversationId AS ConversationId, v.ColumnName AS AffectedColumn, v.RowId, v.OldUtc, v.ProposedUtc, v.Reason
FROM (
    SELECT p.TicketId, p.GenesysConversationId, N'TicketInteractions.InteractionStartedAtUtc' AS ColumnName, p.TicketInteractionId AS RowId, 0 AS K, p.StartStored AS OldUtc, p.StartProposed AS ProposedUtc, p.ReasonText AS Reason FROM @Plan p
    UNION ALL SELECT p.TicketId, p.GenesysConversationId, N'TicketInteractions.EndedAtUtc', p.TicketInteractionId, 1, p.EndStored, p.EndProposed, p.ReasonText FROM @Plan p WHERE p.SubType = 'PROVEN_FULL_OFFSET' AND p.EndStored IS NOT NULL
    UNION ALL SELECT m.TicketId, p.GenesysConversationId, N'TicketInteractionMessages.SentAtUtc', m.TicketInteractionMessageId, 100 + m.Sequence, m.SentStored, m.SentProposed, p.ReasonText FROM @PlanMsg m JOIN @Plan p ON p.TicketInteractionId = m.TicketInteractionId
    UNION ALL SELECT h.TicketId, p.GenesysConversationId, N'TicketInteractionHandlers.ConnectedAtUtc', h.TicketInteractionHandlerId, 200000, h.ConnectedStored, h.ConnectedProposed, N'connect time recorded by TigerCS before it was connected (own evidence)' FROM @PlanHandler h JOIN @Plan p ON p.TicketInteractionId = h.TicketInteractionId
) v ORDER BY v.TicketId, v.K;
SELECT 'PLAN: transcript lines' AS ResultSet, TicketId, TicketInteractionMessageId, Sequence, SentStored, SentProposed FROM @PlanMsg ORDER BY TicketId, Sequence;
SELECT 'PLAN: connect times' AS ResultSet, TicketId, TicketInteractionHandlerId, ConnectedStored, ConnectedProposed FROM @PlanHandler ORDER BY TicketId;

-- SLA REVIEW - separate and report-only: where the First Human Response time came from a shifted line
SELECT 'SLA REVIEW (not changed by this script)' AS ResultSet, p.TicketId, t.TicketNumber, t.TicketStatus,
       t.FirstHumanResponseAtUtc AS FirstHumanResponseStored, DATEADD(HOUR, -@ShiftHours, t.FirstHumanResponseAtUtc) AS FirstHumanResponseIfCorrected,
       s.FirstResponseDueAtUtc, s.FirstResponseBreached AS FirstResponseBreached_Current,
       CASE WHEN DATEADD(HOUR, -@ShiftHours, t.FirstHumanResponseAtUtc) > s.FirstResponseDueAtUtc THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END AS FirstResponseBreached_IfCorrected
FROM @Plan p
JOIN dbo.Tickets t ON t.TicketId = p.TicketId AND t.FirstHumanResponseAtUtc IS NOT NULL
JOIN @PlanMsg m ON m.TicketInteractionId = p.TicketInteractionId AND ABS(DATEDIFF(SECOND, m.SentStored, t.FirstHumanResponseAtUtc)) <= 1
OUTER APPLY (SELECT TOP (1) x.FirstResponseDueAtUtc, x.FirstResponseBreached FROM dbo.TicketSlaInstances x WHERE x.TicketId = t.TicketId ORDER BY x.PeriodStartAtUtc) s
ORDER BY p.TicketId;

-- ROLLBACK STATEMENTS: restore the exact old values. Generated on every run; run only to UNDO a committed correction.
SELECT 'ROLLBACK STATEMENTS' AS ResultSet, Stmt FROM (
    SELECT p.TicketInteractionId AS K1, 0 AS K2,
           N'UPDATE dbo.TicketInteractions SET InteractionStartedAtUtc = ''' + CONVERT(nvarchar(33), p.StartStored, 126) + N''', EndedAtUtc = '
         + CASE WHEN p.EndStored IS NULL THEN N'NULL' ELSE N'''' + CONVERT(nvarchar(33), p.EndStored, 126) + N'''' END
         + N' WHERE TicketInteractionId = ' + CAST(p.TicketInteractionId AS nvarchar(20)) + N';' AS Stmt
    FROM @Plan p
    UNION ALL SELECT m.TicketInteractionId, 1 + m.Sequence, N'UPDATE dbo.TicketInteractionMessages SET SentAtUtc = ''' + CONVERT(nvarchar(33), m.SentStored, 126) + N''' WHERE TicketInteractionMessageId = ' + CAST(m.TicketInteractionMessageId AS nvarchar(20)) + N';' FROM @PlanMsg m
    UNION ALL SELECT h.TicketInteractionId, 100000 + 1, N'UPDATE dbo.TicketInteractionHandlers SET ConnectedAtUtc = ''' + CONVERT(nvarchar(33), h.ConnectedStored, 126) + N''' WHERE TicketInteractionHandlerId = ' + CAST(h.TicketInteractionHandlerId AS nvarchar(20)) + N';' FROM @PlanHandler h
) r ORDER BY K1, K2;

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

-- ---------------------------------------------------------------------------------------------
-- 4. Apply (ONE transaction): optimistic updates, protected-state proof, append-only audit
-- ---------------------------------------------------------------------------------------------
DECLARE @Protected TABLE (K nvarchar(40) PRIMARY KEY, V bigint NOT NULL);
DECLARE @After TABLE (K nvarchar(40) PRIMARY KEY, V bigint NOT NULL);
DECLARE @Run uniqueidentifier = NEWID();
DECLARE @rc int;

BEGIN TRY
    BEGIN TRANSACTION;

    -- the state that must NOT move: statuses, owners, departments, closure/resolution, assignments, status history, SLA rows, workflow events, and every EXISTING audit row
    INSERT @Protected SELECT N'Tickets',            COALESCE(CHECKSUM_AGG(CHECKSUM(TicketId, TicketStatus, CurrentOwnerEmployeeId, AiAgentOwnerEmployeeId, CurrentDepartmentId, SlaState, ResolutionOutcome, ReopenCount, FirstHumanResponseAtUtc)), 0) FROM dbo.Tickets;
    INSERT @Protected SELECT N'TicketInteractionsOther', COALESCE(CHECKSUM_AGG(CHECKSUM(TicketInteractionId, TicketId, CreatedAtUtc, GenesysConversationId, EndReason, HandledByUserId, CustomerPhone)), 0) + COUNT_BIG(*) FROM dbo.TicketInteractions;
    INSERT @Protected SELECT N'TicketAssignments',  COALESCE(CHECKSUM_AGG(CHECKSUM(TicketAssignmentId, AssignedEmployeeId, AssignedDepartmentId, AssignedAtUtc, IsCurrent)), 0) FROM dbo.TicketAssignments;
    INSERT @Protected SELECT N'TicketStatusHistory',COALESCE(CHECKSUM_AGG(CHECKSUM(TicketId, Dimension, OldValue, NewValue, OccurredAtUtc)), 0) + COUNT_BIG(*) FROM dbo.TicketStatusHistory;
    INSERT @Protected SELECT N'TicketResolutions',  COALESCE(CHECKSUM_AGG(CHECKSUM(TicketResolutionId, ResolutionOutcome, ResolvedAtUtc)), 0) FROM dbo.TicketResolutions;
    INSERT @Protected SELECT N'TicketSlaInstances', COALESCE(CHECKSUM_AGG(CHECKSUM(TicketId, PeriodStartAtUtc, FirstResponseDueAtUtc, FirstResponseBreached)), 0) + COUNT_BIG(*) FROM dbo.TicketSlaInstances;
    INSERT @Protected SELECT N'TicketWorkflowEvents', COUNT_BIG(*) FROM dbo.TicketWorkflowEvents;
    INSERT @Protected SELECT N'AuditEntriesBefore', COALESCE(CHECKSUM_AGG(CHECKSUM(AuditEntryId, Action, EntityId, OccurredAtUtc)), 0) + COUNT_BIG(*) FROM dbo.AuditEntries;
    DECLARE @MaxAuditId bigint = (SELECT COALESCE(MAX(AuditEntryId), 0) FROM dbo.AuditEntries);

    UPDATE i SET i.InteractionStartedAtUtc = p.StartProposed, i.EndedAtUtc = p.EndProposed
    FROM dbo.TicketInteractions i JOIN @Plan p ON p.TicketInteractionId = i.TicketInteractionId
    WHERE i.InteractionStartedAtUtc = p.StartStored AND ((i.EndedAtUtc IS NULL AND p.EndStored IS NULL) OR i.EndedAtUtc = p.EndStored);   -- optimistic: only if still exactly the planned old value
    SET @rc = @@ROWCOUNT;
    IF @rc <> @nConv THROW 50310, 'ABORTED: a planned conversation changed since the plan was read (or was updated twice). Rolled back.', 1;

    UPDATE m SET m.SentAtUtc = pm.SentProposed
    FROM dbo.TicketInteractionMessages m JOIN @PlanMsg pm ON pm.TicketInteractionMessageId = m.TicketInteractionMessageId
    WHERE m.SentAtUtc = pm.SentStored;
    SET @rc = @@ROWCOUNT;
    IF @rc <> @nMsg THROW 50311, 'ABORTED: a planned transcript line changed since the plan was read. Rolled back.', 1;

    UPDATE h SET h.ConnectedAtUtc = ph.ConnectedProposed
    FROM dbo.TicketInteractionHandlers h JOIN @PlanHandler ph ON ph.TicketInteractionHandlerId = h.TicketInteractionHandlerId
    WHERE h.ConnectedAtUtc = ph.ConnectedStored;
    SET @rc = @@ROWCOUNT;
    IF @rc <> @nHandler THROW 50312, 'ABORTED: a planned connect time changed since the plan was read. Rolled back.', 1;

    -- append-only trace: one NEW audit row per corrected ticket, old and new values (no existing audit row is touched)
    INSERT dbo.AuditEntries (ActorEmployeeId, Action, EntityType, EntityId, BeforeValue, AfterValue, CorrelationId, OccurredAtUtc)
    SELECT NULL, N'GenesysTimestampCorrected', N'Ticket', CAST(p.TicketId AS nvarchar(100)),
           N'InteractionStartedAtUtc=' + CONVERT(nvarchar(33), p.StartStored, 126) + N';EndedAtUtc=' + COALESCE(CONVERT(nvarchar(33), p.EndStored, 126), N'(none)')
             + N';Lines=' + CAST((SELECT COUNT(*) FROM @PlanMsg m WHERE m.TicketInteractionId = p.TicketInteractionId) AS nvarchar(10)),
           N'InteractionStartedAtUtc=' + CONVERT(nvarchar(33), p.StartProposed, 126) + N';EndedAtUtc=' + COALESCE(CONVERT(nvarchar(33), p.EndProposed, 126), N'(none)')
             + N';ShiftHours=-' + CAST(@ShiftHours AS nvarchar(3)) + N';ConversationId=' + p.GenesysConversationId + N';Proof=' + p.SubType
             + N';Reason=Genesys UTC offset read in the host zone before PR #292 (+4h); ' + p.ReasonText,
           @Run, SYSUTCDATETIME()
    FROM @Plan p;

    -- validation: nothing protected moved; the only change to the audit trail is the appended rows
    INSERT @After SELECT N'Tickets',            COALESCE(CHECKSUM_AGG(CHECKSUM(TicketId, TicketStatus, CurrentOwnerEmployeeId, AiAgentOwnerEmployeeId, CurrentDepartmentId, SlaState, ResolutionOutcome, ReopenCount, FirstHumanResponseAtUtc)), 0) FROM dbo.Tickets;
    INSERT @After SELECT N'TicketInteractionsOther', COALESCE(CHECKSUM_AGG(CHECKSUM(TicketInteractionId, TicketId, CreatedAtUtc, GenesysConversationId, EndReason, HandledByUserId, CustomerPhone)), 0) + COUNT_BIG(*) FROM dbo.TicketInteractions;
    INSERT @After SELECT N'TicketAssignments',  COALESCE(CHECKSUM_AGG(CHECKSUM(TicketAssignmentId, AssignedEmployeeId, AssignedDepartmentId, AssignedAtUtc, IsCurrent)), 0) FROM dbo.TicketAssignments;
    INSERT @After SELECT N'TicketStatusHistory',COALESCE(CHECKSUM_AGG(CHECKSUM(TicketId, Dimension, OldValue, NewValue, OccurredAtUtc)), 0) + COUNT_BIG(*) FROM dbo.TicketStatusHistory;
    INSERT @After SELECT N'TicketResolutions',  COALESCE(CHECKSUM_AGG(CHECKSUM(TicketResolutionId, ResolutionOutcome, ResolvedAtUtc)), 0) FROM dbo.TicketResolutions;
    INSERT @After SELECT N'TicketSlaInstances', COALESCE(CHECKSUM_AGG(CHECKSUM(TicketId, PeriodStartAtUtc, FirstResponseDueAtUtc, FirstResponseBreached)), 0) + COUNT_BIG(*) FROM dbo.TicketSlaInstances;
    INSERT @After SELECT N'TicketWorkflowEvents', COUNT_BIG(*) FROM dbo.TicketWorkflowEvents;
    INSERT @After SELECT N'AuditEntriesBefore', COALESCE(CHECKSUM_AGG(CHECKSUM(AuditEntryId, Action, EntityId, OccurredAtUtc)), 0) + COUNT_BIG(*) FROM dbo.AuditEntries WHERE AuditEntryId <= @MaxAuditId;

    IF EXISTS (SELECT 1 FROM @Protected b JOIN @After a ON a.K = b.K WHERE a.V <> b.V)
        THROW 50320, 'VALIDATION FAILED: a protected table (status / ownership / closure / assignments / status history / SLA / workflow events / existing audit rows) changed. Rolled back.', 1;
    IF (SELECT COUNT(*) FROM dbo.AuditEntries WHERE AuditEntryId > @MaxAuditId) <> @nConv
        THROW 50321, 'VALIDATION FAILED: the number of appended audit rows is not the number of corrected conversations. Rolled back.', 1;
    IF EXISTS (SELECT 1 FROM dbo.TicketInteractions i JOIN @Plan p ON p.TicketInteractionId = i.TicketInteractionId
               WHERE i.InteractionStartedAtUtc > DATEADD(MINUTE, @ToleranceMin, p.CreatedAtUtc))
        THROW 50322, 'VALIDATION FAILED: a corrected start is still later than its ticket creation. Rolled back.', 1;

    COMMIT TRANSACTION;
    PRINT N'COMMITTED: ' + CAST(@nConv AS nvarchar(10)) + N' conversation(s), ' + CAST(@nMsg AS nvarchar(10)) + N' line(s), ' + CAST(@nHandler AS nvarchar(10))
        + N' connect time(s) corrected; run id ' + CAST(@Run AS nvarchar(40)) + N'. Re-running finds nothing left to correct.';
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    PRINT N'FAILED - transaction rolled back, nothing was changed.';
    THROW;
END CATCH;

-- ---------------------------------------------------------------------------------------------
-- 5. Post-commit verification (read-only)
-- ---------------------------------------------------------------------------------------------
SELECT 'POST: corrected conversations' AS ResultSet, p.TicketId, i.InteractionStartedAtUtc AS StartNow, p.StartStored AS StartWas, i.EndedAtUtc AS EndNow, p.EndStored AS EndWas
FROM @Plan p JOIN dbo.TicketInteractions i ON i.TicketInteractionId = p.TicketInteractionId ORDER BY p.TicketId;
SELECT 'POST: audit rows written' AS ResultSet, AuditEntryId, EntityId AS TicketId, BeforeValue, AfterValue FROM dbo.AuditEntries WHERE CorrelationId = @Run ORDER BY AuditEntryId;
