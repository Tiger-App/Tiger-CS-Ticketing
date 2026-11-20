/*
  Diagnose_TigerCS_PROD_GenesysHistoricalTimestamps_ReadOnly.sql
  READ-ONLY. No statement touches a database table except SELECT: the evidence is computed once into a TABLE VARIABLE (memory/tempdb only) and every result set reads from it. No write, no lock for update (READ UNCOMMITTED). Safe on Production.

  WHY.  Until the fix in PR #292 an offset-bearing UTC timestamp from Genesys ("...+00:00") was read into the application HOST's time zone and stored
        as UAE wall-clock time in a column named "...Utc" - exactly +4 hours (ticket 1878: conversation start stored 13:50:26, real 09:50:26 UTC, UI
        showed 17:50:26). "...Z" and zoneless payloads were never affected. The shift hit every instant of such a conversation: start, end, transcript lines
        and the connect (acceptance) time.

  THE RULE (no blind subtraction).  Nothing here assumes a conversation is wrong because of its zone or its date. A conversation is CONFIRMED_PLUS4H only
        when TWO INDEPENDENT facts that TigerCS recorded ITSELF (server clock - never touched by the payload) show the stored Genesys time is impossible AND
        exactly "+4h-shaped":
          (1) START: InteractionStartedAtUtc is later than the ticket's own CreatedAtUtc by more than @ToleranceMin minutes but at most 4h + tolerance
              (a conversation cannot start after the ticket it created);
          (2) END or TRANSCRIPT: EndedAtUtc (or a transcript line's SentAtUtc) is later than the audit-recorded receipt time of the end event
              (AuditEntries 'GenesysConversationEnded') by more than the tolerance but at most 4h + tolerance;
        and, after subtracting 4h, the conversation is internally CONSISTENT (start <= end; every transcript line inside [start, end] +/- tolerance).
        "At most 4h" is what makes the correction safe: it means a -4h result is a PLAUSIBLE instant (not before the thing it follows by more than the tolerance).
        One fact alone, a mixed conversation, or an inconsistent result is SUSPECT: listed with the reason, never proposed for change.

  LIMITS (stated, not hidden).  A shifted value that still lies in the PAST of its receipt (a conversation that started more than 4h before its ticket, or an
        end event received more than 4h after the conversation ended) cannot be recognised by any rule; such rows are not listed. Conversations that came in as
        "Z"/zoneless were never wrong and never match.

  Output (result sets, in order)
    1  Summary by classification
    2  One row per conversation with evidence: stored values, receipt times, deltas, classification, PROPOSED start / end
    3  Transcript lines of CONFIRMED conversations: old -> proposed
    4  Connect (acceptance) times of CONFIRMED conversations: old -> proposed (only with independent evidence)
    5  SLA REVIEW (separate, report only): tickets whose First Human Response time came from a shifted line, with the current SLA flags and what they would be
    6  SUSPECT conversations and why (never corrected)
*/
SET NOCOUNT ON;
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

DECLARE @ToleranceMin int = 5;                  -- clock skew / processing delay allowed before a value counts as "in the future"
DECLARE @ShiftHours   int = 4;                  -- Asia/Dubai is UTC+4 with no daylight saving
DECLARE @TicketId     bigint = NULL;            -- NULL = every Genesys conversation; set 1878 to inspect only that ticket
DECLARE @OnlyCreatedBeforeUtc datetime2 = NULL; -- optional: ignore tickets created at/after the moment the fixed build went live (they cannot be shifted)

DECLARE @Plan TABLE (
    TicketInteractionId bigint PRIMARY KEY, TicketId bigint NOT NULL, GenesysConversationId nvarchar(64) NOT NULL,
    CreatedAtUtc datetime2 NOT NULL, StartStored datetime2 NULL, EndStored datetime2 NULL, EndReceivedAtUtc datetime2 NULL,
    StartEvidence bit NOT NULL, EndEvidence bit NOT NULL, LineEvidenceCount int NOT NULL, EndBeforeStart bit NOT NULL, InconsistentLines int NOT NULL,
    Classification varchar(20) NOT NULL, SuspectReason nvarchar(200) NULL);

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
Evidence AS (
    SELECT c.*,
        CASE WHEN c.InteractionStartedAtUtc > DATEADD(MINUTE, @ToleranceMin, c.CreatedAtUtc)
              AND c.InteractionStartedAtUtc <= DATEADD(MINUTE, @ToleranceMin, DATEADD(HOUR, @ShiftHours, c.CreatedAtUtc)) THEN 1 ELSE 0 END AS StartEvidence,
        CASE WHEN c.EndedAtUtc IS NOT NULL AND c.EndReceivedAtUtc IS NOT NULL
              AND c.EndedAtUtc > DATEADD(MINUTE, @ToleranceMin, c.EndReceivedAtUtc)
              AND c.EndedAtUtc <= DATEADD(MINUTE, @ToleranceMin, DATEADD(HOUR, @ShiftHours, c.EndReceivedAtUtc)) THEN 1 ELSE 0 END AS EndEvidence,
        (SELECT COUNT(*) FROM dbo.TicketInteractionMessages m
          WHERE m.TicketInteractionId = c.TicketInteractionId AND c.EndReceivedAtUtc IS NOT NULL
            AND m.SentAtUtc > DATEADD(MINUTE, @ToleranceMin, c.EndReceivedAtUtc)
            AND m.SentAtUtc <= DATEADD(MINUTE, @ToleranceMin, DATEADD(HOUR, @ShiftHours, c.EndReceivedAtUtc))) AS LineEvidenceCount,
        -- consistency AFTER a -4h correction
        CASE WHEN c.EndedAtUtc IS NOT NULL AND c.EndedAtUtc < c.InteractionStartedAtUtc THEN 1 ELSE 0 END AS EndBeforeStart,
        (SELECT COUNT(*) FROM dbo.TicketInteractionMessages m
          WHERE m.TicketInteractionId = c.TicketInteractionId
            AND (m.SentAtUtc < DATEADD(MINUTE, -@ToleranceMin, c.InteractionStartedAtUtc)
              OR (c.EndedAtUtc IS NOT NULL AND m.SentAtUtc > DATEADD(MINUTE, @ToleranceMin, c.EndedAtUtc)))) AS InconsistentLines
    FROM Conv c)
INSERT @Plan
SELECT e.TicketInteractionId, e.TicketId, e.GenesysConversationId, e.CreatedAtUtc, e.InteractionStartedAtUtc, e.EndedAtUtc, e.EndReceivedAtUtc,
       e.StartEvidence, e.EndEvidence, e.LineEvidenceCount, e.EndBeforeStart, e.InconsistentLines,
       CASE WHEN e.StartEvidence = 1 AND (e.EndEvidence = 1 OR e.LineEvidenceCount > 0) AND e.EndBeforeStart = 0 AND e.InconsistentLines = 0 THEN 'CONFIRMED_PLUS4H'
            WHEN e.StartEvidence = 1 OR e.EndEvidence = 1 OR e.LineEvidenceCount > 0 THEN 'SUSPECT' ELSE 'OK' END,
       CASE WHEN e.StartEvidence = 1 AND (e.EndEvidence = 1 OR e.LineEvidenceCount > 0) AND e.EndBeforeStart = 0 AND e.InconsistentLines = 0 THEN NULL
            WHEN e.StartEvidence = 1 AND e.EndEvidence = 0 AND e.LineEvidenceCount = 0 THEN N'start is impossible but there is no independent second fact (no end receipt, or the end is plausible)'
            WHEN e.StartEvidence = 0 AND (e.EndEvidence = 1 OR e.LineEvidenceCount > 0) THEN N'end/line is impossible but the start is plausible (mixed conversation)'
            WHEN e.EndBeforeStart = 1 THEN N'the end precedes the start'
            WHEN e.InconsistentLines > 0 THEN N'some transcript lines fall outside the conversation (mixed sources)'
            ELSE N'evidence incomplete' END
FROM Evidence e;

-- 1. Summary -----------------------------------------------------------------------------------------------------------------------------------
SELECT Classification, COUNT(*) AS Conversations FROM @Plan WHERE Classification <> 'OK' GROUP BY Classification ORDER BY Classification;

-- 2. Conversation-level evidence and PROPOSED values (nothing is changed by this script) --------------------------------------------------------------
SELECT p.TicketId, t.TicketNumber, t.TicketStatus, p.GenesysConversationId, p.Classification,
       p.CreatedAtUtc AS TicketCreatedAtUtc_ServerClock,
       p.StartStored, CASE WHEN p.Classification = 'CONFIRMED_PLUS4H' THEN DATEADD(HOUR, -@ShiftHours, p.StartStored) END AS StartProposed,
       DATEDIFF(MINUTE, p.CreatedAtUtc, p.StartStored) AS StartMinusCreated_Min,
       p.EndStored, CASE WHEN p.Classification = 'CONFIRMED_PLUS4H' AND p.EndStored IS NOT NULL THEN DATEADD(HOUR, -@ShiftHours, p.EndStored) END AS EndProposed,
       p.EndReceivedAtUtc AS EndReceived_ServerClock, DATEDIFF(MINUTE, p.EndReceivedAtUtc, p.EndStored) AS EndMinusReceived_Min,
       p.StartEvidence, p.EndEvidence, p.LineEvidenceCount, p.EndBeforeStart, p.InconsistentLines
FROM @Plan p JOIN dbo.Tickets t ON t.TicketId = p.TicketId
WHERE p.Classification <> 'OK'
ORDER BY p.Classification DESC, p.CreatedAtUtc DESC;

-- 3. Transcript lines of CONFIRMED conversations: old -> proposed ---------------------------------------------------------------------------------------
SELECT p.TicketId, p.GenesysConversationId, m.TicketInteractionMessageId, m.Sequence, m.Sender, m.SentAtUtc AS SentStored, DATEADD(HOUR, -@ShiftHours, m.SentAtUtc) AS SentProposed
FROM @Plan p JOIN dbo.TicketInteractionMessages m ON m.TicketInteractionId = p.TicketInteractionId
WHERE p.Classification = 'CONFIRMED_PLUS4H'
ORDER BY p.TicketId, m.Sequence;

-- 4. Connect (acceptance) times of CONFIRMED conversations. Independent evidence: TigerCS recorded the row (RecordedAtUtc, server clock) BEFORE the stored
--    connect time, which is impossible, by at most 4h + tolerance. Anything else is left alone and shown as NOT PROPOSED.
SELECT p.TicketId, h.TicketInteractionHandlerId, h.HandledByUserId, h.ConnectedAtUtc AS ConnectedStored, h.RecordedAtUtc AS RecordedAtUtc_ServerClock,
       CASE WHEN h.ConnectedAtUtc > DATEADD(MINUTE, @ToleranceMin, h.RecordedAtUtc) AND h.ConnectedAtUtc <= DATEADD(MINUTE, @ToleranceMin, DATEADD(HOUR, @ShiftHours, h.RecordedAtUtc))
            THEN DATEADD(HOUR, -@ShiftHours, h.ConnectedAtUtc) END AS ConnectedProposed,
       CASE WHEN h.ConnectedAtUtc > DATEADD(MINUTE, @ToleranceMin, h.RecordedAtUtc) AND h.ConnectedAtUtc <= DATEADD(MINUTE, @ToleranceMin, DATEADD(HOUR, @ShiftHours, h.RecordedAtUtc))
            THEN 'PROPOSED' ELSE 'NOT PROPOSED (no independent evidence)' END AS Decision
FROM @Plan p JOIN dbo.TicketInteractionHandlers h ON h.TicketInteractionId = p.TicketInteractionId
WHERE p.Classification = 'CONFIRMED_PLUS4H' AND h.ConnectedAtUtc IS NOT NULL
ORDER BY p.TicketId;

-- 5. SLA REVIEW (separate; report only - this feeds a decision, it changes nothing). A ticket's First Human Response time (Tickets.FirstHumanResponseAtUtc) is taken from the
--    first HumanAgent transcript line; if that line was shifted, so was the response time, and a First Response that was actually on time can read as breached.
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
WHERE p.Classification = 'CONFIRMED_PLUS4H'
ORDER BY p.TicketId;

-- 6. SUSPECT conversations - evidence is partial or inconsistent; NEVER corrected by the remediation script. Review by hand. -------------------------------
SELECT p.TicketId, t.TicketNumber, p.GenesysConversationId, p.SuspectReason, p.StartEvidence, p.EndEvidence, p.LineEvidenceCount, p.EndBeforeStart, p.InconsistentLines,
       p.StartStored, p.EndStored, p.CreatedAtUtc AS TicketCreatedAtUtc, p.EndReceivedAtUtc
FROM @Plan p JOIN dbo.Tickets t ON t.TicketId = p.TicketId
WHERE p.Classification = 'SUSPECT'
ORDER BY p.TicketId;
