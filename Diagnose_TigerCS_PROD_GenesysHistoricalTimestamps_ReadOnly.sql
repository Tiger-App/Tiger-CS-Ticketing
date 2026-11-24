/*
  Diagnose_TigerCS_PROD_GenesysHistoricalTimestamps_ReadOnly.sql
  READ-ONLY. No statement touches a database table except SELECT: the evidence is computed once into a TABLE VARIABLE (memory/tempdb only) and every result set
  reads from it. No write, no lock for update (READ UNCOMMITTED). Safe on Production.

  WHY.  Until the fix in PR #292 an offset-bearing UTC timestamp from Genesys ("...+00:00") was read into the application HOST's time zone and stored as UAE
        wall-clock time in a column named "...Utc" - exactly +4 hours. "...Z" and zoneless payloads were never affected. A conversation can be shifted in ONE
        column only (ticket of 24 Nov 2026: start stored 11:41:58, real 07:41:58, while the first line, the end and the closure were correct) or in all of them
        (ticket 1878: start, end, every transcript line, connect time).

  THREE CLASSES (nothing else exists)
    PROVEN   a correction is proposed. Two INDEPENDENT facts prove it; a +4h difference ALONE is never a fact (see "THE RULES").
    SUSPECT  at least one timestamp is impossible or inconsistent, but the evidence is missing, partial or contradictory. NEVER corrected; listed with the reason.
    CORRECT  nothing impossible or inconsistent was found. (A shifted value that still lies in the past of every anchor cannot be seen by any rule - see LIMITS.)

  THE RULES (no blind subtraction)
    "Server clock" facts are values TigerCS recorded ITSELF and that the Genesys payload never touches: TicketInteractions.CreatedAtUtc and the receipt time of the
    end event (AuditEntries 'GenesysConversationEnded'). Transcript lines are a second, separate Genesys field.

    PROVEN_START_OFFSET  - only InteractionStartedAtUtc is corrected. ALL of:
        (1) SERVER CLOCK: the start is later than the ticket's own CreatedAtUtc by more than @ToleranceMin but at most 4h + tolerance (a conversation cannot start
            after the ticket it created, and -4h gives a plausible instant);
        (2) FIRST TRANSCRIPT LINE: after -4h the start lands within [first line - @MaxStartToFirstLineMin, first line + tolerance] - i.e. the corrected start sits
            just before the conversation's own first message, and the first line is itself not in the future of the end receipt;
        (3) the end and every transcript line are NOT impossible (they are the correct values that prove the start wrong), and the corrected start is not after the end.
    PROVEN_FULL_OFFSET   - start, end, every transcript line and (with own evidence) the connect time are corrected. ALL of:
        (1) the same SERVER CLOCK start fact as above;
        (2) the end (or a transcript line) is later than the audit-recorded receipt of the end event by more than the tolerance but at most 4h + tolerance;
        (3) the conversation is internally consistent (start <= end, every line inside [start, end] +/- tolerance, no line later than receipt + 4h + tolerance).
    A start that is impossible with only ONE of these facts, a value not "+4h-shaped" (later than ticket creation by MORE than 4h), a conversation without a
    transcript and without an end receipt, contradictory or mixed evidence: SUSPECT, never proposed.

  LIMITS (stated, not hidden).  A shifted value that still lies in the PAST of its anchor (a conversation that started more than 4h before its ticket, or an end event
        received more than 4h after the conversation ended) cannot be recognised by any rule and classifies as CORRECT. Conversations that came in as "Z"/zoneless
        were never wrong and never match. Only Genesys instants are examined; nothing else in a ticket is read for correction.

  Output (result sets, in order)
    1  SUMMARY: Classification, SubType, Conversations, ProposedCorrections (individual timestamp values), Reason for correction or exclusion
    2  VERDICT: one line - how many conversations / values can be corrected automatically (zero is reported as zero)
    3  INCONSISTENCY TYPES: how many conversations show each type of impossible timestamp, split PROVEN / SUSPECT
    4  EVIDENCE AVAILABILITY: how many conversations have a transcript / an end receipt, per class (why "no independent evidence" happens)
    5  DRYRUN PREVIEW (PROVEN only): Ticket ID, Conversation ID, affected column, old UTC, proposed UTC, reason  (nothing is written)
    6  SUSPECT conversations and why they are excluded (never corrected)
    7  Connect (acceptance) times of PROVEN conversations that are NOT proposed (no independent evidence)
    8  SLA REVIEW (report only): tickets whose First Human Response came from a line this plan would move
*/
SET NOCOUNT ON;
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

DECLARE @ToleranceMin int = 5;                  -- clock skew / processing delay allowed before a value counts as "in the future"
DECLARE @ShiftHours   int = 4;                  -- Asia/Dubai is UTC+4 with no daylight saving
DECLARE @MaxStartToFirstLineMin int = 15;       -- the real gap between a conversation's start and its first message can be at most this long
DECLARE @TicketId     bigint = NULL;            -- NULL = every Genesys conversation; set 1878 to inspect only that ticket
DECLARE @OnlyCreatedBeforeUtc datetime2 = NULL; -- optional: ignore tickets created at/after the moment the fixed build went live (they cannot be shifted)

DECLARE @Plan TABLE (
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
INSERT @Plan
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

-- Connect times: a connect time is proposed only with ITS OWN independent evidence (TigerCS recorded the row BEFORE the stored connect time - impossible - by at most
-- 4h + tolerance) and only inside a PROVEN conversation; the corrected value must not precede the corrected start.
DECLARE @PlanHandler TABLE (TicketInteractionHandlerId bigint PRIMARY KEY, TicketInteractionId bigint NOT NULL, TicketId bigint NOT NULL,
    ConnectedStored datetime2 NOT NULL, ConnectedProposed datetime2 NULL, RecordedAtUtc datetime2 NOT NULL, Decision varchar(12) NOT NULL);
INSERT @PlanHandler
SELECT h.TicketInteractionHandlerId, h.TicketInteractionId, p.TicketId, h.ConnectedAtUtc,
       CASE WHEN ok.Proposed = 1 THEN DATEADD(HOUR, -@ShiftHours, h.ConnectedAtUtc) END, h.RecordedAtUtc, CASE WHEN ok.Proposed = 1 THEN 'PROPOSED' ELSE 'NOT PROPOSED' END
FROM @Plan p JOIN dbo.TicketInteractionHandlers h ON h.TicketInteractionId = p.TicketInteractionId
CROSS APPLY (SELECT CASE WHEN h.ConnectedAtUtc > DATEADD(MINUTE, @ToleranceMin, h.RecordedAtUtc)
                          AND h.ConnectedAtUtc <= DATEADD(MINUTE, @ToleranceMin, DATEADD(HOUR, @ShiftHours, h.RecordedAtUtc))
                          AND DATEADD(HOUR, -@ShiftHours, h.ConnectedAtUtc) >= DATEADD(MINUTE, -@ToleranceMin, DATEADD(HOUR, -@ShiftHours, p.StartStored)) THEN 1 ELSE 0 END AS Proposed) ok
WHERE p.Classification = 'PROVEN' AND h.ConnectedAtUtc IS NOT NULL;

-- One row per timestamp VALUE the plan would change (proven conversations only). This is the single source of the preview and of the counts.
DECLARE @Changes TABLE (TicketId bigint NOT NULL, TicketInteractionId bigint NOT NULL, GenesysConversationId nvarchar(64) NOT NULL, ColumnName nvarchar(80) NOT NULL,
    RowId bigint NOT NULL, SortKey int NOT NULL, OldUtc datetime2 NOT NULL, ProposedUtc datetime2 NOT NULL, Reason nvarchar(600) NOT NULL);
INSERT @Changes
SELECT p.TicketId, p.TicketInteractionId, p.GenesysConversationId, N'TicketInteractions.InteractionStartedAtUtc', p.TicketInteractionId, 0, p.StartStored, DATEADD(HOUR, -@ShiftHours, p.StartStored),
       CASE WHEN p.SubType = 'PROVEN_START_OFFSET'
            THEN N'Start is ' + CAST(DATEDIFF(MINUTE, p.CreatedAtUtc, p.StartStored) AS nvarchar(10)) + N' min after the ticket was created (server clock) and ' + CAST(DATEDIFF(MINUTE, p.FirstLineUtc, p.StartStored) AS nvarchar(10))
               + N' min after the first transcript line; minus 4h it lands ' + CAST(DATEDIFF(SECOND, DATEADD(HOUR, -@ShiftHours, p.StartStored), p.FirstLineUtc) AS nvarchar(10)) + N' s before that line. End and lines are plausible and unchanged.'
            ELSE N'Start is ' + CAST(DATEDIFF(MINUTE, p.CreatedAtUtc, p.StartStored) AS nvarchar(10)) + N' min after the ticket was created (server clock) and the end/transcript is later than the recorded end receipt: whole conversation shifted +4h.' END
FROM @Plan p WHERE p.Classification = 'PROVEN';
INSERT @Changes
SELECT p.TicketId, p.TicketInteractionId, p.GenesysConversationId, N'TicketInteractions.EndedAtUtc', p.TicketInteractionId, 1, p.EndStored, DATEADD(HOUR, -@ShiftHours, p.EndStored),
       N'End is ' + CAST(DATEDIFF(MINUTE, p.EndReceivedAtUtc, p.EndStored) AS nvarchar(10)) + N' min after TigerCS recorded receiving the end event (server clock).'
FROM @Plan p WHERE p.SubType = 'PROVEN_FULL_OFFSET' AND p.EndStored IS NOT NULL;
INSERT @Changes
SELECT p.TicketId, p.TicketInteractionId, p.GenesysConversationId, N'TicketInteractionMessages.SentAtUtc', m.TicketInteractionMessageId, 100 + m.Sequence, m.SentAtUtc, DATEADD(HOUR, -@ShiftHours, m.SentAtUtc),
       N'Transcript line ' + CAST(m.Sequence AS nvarchar(10)) + N' belongs to a conversation proven shifted +4h (start and end both impossible by the server clock); line lies inside the stored conversation window.'
FROM @Plan p JOIN dbo.TicketInteractionMessages m ON m.TicketInteractionId = p.TicketInteractionId WHERE p.SubType = 'PROVEN_FULL_OFFSET';
INSERT @Changes
SELECT h.TicketId, h.TicketInteractionId, p.GenesysConversationId, N'TicketInteractionHandlers.ConnectedAtUtc', h.TicketInteractionHandlerId, 200000, h.ConnectedStored, h.ConnectedProposed,
       N'Connect time is ' + CAST(DATEDIFF(MINUTE, h.RecordedAtUtc, h.ConnectedStored) AS nvarchar(10)) + N' min after TigerCS recorded the row (server clock) - own evidence, inside a proven conversation.'
FROM @PlanHandler h JOIN @Plan p ON p.TicketInteractionId = h.TicketInteractionId WHERE h.Decision = 'PROPOSED';

-- 1. SUMMARY: classification, conversations, proposed corrections, reason for correction or exclusion ---------------------------------------------------
SELECT p.Classification, p.SubType, p.ReasonCode, COUNT(*) AS Conversations,
       COALESCE(SUM(c.Changes), 0) AS ProposedCorrections, MIN(p.ReasonText) AS Reason
FROM @Plan p
OUTER APPLY (SELECT COUNT(*) AS Changes FROM @Changes x WHERE x.TicketInteractionId = p.TicketInteractionId) c
GROUP BY p.Classification, p.SubType, p.ReasonCode
ORDER BY CASE p.Classification WHEN 'PROVEN' THEN 0 WHEN 'SUSPECT' THEN 1 ELSE 2 END, COUNT(*) DESC;

-- 2. VERDICT --------------------------------------------------------------------------------------------------------------------------------------------
SELECT (SELECT COUNT(*) FROM @Plan) AS ConversationsExamined,
       (SELECT COUNT(*) FROM @Plan WHERE Classification = 'PROVEN') AS Proven,
       (SELECT COUNT(*) FROM @Plan WHERE Classification = 'SUSPECT') AS Suspect,
       (SELECT COUNT(*) FROM @Plan WHERE Classification = 'CORRECT') AS Correct,
       (SELECT COUNT(DISTINCT TicketInteractionId) FROM @Changes) AS SafeAutomaticCorrections_Conversations,
       (SELECT COUNT(*) FROM @Changes) AS SafeAutomaticCorrections_Values,
       CASE WHEN NOT EXISTS (SELECT 1 FROM @Changes) THEN N'ZERO safe automatic corrections: no conversation has sufficient independent evidence. Nothing is proposed.'
            ELSE N'Only the PROVEN conversations in the DRYRUN PREVIEW are eligible; every SUSPECT conversation is excluded and needs manual review.' END AS Verdict;

-- 3. INCONSISTENCY TYPES: how many conversations show each type of impossible/inconsistent timestamp ---------------------------------------------------
SELECT t.InconsistencyType, COUNT(*) AS Conversations,
       SUM(CASE WHEN p.Classification = 'PROVEN' THEN 1 ELSE 0 END) AS InProven, SUM(CASE WHEN p.Classification = 'SUSPECT' THEN 1 ELSE 0 END) AS InSuspect
FROM @Plan p
CROSS APPLY (VALUES ('START_AFTER_TICKET_CREATED', p.StartAfterCreated), ('START_AFTER_TICKET_CREATED_BY_MORE_THAN_4H', CASE WHEN p.StartAfterCreated = 1 AND p.StartShapeCreated = 0 THEN 1 ELSE 0 END),
                    ('START_AFTER_FIRST_TRANSCRIPT_LINE', p.StartAfterFirstLine), ('START_MINUS_4H_ANCHORED_BY_FIRST_LINE', p.StartAnchorFirstLine),
                    ('END_BEFORE_START', p.EndBeforeStart), ('END_AFTER_END_RECEIPT', p.EndAfterReceipt), ('END_AFTER_END_RECEIPT_4H_SHAPED', p.EndShapeReceipt),
                    ('TRANSCRIPT_LINE_AFTER_END_RECEIPT', CASE WHEN p.LinesAfterReceipt > 0 THEN 1 ELSE 0 END),
                    ('TRANSCRIPT_LINE_OUTSIDE_CONVERSATION', CASE WHEN p.LinesOutside > 0 THEN 1 ELSE 0 END),
                    ('CONNECT_TIME_AFTER_RECORDED', CASE WHEN p.ConnectImpossible > 0 THEN 1 ELSE 0 END)) t(InconsistencyType, Flag)
WHERE t.Flag = 1
GROUP BY t.InconsistencyType ORDER BY COUNT(*) DESC;

-- 4. EVIDENCE AVAILABILITY: why "no independent evidence" happens ---------------------------------------------------------------------------------------
SELECT p.Classification,
       CASE WHEN p.LineCount > 0 THEN 'has transcript' ELSE 'NO transcript' END AS Transcript,
       CASE WHEN p.EndReceivedAtUtc IS NOT NULL THEN 'has end receipt' ELSE 'NO end receipt' END AS EndReceipt,
       COUNT(*) AS Conversations
FROM @Plan p GROUP BY p.Classification, CASE WHEN p.LineCount > 0 THEN 'has transcript' ELSE 'NO transcript' END, CASE WHEN p.EndReceivedAtUtc IS NOT NULL THEN 'has end receipt' ELSE 'NO end receipt' END
ORDER BY p.Classification, Conversations DESC;

-- 5. DRYRUN PREVIEW (PROVEN ONLY - nothing is written): Ticket ID, Conversation ID, affected column, old UTC, proposed UTC, reason ----------------------------
SELECT c.TicketId, t.TicketNumber, c.GenesysConversationId AS ConversationId, c.ColumnName AS AffectedColumn, c.RowId,
       c.OldUtc, c.ProposedUtc, DATEADD(HOUR, @ShiftHours, c.OldUtc) AS OldAsShownInUae, DATEADD(HOUR, @ShiftHours, c.ProposedUtc) AS ProposedAsShownInUae, c.Reason
FROM @Changes c JOIN dbo.Tickets t ON t.TicketId = c.TicketId
ORDER BY c.TicketId, c.SortKey;

-- 6. SUSPECT conversations: never corrected; the evidence and the reason ----------------------------------------------------------------------------------
SELECT p.TicketId, t.TicketNumber, t.TicketStatus, p.GenesysConversationId AS ConversationId, p.ReasonCode, p.ReasonText AS Reason,
       p.CreatedAtUtc AS TicketCreatedAtUtc_ServerClock, p.StartStored, DATEDIFF(MINUTE, p.CreatedAtUtc, p.StartStored) AS StartMinusCreated_Min,
       p.FirstLineUtc, DATEDIFF(MINUTE, p.FirstLineUtc, p.StartStored) AS StartMinusFirstLine_Min, p.LineCount,
       p.EndStored, p.EndReceivedAtUtc AS EndReceived_ServerClock, DATEDIFF(MINUTE, p.EndReceivedAtUtc, p.EndStored) AS EndMinusReceived_Min,
       p.StartAnchorFirstLine, p.EndShapeReceipt, p.LinesShapeReceipt, p.EndBeforeStart, p.LinesOutside, p.ConnectImpossible
FROM @Plan p JOIN dbo.Tickets t ON t.TicketId = p.TicketId
WHERE p.Classification = 'SUSPECT'
ORDER BY p.ReasonCode, p.TicketId;

-- 7. Connect (acceptance) times of PROVEN conversations that are NOT proposed ----------------------------------------------------------------------------
SELECT h.TicketId, h.TicketInteractionHandlerId, h.ConnectedStored, h.RecordedAtUtc AS RecordedAtUtc_ServerClock, h.Decision,
       N'no independent evidence that this connect time is shifted - left unchanged' AS Reason
FROM @PlanHandler h WHERE h.Decision = 'NOT PROPOSED' ORDER BY h.TicketId;

-- 8. SLA REVIEW (report only - this feeds a decision, it changes nothing). First Human Response (Tickets.FirstHumanResponseAtUtc) comes from the first HumanAgent
--    transcript line; if this plan would move that line, the stored response time (and an SLA flag derived from it) is affected.
SELECT p.TicketId, t.TicketNumber, t.TicketStatus, t.FirstHumanResponseAtUtc AS FirstHumanResponseStored, DATEADD(HOUR, -@ShiftHours, t.FirstHumanResponseAtUtc) AS FirstHumanResponseIfCorrected,
       s.PeriodStartAtUtc AS SlaPeriodStartAtUtc, s.FirstResponseDueAtUtc, s.FirstResponseBreached AS FirstResponseBreached_Current,
       CASE WHEN DATEADD(HOUR, -@ShiftHours, t.FirstHumanResponseAtUtc) > s.FirstResponseDueAtUtc THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END AS FirstResponseBreached_IfCorrected,
       CASE WHEN s.FirstResponseBreached = 1 AND DATEADD(HOUR, -@ShiftHours, t.FirstHumanResponseAtUtc) <= s.FirstResponseDueAtUtc THEN 'FLAG WOULD CLEAR' ELSE 'flag unchanged' END AS Impact
FROM @Plan p
JOIN dbo.Tickets t ON t.TicketId = p.TicketId AND t.FirstHumanResponseAtUtc IS NOT NULL
JOIN dbo.TicketInteractionMessages m ON m.TicketInteractionId = p.TicketInteractionId AND m.Sender = 2   -- HumanAgent
   AND ABS(DATEDIFF(SECOND, m.SentAtUtc, t.FirstHumanResponseAtUtc)) <= 1
OUTER APPLY (SELECT TOP (1) x.PeriodStartAtUtc, x.FirstResponseDueAtUtc, x.FirstResponseBreached FROM dbo.TicketSlaInstances x
             WHERE x.TicketId = t.TicketId ORDER BY x.PeriodStartAtUtc) s   -- First Response is measured on the FIRST SLA period and is never restarted
WHERE p.SubType = 'PROVEN_FULL_OFFSET'
ORDER BY p.TicketId;
