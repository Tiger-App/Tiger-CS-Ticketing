/*
================================================================================
 Diagnose_TigerCS_PROD_StaleAiThreshold_15m_30m_1h_2h_ReadOnly.sql      (SELECT ONLY - NO WRITES)
 Database : TigerCS_Ticketing_Prod

 WHY: the 12h/24h/48h comparison showed 134 of 141 active unowned tickets eligible at 12h and 76 at 24h, but the Production Backfill Preview found
      0 eligible (all ActiveAiConversation, because CsAgentBackfill:StaleAiAfterHours is null). This script shows, WITHOUT changing anything, what much
      SHORTER thresholds - 15, 30, 60 and 120 minutes - would make eligible (12h and 24h are kept as reference rows), split by Phone vs other
      channels, so the threshold can be chosen on evidence. Nothing here proposes a value.

 SAME RULE AS THE APPLICATION (CsAgentBackfillClassifier), evaluated per ticket:
   candidate        = Status not Resolved(5)/Closed(6), no owner, no Pending approval (TicketApprovals.Status = 1), created more than @MinTicketAgeMinutes ago
   human needed     = ANY interaction with RequiresHumanFollowUp, an open handoff (TicketAgentHandoffs.Status 2/3/4), a HumanAgent transcript line, an
                      interaction-handler row or HandledByUserId, FirstHumanResponseAtUtc, or workflow events 11/12/18/19  -> ELIGIBLE NOW (not threshold-specific)
   live AI          = a Genesys interaction (Source = 2) with EndedAtUtc and InactivityClosedAtUtc both NULL, and NO human need
   last activity    = latest of ticket CreatedAtUtc, interaction CreatedAtUtc / InteractionStartedAtUtc / LastCustomerInteractionAtUtc /
                      AwaitingCustomerReplySinceUtc, transcript SentAtUtc (customer, AI and agent lines), Genesys workflow events (types 10-19)
   stale            = live AI AND (now - last activity) is MORE than the threshold (exactly the threshold is NOT stale)
   not live AI      = no Genesys interaction, or every Genesys interaction ended/closed -> eligible now (existing rule, IncludeAiEndedWithoutHuman)

 CAUTION for short thresholds: a live voice or chat conversation can legitimately be silent for many minutes (a long call, a customer who stepped away).
 Result set 4 lists the live Phone tickets with their silence so a very short threshold can be judged against calls that are still genuinely open.
 The 12h/24h rows are reference only.

 READ-ONLY: only SELECTs and session temp tables (tempdb); READ UNCOMMITTED; safe to run any time from a db_datareader login.
 Ticket numbers are listed; no customer data is selected.
================================================================================
*/
SET NOCOUNT ON;
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

DECLARE @ExpectedDb sysname = N'TigerCS_Ticketing_Prod';
IF DB_NAME() <> @ExpectedDb
BEGIN
    DECLARE @m nvarchar(400) = N'ABORTED: connected to [' + ISNULL(DB_NAME(), N'?') + N'], this diagnostic is for [' + @ExpectedDb + N'] only.';
    THROW 50501, @m, 1;
END;

DECLARE @MinTicketAgeMinutes int = 3;    -- CsAgentBackfill:MinTicketAgeMinutes
DECLARE @Now datetime2 = SYSUTCDATETIME();

-- ---------------------------------------------------------------------------------------------------------------------------
-- candidates: active, unowned, no pending approval, not brand new
-- ---------------------------------------------------------------------------------------------------------------------------
CREATE TABLE #T (TicketId bigint NOT NULL PRIMARY KEY, TicketNumber nvarchar(80) NULL, DeptId int NOT NULL, CreatedAtUtc datetime2 NOT NULL,
    FirstHumanResponseAtUtc datetime2 NULL);
INSERT #T
SELECT t.TicketId, t.TicketNumber, t.CurrentDepartmentId, t.CreatedAtUtc, t.FirstHumanResponseAtUtc
FROM dbo.Tickets t
WHERE t.TicketStatus NOT IN (5, 6)
  AND t.CurrentOwnerEmployeeId IS NULL
  AND t.CreatedAtUtc <= DATEADD(minute, -@MinTicketAgeMinutes, @Now)
  AND NOT EXISTS (SELECT 1 FROM dbo.TicketApprovals ap WHERE ap.TicketId = t.TicketId AND ap.Status = 1);

CREATE TABLE #E (TicketId bigint NOT NULL PRIMARY KEY, HasPhone bit NOT NULL, HasGenesys bit NOT NULL, HasLiveGenesys bit NOT NULL, HumanNeeded bit NOT NULL, OpenHandoff bit NOT NULL,
    LastActivityUtc datetime2 NOT NULL);
INSERT #E
SELECT t.TicketId,
       CASE WHEN EXISTS (SELECT 1 FROM dbo.TicketInteractions i WHERE i.TicketId = t.TicketId AND i.Source = 2 AND i.ChannelId = 1) THEN 1 ELSE 0 END,
       CASE WHEN EXISTS (SELECT 1 FROM dbo.TicketInteractions i WHERE i.TicketId = t.TicketId AND i.Source = 2) THEN 1 ELSE 0 END,
       CASE WHEN EXISTS (SELECT 1 FROM dbo.TicketInteractions i WHERE i.TicketId = t.TicketId AND i.Source = 2 AND i.EndedAtUtc IS NULL AND i.InactivityClosedAtUtc IS NULL) THEN 1 ELSE 0 END,
       CASE WHEN t.FirstHumanResponseAtUtc IS NOT NULL
              OR EXISTS (SELECT 1 FROM dbo.TicketInteractions i WHERE i.TicketId = t.TicketId AND (i.RequiresHumanFollowUp = 1 OR i.HandledByUserId IS NOT NULL))
              OR EXISTS (SELECT 1 FROM dbo.TicketAgentHandoffs h WHERE h.TicketId = t.TicketId AND h.Status IN (2, 3, 4))
              OR EXISTS (SELECT 1 FROM dbo.TicketInteractionHandlers h WHERE h.TicketId = t.TicketId)
              OR EXISTS (SELECT 1 FROM dbo.TicketInteractions i JOIN dbo.TicketInteractionMessages mm ON mm.TicketInteractionId = i.TicketInteractionId
                         WHERE i.TicketId = t.TicketId AND mm.Sender = 2)
              OR EXISTS (SELECT 1 FROM dbo.TicketWorkflowEvents e WHERE e.TicketId = t.TicketId AND e.EventType IN (11, 12, 18, 19))
            THEN 1 ELSE 0 END,
       CASE WHEN EXISTS (SELECT 1 FROM dbo.TicketAgentHandoffs h WHERE h.TicketId = t.TicketId AND h.Status IN (2, 3, 4)) THEN 1 ELSE 0 END,
       (SELECT MAX(v) FROM (VALUES
            (t.CreatedAtUtc),
            ((SELECT MAX(x.v) FROM dbo.TicketInteractions i
                CROSS APPLY (VALUES (i.CreatedAtUtc), (i.InteractionStartedAtUtc), (i.LastCustomerInteractionAtUtc), (i.AwaitingCustomerReplySinceUtc)) x(v)
              WHERE i.TicketId = t.TicketId)),
            ((SELECT MAX(mm.SentAtUtc) FROM dbo.TicketInteractions i JOIN dbo.TicketInteractionMessages mm ON mm.TicketInteractionId = i.TicketInteractionId
              WHERE i.TicketId = t.TicketId)),
            ((SELECT MAX(e.OccurredAtUtc) FROM dbo.TicketWorkflowEvents e WHERE e.TicketId = t.TicketId AND e.EventType BETWEEN 10 AND 19))
        ) a(v))
FROM #T t;

-- ===========================================================================================================================
-- 1. HEADLINE - what is already eligible without any stale rule, and what a stale rule would add
-- ===========================================================================================================================
SELECT @@SERVERNAME AS [Server], DB_NAME() AS [Database], @Now AS DiagnosedAtUtc,
       (SELECT COUNT(*) FROM #T) AS ActiveUnownedNoPendingApproval,
       (SELECT COUNT(*) FROM #E WHERE HumanNeeded = 1) AS HumanNeeded_EligibleNow,
       (SELECT COUNT(*) FROM #E WHERE OpenHandoff = 1) AS OfWhich_OpenHandoff,
       (SELECT COUNT(*) FROM #E WHERE HumanNeeded = 0 AND (HasGenesys = 0 OR HasLiveGenesys = 0)) AS NoLiveAi_EligibleNow,
       (SELECT COUNT(*) FROM #E WHERE HumanNeeded = 0 AND HasGenesys = 1 AND HasLiveGenesys = 1) AS LiveAi_ProtectedUnlessStale,
       (SELECT COUNT(*) FROM #E WHERE HumanNeeded = 0 AND HasLiveGenesys = 1 AND HasPhone = 1) AS OfWhich_LivePhone,
       (SELECT COUNT(*) FROM dbo.Tickets t WHERE t.TicketStatus NOT IN (5, 6) AND t.CurrentOwnerEmployeeId IS NULL
            AND EXISTS (SELECT 1 FROM dbo.TicketApprovals ap WHERE ap.TicketId = t.TicketId AND ap.Status = 1)) AS PendingApproval_AlwaysProtected;

-- ===========================================================================================================================
-- 2. COMPARISON - tickets a stale rule would ADD at each threshold (minutes), and what stays protected. 720 / 1440 = 12h / 24h reference.
-- ===========================================================================================================================
SELECT th.Minutes AS StaleAiAfterMinutes,
       (SELECT COUNT(*) FROM #E WHERE HumanNeeded = 1 OR HasGenesys = 0 OR HasLiveGenesys = 0) AS EligibleWithoutStaleRule,
       (SELECT COUNT(*) FROM #E WHERE HumanNeeded = 0 AND HasLiveGenesys = 1 AND DATEDIFF(second, LastActivityUtc, @Now) > th.Minutes * 60) AS AddedByStaleRule,
       (SELECT COUNT(*) FROM #E WHERE HumanNeeded = 0 AND HasLiveGenesys = 1 AND HasPhone = 1 AND DATEDIFF(second, LastActivityUtc, @Now) > th.Minutes * 60) AS AddedOfWhich_Phone,
       (SELECT COUNT(*) FROM #E WHERE HumanNeeded = 1 OR HasGenesys = 0 OR HasLiveGenesys = 0
            OR DATEDIFF(second, LastActivityUtc, @Now) > th.Minutes * 60) AS TotalEligible,
       (SELECT COUNT(*) FROM #E WHERE HumanNeeded = 0 AND HasLiveGenesys = 1 AND DATEDIFF(second, LastActivityUtc, @Now) <= th.Minutes * 60) AS StillProtected_RecentAi
FROM (VALUES (15), (30), (60), (120), (720), (1440)) th(Minutes)
ORDER BY th.Minutes;

-- ===========================================================================================================================
-- 3. WHERE the tickets a stale rule would add are (current department x threshold) - department and status are NOT changed by the backfill
-- ===========================================================================================================================
SELECT d.Name AS CurrentDepartment,
       SUM(CASE WHEN DATEDIFF(second, e.LastActivityUtc, @Now) > 15 * 60 THEN 1 ELSE 0 END) AS Stale_Over15m,
       SUM(CASE WHEN DATEDIFF(second, e.LastActivityUtc, @Now) > 30 * 60 THEN 1 ELSE 0 END) AS Stale_Over30m,
       SUM(CASE WHEN DATEDIFF(second, e.LastActivityUtc, @Now) > 60 * 60 THEN 1 ELSE 0 END) AS Stale_Over1h,
       SUM(CASE WHEN DATEDIFF(second, e.LastActivityUtc, @Now) > 120 * 60 THEN 1 ELSE 0 END) AS Stale_Over2h
FROM #E e JOIN #T t ON t.TicketId = e.TicketId LEFT JOIN dbo.Departments d ON d.DepartmentId = t.DeptId
WHERE e.HumanNeeded = 0 AND e.HasLiveGenesys = 1
GROUP BY d.Name
HAVING SUM(CASE WHEN DATEDIFF(second, e.LastActivityUtc, @Now) > 15 * 60 THEN 1 ELSE 0 END) > 0
ORDER BY Stale_Over15m DESC, d.Name;

-- ===========================================================================================================================
-- 4. SILENCE DISTRIBUTION of the protected live-AI tickets (a gap between buckets shows a natural threshold), Phone vs other
-- ===========================================================================================================================
SELECT b.Bucket, COUNT(e.TicketId) AS Tickets, SUM(CASE WHEN e.HasPhone = 1 THEN 1 ELSE 0 END) AS Phone
FROM (VALUES (1, N'<= 15m'), (2, N'15m - 30m'), (3, N'30m - 1h'), (4, N'1h - 2h'), (5, N'2h - 12h'), (6, N'12h - 24h'), (7, N'> 24h')) b(Ord, Bucket)
LEFT JOIN (SELECT TicketId, HasPhone, DATEDIFF(second, LastActivityUtc, @Now) / 60.0 AS M FROM #E WHERE HumanNeeded = 0 AND HasLiveGenesys = 1) e
       ON b.Ord = CASE WHEN e.M <= 15 THEN 1 WHEN e.M <= 30 THEN 2 WHEN e.M <= 60 THEN 3 WHEN e.M <= 120 THEN 4 WHEN e.M <= 720 THEN 5 WHEN e.M <= 1440 THEN 6 ELSE 7 END
GROUP BY b.Ord, b.Bucket ORDER BY b.Ord;

-- ===========================================================================================================================
-- 5. TICKET LIST for review: every ticket a 15-minute rule would add, with the thresholds that catch it (read-only)
-- ===========================================================================================================================
SELECT TOP (500) t.TicketNumber, d.Name AS CurrentDepartment, CASE WHEN e.HasPhone = 1 THEN N'Phone' ELSE N'Other' END AS GenesysChannel,
       t.CreatedAtUtc, e.LastActivityUtc,
       CAST(DATEDIFF(second, e.LastActivityUtc, @Now) / 60.0 AS decimal(12, 1)) AS SilentMinutes,
       CONCAT(N'15m',
              CASE WHEN DATEDIFF(second, e.LastActivityUtc, @Now) > 30 * 60 THEN N',30m' ELSE N'' END,
              CASE WHEN DATEDIFF(second, e.LastActivityUtc, @Now) > 60 * 60 THEN N',1h' ELSE N'' END,
              CASE WHEN DATEDIFF(second, e.LastActivityUtc, @Now) > 120 * 60 THEN N',2h' ELSE N'' END) AS CaughtByThresholds
FROM #E e JOIN #T t ON t.TicketId = e.TicketId LEFT JOIN dbo.Departments d ON d.DepartmentId = t.DeptId
WHERE e.HumanNeeded = 0 AND e.HasLiveGenesys = 1 AND DATEDIFF(second, e.LastActivityUtc, @Now) > 15 * 60
ORDER BY e.LastActivityUtc;

DROP TABLE #E;
DROP TABLE #T;
