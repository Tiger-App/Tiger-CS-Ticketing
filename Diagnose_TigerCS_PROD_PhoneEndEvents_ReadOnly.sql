/*
================================================================================
 Diagnose_TigerCS_PROD_PhoneEndEvents_ReadOnly.sql          (SELECT ONLY - NO WRITES)
 Database : TigerCS_Ticketing_Prod

 WHY: the ACTIVE_AI diagnostic found 181 held-back tickets, ALL with a live PHONE Genesys interaction and no recorded end event. TigerCS records an end only when
      Genesys sends  PATCH /api/genesys/tickets/{ticketId}  with an "ended" part  { "conversationId": "...", "ended": { "endedAtUtc": "...", "endReason": "..." } }
      (data action 04 "TigerCS - End Conversation", fired by the Genesys workflow "TigerCS - Conversation Event" on the trigger topic
       v2.detail.events.conversation.{id}.customer.end). A rejected PATCH leaves NO row in TigerCS (it is answered 4xx and not stored), so the database can show only
      the absence of an end. This script separates the possible causes from the evidence the database does hold, and (section F) compares TigerCS with an EXPORT FROM GENESYS.

 READ-ONLY: SELECTs and table variables / session temp tables only; READ UNCOMMITTED; safe from a db_datareader login. No customer phone / name / e-mail is selected.

 SECTIONS
   A  End-event rate by week and channel     did end events EVER arrive for phone calls, and since when do they stop?
   B  Audit trail by day (last 45 days)      GenesysInquiryIngested vs GenesysRoutingChanged vs GenesysConversationEnded: is Genesys calling PATCH at all?
   C  What ended calls look like             EndReason values Genesys sends, and the call duration TigerCS recorded
   D  Un-ended phone calls: other PATCH evidence   queue / agent / routing audit present or absent, ended vs un-ended
   E  The 181 (ACTIVE_AI set): one row per live phone interaction, with sibling interactions of the same conversation
   F  Genesys export match                   paste the Genesys conversation-details export into @GenesysExport (below) to learn which are really still connected
================================================================================
*/
SET NOCOUNT ON;
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

DECLARE @ExpectedDb sysname = N'TigerCS_Ticketing_Prod';
IF DB_NAME() <> @ExpectedDb
BEGIN
    DECLARE @m nvarchar(400) = N'ABORTED: connected to [' + ISNULL(DB_NAME(), N'?') + N'], this diagnostic is for [' + @ExpectedDb + N'] only.';
    THROW 50520, @m, 1;
END;

DECLARE @Now datetime2 = SYSUTCDATETIME();
DECLARE @MinTicketAgeMinutes int = 3;
DECLARE @ColId int = (SELECT DepartmentId FROM dbo.Departments WHERE Code = N'COL');

-- ============================================================================================================================
-- F. PASTE THE GENESYS EXPORT HERE (one INSERT row per conversation; leave the list empty to skip section F).
--    Source: Genesys Cloud > Performance > Interactions (or Analytics conversation-detail query) for the conversation ids listed by section E.
--    ConversationEndedUtc = the conversation's end time (UTC) as Genesys reports it; leave NULL when Genesys shows the conversation as still connected.
--    DisconnectType = Genesys' disconnect type (peer / endpoint / system / transfer ...), optional.
-- ============================================================================================================================
DECLARE @GenesysExport TABLE (ConversationId nvarchar(64) COLLATE DATABASE_DEFAULT NOT NULL PRIMARY KEY, ConversationEndedUtc datetime2 NULL, DisconnectType nvarchar(50) COLLATE DATABASE_DEFAULT NULL);
-- INSERT @GenesysExport (ConversationId, ConversationEndedUtc, DisconnectType) VALUES
--     (N'00000000-0000-0000-0000-000000000000', '2026-11-04T10:15:30', N'peer'),
--     (N'00000000-0000-0000-0000-000000000001', NULL, NULL);          -- still connected in Genesys

-- ---------------------------------------------------------------------------------------------------------------------------
-- A. End-event rate by week and channel (all Genesys interactions, last 16 weeks)
-- ---------------------------------------------------------------------------------------------------------------------------
SELECT DATEADD(day, -(DATEDIFF(day, '19000101', i.CreatedAtUtc) % 7), CAST(i.CreatedAtUtc AS date)) AS WeekStarting,
       CASE WHEN i.ChannelId = 1 THEN N'Phone' ELSE N'Chat/Other' END AS Channel,
       COUNT(*) AS Interactions,
       SUM(CASE WHEN i.EndedAtUtc IS NOT NULL THEN 1 ELSE 0 END) AS Ended,
       SUM(CASE WHEN i.EndedAtUtc IS NULL AND i.InactivityClosedAtUtc IS NOT NULL THEN 1 ELSE 0 END) AS ClosedForInactivityOnly,
       SUM(CASE WHEN i.EndedAtUtc IS NULL AND i.InactivityClosedAtUtc IS NULL THEN 1 ELSE 0 END) AS NoEndRecorded,
       CAST(100.0 * SUM(CASE WHEN i.EndedAtUtc IS NOT NULL THEN 1 ELSE 0 END) / NULLIF(COUNT(*), 0) AS decimal(5,1)) AS PctEnded
FROM dbo.TicketInteractions i
WHERE i.Source = 2 AND i.CreatedAtUtc >= DATEADD(week, -16, @Now)
GROUP BY DATEADD(day, -(DATEDIFF(day, '19000101', i.CreatedAtUtc) % 7), CAST(i.CreatedAtUtc AS date)), CASE WHEN i.ChannelId = 1 THEN N'Phone' ELSE N'Chat/Other' END
ORDER BY WeekStarting DESC, Channel;

-- ---------------------------------------------------------------------------------------------------------------------------
-- B. Audit trail by day: is Genesys reaching TigerCS with PATCH at all? (ingest = POST, routing/ended = PATCH parts)
-- ---------------------------------------------------------------------------------------------------------------------------
SELECT CAST(a.OccurredAtUtc AS date) AS [Day],
       SUM(CASE WHEN a.Action = N'GenesysInquiryIngested' THEN 1 ELSE 0 END) AS TicketsCreated_POST,
       SUM(CASE WHEN a.Action = N'GenesysRoutingChanged' THEN 1 ELSE 0 END) AS RoutingChanged_PATCH,
       SUM(CASE WHEN a.Action = N'GenesysInteractionHandlerRecorded' OR a.Action = N'GenesysInteractionHandlerAdded' THEN 1 ELSE 0 END) AS AgentRecorded_PATCH,
       SUM(CASE WHEN a.Action = N'GenesysTranscriptAppended' THEN 1 ELSE 0 END) AS TranscriptAppended_PATCH,
       SUM(CASE WHEN a.Action = N'GenesysConversationEnded' THEN 1 ELSE 0 END) AS ConversationEnded_PATCH
FROM dbo.AuditEntries a
WHERE a.OccurredAtUtc >= DATEADD(day, -45, @Now)
  AND a.Action IN (N'GenesysInquiryIngested', N'GenesysRoutingChanged', N'GenesysInteractionHandlerRecorded', N'GenesysInteractionHandlerAdded', N'GenesysTranscriptAppended', N'GenesysConversationEnded')
GROUP BY CAST(a.OccurredAtUtc AS date) ORDER BY [Day] DESC;

SELECT N'Last ever' AS Measure,
       (SELECT MAX(OccurredAtUtc) FROM dbo.AuditEntries WHERE Action = N'GenesysInquiryIngested') AS LastTicketCreated,
       (SELECT MAX(OccurredAtUtc) FROM dbo.AuditEntries WHERE Action = N'GenesysRoutingChanged') AS LastRoutingChanged,
       (SELECT MAX(OccurredAtUtc) FROM dbo.AuditEntries WHERE Action = N'GenesysConversationEnded') AS LastConversationEnded,
       (SELECT MAX(EndedAtUtc) FROM dbo.TicketInteractions WHERE Source = 2 AND ChannelId = 1) AS LastPhoneEndedAtUtc,
       (SELECT MAX(EndedAtUtc) FROM dbo.TicketInteractions WHERE Source = 2 AND ChannelId <> 1) AS LastChatEndedAtUtc;

-- ---------------------------------------------------------------------------------------------------------------------------
-- C. What the ended calls look like: the reasons Genesys sends and the recorded duration
-- ---------------------------------------------------------------------------------------------------------------------------
SELECT CASE WHEN i.ChannelId = 1 THEN N'Phone' ELSE N'Chat/Other' END AS Channel, ISNULL(NULLIF(LTRIM(RTRIM(i.EndReason)), N''), N'(blank)') AS EndReason, COUNT(*) AS Interactions,
       CAST(AVG(CAST(DATEDIFF(second, COALESCE(i.InteractionStartedAtUtc, i.CreatedAtUtc), i.EndedAtUtc) AS float)) / 60.0 AS decimal(12,1)) AS AvgMinutesStartToEnd,
       SUM(CASE WHEN i.EndedAtUtc < COALESCE(i.InteractionStartedAtUtc, i.CreatedAtUtc) THEN 1 ELSE 0 END) AS EndBeforeStart_Anomaly
FROM dbo.TicketInteractions i WHERE i.Source = 2 AND i.EndedAtUtc IS NOT NULL
GROUP BY CASE WHEN i.ChannelId = 1 THEN N'Phone' ELSE N'Chat/Other' END, ISNULL(NULLIF(LTRIM(RTRIM(i.EndReason)), N''), N'(blank)')
ORDER BY Channel, Interactions DESC;

-- ---------------------------------------------------------------------------------------------------------------------------
-- D. Phone calls: ended vs not ended, and what other PATCH-borne evidence they carry
-- ---------------------------------------------------------------------------------------------------------------------------
-- The per-call flags (which use subqueries) are computed first in a derived table; the outer query only aggregates plain columns
-- (SQL Server rejects an aggregate over an expression that contains a subquery, Msg 130).
SELECT x.PhoneCallState,
       COUNT(*) AS Interactions,
       SUM(x.HasQueue) AS WithQueue,
       SUM(x.HasGenesysAgentId) AS WithGenesysAgentId,
       SUM(x.HasMappedHandler) AS WithMappedHandler,
       SUM(x.HasRoutingPatchAudit) AS WithRoutingPatchAudit,
       SUM(x.HasTranscript) AS WithTranscript
FROM (
    SELECT CASE WHEN i.EndedAtUtc IS NOT NULL THEN N'Ended' WHEN i.InactivityClosedAtUtc IS NOT NULL THEN N'ClosedForInactivity' ELSE N'NoEndRecorded' END AS PhoneCallState,
           CASE WHEN i.GenesysQueueId IS NOT NULL THEN 1 ELSE 0 END AS HasQueue,
           CASE WHEN NULLIF(LTRIM(RTRIM(i.GenesysAgentId)), N'') IS NOT NULL THEN 1 ELSE 0 END AS HasGenesysAgentId,
           CASE WHEN i.HandledByUserId IS NOT NULL THEN 1 ELSE 0 END AS HasMappedHandler,
           CASE WHEN EXISTS (SELECT 1 FROM dbo.AuditEntries a WHERE a.Action = N'GenesysRoutingChanged' AND a.EntityId = i.GenesysConversationId) THEN 1 ELSE 0 END AS HasRoutingPatchAudit,
           CASE WHEN EXISTS (SELECT 1 FROM dbo.TicketInteractionMessages mm WHERE mm.TicketInteractionId = i.TicketInteractionId) THEN 1 ELSE 0 END AS HasTranscript
    FROM dbo.TicketInteractions i WHERE i.Source = 2 AND i.ChannelId = 1
) x
GROUP BY x.PhoneCallState
ORDER BY x.PhoneCallState;

-- ---------------------------------------------------------------------------------------------------------------------------
-- E. The ACTIVE_AI set (same definition as the backfill and Diagnose_TigerCS_PROD_BackfillActiveAi_ReadOnly.sql)
-- ---------------------------------------------------------------------------------------------------------------------------
CREATE TABLE #S (TicketId bigint NOT NULL PRIMARY KEY, TicketNumber nvarchar(80) NULL, TicketStatus tinyint NOT NULL, DeptId int NOT NULL, CreatedAtUtc datetime2 NOT NULL);
INSERT #S
SELECT t.TicketId, t.TicketNumber, t.TicketStatus, t.CurrentDepartmentId, t.CreatedAtUtc
FROM dbo.Tickets t
WHERE t.TicketStatus BETWEEN 1 AND 4 AND t.CurrentOwnerEmployeeId IS NULL AND t.CurrentDepartmentId <> ISNULL(@ColId, -1)
  AND t.CreatedAtUtc <= DATEADD(minute, -@MinTicketAgeMinutes, @Now)
  AND NOT EXISTS (SELECT 1 FROM dbo.TicketApprovals ap WHERE ap.TicketId = t.TicketId AND ap.Status = 1)
  AND EXISTS (SELECT 1 FROM dbo.TicketInteractions i WHERE i.TicketId = t.TicketId AND i.Source = 2 AND i.EndedAtUtc IS NULL AND i.InactivityClosedAtUtc IS NULL)
  AND NOT (   EXISTS (SELECT 1 FROM dbo.TicketInteractions i WHERE i.TicketId = t.TicketId AND i.RequiresHumanFollowUp = 1)
           OR EXISTS (SELECT 1 FROM dbo.TicketAgentHandoffs h WHERE h.TicketId = t.TicketId AND h.Status IN (2, 3, 4))
           OR t.FirstHumanResponseAtUtc IS NOT NULL
           OR EXISTS (SELECT 1 FROM dbo.TicketInteractions i JOIN dbo.TicketInteractionMessages m ON m.TicketInteractionId = i.TicketInteractionId WHERE i.TicketId = t.TicketId AND m.Sender = 2)
           OR EXISTS (SELECT 1 FROM dbo.TicketInteractionHandlers h WHERE h.TicketId = t.TicketId)
           OR EXISTS (SELECT 1 FROM dbo.TicketInteractions i WHERE i.TicketId = t.TicketId AND i.HandledByUserId IS NOT NULL)
           OR EXISTS (SELECT 1 FROM dbo.TicketWorkflowEvents ev WHERE ev.TicketId = t.TicketId AND ev.EventType IN (11, 12, 18, 19))
           OR EXISTS (SELECT 1 FROM dbo.TicketInteractions i WHERE i.TicketId = t.TicketId AND i.Source = 2 AND i.ChannelId = 1
                      AND NULLIF(LTRIM(RTRIM(i.GenesysAgentId)), N'') IS NOT NULL AND i.HandledByUserId IS NULL));

SELECT (SELECT COUNT(*) FROM #S) AS ActiveAiTickets,
       (SELECT COUNT(*) FROM #S s JOIN dbo.TicketInteractions i ON i.TicketId = s.TicketId AND i.Source = 2 AND i.EndedAtUtc IS NULL AND i.InactivityClosedAtUtc IS NULL) AS LiveInteractions,
       (SELECT COUNT(DISTINCT i.GenesysConversationId) FROM #S s JOIN dbo.TicketInteractions i ON i.TicketId = s.TicketId AND i.Source = 2 AND i.EndedAtUtc IS NULL AND i.InactivityClosedAtUtc IS NULL) AS DistinctConversations,
       (SELECT COUNT(*) FROM #S s JOIN dbo.TicketInteractions i ON i.TicketId = s.TicketId AND i.Source = 2 AND i.EndedAtUtc IS NULL AND i.InactivityClosedAtUtc IS NULL
          WHERE i.GenesysConversationId IS NULL) AS LiveInteractionsWithoutConversationId,
       -- hypothesis "the end was applied to a sibling ticket of the same conversation": a sibling interaction of the same conversation IS ended
       (SELECT COUNT(*) FROM #S s JOIN dbo.TicketInteractions i ON i.TicketId = s.TicketId AND i.Source = 2 AND i.EndedAtUtc IS NULL AND i.InactivityClosedAtUtc IS NULL
          WHERE EXISTS (SELECT 1 FROM dbo.TicketInteractions o WHERE o.GenesysConversationId = i.GenesysConversationId AND o.TicketInteractionId <> i.TicketInteractionId AND o.EndedAtUtc IS NOT NULL)) AS WithAnEndedSiblingInteraction;

SELECT TOP (1000) s.TicketNumber, d.Code AS CurrentDepartment, i.TicketInteractionId, i.GenesysConversationId, i.GenesysServiceRequestId, i.CreatedAtUtc, i.InteractionStartedAtUtc,
       CAST(DATEDIFF(minute, COALESCE(i.InteractionStartedAtUtc, i.CreatedAtUtc), @Now) / 60.0 AS decimal(12,1)) AS AgeHours,
       i.GenesysQueueName, CASE WHEN NULLIF(LTRIM(RTRIM(i.GenesysAgentId)), N'') IS NULL THEN 0 ELSE 1 END AS HasAgentId, i.Direction,
       (SELECT COUNT(*) FROM dbo.TicketInteractions o WHERE o.GenesysConversationId = i.GenesysConversationId AND o.TicketInteractionId <> i.TicketInteractionId) AS SiblingInteractions,
       (SELECT COUNT(*) FROM dbo.TicketInteractions o WHERE o.GenesysConversationId = i.GenesysConversationId AND o.TicketInteractionId <> i.TicketInteractionId AND o.EndedAtUtc IS NOT NULL) AS SiblingsEnded,
       (SELECT COUNT(*) FROM dbo.AuditEntries a WHERE a.Action = N'GenesysConversationEnded' AND a.EntityId = i.GenesysConversationId) AS EndAuditRows
FROM #S s JOIN dbo.Departments d ON d.DepartmentId = s.DeptId
JOIN dbo.TicketInteractions i ON i.TicketId = s.TicketId AND i.Source = 2 AND i.EndedAtUtc IS NULL AND i.InactivityClosedAtUtc IS NULL
ORDER BY AgeHours DESC, s.TicketId;

-- ---------------------------------------------------------------------------------------------------------------------------
-- F. Compare with the Genesys export (nothing is shown when @GenesysExport is empty)
-- ---------------------------------------------------------------------------------------------------------------------------
IF EXISTS (SELECT 1 FROM @GenesysExport)
BEGIN
    SELECT CASE
             WHEN i.TicketInteractionId IS NULL                                  THEN 'NOT_IN_TIGERCS'
             WHEN x.ConversationEndedUtc IS NULL                                 THEN 'GENESYS_STILL_CONNECTED (keep protected)'
             WHEN i.EndedAtUtc IS NULL                                           THEN 'GENESYS_ENDED_TIGERCS_MISSING_END (end event never recorded: replay candidate)'
             ELSE 'BOTH_ENDED' END AS Verdict,
           COUNT(*) AS Conversations
    FROM @GenesysExport x
    LEFT JOIN dbo.TicketInteractions i ON i.GenesysConversationId = x.ConversationId AND i.Source = 2
    GROUP BY CASE
             WHEN i.TicketInteractionId IS NULL                                  THEN 'NOT_IN_TIGERCS'
             WHEN x.ConversationEndedUtc IS NULL                                 THEN 'GENESYS_STILL_CONNECTED (keep protected)'
             WHEN i.EndedAtUtc IS NULL                                           THEN 'GENESYS_ENDED_TIGERCS_MISSING_END (end event never recorded: replay candidate)'
             ELSE 'BOTH_ENDED' END;

    SELECT x.ConversationId, t.TicketNumber, i.TicketInteractionId, i.GenesysServiceRequestId, x.DisconnectType, x.ConversationEndedUtc AS GenesysEndedUtc, i.EndedAtUtc AS TigerCsEndedUtc,
           CASE WHEN x.ConversationEndedUtc IS NOT NULL AND i.EndedAtUtc IS NOT NULL THEN DATEDIFF(second, x.ConversationEndedUtc, i.EndedAtUtc) END AS TigerCsMinusGenesysSeconds,
           CASE WHEN i.TicketInteractionId IS NULL THEN 'NOT_IN_TIGERCS'
                WHEN x.ConversationEndedUtc IS NULL THEN 'GENESYS_STILL_CONNECTED'
                WHEN i.EndedAtUtc IS NULL THEN 'REPLAY_CANDIDATE'
                ELSE 'BOTH_ENDED' END AS Verdict
    FROM @GenesysExport x
    LEFT JOIN dbo.TicketInteractions i ON i.GenesysConversationId = x.ConversationId AND i.Source = 2
    LEFT JOIN dbo.Tickets t ON t.TicketId = i.TicketId
    ORDER BY Verdict, x.ConversationId;

    -- tickets of the ACTIVE_AI set that the export does not cover: cannot be verified, stay protected
    SELECT s.TicketNumber, i.GenesysConversationId, N'NOT_IN_EXPORT (unverified, stays protected)' AS Verdict
    FROM #S s JOIN dbo.TicketInteractions i ON i.TicketId = s.TicketId AND i.Source = 2 AND i.EndedAtUtc IS NULL AND i.InactivityClosedAtUtc IS NULL
    WHERE NOT EXISTS (SELECT 1 FROM @GenesysExport x WHERE x.ConversationId = i.GenesysConversationId);
END
ELSE
    SELECT N'Section F skipped: @GenesysExport is empty. Fill it from the Genesys export of the conversation ids listed in section E.' AS Note;

DROP TABLE #S;
