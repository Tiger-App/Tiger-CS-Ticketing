/*
================================================================================
 Diagnose_TigerCS_PROD_PhoneEndAuditChannel_ReadOnly.sql          (SELECT ONLY - NO WRITES)
 Database : TigerCS_Ticketing_Prod

 WHY: the phone end-event diagnostic showed that Phone end events almost never arrive (week of 2026-11-02: 192 phone interactions, 1 ended) although
      'GenesysConversationEnded' audit rows exist. This script answers, from the database only:
   1  Do the GenesysConversationEnded audit rows belong to PHONE or CHAT conversations (or to no interaction at all)?
   2  For phone calls created each day (last 14 days): how many reached TigerCS with a routing PATCH, with an end audit, and ended?
        - phone calls WITH a routing PATCH audit but no end  = the workflow RAN but took the 'Update Routing' branch (disconnectType empty) or End failed
        - phone calls with NEITHER                           = no PATCH reached TigerCS at all (trigger/workflow/proxy/auth) or the call never got that far
   3  The phone calls that DID end (reason, duration, whether an end audit row exists): what a working end looks like
   4  Consistency: end audits whose interaction is not marked ended, and ended interactions without an audit row (an end that did not go through PATCH)

 Same evidence rules as Diagnose_TigerCS_PROD_PhoneEndEvents_ReadOnly.sql. READ-ONLY: SELECT only (CTEs), READ UNCOMMITTED, no customer data selected.
 Aggregates run over plain columns (no aggregate over a subquery: SQL Server Msg 130).
================================================================================
*/
SET NOCOUNT ON;
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

DECLARE @ExpectedDb sysname = N'TigerCS_Ticketing_Prod';
IF DB_NAME() <> @ExpectedDb
BEGIN
    DECLARE @m nvarchar(400) = N'ABORTED: connected to [' + ISNULL(DB_NAME(), N'?') + N'], this diagnostic is for [' + @ExpectedDb + N'] only.';
    THROW 50530, @m, 1;
END;

DECLARE @Now datetime2 = SYSUTCDATETIME();

-- ---------------------------------------------------------------------------------------------------------------------------
-- 1. End audits by day x channel (last 30 days). One row per conversation (first end audit).
-- ---------------------------------------------------------------------------------------------------------------------------
;WITH EndAudit AS (
    SELECT a.EntityId AS ConversationId, MIN(a.OccurredAtUtc) AS FirstEndAuditUtc, COUNT(*) AS AuditRows
    FROM dbo.AuditEntries a WHERE a.Action = N'GenesysConversationEnded' GROUP BY a.EntityId
), Conv AS (
    SELECT i.GenesysConversationId AS ConversationId, MAX(CASE WHEN i.ChannelId = 1 THEN 1 ELSE 0 END) AS IsPhone
    FROM dbo.TicketInteractions i WHERE i.Source = 2 AND i.GenesysConversationId IS NOT NULL GROUP BY i.GenesysConversationId
)
SELECT CAST(e.FirstEndAuditUtc AS date) AS [Day],
       CASE WHEN c.ConversationId IS NULL THEN N'No matching interaction' WHEN c.IsPhone = 1 THEN N'Phone' ELSE N'Chat/Other' END AS Channel,
       COUNT(*) AS Conversations, SUM(e.AuditRows) AS AuditRows
FROM EndAudit e LEFT JOIN Conv c ON c.ConversationId = e.ConversationId
WHERE e.FirstEndAuditUtc >= DATEADD(day, -30, @Now)
GROUP BY CAST(e.FirstEndAuditUtc AS date),
         CASE WHEN c.ConversationId IS NULL THEN N'No matching interaction' WHEN c.IsPhone = 1 THEN N'Phone' ELSE N'Chat/Other' END
ORDER BY [Day] DESC, Channel;

-- all-time split, same rule
;WITH EndAudit AS (
    SELECT a.EntityId AS ConversationId FROM dbo.AuditEntries a WHERE a.Action = N'GenesysConversationEnded' GROUP BY a.EntityId
), Conv AS (
    SELECT i.GenesysConversationId AS ConversationId, MAX(CASE WHEN i.ChannelId = 1 THEN 1 ELSE 0 END) AS IsPhone
    FROM dbo.TicketInteractions i WHERE i.Source = 2 AND i.GenesysConversationId IS NOT NULL GROUP BY i.GenesysConversationId
)
SELECT CASE WHEN c.ConversationId IS NULL THEN N'No matching interaction' WHEN c.IsPhone = 1 THEN N'Phone' ELSE N'Chat/Other' END AS Channel, COUNT(*) AS ConversationsEverEndedViaPatch
FROM EndAudit e LEFT JOIN Conv c ON c.ConversationId = e.ConversationId
GROUP BY CASE WHEN c.ConversationId IS NULL THEN N'No matching interaction' WHEN c.IsPhone = 1 THEN N'Phone' ELSE N'Chat/Other' END
ORDER BY Channel;

-- ---------------------------------------------------------------------------------------------------------------------------
-- 2. Phone calls by creation day (last 14 days): did ANY PATCH reach TigerCS for them?
-- ---------------------------------------------------------------------------------------------------------------------------
;WITH RoutingAudit AS (
    SELECT a.EntityId AS ConversationId FROM dbo.AuditEntries a WHERE a.Action = N'GenesysRoutingChanged' GROUP BY a.EntityId
), EndAudit AS (
    SELECT a.EntityId AS ConversationId FROM dbo.AuditEntries a WHERE a.Action = N'GenesysConversationEnded' GROUP BY a.EntityId
), Phone AS (
    SELECT i.TicketInteractionId, CAST(i.CreatedAtUtc AS date) AS CreatedDay, i.GenesysConversationId, i.EndedAtUtc, i.InactivityClosedAtUtc,
           CASE WHEN i.GenesysQueueId IS NOT NULL THEN 1 ELSE 0 END AS HasQueue,
           CASE WHEN NULLIF(LTRIM(RTRIM(i.GenesysAgentId)), N'') IS NOT NULL THEN 1 ELSE 0 END AS HasAgentId
    FROM dbo.TicketInteractions i
    WHERE i.Source = 2 AND i.ChannelId = 1 AND i.CreatedAtUtc >= DATEADD(day, -14, @Now)
)
SELECT p.CreatedDay,
       COUNT(*) AS PhoneInteractions,
       SUM(CASE WHEN p.EndedAtUtc IS NOT NULL THEN 1 ELSE 0 END) AS Ended,
       SUM(CASE WHEN r.ConversationId IS NOT NULL THEN 1 ELSE 0 END) AS WithRoutingPatchAudit,
       SUM(CASE WHEN e.ConversationId IS NOT NULL THEN 1 ELSE 0 END) AS WithEndPatchAudit,
       SUM(CASE WHEN p.EndedAtUtc IS NULL AND r.ConversationId IS NOT NULL THEN 1 ELSE 0 END) AS NotEnded_ButRoutingPatchArrived,
       SUM(CASE WHEN p.EndedAtUtc IS NULL AND r.ConversationId IS NULL AND e.ConversationId IS NULL THEN 1 ELSE 0 END) AS NotEnded_NoPatchEvidenceAtAll,
       SUM(p.HasQueue) AS WithQueue, SUM(p.HasAgentId) AS WithGenesysAgentId
FROM Phone p
LEFT JOIN RoutingAudit r ON r.ConversationId = p.GenesysConversationId
LEFT JOIN EndAudit e ON e.ConversationId = p.GenesysConversationId
GROUP BY p.CreatedDay ORDER BY p.CreatedDay DESC;

-- ---------------------------------------------------------------------------------------------------------------------------
-- 3. The phone calls that DID end: what a working end looks like (most recent 30)
-- ---------------------------------------------------------------------------------------------------------------------------
;WITH EndAudit AS (
    SELECT a.EntityId AS ConversationId, MIN(a.OccurredAtUtc) AS FirstEndAuditUtc FROM dbo.AuditEntries a WHERE a.Action = N'GenesysConversationEnded' GROUP BY a.EntityId
)
SELECT TOP (30) i.TicketInteractionId, i.GenesysConversationId, i.CreatedAtUtc, i.InteractionStartedAtUtc, i.EndedAtUtc,
       ISNULL(NULLIF(LTRIM(RTRIM(i.EndReason)), N''), N'(blank)') AS EndReason,
       CAST(DATEDIFF(second, COALESCE(i.InteractionStartedAtUtc, i.CreatedAtUtc), i.EndedAtUtc) / 60.0 AS decimal(12,1)) AS MinutesStartToEnd,
       e.FirstEndAuditUtc, CASE WHEN e.ConversationId IS NULL THEN 0 ELSE 1 END AS HasEndAuditRow,
       CASE WHEN NULLIF(LTRIM(RTRIM(i.GenesysAgentId)), N'') IS NULL THEN 0 ELSE 1 END AS HasAgentId, i.GenesysQueueName
FROM dbo.TicketInteractions i LEFT JOIN EndAudit e ON e.ConversationId = i.GenesysConversationId
WHERE i.Source = 2 AND i.ChannelId = 1 AND i.EndedAtUtc IS NOT NULL
ORDER BY i.EndedAtUtc DESC;

-- ---------------------------------------------------------------------------------------------------------------------------
-- 4. Consistency between the audit trail and the interaction rows (all channels, split)
-- ---------------------------------------------------------------------------------------------------------------------------
;WITH EndAudit AS (
    SELECT a.EntityId AS ConversationId FROM dbo.AuditEntries a WHERE a.Action = N'GenesysConversationEnded' GROUP BY a.EntityId
)
SELECT CASE WHEN i.ChannelId = 1 THEN N'Phone' ELSE N'Chat/Other' END AS Channel,
       SUM(CASE WHEN i.EndedAtUtc IS NOT NULL AND e.ConversationId IS NOT NULL THEN 1 ELSE 0 END) AS Ended_WithAudit,
       SUM(CASE WHEN i.EndedAtUtc IS NOT NULL AND e.ConversationId IS NULL THEN 1 ELSE 0 END) AS Ended_WithoutAudit,
       SUM(CASE WHEN i.EndedAtUtc IS NULL AND e.ConversationId IS NOT NULL THEN 1 ELSE 0 END) AS AuditButNotEnded_SiblingTicketLikely
FROM dbo.TicketInteractions i LEFT JOIN EndAudit e ON e.ConversationId = i.GenesysConversationId
WHERE i.Source = 2
GROUP BY CASE WHEN i.ChannelId = 1 THEN N'Phone' ELSE N'Chat/Other' END
ORDER BY Channel;
