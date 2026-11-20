/*
  Diagnose_TigerCS_PROD_Ticket1878_GenesysTimelineOwnership_ReadOnly.sql
  READ-ONLY. Every statement is a SELECT; nothing is written, updated, deleted or locked for update. Safe on Production (READ UNCOMMITTED).

  Ticket 1878 = TG-REG-20261119-0027. Reported: created 13:51:04, AI conversation start shown 17:50:26 (screenshot taken 14:30 UAE),
  conversation ended 14:13:31, marked human work + Call Center transfer + owner "Bader El Almi" at 14:13:32, Assignment History shows
  only the initial Genesys Integration row. Times below are stored UTC; the UI adds +4h once for UAE.

  Sections
    0  Which build wrote this ticket?  (deployment evidence from the database - the build itself is read from GET /health)
    1  The ticket as stored
    2  Timestamps: stored interaction start vs ticket creation, first message, audit time   -> zone / future-start evidence
    3  What the Genesys calls carried (audit rows: ingestion, anomaly, human follow-up, assignment)
    4  Ownership: current owner, assignment rows, Assignment History rows, who/why for each
    5  Human evidence: was there ANY acceptance, handler or HumanAgent line?  (an assignment is not one)
    6  Timeline-relevant workflow events and status history
    7  Closed-ticket protection: anything after closure?  (expected: empty for 1878 while it is open)
    8  Fleet-wide: other tickets with the same fingerprint (start later than creation / assigned one second after an end)
*/
SET NOCOUNT ON;
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;
DECLARE @TicketId bigint = 1878;

/* ---------- 0. Deployment evidence ---------- */
-- 0a. Migrations applied. PR #288 added AddTicketAiAgentOwner (20261118124858): present => a build containing #288 has been run against THIS database.
SELECT TOP (5) MigrationId, ProductVersion FROM dbo.__EFMigrationsHistory ORDER BY MigrationId DESC;
-- 0b. Did the #288 code run when the ticket was created? #288 writes the audit action 'AssignToAiAgent' and sets Tickets.AiAgentOwnerEmployeeId at creation
--     of an AI-first ticket. No such row for a ticket created 19 Nov => the creating request was served by a build WITHOUT #288.
SELECT a.AuditEntryId, a.Action, a.OccurredAtUtc, a.AfterValue
FROM dbo.AuditEntries a WHERE a.EntityType = 'Ticket' AND a.EntityId = CAST(@TicketId AS nvarchar(50)) AND a.Action IN ('AssignToAiAgent', 'Create')
ORDER BY a.OccurredAtUtc;
-- 0c. When did the first #288-era audit row (AssignToAiAgent) appear anywhere? Tickets created before this moment cannot have been served by #288.
SELECT MIN(OccurredAtUtc) AS FirstAssignToAiAgentUtc, COUNT(*) AS Rows FROM dbo.AuditEntries WHERE Action = 'AssignToAiAgent';

/* ---------- 1. The ticket ---------- */
SELECT t.TicketId, t.TicketNumber, t.TicketStatus, t.CreatedAtUtc, DATEADD(HOUR, 4, t.CreatedAtUtc) AS CreatedAtUae,
       t.CurrentDepartmentId, d.Name AS CurrentDepartment, t.OriginatingDepartmentId,
       t.CurrentOwnerEmployeeId, u.DisplayName AS CurrentOwner, t.AiAgentOwnerEmployeeId, t.OriginalHumanAgentId
FROM dbo.Tickets t
LEFT JOIN dbo.Departments d ON d.DepartmentId = t.CurrentDepartmentId
LEFT JOIN dbo.Employees u ON u.EmployeeId = t.CurrentOwnerEmployeeId
WHERE t.TicketId = @TicketId;

/* ---------- 2. Timestamps ---------- */
-- Stored interaction facts. InteractionStartedAtUtc is what Genesys said; CreatedAtUtc is the SERVER clock when TigerCS recorded the row.
-- Start later than CreatedAtUtc by ~4h  => the sender's wall-clock was written as UTC (or an offset-bearing value was stored as local by a pre-#288 build).
SELECT i.TicketInteractionId, i.GenesysConversationId, i.ChannelId, i.InteractionStartedAtUtc, DATEADD(HOUR, 4, i.InteractionStartedAtUtc) AS StartShownInUiUae,
       i.CreatedAtUtc AS RecordedByServerUtc, DATEDIFF(MINUTE, i.CreatedAtUtc, i.InteractionStartedAtUtc) AS StartMinusServerRecorded_Min,
       i.EndedAtUtc, i.EndReason, i.RequiresHumanFollowUp, i.HandledByUserId, i.GenesysAgentId, i.GenesysAgentUserId
FROM dbo.TicketInteractions i WHERE i.TicketId = @TicketId;

SELECT m.TicketInteractionMessageId, m.Sequence, m.Sender, m.SenderName, m.SentAtUtc, DATEADD(HOUR, 4, m.SentAtUtc) AS SentAtUae, LEFT(m.Body, 80) AS BodyStart
FROM dbo.TicketInteractionMessages m JOIN dbo.TicketInteractions i ON i.TicketInteractionId = m.TicketInteractionId
WHERE i.TicketId = @TicketId ORDER BY m.Sequence;

/* ---------- 3. What the Genesys calls recorded ---------- */
-- 'GenesysTimestampAnomaly' rows (written by the fix) carry the value AS SENT and its wire form (Utc = ...Z, Offset = ...+04:00, Zoneless = no zone).
SELECT a.AuditEntryId, a.OccurredAtUtc, a.Action, a.EntityType, a.EntityId, a.ActorEmployeeId, a.BeforeValue, a.AfterValue
FROM dbo.AuditEntries a
WHERE (a.EntityType = 'Ticket' AND a.EntityId = CAST(@TicketId AS nvarchar(50)))
   OR (a.EntityType = 'GenesysConversation' AND a.EntityId IN (SELECT GenesysConversationId FROM dbo.TicketInteractions WHERE TicketId = @TicketId))
ORDER BY a.OccurredAtUtc, a.AuditEntryId;

/* ---------- 4. Ownership ---------- */
SELECT a.TicketAssignmentId, a.AssignedEmployeeId, u.DisplayName AS AssignedTo, a.AssignedDepartmentId, a.AssignedAtUtc, a.AssigningActorEmployeeId, a.IsCurrent
FROM dbo.TicketAssignments a LEFT JOIN dbo.Employees u ON u.EmployeeId = a.AssignedEmployeeId
WHERE a.TicketId = @TicketId ORDER BY a.AssignedAtUtc, a.TicketAssignmentId;

-- Assignment History (what the UI tab shows). Source: 1 AI Agent, 2 direct call, 3 call transfer, 4 human follow-up (system, provisional), 5 manual.
-- For 1878 before the fix this returns ONLY the Source=1 row: Round Robin wrote an assignment row + audit + workflow event but no history row.
SELECT h.TicketAssignmentHistoryEntryId, h.ChangedAtUtc, h.Source, h.Kind, h.DepartmentId, pn.DisplayName AS PreviousEmployee, nn.DisplayName AS NewEmployee, h.Reason, h.GenesysConversationId
FROM dbo.TicketAssignmentHistory h
LEFT JOIN dbo.Employees pn ON pn.EmployeeId = h.PreviousEmployeeId
LEFT JOIN dbo.Employees nn ON nn.EmployeeId = h.NewEmployeeId
WHERE h.TicketId = @TicketId ORDER BY h.ChangedAtUtc, h.TicketAssignmentHistoryEntryId;

/* ---------- 5. Human evidence ---------- */
SELECT 'Handler rows (TicketInteractionHandlers)' AS Evidence, COUNT(*) AS N
FROM dbo.TicketInteractionHandlers x JOIN dbo.TicketInteractions i ON i.TicketInteractionId = x.TicketInteractionId WHERE i.TicketId = @TicketId
UNION ALL
SELECT 'HumanAgent transcript lines', COUNT(*)
FROM dbo.TicketInteractionMessages m JOIN dbo.TicketInteractions i ON i.TicketInteractionId = m.TicketInteractionId WHERE i.TicketId = @TicketId AND m.Sender = 2
UNION ALL
SELECT 'Agent handoffs (TicketAgentHandoffs)', COUNT(*) FROM dbo.TicketAgentHandoffs WHERE TicketId = @TicketId
UNION ALL
SELECT 'First human response recorded (Tickets.FirstHumanResponseAtUtc)', COUNT(*) FROM dbo.Tickets t WHERE t.TicketId = @TicketId AND t.FirstHumanResponseAtUtc IS NOT NULL;
-- Reading: all zeros while the ticket has an owner = the owner was ASSIGNED by the system, never accepted/answered. The audit row below names the trigger.
SELECT a.OccurredAtUtc, a.Action, a.AfterValue
FROM dbo.AuditEntries a
WHERE a.EntityType IN ('Ticket', 'GenesysConversation')
  AND (a.EntityId = CAST(@TicketId AS nvarchar(50)) OR a.EntityId IN (SELECT GenesysConversationId FROM dbo.TicketInteractions WHERE TicketId = @TicketId))
  AND a.Action IN ('GenesysHumanFollowUpRequired', 'Transfer', 'AutoAssign', 'AssignOnAnswer')
ORDER BY a.OccurredAtUtc;   -- 'GenesysHumanFollowUpRequired' with HumanSignal=AiEndedUnresolved = the promoted-end-event path removed by the fix

/* ---------- 6. Workflow events / status ---------- */
SELECT e.OccurredAtUtc, e.EventType, e.FromDepartmentId, e.ToDepartmentId, e.ToEmployeeId, e.Note FROM dbo.TicketWorkflowEvents e WHERE e.TicketId = @TicketId ORDER BY e.OccurredAtUtc;
SELECT h.OccurredAtUtc, h.Dimension, h.OldValue, h.NewValue, h.ActorEmployeeId, h.Note FROM dbo.TicketStatusHistory h WHERE h.TicketId = @TicketId ORDER BY h.OccurredAtUtc;

/* ---------- 7. Closed-ticket protection ---------- */
SELECT 'Closed now?' AS Question, CASE WHEN TicketStatus = 6 THEN 'YES' ELSE 'no' END AS Answer FROM dbo.Tickets WHERE TicketId = @TicketId;
SELECT a.OccurredAtUtc, a.Action, a.AfterValue FROM dbo.AuditEntries a
WHERE a.EntityType = 'Ticket' AND a.EntityId = CAST(@TicketId AS nvarchar(50)) AND a.Action = 'GenesysEventIgnoredOnClosedTicket';

/* ---------- 8. Fleet-wide fingerprints ---------- */
-- 8a. Genesys interactions whose recorded start is later than the server clock when the row was recorded (an event cannot start in the future).
SELECT TOP (200) i.TicketId, t.TicketNumber, i.GenesysConversationId, i.InteractionStartedAtUtc, i.CreatedAtUtc,
       DATEDIFF(MINUTE, i.CreatedAtUtc, i.InteractionStartedAtUtc) AS MinutesAhead
FROM dbo.TicketInteractions i JOIN dbo.Tickets t ON t.TicketId = i.TicketId
WHERE i.GenesysConversationId IS NOT NULL AND i.InteractionStartedAtUtc > DATEADD(MINUTE, 5, i.CreatedAtUtc)
ORDER BY i.CreatedAtUtc DESC;
-- 8b. Tickets given to a person by the system within 5 seconds of the AI conversation ending, with no human evidence at all (the AiEndedUnresolved pattern).
SELECT TOP (200) t.TicketId, t.TicketNumber, i.EndedAtUtc, a.AssignedAtUtc, a.AssignedEmployeeId
FROM dbo.Tickets t
JOIN dbo.TicketInteractions i ON i.TicketId = t.TicketId AND i.GenesysConversationId IS NOT NULL AND i.EndedAtUtc IS NOT NULL
JOIN dbo.TicketAssignments a ON a.TicketId = t.TicketId AND a.AssigningActorEmployeeId IS NULL AND a.AssignedAtUtc BETWEEN i.EndedAtUtc AND DATEADD(SECOND, 5, i.EndedAtUtc)
WHERE i.HandledByUserId IS NULL
  AND NOT EXISTS (SELECT 1 FROM dbo.TicketInteractionMessages m WHERE m.TicketInteractionId = i.TicketInteractionId AND m.Sender = 2)
  AND EXISTS (SELECT 1 FROM dbo.AuditEntries x WHERE x.EntityType = 'GenesysConversation' AND x.EntityId = i.GenesysConversationId
              AND x.Action = 'GenesysHumanFollowUpRequired' AND x.AfterValue LIKE '%HumanSignal=AiEndedUnresolved%')
ORDER BY a.AssignedAtUtc DESC;
