/*
  Diagnose_TigerCS_PROD_AutoReopenAndTimestamps_ReadOnly.sql
  READ-ONLY. Nothing here writes, locks for update or changes data: every statement is a SELECT. Run it against a read replica or
  during a quiet period; it makes no change to Production data and none is proposed without approval.

  Answers two questions about history:
    A-C  Was any ticket reopened other than by the explicit manual Reopen? Did anything automatic change a ticket after it was Closed?
    D-F  Which Genesys timestamps are wrong (stored in the wrong zone, or an ownership change dated at processing time, not at the event)?

  Codes: TicketStatus 6 = Closed; TicketStatusDimension 1 = TicketStatus; WorkflowEventType 9 = Reopened, 17 = TicketTransferred.
  Only the manual Reopen (TicketLifecycleAppService.ReopenAsync) writes a Closed -> other status row, a Reopened workflow event AND an audit row
  with a human actor, so those three together are what a LEGITIMATE reopen looks like.
*/
SET NOCOUNT ON;
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

/* ---------- A. Every Closed -> not-Closed transition, classified ---------- */
SELECT h.TicketId, t.TicketNumber, h.OccurredAtUtc AS ReopenedAtUtc, h.NewValue AS NewStatus, h.ActorEmployeeId, h.ActorIsSystem, h.Note,
       CASE WHEN h.ActorIsSystem = 0 AND h.ActorEmployeeId IS NOT NULL
                 AND EXISTS (SELECT 1 FROM dbo.TicketWorkflowEvents e WHERE e.TicketId = h.TicketId AND e.EventType = 9
                             AND ABS(DATEDIFF(SECOND, e.OccurredAtUtc, h.OccurredAtUtc)) <= 5)
            THEN 'MANUAL_REOPEN (human actor + Reopened event)'
            ELSE 'SUSPECT: no human actor or no Reopened event' END AS Classification
FROM dbo.TicketStatusHistory h
JOIN dbo.Tickets t ON t.TicketId = h.TicketId
WHERE h.Dimension = 1 AND h.OldValue = 6 AND h.NewValue <> 6
ORDER BY Classification DESC, h.OccurredAtUtc DESC;

/* ---------- B. ReopenCount that the recorded Reopened events do not explain ---------- */
SELECT t.TicketId, t.TicketNumber, t.ReopenCount,
       (SELECT COUNT(*) FROM dbo.TicketWorkflowEvents e WHERE e.TicketId = t.TicketId AND e.EventType = 9) AS ReopenedEvents,
       (SELECT COUNT(*) FROM dbo.TicketStatusHistory h WHERE h.TicketId = t.TicketId AND h.Dimension = 1 AND h.OldValue = 6 AND h.NewValue <> 6) AS ReopenStatusRows
FROM dbo.Tickets t
WHERE t.ReopenCount > 0
  AND (t.ReopenCount <> (SELECT COUNT(*) FROM dbo.TicketWorkflowEvents e WHERE e.TicketId = t.TicketId AND e.EventType = 9)
       OR t.ReopenCount <> (SELECT COUNT(*) FROM dbo.TicketStatusHistory h WHERE h.TicketId = t.TicketId AND h.Dimension = 1 AND h.OldValue = 6 AND h.NewValue <> 6))
ORDER BY t.TicketId;

/* ---------- C. Things that changed on a ticket AFTER its (latest) closure, other than a manual reopen ---------- */
;WITH Closure AS (
    SELECT h.TicketId, MAX(h.OccurredAtUtc) AS ClosedAtUtc
    FROM dbo.TicketStatusHistory h WHERE h.Dimension = 1 AND h.NewValue = 6 GROUP BY h.TicketId)
SELECT c.TicketId, t.TicketNumber, c.ClosedAtUtc, x.What, x.AtUtc, x.Detail
FROM Closure c
JOIN dbo.Tickets t ON t.TicketId = c.TicketId AND t.TicketStatus = 6          -- still Closed: nothing may have changed since
CROSS APPLY (
    SELECT 'ASSIGNMENT' AS What, a.AssignedAtUtc AS AtUtc, CONCAT('AssignedEmployeeId=', a.AssignedEmployeeId) AS Detail
      FROM dbo.TicketAssignments a WHERE a.TicketId = c.TicketId AND a.AssignedAtUtc > DATEADD(SECOND, 5, c.ClosedAtUtc)
    UNION ALL
    SELECT CONCAT('WORKFLOW_EVENT_', e.EventType), e.OccurredAtUtc, e.Note
      FROM dbo.TicketWorkflowEvents e WHERE e.TicketId = c.TicketId AND e.OccurredAtUtc > DATEADD(SECOND, 5, c.ClosedAtUtc)
    UNION ALL
    SELECT 'STATUS_HISTORY', h2.OccurredAtUtc, CONCAT('Dim=', h2.Dimension, ' ', h2.OldValue, '->', h2.NewValue)
      FROM dbo.TicketStatusHistory h2 WHERE h2.TicketId = c.TicketId AND h2.OccurredAtUtc > DATEADD(SECOND, 5, c.ClosedAtUtc)
) x
ORDER BY c.TicketId, x.AtUtc;

/* C2. Closed tickets that still carry a HUMAN owner (closure clears it) */
SELECT t.TicketId, t.TicketNumber, t.CurrentOwnerEmployeeId, t.CurrentDepartmentId
FROM dbo.Tickets t WHERE t.TicketStatus = 6 AND t.CurrentOwnerEmployeeId IS NOT NULL;

/* C3. Late events the new guard recorded as evidence only (exist after the fix is deployed) */
SELECT a.OccurredAtUtc, a.Action, a.EntityId AS TicketId, a.AfterValue
FROM dbo.AuditEntries a
WHERE a.Action IN (N'GenesysEventIgnoredOnClosedTicket', N'TicketHumanActivityAfterClosure')
ORDER BY a.OccurredAtUtc DESC;

/* ---------- D. Genesys start time later than the conversation's own first message (stored in the wrong zone) ---------- */
SELECT i.TicketInteractionId, i.TicketId, i.GenesysConversationId, i.InteractionStartedAtUtc,
       MIN(m.SentAtUtc) AS FirstMessageAtUtc,
       DATEDIFF(MINUTE, MIN(m.SentAtUtc), i.InteractionStartedAtUtc) AS MinutesLater,
       CASE WHEN DATEDIFF(MINUTE, MIN(m.SentAtUtc), i.InteractionStartedAtUtc) BETWEEN 230 AND 250 THEN 'LIKELY +4h (UAE wall-clock stored as UTC)' ELSE 'other' END AS Pattern
FROM dbo.TicketInteractions i
JOIN dbo.TicketInteractionMessages m ON m.TicketInteractionId = i.TicketInteractionId
WHERE i.GenesysConversationId IS NOT NULL AND i.InteractionStartedAtUtc IS NOT NULL
GROUP BY i.TicketInteractionId, i.TicketId, i.GenesysConversationId, i.InteractionStartedAtUtc
HAVING i.InteractionStartedAtUtc > DATEADD(MINUTE, 2, MIN(m.SentAtUtc))
ORDER BY MinutesLater DESC;

/* D2. Start time later than the ticket that the same conversation created (a ticket cannot precede its conversation) */
SELECT i.TicketInteractionId, i.TicketId, t.CreatedAtUtc AS TicketCreatedAtUtc, i.InteractionStartedAtUtc,
       DATEDIFF(MINUTE, t.CreatedAtUtc, i.InteractionStartedAtUtc) AS MinutesAfterTicket
FROM dbo.TicketInteractions i JOIN dbo.Tickets t ON t.TicketId = i.TicketId
WHERE i.IsOriginatingInteraction = 1 AND i.GenesysConversationId IS NOT NULL AND i.InteractionStartedAtUtc > DATEADD(MINUTE, 5, t.CreatedAtUtc)
ORDER BY MinutesAfterTicket DESC;

/* D3. Messages dated after the conversation ended, or an end before the start */
SELECT i.TicketInteractionId, i.TicketId, i.InteractionStartedAtUtc, i.EndedAtUtc, MAX(m.SentAtUtc) AS LastMessageAtUtc
FROM dbo.TicketInteractions i LEFT JOIN dbo.TicketInteractionMessages m ON m.TicketInteractionId = i.TicketInteractionId
WHERE i.GenesysConversationId IS NOT NULL AND i.EndedAtUtc IS NOT NULL
GROUP BY i.TicketInteractionId, i.TicketId, i.InteractionStartedAtUtc, i.EndedAtUtc
HAVING i.EndedAtUtc < i.InteractionStartedAtUtc OR MAX(m.SentAtUtc) > DATEADD(MINUTE, 2, i.EndedAtUtc)
ORDER BY i.TicketId;

/* ---------- E. Ownership changes recorded long after the first human reply (before the event-time fix, dated at processing time) ---------- */
SELECT e.TicketId, t.TicketNumber, e.OccurredAtUtc AS TransferRecordedAtUtc, fm.FirstHumanReplyAtUtc,
       DATEDIFF(MINUTE, fm.FirstHumanReplyAtUtc, e.OccurredAtUtc) AS MinutesAfterFirstHumanReply
FROM dbo.TicketWorkflowEvents e
JOIN dbo.Tickets t ON t.TicketId = e.TicketId
CROSS APPLY (SELECT MIN(m.SentAtUtc) AS FirstHumanReplyAtUtc
               FROM dbo.TicketInteractions i JOIN dbo.TicketInteractionMessages m ON m.TicketInteractionId = i.TicketInteractionId AND m.Sender = 2
              WHERE i.TicketId = e.TicketId) fm
WHERE e.EventType = 17 AND fm.FirstHumanReplyAtUtc IS NOT NULL
  AND e.OccurredAtUtc > DATEADD(MINUTE, 5, fm.FirstHumanReplyAtUtc)
ORDER BY MinutesAfterFirstHumanReply DESC;

/* ---------- F. Assignments dated before the ticket existed ---------- */
SELECT a.TicketId, t.TicketNumber, t.CreatedAtUtc, a.AssignedAtUtc, a.AssignedEmployeeId
FROM dbo.TicketAssignments a JOIN dbo.Tickets t ON t.TicketId = a.TicketId
WHERE a.AssignedAtUtc < DATEADD(MINUTE, -1, t.CreatedAtUtc)
ORDER BY t.CreatedAtUtc DESC;
