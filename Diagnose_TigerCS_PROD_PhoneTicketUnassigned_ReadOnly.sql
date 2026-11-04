/*
================================================================================
 Diagnose_TigerCS_PROD_PhoneTicketUnassigned_ReadOnly.sql          (SELECT ONLY - NO WRITES)
 Database : TigerCS_Ticketing_Prod

 WHY: new Genesys Phone tickets (example TG-ACC-20261104-0005) are still Unassigned after PR #163 / #164. This script finds the actual root cause
      for the named ticket and for every Genesys Phone ticket created in the look-back window, BEFORE any further routing change is made.

 HOW THE APPLICATION DECIDES (src/TigerCS.Application/Modules/GenesysIntegration/Services/GenesysInquiryIngestionAppService.cs,
 GenesysHumanHandlingEvidence.cs, TicketCreationAppService.cs, TicketAutoAssignmentService.cs):
   1. A Phone inquiry is "human work" ONLY when Genesys sent humanFollowUpRequired = true, OR the inquiry named an agent id that maps to a
      Ticketing user (AspNetUsers.GenesysUserId = agentId). A Phone ticket with neither is treated as AI-handled: it is NOT moved to the Call
      Center and NO CS Agent is assigned (needsHuman = false) - it stays in its queue department, unowned.
   2. needsHuman + a mapped, eligible Call Center CS Agent (Genesys:AssignOnAnswer, default true) -> assigned to THAT agent (audit AssignOnAnswer).
   3. needsHuman + unmapped / ineligible agent, or no agent -> Call Center Round Robin over eligible CS Agents (audit AutoAssign).
   4. Round Robin with an empty pool leaves the ticket in the Call Center queue, audited AutoAssign "DepartmentQueue ... Reason=...", plus a
      ManualAssignmentRequired workflow event (type 20, marker CsAgentAssignmentFailed).
   5. The backfill / reconciler treats a ticket whose Genesys interaction is still "live" (no EndedAtUtc, no InactivityClosedAtUtc) and has no human
      evidence as ActiveAiConversation: protected until the (disabled by default) stale rule fires. A Phone call whose end event never arrived
      therefore looks exactly like an active AI chat.

 ROOT-CAUSE CLASSES (first match wins) - see the "Meaning" column of result set 2:
   HAS_OWNER                           not unassigned (nothing to explain)
   NOT_ACTIVE                          Resolved / Closed
   PENDING_APPROVAL                    held by the approval process
   PHONE_NO_AGENT_NO_HUMAN_FLAG        no agent id and RequiresHumanFollowUp = 0: Genesys never said a person was needed -> AI path (rule 1)
   PHONE_AGENT_ID_NOT_MAPPED           agent id present, maps to no AspNetUsers.GenesysUserId (missing mapping) -> should be Round Robin
   PHONE_AGENT_MAPPED_NOT_ELIGIBLE     agent maps to a user who is deactivated, locked out, not a CS Agent, or not a Call Center member
   HUMAN_NOT_IN_CALL_CENTER            a human signal exists but the ticket is not in the Call Center (routing never ran / failed)
   HUMAN_IN_CC_EMPTY_POOL              in the Call Center, human work, and the eligible CS Agent pool is EMPTY right now
   HUMAN_IN_CC_POOL_AVAILABLE          in the Call Center, human work, pool is not empty, yet nobody was picked (lost race / crash / not retried)
   LIVE_AI_PHONE_NO_END_EVENT          (reported separately, flag column) a live Phone interaction with no human evidence and no end event

 READ-ONLY: only SELECTs and session temp tables (tempdb); READ UNCOMMITTED; safe from a db_datareader login at any time.
 It never changes a status, owner, assignment, interaction, rotation or audit row. No customer name / phone / e-mail is selected.
 What a SQL script CANNOT see: Genesys:AssignOnAnswer and the Genesys flow's actual payload (was humanFollowUpRequired sent? did agentId arrive?).
 For those, compare the audit line in result set 5 (HumanSignal=..., AgentMapping=...) written by the ingestion at creation time.
================================================================================
*/
SET NOCOUNT ON;
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

DECLARE @ExpectedDb sysname = N'TigerCS_Ticketing_Prod';
IF DB_NAME() <> @ExpectedDb
BEGIN
    DECLARE @m nvarchar(400) = N'ABORTED: connected to [' + ISNULL(DB_NAME(), N'?') + N'], this diagnostic is for [' + @ExpectedDb + N'] only.';
    THROW 50700, @m, 1;
END;

-- ---------------------------------------------------------------------------------------------------------------------------
-- parameters
-- ---------------------------------------------------------------------------------------------------------------------------
DECLARE @TicketNumber nvarchar(80) = N'TG-ACC-20261104-0005';   -- the example ticket; always included whatever the window
DECLARE @LookbackDays int = 7;                                  -- every Genesys Phone ticket created in the last N days is classified too
DECLARE @MinTicketAgeMinutes int = 3;                           -- CsAgentBackfill:MinTicketAgeMinutes
DECLARE @CsAgentRole nvarchar(256) = N'CS Agent';
DECLARE @Now datetime2 = SYSUTCDATETIME();

DECLARE @CcId int = (SELECT TOP (1) DepartmentId FROM dbo.Departments WHERE Code = N'CC' AND IsActive = 1);

-- ---------------------------------------------------------------------------------------------------------------------------
-- user eligibility (database-visible part only: the application also excludes service identities, which live in configuration)
-- ---------------------------------------------------------------------------------------------------------------------------
CREATE TABLE #Elig (UserId uniqueidentifier NOT NULL PRIMARY KEY, IsActive bit NOT NULL, IsLockedOut bit NOT NULL, IsCsAgent bit NOT NULL, IsCcMember bit NOT NULL);
INSERT #Elig
SELECT u.Id,
       CASE WHEN e.EmployeeId IS NOT NULL AND e.DeactivatedAtUtc IS NULL THEN 1 ELSE 0 END,
       CASE WHEN u.LockoutEnd IS NOT NULL AND u.LockoutEnd > SYSDATETIMEOFFSET() THEN 1 ELSE 0 END,
       CASE WHEN EXISTS (SELECT 1 FROM dbo.AspNetUserRoles ur JOIN dbo.AspNetRoles r ON r.Id = ur.RoleId WHERE ur.UserId = u.Id AND r.Name = @CsAgentRole) THEN 1 ELSE 0 END,
       CASE WHEN @CcId IS NOT NULL AND EXISTS (SELECT 1 FROM dbo.UserDepartmentAssignments uda WHERE uda.EmployeeId = u.Id AND uda.DepartmentId = @CcId) THEN 1 ELSE 0 END
FROM dbo.AspNetUsers u LEFT JOIN dbo.Employees e ON e.EmployeeId = u.Id;

DECLARE @PoolSize int = (SELECT COUNT(*) FROM #Elig WHERE IsActive = 1 AND IsLockedOut = 0 AND IsCsAgent = 1 AND IsCcMember = 1);

-- ---------------------------------------------------------------------------------------------------------------------------
-- one row per Genesys Phone ticket in scope
-- ---------------------------------------------------------------------------------------------------------------------------
CREATE TABLE #P (
    TicketId bigint NOT NULL PRIMARY KEY, TicketNumber nvarchar(80) NULL, TicketStatus tinyint NOT NULL, DeptId int NOT NULL, OriginDeptId int NOT NULL,
    OwnerId uniqueidentifier NULL, CreatedAtUtc datetime2 NOT NULL, PendingApproval bit NOT NULL,
    PhoneInteractions int NOT NULL, RequiresHuman bit NOT NULL, OpenHandoff bit NOT NULL, HumanEvidence bit NOT NULL,
    NamedAgentId nvarchar(100) NULL, AgentMapped bit NOT NULL, MappedUserId uniqueidentifier NULL, HandledByUserId uniqueidentifier NULL,
    LiveInteraction bit NOT NULL, FailureFlagged bit NOT NULL, Class varchar(40) NULL);

INSERT #P
SELECT t.TicketId, t.TicketNumber, t.TicketStatus, t.CurrentDepartmentId, t.OriginatingDepartmentId, t.CurrentOwnerEmployeeId, t.CreatedAtUtc,
       CASE WHEN EXISTS (SELECT 1 FROM dbo.TicketApprovals ap WHERE ap.TicketId = t.TicketId AND ap.Status = 1) THEN 1 ELSE 0 END,
       (SELECT COUNT(*) FROM dbo.TicketInteractions i WHERE i.TicketId = t.TicketId AND i.Source = 2 AND i.ChannelId = 1),
       CASE WHEN EXISTS (SELECT 1 FROM dbo.TicketInteractions i WHERE i.TicketId = t.TicketId AND i.RequiresHumanFollowUp = 1) THEN 1 ELSE 0 END,
       CASE WHEN EXISTS (SELECT 1 FROM dbo.TicketAgentHandoffs h WHERE h.TicketId = t.TicketId AND h.Status IN (2, 3, 4)) THEN 1 ELSE 0 END,
       CASE WHEN t.FirstHumanResponseAtUtc IS NOT NULL
              OR EXISTS (SELECT 1 FROM dbo.TicketInteractions i JOIN dbo.TicketInteractionMessages mm ON mm.TicketInteractionId = i.TicketInteractionId WHERE i.TicketId = t.TicketId AND mm.Sender = 2)
              OR EXISTS (SELECT 1 FROM dbo.TicketInteractionHandlers h WHERE h.TicketId = t.TicketId)
              OR EXISTS (SELECT 1 FROM dbo.TicketWorkflowEvents e WHERE e.TicketId = t.TicketId AND e.EventType IN (11, 12, 18, 19)) THEN 1 ELSE 0 END,
       (SELECT TOP (1) i.GenesysAgentId FROM dbo.TicketInteractions i WHERE i.TicketId = t.TicketId AND i.Source = 2 AND i.ChannelId = 1 AND NULLIF(LTRIM(RTRIM(i.GenesysAgentId)), N'') IS NOT NULL ORDER BY i.TicketInteractionId),
       0, NULL,
       (SELECT TOP (1) i.HandledByUserId FROM dbo.TicketInteractions i WHERE i.TicketId = t.TicketId AND i.HandledByUserId IS NOT NULL ORDER BY i.TicketInteractionId),
       CASE WHEN EXISTS (SELECT 1 FROM dbo.TicketInteractions i WHERE i.TicketId = t.TicketId AND i.Source = 2 AND i.ChannelId = 1 AND i.EndedAtUtc IS NULL AND i.InactivityClosedAtUtc IS NULL) THEN 1 ELSE 0 END,
       CASE WHEN EXISTS (SELECT 1 FROM dbo.TicketWorkflowEvents e WHERE e.TicketId = t.TicketId AND e.EventType = 20) THEN 1 ELSE 0 END,
       NULL
FROM dbo.Tickets t
WHERE EXISTS (SELECT 1 FROM dbo.TicketInteractions i WHERE i.TicketId = t.TicketId AND i.Source = 2 AND i.ChannelId = 1)
  AND (t.CreatedAtUtc >= DATEADD(day, -@LookbackDays, @Now) OR t.TicketNumber = @TicketNumber);

-- AspNetUsers.GenesysUserId is the ONLY key the application uses to map an agent (names and e-mails never match).
UPDATE #P SET AgentMapped = 1, MappedUserId = u.Id
FROM #P p JOIN dbo.AspNetUsers u ON UPPER(u.GenesysUserId) = UPPER(LTRIM(RTRIM(p.NamedAgentId)))
WHERE p.NamedAgentId IS NOT NULL;
-- (this UPDATE touches only the session temp table #P)

UPDATE #P SET Class =
    CASE WHEN p.OwnerId IS NOT NULL THEN 'HAS_OWNER'
         WHEN p.TicketStatus IN (5, 6) THEN 'NOT_ACTIVE'
         WHEN p.PendingApproval = 1 THEN 'PENDING_APPROVAL'
         WHEN p.NamedAgentId IS NOT NULL AND p.AgentMapped = 0 AND p.RequiresHuman = 0 AND p.OpenHandoff = 0 THEN 'PHONE_AGENT_ID_NOT_MAPPED'
         WHEN p.AgentMapped = 1 AND NOT EXISTS (SELECT 1 FROM #Elig x WHERE x.UserId = p.MappedUserId AND x.IsActive = 1 AND x.IsLockedOut = 0 AND x.IsCsAgent = 1 AND x.IsCcMember = 1)
              THEN 'PHONE_AGENT_MAPPED_NOT_ELIGIBLE'
         WHEN p.RequiresHuman = 0 AND p.OpenHandoff = 0 AND p.HumanEvidence = 0 AND p.NamedAgentId IS NULL THEN 'PHONE_NO_AGENT_NO_HUMAN_FLAG'
         WHEN p.DeptId <> ISNULL(@CcId, -1) THEN 'HUMAN_NOT_IN_CALL_CENTER'
         WHEN @PoolSize = 0 THEN 'HUMAN_IN_CC_EMPTY_POOL'
         ELSE 'HUMAN_IN_CC_POOL_AVAILABLE' END
FROM #P p;

-- ===========================================================================================================================
-- 1. HEADLINE - environment facts that decide everything else
-- ===========================================================================================================================
SELECT @@SERVERNAME AS [Server], DB_NAME() AS [Database], @Now AS DiagnosedAtUtc,
       @CcId AS CallCenterDepartmentId, (SELECT COUNT(*) FROM dbo.Departments WHERE Code = N'CC' AND IsActive = 1) AS ActiveCallCenterDepartments,   -- must be exactly 1
       @PoolSize AS EligibleCallCenterCsAgentsNow,
       (SELECT COUNT(*) FROM dbo.AspNetUsers u JOIN #Elig e ON e.UserId = u.Id WHERE e.IsActive = 1 AND e.IsCsAgent = 1 AND e.IsCcMember = 1
            AND (u.GenesysUserId IS NULL OR LTRIM(RTRIM(u.GenesysUserId)) = N'')) AS ActiveCcCsAgentsWithoutGenesysMapping,
       (SELECT COUNT(*) FROM (SELECT UPPER(GenesysUserId) g FROM dbo.AspNetUsers WHERE GenesysUserId IS NOT NULL GROUP BY UPPER(GenesysUserId) HAVING COUNT(*) > 1) q) AS DuplicateGenesysUserIds,
       @LookbackDays AS LookbackDays, (SELECT COUNT(*) FROM #P) AS PhoneTicketsInScope,
       (SELECT COUNT(*) FROM #P WHERE OwnerId IS NULL AND TicketStatus NOT IN (5, 6)) AS PhoneTicketsActiveUnowned,
       (SELECT COUNT(*) FROM #P WHERE OwnerId IS NULL AND TicketStatus NOT IN (5, 6) AND LiveInteraction = 1 AND HumanEvidence = 0) AS OfWhich_LiveAiPhoneNoEndEvent;

-- ===========================================================================================================================
-- 2. ROOT CAUSE COUNTS (the answer) for active unowned Genesys Phone tickets
-- ===========================================================================================================================
SELECT p.Class, COUNT(*) AS Tickets,
       SUM(CASE WHEN p.CreatedAtUtc >= DATEADD(day, -1, @Now) THEN 1 ELSE 0 END) AS CreatedLast24h,
       CASE p.Class
         WHEN 'PENDING_APPROVAL'                THEN 'HELD: approval process decides'
         WHEN 'PHONE_NO_AGENT_NO_HUMAN_FLAG'    THEN 'ROOT CAUSE A: Genesys sent no agentId and no humanFollowUpRequired, so ingestion treats the call as AI-handled (needsHuman=false): no Call Center routing, no CS Agent. Fix is in the Genesys flow payload and/or a Phone-specific routing rule - needs a decision'
         WHEN 'PHONE_AGENT_ID_NOT_MAPPED'       THEN 'ROOT CAUSE B: an agent id arrived but AspNetUsers.GenesysUserId has no match (unmapped agent): treated as possibly a bot, so no human signal. Fix is data: map the agent (result set 4)'
         WHEN 'PHONE_AGENT_MAPPED_NOT_ELIGIBLE' THEN 'ROOT CAUSE C: agent is mapped but is deactivated / locked out / not a CS Agent / not a Call Center member (result set 4)'
         WHEN 'HUMAN_NOT_IN_CALL_CENTER'        THEN 'ROOT CAUSE D: human signal present but the ticket never reached the Call Center (routing failed or never ran)'
         WHEN 'HUMAN_IN_CC_EMPTY_POOL'          THEN 'ROOT CAUSE E: no eligible CS Agent in the Call Center pool (check result set 1 EligibleCallCenterCsAgentsNow)'
         WHEN 'HUMAN_IN_CC_POOL_AVAILABLE'      THEN 'ROOT CAUSE F: pool is not empty but nobody was picked (lost race / crash / reconciler not run); inspect audit in result set 5'
         ELSE '' END AS Meaning
FROM #P p WHERE p.OwnerId IS NULL AND p.TicketStatus NOT IN (5, 6)
GROUP BY p.Class ORDER BY Tickets DESC;

-- ===========================================================================================================================
-- 3. THE NAMED TICKET - facts, one row (no customer data)
-- ===========================================================================================================================
SELECT p.TicketNumber, p.Class AS RootCauseClass, p.TicketStatus, d.Name AS CurrentDepartment, od.Name AS OriginatingDepartment,
       CASE WHEN p.DeptId = @CcId THEN 'YES' ELSE 'NO' END AS InCallCenter,
       p.OwnerId, p.CreatedAtUtc, DATEDIFF(minute, p.CreatedAtUtc, @Now) AS AgeMinutes,
       p.PhoneInteractions, p.RequiresHuman AS RequiresHumanFollowUp, p.OpenHandoff, p.HumanEvidence, p.LiveInteraction AS HasLiveInteraction,
       p.NamedAgentId AS GenesysAgentIdOnInteraction, p.AgentMapped AS AgentIdMapsToUser, p.MappedUserId, p.HandledByUserId,
       p.FailureFlagged AS ManualAssignmentRequiredFlagged,
       CASE WHEN EXISTS (SELECT 1 FROM dbo.TicketAssignments a WHERE a.TicketId = p.TicketId AND a.IsCurrent = 1) THEN 1 ELSE 0 END AS HasCurrentAssignmentRow
FROM #P p LEFT JOIN dbo.Departments d ON d.DepartmentId = p.DeptId LEFT JOIN dbo.Departments od ON od.DepartmentId = p.OriginDeptId
WHERE p.TicketNumber = @TicketNumber;

-- 3b. Its interactions (Genesys ids only; the customer phone/name/e-mail columns are deliberately not selected)
SELECT i.TicketInteractionId, i.ChannelId, i.Source, i.IsOriginatingInteraction, i.GenesysConversationId, i.GenesysQueueName, i.GenesysAgentId, i.GenesysAgentName,
       i.GenesysAgentUserId, i.HandledByUserId, i.RequiresHumanFollowUp, i.Direction, i.InteractionStartedAtUtc, i.CreatedAtUtc, i.EndedAtUtc, i.EndReason, i.InactivityClosedAtUtc
FROM dbo.TicketInteractions i JOIN #P p ON p.TicketId = i.TicketId WHERE p.TicketNumber = @TicketNumber ORDER BY i.TicketInteractionId;

-- ===========================================================================================================================
-- 4. AGENT MAPPING / ELIGIBILITY for the agent ids seen on unowned Phone tickets
-- ===========================================================================================================================
SELECT p.NamedAgentId AS GenesysAgentId, COUNT(*) AS UnownedPhoneTickets, MAX(p.AgentMapped * 1) AS MapsToUser, MAX(CONVERT(varchar(36), p.MappedUserId)) AS MappedUserId,
       MAX(CASE WHEN e.UserId IS NULL THEN NULL ELSE e.IsActive END) AS IsActive, MAX(e.IsLockedOut * 1) AS IsLockedOut, MAX(e.IsCsAgent * 1) AS IsCsAgent, MAX(e.IsCcMember * 1) AS IsCallCenterMember
FROM #P p LEFT JOIN #Elig e ON e.UserId = p.MappedUserId
WHERE p.OwnerId IS NULL AND p.TicketStatus NOT IN (5, 6) AND p.NamedAgentId IS NOT NULL
GROUP BY p.NamedAgentId ORDER BY UnownedPhoneTickets DESC;

-- ===========================================================================================================================
-- 5. WHAT THE APPLICATION RECORDED for the named ticket: audit trail + workflow events (shows HumanSignal / AgentMapping / AutoAssign reasons)
-- ===========================================================================================================================
SELECT a.OccurredAtUtc, N'Audit' AS Kind, a.Action, a.EntityType, LEFT(a.BeforeValue, 200) AS BeforeValue, LEFT(a.AfterValue, 900) AS AfterValue
FROM dbo.AuditEntries a JOIN #P p ON p.TicketNumber = @TicketNumber
WHERE (a.EntityType = N'Ticket' AND a.EntityId = CONVERT(nvarchar(50), p.TicketId))
   OR (a.EntityType = N'GenesysConversation' AND a.AfterValue LIKE N'TicketId=' + CONVERT(nvarchar(30), p.TicketId) + N';%')
UNION ALL
SELECT e.OccurredAtUtc, N'WorkflowEvent', CONVERT(nvarchar(20), e.EventType), NULL, NULL, LEFT(e.Note, 900)
FROM dbo.TicketWorkflowEvents e JOIN #P p ON p.TicketId = e.TicketId WHERE p.TicketNumber = @TicketNumber
ORDER BY OccurredAtUtc;

-- ===========================================================================================================================
-- 6. EVERY ACTIVE UNOWNED PHONE TICKET in scope (ticket numbers only), oldest first
-- ===========================================================================================================================
SELECT TOP (500) p.TicketNumber, p.Class AS RootCauseClass, d.Name AS CurrentDepartment, p.CreatedAtUtc, DATEDIFF(minute, p.CreatedAtUtc, @Now) AS AgeMinutes,
       p.RequiresHuman AS RequiresHumanFollowUp, p.NamedAgentId AS GenesysAgentId, p.AgentMapped AS AgentMapped, p.LiveInteraction, p.FailureFlagged
FROM #P p LEFT JOIN dbo.Departments d ON d.DepartmentId = p.DeptId
WHERE p.OwnerId IS NULL AND p.TicketStatus NOT IN (5, 6)
ORDER BY p.CreatedAtUtc;

DROP TABLE #P;
DROP TABLE #Elig;
