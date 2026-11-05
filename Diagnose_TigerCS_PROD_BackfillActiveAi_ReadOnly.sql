/*
================================================================================
 Diagnose_TigerCS_PROD_BackfillActiveAi_ReadOnly.sql          (SELECT ONLY - NO WRITES)
 Database : TigerCS_Ticketing_Prod

 WHY: the historical-backfill dry run held back 181 active, unowned tickets as ACTIVE_AI (a Genesys interaction with no EndedAtUtc and no InactivityClosedAtUtc,
      and no human evidence). This script answers, for exactly that set, whether the conversations are genuinely live or whether their END EVENT NEVER ARRIVED.
      The application cannot ask Genesys for a conversation's state (Genesys only calls TigerCS), so the evidence is what TigerCS stored: timestamps, transcript
      lines and Genesys workflow events.

 Nothing is changed: SELECT only, READ UNCOMMITTED, session temp tables in tempdb. Safe from a db_datareader login at any time.
 No customer name / phone / e-mail is selected. Genesys conversation ids ARE listed (result 6) so each can be checked in Genesys.

 SET = the backfill's ACTIVE_AI class (Backfill_TigerCS_PUBLISHED_ClassificationAndAssignment.sql, @Asg): status Open..PendingThirdParty, no owner, not Collections,
       no pending approval, older than 3 minutes, a Genesys interaction (Source = 2) that is still "live", and NO human evidence (follow-up flag, open handoff,
       first human response, HumanAgent line, handler row, handled-by user, human workflow event 11/12/18/19, unmapped phone pick-up).

 RESULT SETS
   1  Reconciliation          size of the set (must equal the dry run's 181) and a split by status / channel
   2  Do end events arrive?   for ALL Genesys interactions by channel and age: ended / closed for inactivity / still live. If old Phone calls are ~all ended, the
                              end endpoint works and these tickets are the exception; if old calls are ~all live, end events do not reach TigerCS for that channel.
   3  Did the jobs ever run?  last recorded end event, last inactivity close, last 'AutoCloseCustomerInactivity' audit row
   4  Cause of each ticket    GENUINELY_RECENT | CLOSE_RULE_OVERDUE | EXCLUDED_CALL_CENTER | NO_CLOCK_NO_END_EVENT  (definitions below)
   5  Silence distribution    how long since the last sign of life, Phone vs other
   6  Ticket + conversation list for review (oldest silence first)

 CAUSES (first match wins)
   GENUINELY_RECENT       last activity within @RecentMinutes: plausibly still a live conversation. Stays protected.
   CLOSE_RULE_OVERDUE     a customer-inactivity clock (10 min) or awaiting-reply clock (5 min) is overdue and the ticket is NOT in the Call Center: the approved
                          inactivity job (ChatbotInactivityCloseAppService) should already have closed it -> the job is not running / not firing (BackgroundJobs:Enabled).
   EXCLUDED_CALL_CENTER   the ticket is in the Call Center: the inactivity rule never applies there (voice). Only a Genesys end event can end the interaction.
   NO_CLOCK_NO_END_EVENT  no inactivity clock and no end: no approved rule can ever end it; the Genesys end event never reached TigerCS.
================================================================================
*/
SET NOCOUNT ON;
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

DECLARE @ExpectedDb sysname = N'TigerCS_Ticketing_Prod';
IF DB_NAME() <> @ExpectedDb
BEGIN
    DECLARE @m nvarchar(400) = N'ABORTED: connected to [' + ISNULL(DB_NAME(), N'?') + N'], this diagnostic is for [' + @ExpectedDb + N'] only.';
    THROW 50510, @m, 1;
END;

DECLARE @RecentMinutes int = 60;               -- activity within this many minutes = plausibly live
DECLARE @CustomerInactivityMinutes int = 10;   -- Genesys:CustomerInactivityTimeoutMinutes (Production value to be confirmed)
DECLARE @AwaitingReplyMinutes int = 5;         -- Genesys:AwaitingReplyTimeoutMinutes
DECLARE @MinTicketAgeMinutes int = 3;
DECLARE @Now datetime2 = SYSUTCDATETIME();
DECLARE @ColId int = (SELECT DepartmentId FROM dbo.Departments WHERE Code = N'COL');
DECLARE @CcId  int = (SELECT DepartmentId FROM dbo.Departments WHERE Code = N'CC' AND IsActive = 1);

-- ---------------------------------------------------------------------------------------------------------------------------
-- the set: identical to the backfill's ACTIVE_AI
-- ---------------------------------------------------------------------------------------------------------------------------
CREATE TABLE #S (TicketId bigint NOT NULL PRIMARY KEY, TicketNumber nvarchar(80) NULL, TicketStatus tinyint NOT NULL, DeptId int NOT NULL, CreatedAtUtc datetime2 NOT NULL,
    HumanNeeded bit NOT NULL, HasLive bit NOT NULL, LastActivityUtc datetime2 NOT NULL);
INSERT #S
SELECT x.TicketId, x.TicketNumber, x.TicketStatus, x.CurrentDepartmentId, x.CreatedAtUtc, x.HumanNeeded, x.HasLive, x.LastActivityUtc
FROM (
    SELECT t.TicketId, t.TicketNumber, t.TicketStatus, t.CurrentDepartmentId, t.CreatedAtUtc,
           CASE WHEN EXISTS (SELECT 1 FROM dbo.TicketInteractions i WHERE i.TicketId = t.TicketId AND i.RequiresHumanFollowUp = 1)
                  OR EXISTS (SELECT 1 FROM dbo.TicketAgentHandoffs h WHERE h.TicketId = t.TicketId AND h.Status IN (2, 3, 4))
                  OR t.FirstHumanResponseAtUtc IS NOT NULL
                  OR EXISTS (SELECT 1 FROM dbo.TicketInteractions i JOIN dbo.TicketInteractionMessages m ON m.TicketInteractionId = i.TicketInteractionId
                             WHERE i.TicketId = t.TicketId AND m.Sender = 2)
                  OR EXISTS (SELECT 1 FROM dbo.TicketInteractionHandlers h WHERE h.TicketId = t.TicketId)
                  OR EXISTS (SELECT 1 FROM dbo.TicketInteractions i WHERE i.TicketId = t.TicketId AND i.HandledByUserId IS NOT NULL)
                  OR EXISTS (SELECT 1 FROM dbo.TicketWorkflowEvents ev WHERE ev.TicketId = t.TicketId AND ev.EventType IN (11, 12, 18, 19))
                  OR EXISTS (SELECT 1 FROM dbo.TicketInteractions i WHERE i.TicketId = t.TicketId AND i.Source = 2 AND i.ChannelId = 1
                             AND NULLIF(LTRIM(RTRIM(i.GenesysAgentId)), N'') IS NOT NULL AND i.HandledByUserId IS NULL)
                THEN 1 ELSE 0 END AS HumanNeeded,
           CASE WHEN EXISTS (SELECT 1 FROM dbo.TicketInteractions i WHERE i.TicketId = t.TicketId AND i.Source = 2 AND i.EndedAtUtc IS NULL AND i.InactivityClosedAtUtc IS NULL) THEN 1 ELSE 0 END AS HasLive,
           (SELECT MAX(v) FROM (VALUES
                (t.CreatedAtUtc),
                ((SELECT MAX(q.v) FROM dbo.TicketInteractions i
                    CROSS APPLY (VALUES (i.CreatedAtUtc), (i.InteractionStartedAtUtc), (i.LastCustomerInteractionAtUtc), (i.AwaitingCustomerReplySinceUtc)) q(v)
                  WHERE i.TicketId = t.TicketId)),
                ((SELECT MAX(mm.SentAtUtc) FROM dbo.TicketInteractions i JOIN dbo.TicketInteractionMessages mm ON mm.TicketInteractionId = i.TicketInteractionId
                  WHERE i.TicketId = t.TicketId)),
                ((SELECT MAX(e.OccurredAtUtc) FROM dbo.TicketWorkflowEvents e WHERE e.TicketId = t.TicketId AND e.EventType BETWEEN 10 AND 19))
            ) a(v)) AS LastActivityUtc
    FROM dbo.Tickets t
    WHERE t.TicketStatus BETWEEN 1 AND 4 AND t.CurrentOwnerEmployeeId IS NULL
      AND t.CurrentDepartmentId <> ISNULL(@ColId, -1)
      AND t.CreatedAtUtc <= DATEADD(minute, -@MinTicketAgeMinutes, @Now)
      AND NOT EXISTS (SELECT 1 FROM dbo.TicketApprovals ap WHERE ap.TicketId = t.TicketId AND ap.Status = 1)
) x
WHERE x.HumanNeeded = 0 AND x.HasLive = 1
  AND EXISTS (SELECT 1 FROM dbo.TicketInteractions i WHERE i.TicketId = x.TicketId AND i.Source = 2);

-- per ticket cause
CREATE TABLE #C (TicketId bigint NOT NULL PRIMARY KEY, IsPhone bit NOT NULL, LiveInteractions int NOT NULL, TotalGenesys int NOT NULL,
    HasCustomerClock bit NOT NULL, HasAwaitingClock bit NOT NULL, ClockOverdue bit NOT NULL, InCallCenter bit NOT NULL, SilentMinutes decimal(12,1) NOT NULL, Cause varchar(24) NOT NULL);
INSERT #C
SELECT s.TicketId,
       CASE WHEN EXISTS (SELECT 1 FROM dbo.TicketInteractions i WHERE i.TicketId = s.TicketId AND i.Source = 2 AND i.EndedAtUtc IS NULL AND i.InactivityClosedAtUtc IS NULL AND i.ChannelId = 1) THEN 1 ELSE 0 END,
       (SELECT COUNT(*) FROM dbo.TicketInteractions i WHERE i.TicketId = s.TicketId AND i.Source = 2 AND i.EndedAtUtc IS NULL AND i.InactivityClosedAtUtc IS NULL),
       (SELECT COUNT(*) FROM dbo.TicketInteractions i WHERE i.TicketId = s.TicketId AND i.Source = 2),
       CASE WHEN EXISTS (SELECT 1 FROM dbo.TicketInteractions i WHERE i.TicketId = s.TicketId AND i.Source = 2 AND i.EndedAtUtc IS NULL AND i.InactivityClosedAtUtc IS NULL AND i.LastCustomerInteractionAtUtc IS NOT NULL) THEN 1 ELSE 0 END,
       CASE WHEN EXISTS (SELECT 1 FROM dbo.TicketInteractions i WHERE i.TicketId = s.TicketId AND i.Source = 2 AND i.EndedAtUtc IS NULL AND i.InactivityClosedAtUtc IS NULL AND i.AwaitingCustomerReplySinceUtc IS NOT NULL) THEN 1 ELSE 0 END,
       CASE WHEN EXISTS (SELECT 1 FROM dbo.TicketInteractions i WHERE i.TicketId = s.TicketId AND i.Source = 2 AND i.EndedAtUtc IS NULL AND i.InactivityClosedAtUtc IS NULL
                           AND ((i.LastCustomerInteractionAtUtc IS NOT NULL AND i.LastCustomerInteractionAtUtc < DATEADD(minute, -@CustomerInactivityMinutes, @Now))
                             OR (i.LastCustomerInteractionAtUtc IS NULL AND i.AwaitingCustomerReplySinceUtc IS NOT NULL AND i.AwaitingCustomerReplySinceUtc < DATEADD(minute, -@AwaitingReplyMinutes, @Now)))) THEN 1 ELSE 0 END,
       CASE WHEN s.DeptId = @CcId OR EXISTS (SELECT 1 FROM dbo.Tickets t WHERE t.TicketId = s.TicketId AND t.OriginatingDepartmentId = @CcId) THEN 1 ELSE 0 END,
       CAST(DATEDIFF(second, s.LastActivityUtc, @Now) / 60.0 AS decimal(12,1)),
       'PENDING'
FROM #S s;

UPDATE c SET Cause = CASE WHEN c.SilentMinutes <= @RecentMinutes THEN 'GENUINELY_RECENT'
                          WHEN c.InCallCenter = 1 THEN 'EXCLUDED_CALL_CENTER'
                          WHEN c.ClockOverdue = 1 THEN 'CLOSE_RULE_OVERDUE'
                          ELSE 'NO_CLOCK_NO_END_EVENT' END
FROM #C c;

-- 1. Reconciliation --------------------------------------------------------------------------------------------------------------
SELECT @@SERVERNAME AS [Server], DB_NAME() AS [Database], @Now AS DiagnosedAtUtc, (SELECT COUNT(*) FROM #S) AS ActiveAiSet_ShouldEqualDryRun181,
       (SELECT COUNT(*) FROM #C WHERE IsPhone = 1) AS WithLivePhoneInteraction, (SELECT COUNT(*) FROM #C WHERE IsPhone = 0) AS WithLiveChatOrOtherInteraction,
       (SELECT COUNT(*) FROM #C WHERE LiveInteractions > 1) AS WithMoreThanOneLiveInteraction,
       (SELECT COUNT(*) FROM #C WHERE LiveInteractions < TotalGenesys) AS WithAnEndedInteractionToo;
SELECT CASE s.TicketStatus WHEN 1 THEN 'Open' WHEN 2 THEN 'InProgress' WHEN 3 THEN 'PendingCustomer' WHEN 4 THEN 'PendingThirdParty' END AS Status, d.Code AS CurrentDepartment, COUNT(*) AS Tickets
FROM #S s JOIN dbo.Departments d ON d.DepartmentId = s.DeptId GROUP BY s.TicketStatus, d.Code ORDER BY Tickets DESC;

-- 2. Do end events arrive at all? (every Genesys interaction, by channel and age) ----------------------------------------------------------
SELECT CASE WHEN i.ChannelId = 1 THEN N'Phone' ELSE N'Chat/Other' END AS Channel,
       CASE WHEN i.CreatedAtUtc >= DATEADD(day, -1, @Now) THEN N'1. < 1 day' WHEN i.CreatedAtUtc >= DATEADD(day, -7, @Now) THEN N'2. 1-7 days'
            WHEN i.CreatedAtUtc >= DATEADD(day, -30, @Now) THEN N'3. 7-30 days' ELSE N'4. > 30 days' END AS Age,
       COUNT(*) AS Interactions,
       SUM(CASE WHEN i.EndedAtUtc IS NOT NULL THEN 1 ELSE 0 END) AS EndedByGenesysEndEvent,
       SUM(CASE WHEN i.EndedAtUtc IS NULL AND i.InactivityClosedAtUtc IS NOT NULL THEN 1 ELSE 0 END) AS ClosedForInactivityOnly,
       SUM(CASE WHEN i.EndedAtUtc IS NULL AND i.InactivityClosedAtUtc IS NULL THEN 1 ELSE 0 END) AS StillLive_NoEndRecorded,
       CAST(100.0 * SUM(CASE WHEN i.EndedAtUtc IS NULL AND i.InactivityClosedAtUtc IS NULL THEN 1 ELSE 0 END) / NULLIF(COUNT(*), 0) AS decimal(5,1)) AS PctStillLive
FROM dbo.TicketInteractions i WHERE i.Source = 2
GROUP BY CASE WHEN i.ChannelId = 1 THEN N'Phone' ELSE N'Chat/Other' END,
         CASE WHEN i.CreatedAtUtc >= DATEADD(day, -1, @Now) THEN N'1. < 1 day' WHEN i.CreatedAtUtc >= DATEADD(day, -7, @Now) THEN N'2. 1-7 days'
              WHEN i.CreatedAtUtc >= DATEADD(day, -30, @Now) THEN N'3. 7-30 days' ELSE N'4. > 30 days' END
ORDER BY Channel, Age;

-- 3. Did the end endpoint and the inactivity job ever run? --------------------------------------------------------------------------------
SELECT (SELECT MAX(EndedAtUtc) FROM dbo.TicketInteractions WHERE Source = 2) AS LastGenesysEndEventRecorded,
       (SELECT MAX(InactivityClosedAtUtc) FROM dbo.TicketInteractions WHERE Source = 2) AS LastInactivityClose,
       (SELECT COUNT(*) FROM dbo.TicketInteractions WHERE Source = 2 AND InactivityClosedAtUtc IS NOT NULL) AS InteractionsClosedForInactivity_Ever,
       (SELECT MAX(OccurredAtUtc) FROM dbo.AuditEntries WHERE Action = N'GenesysConversationEnded') AS LastEndAudit,
       (SELECT MAX(OccurredAtUtc) FROM dbo.AuditEntries WHERE Action = N'AutoCloseCustomerInactivity') AS LastInactivityCloseAudit,
       (SELECT MAX(OccurredAtUtc) FROM dbo.AuditEntries WHERE Action = N'GenesysCustomerReplyTimerStarted') AS LastReplyTimerStarted,
       (SELECT MAX(OccurredAtUtc) FROM dbo.AuditEntries WHERE Action = N'GenesysTranscriptAppended') AS LastTranscriptAppended;

-- 4. Cause of each of the set ---------------------------------------------------------------------------------------------------------------
SELECT c.Cause, CASE WHEN c.IsPhone = 1 THEN N'Phone' ELSE N'Chat/Other' END AS Channel, COUNT(*) AS Tickets,
       MIN(c.SilentMinutes) AS MinSilentMinutes, MAX(c.SilentMinutes) AS MaxSilentMinutes
FROM #C c GROUP BY c.Cause, c.IsPhone ORDER BY c.Cause, Channel;

-- 5. Silence distribution ---------------------------------------------------------------------------------------------------------------------
SELECT b.Bucket, COUNT(c.TicketId) AS Tickets, SUM(CASE WHEN c.IsPhone = 1 THEN 1 ELSE 0 END) AS Phone
FROM (VALUES (1, N'<= 15m'), (2, N'15m - 1h'), (3, N'1h - 2h'), (4, N'2h - 12h'), (5, N'12h - 24h'), (6, N'1 - 7 days'), (7, N'> 7 days')) b(Ord, Bucket)
LEFT JOIN #C c ON b.Ord = CASE WHEN c.SilentMinutes <= 15 THEN 1 WHEN c.SilentMinutes <= 60 THEN 2 WHEN c.SilentMinutes <= 120 THEN 3 WHEN c.SilentMinutes <= 720 THEN 4
                               WHEN c.SilentMinutes <= 1440 THEN 5 WHEN c.SilentMinutes <= 10080 THEN 6 ELSE 7 END
GROUP BY b.Ord, b.Bucket ORDER BY b.Ord;

-- 6. Ticket + conversation list, longest silence first -----------------------------------------------------------------------------------------
SELECT TOP (1000) s.TicketNumber, d.Code AS CurrentDepartment, CASE s.TicketStatus WHEN 1 THEN 'Open' WHEN 2 THEN 'InProgress' WHEN 3 THEN 'PendingCustomer' WHEN 4 THEN 'PendingThirdParty' END AS Status,
       c.Cause, CASE WHEN c.IsPhone = 1 THEN N'Phone' ELSE N'Chat/Other' END AS Channel, s.CreatedAtUtc, s.LastActivityUtc, c.SilentMinutes,
       c.LiveInteractions, c.TotalGenesys, c.HasCustomerClock, c.HasAwaitingClock, c.InCallCenter,
       i.TicketInteractionId, i.GenesysConversationId, i.InteractionStartedAtUtc, i.LastCustomerInteractionAtUtc, i.AwaitingCustomerReplySinceUtc
FROM #S s JOIN #C c ON c.TicketId = s.TicketId JOIN dbo.Departments d ON d.DepartmentId = s.DeptId
JOIN dbo.TicketInteractions i ON i.TicketId = s.TicketId AND i.Source = 2 AND i.EndedAtUtc IS NULL AND i.InactivityClosedAtUtc IS NULL
ORDER BY c.SilentMinutes DESC, s.TicketId;

DROP TABLE #C;
DROP TABLE #S;
