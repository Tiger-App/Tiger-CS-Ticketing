/*
================================================================================
 Diagnose_TigerCS_PROD_ActiveAiConversations_ReadOnly.sql          (SELECT ONLY - NO WRITES)
 Database : TigerCS_Ticketing_Prod

 WHY: the backlog Preview holds back an Open ticket as "ACTIVE AI CONVERSATION" when it has a Genesys interaction (Source = 2) with
      EndedAtUtc IS NULL, InactivityClosedAtUtc IS NULL, RequiresHumanFollowUp = 0, HandledByUserId IS NULL, no interaction flagged for a human and no open handoff.
      The test looks ONLY at "no end event was recorded". A conversation whose end event never reached TigerCS (Genesys never called the end endpoint, the
      inactivity sweep is not running, the call/chat ended long ago) therefore stays "active" for ever. This script separates GENUINELY ACTIVE conversations
      from STALE ones using the evidence that exists in the database, WITHOUT changing the rule and WITHOUT writing anything.

 READ-ONLY: only SELECTs and session temp tables (tempdb); READ UNCOMMITTED; safe to run any time from a db_datareader login.

 EVIDENCE USED (per ticket, over ALL its interactions):
   last activity  = latest of interaction CreatedAtUtc / InteractionStartedAtUtc / LastCustomerInteractionAtUtc / AwaitingCustomerReplySinceUtc,
                    transcript message SentAtUtc, interaction-handler ConnectedAtUtc/RecordedAtUtc, handoff and Genesys workflow events (types 10-19)
   human evidence = a HumanAgent transcript line, an interaction-handler row, FirstHumanResponseAtUtc, any handoff row (any status), or a workflow event
                    HandoffAssigned / HandoffStarted / GenesysAgentConnected / GenesysCallTransferred  (a person took part: this is human work, not an AI-only ticket)
   end evidence   = EndedAtUtc / InactivityClosedAtUtc on ANY interaction of the ticket (the active-AI rule needs a live one, so these are the "other" interactions)

 STATES (each held-back ticket gets exactly one, first match wins):
   1 HUMAN_TOUCHED_NOT_FLAGGED   a person took part but the interaction row still says "AI"                       -> human work; in scope for redistribution
   2 GENUINELY_ACTIVE            last activity within @RecentMinutes                                              -> stays protected
   3 SILENT_BUT_UNDER_THRESHOLD  silent longer than @RecentMinutes but not longer than @StaleAfterHours           -> stays protected (could still be a slow customer / open call)
   4 STALE_NO_END_EVENT          silent longer than @StaleAfterHours, no human evidence                           -> HISTORICAL: end event missing -> proposed correction
   (a ticket with no interaction row at all is never held back - see "missing records")

 PROPOSED CORRECTION (no data is changed by it): Preview and Execute get two switches, both OFF by default, that make the SAME classification in memory:
   @StaleAiAfterHours (NULL = off)  and  @TreatHumanTouchedAiAsHistorical (0 = off).  Rows in TicketInteractions are not modified.
================================================================================
*/
SET NOCOUNT ON;
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

DECLARE @ExpectedDb sysname = N'TigerCS_Ticketing_Prod';
IF DB_NAME() <> @ExpectedDb
BEGIN
    DECLARE @m nvarchar(400) = N'ABORTED: connected to [' + ISNULL(DB_NAME(), N'?') + N'], this diagnostic is for [' + @ExpectedDb + N'] only.';
    THROW 50500, @m, 1;
END;

DECLARE @RecentMinutes int = 60;         -- activity within this many minutes = genuinely active
DECLARE @StaleAfterHours int = 24;       -- silent for longer than this (and no human evidence) = stale; the table "projected by threshold" below shows other values
DECLARE @Now datetime2 = SYSUTCDATETIME();

-- ---------------------------------------------------------------------------------------------------------------------------
-- every Open ticket with the SAME active-AI test the Preview uses
-- ---------------------------------------------------------------------------------------------------------------------------
CREATE TABLE #T (TicketId bigint NOT NULL PRIMARY KEY, TicketNumber nvarchar(80) NULL, CreatedAtUtc datetime2 NOT NULL, DeptId int NOT NULL, Owned bit NOT NULL,
    PendingApproval bit NOT NULL, ActiveAi bit NOT NULL, FirstHumanResponseAtUtc datetime2 NULL);
INSERT #T
SELECT t.TicketId, t.TicketNumber, t.CreatedAtUtc, t.CurrentDepartmentId, CASE WHEN t.CurrentOwnerEmployeeId IS NULL THEN 0 ELSE 1 END,
       CASE WHEN EXISTS (SELECT 1 FROM dbo.TicketApprovals ap WHERE ap.TicketId = t.TicketId AND ap.Status = 1) THEN 1 ELSE 0 END,
       CASE WHEN (EXISTS (SELECT 1 FROM dbo.TicketInteractions i WHERE i.TicketId = t.TicketId AND i.Source = 2 AND i.EndedAtUtc IS NULL
                     AND i.InactivityClosedAtUtc IS NULL AND i.RequiresHumanFollowUp = 0 AND i.HandledByUserId IS NULL)
       AND NOT EXISTS (SELECT 1 FROM dbo.TicketInteractions i2 WHERE i2.TicketId = t.TicketId AND i2.RequiresHumanFollowUp = 1)
       AND NOT EXISTS (SELECT 1 FROM dbo.TicketAgentHandoffs h WHERE h.TicketId = t.TicketId AND h.Status IN (2, 3, 4))) THEN 1 ELSE 0 END,
       t.FirstHumanResponseAtUtc
FROM dbo.Tickets t WHERE t.TicketStatus = 1;

-- evidence, aggregated once per ticket (only for tickets that have a Genesys interaction)
CREATE TABLE #Ev (TicketId bigint NOT NULL PRIMARY KEY, Interactions int NOT NULL, LiveInteractions int NOT NULL, EndedInteractions int NOT NULL, InactivityClosed int NOT NULL,
    PhoneInteractions int NOT NULL, ChatInteractions int NOT NULL, LiveWithoutConversationId int NOT NULL, LiveWithoutStart int NOT NULL,
    Messages int NOT NULL, HumanMessages int NOT NULL, CustomerMessages int NOT NULL, VirtualMessages int NOT NULL, LastMessageUtc datetime2 NULL,
    Handlers int NOT NULL, Handoffs int NOT NULL, HumanEvents int NOT NULL, LastEventUtc datetime2 NULL,
    LastInteractionStampUtc datetime2 NULL, LastEndedUtc datetime2 NULL, LiveStartedUtc datetime2 NULL, LiveAgentIdSet int NOT NULL);
INSERT #Ev
SELECT t.TicketId,
       (SELECT COUNT(*) FROM dbo.TicketInteractions i WHERE i.TicketId = t.TicketId),
       (SELECT COUNT(*) FROM dbo.TicketInteractions i WHERE i.TicketId = t.TicketId AND i.Source = 2 AND i.EndedAtUtc IS NULL AND i.InactivityClosedAtUtc IS NULL),
       (SELECT COUNT(*) FROM dbo.TicketInteractions i WHERE i.TicketId = t.TicketId AND i.EndedAtUtc IS NOT NULL),
       (SELECT COUNT(*) FROM dbo.TicketInteractions i WHERE i.TicketId = t.TicketId AND i.InactivityClosedAtUtc IS NOT NULL),
       (SELECT COUNT(*) FROM dbo.TicketInteractions i WHERE i.TicketId = t.TicketId AND i.Source = 2 AND i.EndedAtUtc IS NULL AND i.InactivityClosedAtUtc IS NULL AND i.ChannelId = 1),
       (SELECT COUNT(*) FROM dbo.TicketInteractions i WHERE i.TicketId = t.TicketId AND i.Source = 2 AND i.EndedAtUtc IS NULL AND i.InactivityClosedAtUtc IS NULL AND i.ChannelId <> 1),
       (SELECT COUNT(*) FROM dbo.TicketInteractions i WHERE i.TicketId = t.TicketId AND i.Source = 2 AND i.EndedAtUtc IS NULL AND i.InactivityClosedAtUtc IS NULL AND NULLIF(i.GenesysConversationId, N'') IS NULL),
       (SELECT COUNT(*) FROM dbo.TicketInteractions i WHERE i.TicketId = t.TicketId AND i.Source = 2 AND i.EndedAtUtc IS NULL AND i.InactivityClosedAtUtc IS NULL AND i.InteractionStartedAtUtc IS NULL),
       m.Messages, ISNULL(m.HumanMessages, 0), ISNULL(m.CustomerMessages, 0), ISNULL(m.VirtualMessages, 0), m.LastMessageUtc,
       (SELECT COUNT(*) FROM dbo.TicketInteractionHandlers h WHERE h.TicketId = t.TicketId),
       (SELECT COUNT(*) FROM dbo.TicketAgentHandoffs h WHERE h.TicketId = t.TicketId),
       (SELECT COUNT(*) FROM dbo.TicketWorkflowEvents e WHERE e.TicketId = t.TicketId AND e.EventType IN (11, 12, 18, 19)),
       (SELECT MAX(e.OccurredAtUtc) FROM dbo.TicketWorkflowEvents e WHERE e.TicketId = t.TicketId AND e.EventType BETWEEN 10 AND 19),
       (SELECT MAX(x.v) FROM dbo.TicketInteractions i CROSS APPLY (VALUES (i.CreatedAtUtc), (i.InteractionStartedAtUtc), (i.LastCustomerInteractionAtUtc), (i.AwaitingCustomerReplySinceUtc)) x(v) WHERE i.TicketId = t.TicketId),
       (SELECT MAX(i.EndedAtUtc) FROM dbo.TicketInteractions i WHERE i.TicketId = t.TicketId),
       (SELECT MIN(ISNULL(i.InteractionStartedAtUtc, i.CreatedAtUtc)) FROM dbo.TicketInteractions i WHERE i.TicketId = t.TicketId AND i.Source = 2 AND i.EndedAtUtc IS NULL AND i.InactivityClosedAtUtc IS NULL),
       (SELECT COUNT(*) FROM dbo.TicketInteractions i WHERE i.TicketId = t.TicketId AND i.Source = 2 AND i.EndedAtUtc IS NULL AND i.InactivityClosedAtUtc IS NULL AND NULLIF(i.GenesysAgentId, N'') IS NOT NULL)
FROM #T t
OUTER APPLY (SELECT COUNT(*) AS Messages, SUM(CASE WHEN mm.Sender = 2 THEN 1 ELSE 0 END) AS HumanMessages, SUM(CASE WHEN mm.Sender = 1 THEN 1 ELSE 0 END) AS CustomerMessages,
                    SUM(CASE WHEN mm.Sender NOT IN (1, 2) THEN 1 ELSE 0 END) AS VirtualMessages, MAX(mm.SentAtUtc) AS LastMessageUtc
             FROM dbo.TicketInteractions i JOIN dbo.TicketInteractionMessages mm ON mm.TicketInteractionId = i.TicketInteractionId WHERE i.TicketId = t.TicketId) m
WHERE EXISTS (SELECT 1 FROM dbo.TicketInteractions i WHERE i.TicketId = t.TicketId);

-- classification of the held-back (active-AI) tickets
CREATE TABLE #C (TicketId bigint NOT NULL PRIMARY KEY, TicketNumber nvarchar(80) NULL, DeptId int NOT NULL, Owned bit NOT NULL, PendingApproval bit NOT NULL,
    Channel varchar(12) NOT NULL, LastActivityUtc datetime2 NULL, AgeHours decimal(12, 1) NULL, State varchar(40) NOT NULL, HumanEvidence varchar(200) NOT NULL,
    Messages int NOT NULL, LiveInteractions int NOT NULL, EndedInteractions int NOT NULL, NoConversationId int NOT NULL, SupersededByEndedConversation bit NOT NULL);
INSERT #C
SELECT x.TicketId, x.TicketNumber, x.DeptId, x.Owned, x.PendingApproval, x.Channel, x.LastActivityUtc,
       CAST(DATEDIFF(minute, x.LastActivityUtc, @Now) / 60.0 AS decimal(12, 1)),
       CASE WHEN x.HumanEvidence <> '' THEN 'HUMAN_TOUCHED_NOT_FLAGGED'
            WHEN x.LastActivityUtc >= DATEADD(minute, -@RecentMinutes, @Now) THEN 'GENUINELY_ACTIVE'
            WHEN x.LastActivityUtc >= DATEADD(hour, -@StaleAfterHours, @Now) THEN 'SILENT_BUT_UNDER_THRESHOLD'
            ELSE 'STALE_NO_END_EVENT' END,
       x.HumanEvidence, x.Messages, x.LiveInteractions, x.EndedInteractions, x.NoConvId,
       CASE WHEN x.LastEndedUtc IS NOT NULL AND x.LiveStartedUtc IS NOT NULL AND x.LastEndedUtc >= x.LiveStartedUtc THEN 1 ELSE 0 END
FROM (SELECT t.TicketId, t.TicketNumber, t.DeptId, t.Owned, t.PendingApproval,
             CASE WHEN e.PhoneInteractions > 0 AND e.ChatInteractions > 0 THEN 'mixed' WHEN e.PhoneInteractions > 0 THEN 'phone' ELSE 'chat/other' END AS Channel,
             (SELECT MAX(v) FROM (VALUES (e.LastInteractionStampUtc), (e.LastMessageUtc), (e.LastEventUtc), (t.CreatedAtUtc)) a(v)) AS LastActivityUtc,
             LTRIM(CASE WHEN e.HumanMessages > 0 THEN 'human-message ' ELSE '' END + CASE WHEN e.Handlers > 0 THEN 'handler ' ELSE '' END
                 + CASE WHEN t.FirstHumanResponseAtUtc IS NOT NULL THEN 'first-response ' ELSE '' END + CASE WHEN e.Handoffs > 0 THEN 'handoff ' ELSE '' END
                 + CASE WHEN e.HumanEvents > 0 THEN 'agent-event' ELSE '' END) AS HumanEvidence,
             e.Messages, e.LiveInteractions, e.EndedInteractions, e.LiveWithoutConversationId AS NoConvId, e.LastEndedUtc, e.LiveStartedUtc
      FROM #T t JOIN #Ev e ON e.TicketId = t.TicketId WHERE t.ActiveAi = 1) x;

-- ===========================================================================================================================
-- 1. HEADLINE
-- ===========================================================================================================================
SELECT @@SERVERNAME AS [Server], DB_NAME() AS [Database], @Now AS DiagnosedAtUtc, @RecentMinutes AS RecentMinutes, @StaleAfterHours AS StaleAfterHours,
       (SELECT COUNT(*) FROM #T) AS TotalOpen,
       (SELECT COUNT(*) FROM #T WHERE ActiveAi = 1) AS FlaggedActiveAi,
       (SELECT COUNT(*) FROM #T WHERE ActiveAi = 1 AND PendingApproval = 1) AS FlaggedAndAlsoPendingApproval,
       (SELECT COUNT(*) FROM #T WHERE ActiveAi = 1 AND Owned = 1) AS FlaggedAndCurrentlyOwned,
       (SELECT COUNT(*) FROM #T WHERE ActiveAi = 0 AND PendingApproval = 0) AS NotHeldBack_Eligible,
       (SELECT COUNT(*) FROM #T WHERE ActiveAi = 0 AND PendingApproval = 1) AS HeldBackOnlyForPendingApproval;

-- ===========================================================================================================================
-- 2. COUNTS BY CONVERSATION STATE (and channel)  - the answer to "genuinely active vs historical"
-- ===========================================================================================================================
SELECT State, COUNT(*) AS Tickets, SUM(CASE WHEN Channel = 'phone' THEN 1 ELSE 0 END) AS Phone, SUM(CASE WHEN Channel = 'chat/other' THEN 1 ELSE 0 END) AS ChatOrOther,
       SUM(CASE WHEN Channel = 'mixed' THEN 1 ELSE 0 END) AS Mixed, SUM(CASE WHEN Owned = 1 THEN 1 ELSE 0 END) AS CurrentlyOwned, SUM(CASE WHEN PendingApproval = 1 THEN 1 ELSE 0 END) AS AlsoPendingApproval,
       MIN(AgeHours) AS MinAgeHours, MAX(AgeHours) AS MaxAgeHours
FROM #C GROUP BY State
UNION ALL SELECT '** TOTAL **', COUNT(*), SUM(CASE WHEN Channel = 'phone' THEN 1 ELSE 0 END), SUM(CASE WHEN Channel = 'chat/other' THEN 1 ELSE 0 END), SUM(CASE WHEN Channel = 'mixed' THEN 1 ELSE 0 END),
       SUM(CASE WHEN Owned = 1 THEN 1 ELSE 0 END), SUM(CASE WHEN PendingApproval = 1 THEN 1 ELSE 0 END), MIN(AgeHours), MAX(AgeHours) FROM #C
ORDER BY Tickets DESC;

-- ===========================================================================================================================
-- 3. COUNTS BY REASON (why each flagged ticket looks active; a ticket can have several)
-- ===========================================================================================================================
SELECT r.Reason, r.Tickets FROM (VALUES
  (N'flagged active: no EndedAtUtc and no InactivityClosedAtUtc on a Genesys interaction (always true for the whole set)', (SELECT COUNT(*) FROM #C)),
  (N'  ...and the interaction has NO transcript message at all (empty shell: nothing was ever stored)', (SELECT COUNT(*) FROM #C WHERE Messages = 0)),
  (N'  ...and no Genesys conversation id on the live interaction (cannot be matched to an end event)', (SELECT COUNT(*) FROM #C WHERE NoConversationId > 0)),
  (N'  ...phone interaction still "live" (calls end in minutes; an end event was lost)', (SELECT COUNT(*) FROM #C WHERE Channel IN ('phone', 'mixed'))),
  (N'  ...chat/other interaction silent longer than the 10-minute customer-inactivity rule but never closed by it (sweep not running / not covering it)', (SELECT COUNT(*) FROM #C WHERE Channel IN ('chat/other', 'mixed') AND AgeHours > 10.0 / 60)),
  (N'  ...ticket also has an ENDED interaction (a conversation did end; this live row is older or unrelated)', (SELECT COUNT(*) FROM #C WHERE EndedInteractions > 0)),
  (N'  ...an ended interaction is NEWER than the live one (live row superseded by a later, ended conversation)', (SELECT COUNT(*) FROM #C WHERE SupersededByEndedConversation = 1)),
  (N'  ...human evidence present although the row says AI (human message / handler / first response / handoff row / agent event)', (SELECT COUNT(*) FROM #C WHERE HumanEvidence <> '')),
  (N'  ...transcript contains a HumanAgent line', (SELECT COUNT(*) FROM #C c JOIN #Ev e ON e.TicketId = c.TicketId WHERE e.HumanMessages > 0)),
  (N'  ...interaction-handler (agent connected) record exists', (SELECT COUNT(*) FROM #C c JOIN #Ev e ON e.TicketId = c.TicketId WHERE e.Handlers > 0)),
  (N'  ...a handoff row exists (completed / cancelled; open ones are not "AI-only" by the rule)', (SELECT COUNT(*) FROM #C c JOIN #Ev e ON e.TicketId = c.TicketId WHERE e.Handoffs > 0)),
  (N'  ...a Genesys agent id is stored on the live interaction (an agent was named)', (SELECT COUNT(*) FROM #C c JOIN #Ev e ON e.TicketId = c.TicketId WHERE e.LiveAgentIdSet > 0))
) r(Reason, Tickets);

-- ===========================================================================================================================
-- 4. AGE OF LAST ACTIVITY (how long each flagged conversation has been silent)
-- ===========================================================================================================================
SELECT b.Bucket, COUNT(c.TicketId) AS Tickets
FROM (VALUES (1, N'<= 10 minutes'), (2, N'10-60 minutes'), (3, N'1-6 hours'), (4, N'6-24 hours'), (5, N'1-3 days'), (6, N'3-7 days'), (7, N'7-30 days'), (8, N'> 30 days')) b(Ord, Bucket)
LEFT JOIN #C c ON b.Ord = CASE WHEN c.AgeHours <= 10.0 / 60 THEN 1 WHEN c.AgeHours <= 1 THEN 2 WHEN c.AgeHours <= 6 THEN 3 WHEN c.AgeHours <= 24 THEN 4 WHEN c.AgeHours <= 72 THEN 5
                                   WHEN c.AgeHours <= 168 THEN 6 WHEN c.AgeHours <= 720 THEN 7 ELSE 8 END
GROUP BY b.Ord, b.Bucket ORDER BY b.Ord;

-- by creation month of the ticket (a sharp cut-off date points to the day an end-event feed stopped / started)
SELECT CONVERT(char(7), t.CreatedAtUtc, 120) AS TicketCreatedMonth, COUNT(*) AS FlaggedTickets, SUM(CASE WHEN c.State = 'STALE_NO_END_EVENT' THEN 1 ELSE 0 END) AS Stale
FROM #C c JOIN #T t ON t.TicketId = c.TicketId GROUP BY CONVERT(char(7), t.CreatedAtUtc, 120) ORDER BY 1;

-- ===========================================================================================================================
-- 5. PROJECTION: how many Open tickets are safely eligible, for each threshold (what the Preview would show with the proposed switches)
-- ===========================================================================================================================
SELECT th.Label, th.Hours AS StaleAfterHours,
       (SELECT COUNT(*) FROM #T WHERE ActiveAi = 0 AND PendingApproval = 0) AS EligibleNow,
       (SELECT COUNT(*) FROM #C WHERE PendingApproval = 0 AND HumanEvidence <> '') AS PlusHumanTouched,
       (SELECT COUNT(*) FROM #C WHERE PendingApproval = 0 AND HumanEvidence = '' AND AgeHours > th.Hours) AS PlusStale,
       (SELECT COUNT(*) FROM #T WHERE ActiveAi = 0 AND PendingApproval = 0)
         + (SELECT COUNT(*) FROM #C WHERE PendingApproval = 0 AND (HumanEvidence <> '' OR AgeHours > th.Hours)) AS ProjectedEligible,
       (SELECT COUNT(*) FROM #C WHERE PendingApproval = 0 AND HumanEvidence = '' AND AgeHours <= th.Hours) AS StillHeld_ActiveAi,
       (SELECT COUNT(*) FROM #T WHERE PendingApproval = 1) AS StillHeld_PendingApproval
FROM (VALUES (N'1 hour', 1), (N'6 hours', 6), (N'24 hours', 24), (N'3 days', 72), (N'7 days', 168), (N'30 days', 720)) th(Label, Hours)
ORDER BY th.Hours;

-- ===========================================================================================================================
-- 6. MISSING / INCOMPLETE CONVERSATION RECORDS among Open tickets
-- ===========================================================================================================================
SELECT
  (SELECT COUNT(*) FROM #T t WHERE NOT EXISTS (SELECT 1 FROM dbo.TicketInteractions i WHERE i.TicketId = t.TicketId))                           AS OpenTickets_NoInteractionRow_NeverHeldBack,
  (SELECT COUNT(*) FROM #T t WHERE NOT EXISTS (SELECT 1 FROM dbo.TicketInteractions i WHERE i.TicketId = t.TicketId) AND t.Owned = 0)            AS OfWhich_Unowned,
  (SELECT COUNT(*) FROM #T t WHERE EXISTS (SELECT 1 FROM dbo.TicketInteractions i WHERE i.TicketId = t.TicketId AND i.Source = 1)
        AND NOT EXISTS (SELECT 1 FROM dbo.TicketInteractions i WHERE i.TicketId = t.TicketId AND i.Source = 2))                                 AS OpenTickets_OnlyTicketingInteraction_NotGenesys,
  (SELECT COUNT(*) FROM #C WHERE Messages = 0)                                                                                                  AS FlaggedWithNoTranscript,
  (SELECT COUNT(*) FROM #C WHERE NoConversationId > 0)                                                                                          AS FlaggedLiveRowWithoutConversationId,
  (SELECT COUNT(*) FROM dbo.TicketInteractions i JOIN #T t ON t.TicketId = i.TicketId WHERE i.Source = 2 AND i.EndedAtUtc IS NULL AND i.InactivityClosedAtUtc IS NULL AND i.InteractionStartedAtUtc IS NULL) AS LiveRowsWithoutStartTime,
  (SELECT COUNT(*) FROM dbo.TicketInteractions i JOIN #T t ON t.TicketId = i.TicketId WHERE i.EndedAtUtc IS NOT NULL AND i.EndReason IS NULL)  AS EndedRowsWithoutReason,
  (SELECT COUNT(*) FROM dbo.TicketInteractions i WHERE i.Source = 2 AND i.EndedAtUtc IS NULL AND i.InactivityClosedAtUtc IS NULL
        AND NOT EXISTS (SELECT 1 FROM dbo.Tickets t WHERE t.TicketId = i.TicketId AND t.TicketStatus = 1))                                       AS LiveRows_OnTicketsThatAreNotOpen_FYI;

-- the same flag across ALL history, to see whether "never ended" is the norm (an end-event feed that never worked) or recent
SELECT CONVERT(char(7), i.CreatedAtUtc, 120) AS InteractionMonth, COUNT(*) AS GenesysInteractions,
       SUM(CASE WHEN i.EndedAtUtc IS NOT NULL THEN 1 ELSE 0 END) AS Ended, SUM(CASE WHEN i.InactivityClosedAtUtc IS NOT NULL THEN 1 ELSE 0 END) AS InactivityClosed,
       SUM(CASE WHEN i.EndedAtUtc IS NULL AND i.InactivityClosedAtUtc IS NULL THEN 1 ELSE 0 END) AS NeverEnded
FROM dbo.TicketInteractions i WHERE i.Source = 2 GROUP BY CONVERT(char(7), i.CreatedAtUtc, 120) ORDER BY 1;

-- ===========================================================================================================================
-- 7. EVERY FLAGGED TICKET with its state and evidence (ticket numbers only - no customer data), stale / human first
-- ===========================================================================================================================
SELECT c.TicketNumber, d.Code AS Department, CASE WHEN c.Owned = 1 THEN 'owned' ELSE 'unowned' END AS Ownership, c.Channel, c.State, c.LastActivityUtc, c.AgeHours,
       c.Messages, c.LiveInteractions, c.EndedInteractions, c.HumanEvidence, CASE WHEN c.PendingApproval = 1 THEN 'ALSO PENDING APPROVAL (stays held back)' ELSE '' END AS Note
FROM #C c JOIN dbo.Departments d ON d.DepartmentId = c.DeptId
ORDER BY CASE c.State WHEN 'HUMAN_TOUCHED_NOT_FLAGGED' THEN 0 WHEN 'STALE_NO_END_EVENT' THEN 1 WHEN 'SILENT_BUT_UNDER_THRESHOLD' THEN 2 ELSE 3 END, c.AgeHours DESC, c.TicketNumber;

DROP TABLE #C; DROP TABLE #Ev; DROP TABLE #T;
