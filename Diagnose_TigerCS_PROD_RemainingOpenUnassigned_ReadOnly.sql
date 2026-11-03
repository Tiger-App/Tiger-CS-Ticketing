/*
================================================================================
 Diagnose_TigerCS_PROD_RemainingOpenUnassigned_ReadOnly.sql        (SELECT ONLY - NO WRITES)
 Database : TigerCS_Ticketing_Prod

 WHY: after the one-time backlog distribution, Open tickets such as TG-LCS-20261102-0021, TG-REG-20261102-0026 and TG-ACC-20261102-0023 still
      have no employee owner (department queue only). This script classifies EVERY Open ticket with CurrentOwnerEmployeeId IS NULL, in every
      department, with exact counts and root causes. It uses the same taxonomy, in the same order, as the application's
      UnassignedTicketClassifier (src/TigerCS.Application/Modules/Ticketing/Services/UnassignedTicketClassifier.cs) so these numbers can be
      compared one to one with the reconciler's dry-run log.

 READ-ONLY: only SELECTs and session temp tables (tempdb); READ UNCOMMITTED; safe from a db_datareader login at any time.
 It never calls the application, never rewrites a status, owner, assignment, interaction or audit row, and does not re-run the backlog batch.

 CLASSES (first match wins - identical to the classifier):
   NOT_OWNED_SCOPE            only Open (TicketStatus = 1) tickets with no owner are examined
   PENDING_APPROVAL           a pending approval (TicketApprovals.Status = 1): held until the approval process completes
   TRANSFERRED_OUT_OF_CC      the Call Center transferred it onward (TicketTransferred event From = CC) or it originated in CC and left: department queue is by design
   RELEASED_BY_PERSON         in CC, a person's audited Unassign released it: left for that person
   HUMAN_ANSWERED_MAPPED      a mapped Genesys agent answered (TicketInteractionHandlers row / HandledByUserId): assign to THAT agent
   HUMAN_ANSWERED_UNMAPPED    a Phone interaction names a Genesys agent id that resolves to no Ticketing user (missing agent mapping): a person answered -> Round Robin
   HUMAN_REQUIRED_NOT_ROUTED  RequiresHumanFollowUp / open handoff (Waiting, Assigned, InProgress) / human evidence, but the ticket never reached the CC: Genesys routing failed or never ran
   HUMAN_REQUIRED_IN_CC       same, ticket is in the CC but nobody was picked (no eligible agent at that moment, lost race, crash)
   MANUAL_OR_INTEGRATION      no Genesys conversation at all (manual creation, integration): always human work, still in a department queue
   AI_ACTIVE                  live Genesys interaction (no end, no inactivity close), no human signal, activity within @StaleAfterHours: PROTECTED
   AI_STALE_NO_END_EVENT      same but silent longer than @StaleAfterHours: its end event never arrived (reported; not human work by itself)
   TOO_NEW_STILL_CREATING     human work younger than @MinTicketAgeMinutes: its own creation request may still be routing it
   AI_ENDED                   the AI conversation ended or was closed for inactivity: completed AI ticket, PRESERVED

 Each ticket is also tagged AFTER_BULK = created after the BacklogDistributionBatch audit row (tickets the batch could never have seen).
================================================================================
*/
SET NOCOUNT ON;
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

DECLARE @ExpectedDb sysname = N'TigerCS_Ticketing_Prod';
IF DB_NAME() <> @ExpectedDb
BEGIN
    DECLARE @m nvarchar(400) = N'ABORTED: connected to [' + ISNULL(DB_NAME(), N'?') + N'], this diagnostic is for [' + @ExpectedDb + N'] only.';
    THROW 50600, @m, 1;
END;

DECLARE @StaleAfterHours int = 24;       -- a live-looking AI conversation silent longer than this has lost its end event
DECLARE @MinTicketAgeMinutes int = 3;    -- the reconciler waits this long after creation (a ticket still being created is not "stuck")
DECLARE @Now datetime2 = SYSUTCDATETIME();

DECLARE @CcId int = (SELECT TOP (1) DepartmentId FROM dbo.Departments WHERE Code = N'CC' AND IsActive = 1);
DECLARE @BatchAtUtc datetime2 = (SELECT MIN(OccurredAtUtc) FROM dbo.AuditEntries WHERE Action = N'BacklogDistributionBatch');

-- ---------------------------------------------------------------------------------------------------------------------------
-- evidence, one row per Open unowned ticket
-- ---------------------------------------------------------------------------------------------------------------------------
CREATE TABLE #U (
    TicketId bigint NOT NULL PRIMARY KEY, TicketNumber nvarchar(80) NULL, DeptId int NOT NULL, OriginDeptId int NOT NULL, CreatedAtUtc datetime2 NOT NULL,
    HasCurrentAssignmentRow bit NOT NULL, PendingApproval bit NOT NULL,
    HasGenesys bit NOT NULL, LiveGenesys bit NOT NULL, RequiresHuman bit NOT NULL, OpenHandoff bit NOT NULL, HumanEvidence bit NOT NULL,
    AnsweringEmployeeId uniqueidentifier NULL, PhoneUnmappedAgent bit NOT NULL, UnmappedGenesysAgentId nvarchar(100) NULL,
    PhoneInteraction bit NOT NULL, LastActivityUtc datetime2 NOT NULL, LeftCc bit NOT NULL, ReleasedByPerson bit NOT NULL,
    ChannelId tinyint NULL, FailedOrCancelledHandoff bit NOT NULL, FlaggedManualAssignment bit NOT NULL,
    Class varchar(30) NULL);

INSERT #U (TicketId, TicketNumber, DeptId, OriginDeptId, CreatedAtUtc, HasCurrentAssignmentRow, PendingApproval, HasGenesys, LiveGenesys, RequiresHuman, OpenHandoff, HumanEvidence,
           AnsweringEmployeeId, PhoneUnmappedAgent, UnmappedGenesysAgentId, PhoneInteraction, LastActivityUtc, LeftCc, ReleasedByPerson, ChannelId, FailedOrCancelledHandoff, FlaggedManualAssignment)
SELECT t.TicketId, t.TicketNumber, t.CurrentDepartmentId, t.OriginatingDepartmentId, t.CreatedAtUtc,
       CASE WHEN EXISTS (SELECT 1 FROM dbo.TicketAssignments a WHERE a.TicketId = t.TicketId AND a.IsCurrent = 1) THEN 1 ELSE 0 END,
       CASE WHEN EXISTS (SELECT 1 FROM dbo.TicketApprovals ap WHERE ap.TicketId = t.TicketId AND ap.Status = 1) THEN 1 ELSE 0 END,
       CASE WHEN EXISTS (SELECT 1 FROM dbo.TicketInteractions i WHERE i.TicketId = t.TicketId AND i.Source = 2) THEN 1 ELSE 0 END,
       CASE WHEN EXISTS (SELECT 1 FROM dbo.TicketInteractions i WHERE i.TicketId = t.TicketId AND i.Source = 2 AND i.EndedAtUtc IS NULL AND i.InactivityClosedAtUtc IS NULL) THEN 1 ELSE 0 END,
       CASE WHEN EXISTS (SELECT 1 FROM dbo.TicketInteractions i WHERE i.TicketId = t.TicketId AND i.RequiresHumanFollowUp = 1) THEN 1 ELSE 0 END,
       CASE WHEN EXISTS (SELECT 1 FROM dbo.TicketAgentHandoffs h WHERE h.TicketId = t.TicketId AND h.Status IN (2, 3, 4)) THEN 1 ELSE 0 END,
       CASE WHEN t.FirstHumanResponseAtUtc IS NOT NULL
              OR EXISTS (SELECT 1 FROM dbo.TicketInteractions i JOIN dbo.TicketInteractionMessages mm ON mm.TicketInteractionId = i.TicketInteractionId WHERE i.TicketId = t.TicketId AND mm.Sender = 2)
              OR EXISTS (SELECT 1 FROM dbo.TicketWorkflowEvents e WHERE e.TicketId = t.TicketId AND e.EventType IN (11, 12, 18, 19)) THEN 1 ELSE 0 END,
       COALESCE((SELECT TOP (1) h.HandledByUserId FROM dbo.TicketInteractionHandlers h WHERE h.TicketId = t.TicketId ORDER BY ISNULL(h.ConnectedAtUtc, h.RecordedAtUtc) DESC),
                (SELECT TOP (1) i.HandledByUserId FROM dbo.TicketInteractions i WHERE i.TicketId = t.TicketId AND i.HandledByUserId IS NOT NULL)),
       0, NULL,
       CASE WHEN EXISTS (SELECT 1 FROM dbo.TicketInteractions i WHERE i.TicketId = t.TicketId AND i.Source = 2 AND i.ChannelId = 1) THEN 1 ELSE 0 END,
       (SELECT MAX(v) FROM (VALUES
            ((SELECT MAX(z.v) FROM dbo.TicketInteractions i CROSS APPLY (VALUES (i.CreatedAtUtc), (i.InteractionStartedAtUtc), (i.LastCustomerInteractionAtUtc), (i.AwaitingCustomerReplySinceUtc)) z(v) WHERE i.TicketId = t.TicketId)),
            ((SELECT MAX(mm.SentAtUtc) FROM dbo.TicketInteractions i JOIN dbo.TicketInteractionMessages mm ON mm.TicketInteractionId = i.TicketInteractionId WHERE i.TicketId = t.TicketId)),
            ((SELECT MAX(e.OccurredAtUtc) FROM dbo.TicketWorkflowEvents e WHERE e.TicketId = t.TicketId AND e.EventType BETWEEN 10 AND 19)),
            (t.CreatedAtUtc)) a(v)),
       CASE WHEN @CcId IS NOT NULL AND (EXISTS (SELECT 1 FROM dbo.TicketWorkflowEvents e WHERE e.TicketId = t.TicketId AND e.EventType = 17 AND e.FromDepartmentId = @CcId)
                                         OR (t.OriginatingDepartmentId = @CcId AND t.CurrentDepartmentId <> @CcId)) THEN 1 ELSE 0 END,
       CASE WHEN EXISTS (SELECT 1 FROM dbo.AuditEntries a WHERE a.EntityType = N'Ticket' AND a.EntityId = CONVERT(nvarchar(50), t.TicketId) AND a.Action = N'Unassign' AND a.ActorEmployeeId IS NOT NULL) THEN 1 ELSE 0 END,
       (SELECT TOP (1) i.ChannelId FROM dbo.TicketInteractions i WHERE i.TicketId = t.TicketId ORDER BY i.TicketInteractionId),
       CASE WHEN EXISTS (SELECT 1 FROM dbo.TicketAgentHandoffs h WHERE h.TicketId = t.TicketId AND h.Status = 6) THEN 1 ELSE 0 END,
       CASE WHEN EXISTS (SELECT 1 FROM dbo.TicketWorkflowEvents e WHERE e.TicketId = t.TicketId AND e.Note LIKE N'%Left in the Call Center queue for manual assignment%') THEN 1 ELSE 0 END
FROM dbo.Tickets t
WHERE t.TicketStatus = 1 AND t.CurrentOwnerEmployeeId IS NULL;

-- a Phone interaction whose agent id maps to nobody (AspNetUsers.GenesysUserId is the ONLY key the application uses)
UPDATE #U SET PhoneUnmappedAgent = 1,
             UnmappedGenesysAgentId = (SELECT TOP (1) i.GenesysAgentId FROM dbo.TicketInteractions i WHERE i.TicketId = u.TicketId AND i.Source = 2 AND i.ChannelId = 1 AND NULLIF(i.GenesysAgentId, N'') IS NOT NULL AND i.HandledByUserId IS NULL)
FROM #U u
WHERE u.AnsweringEmployeeId IS NULL
  AND EXISTS (SELECT 1 FROM dbo.TicketInteractions i WHERE i.TicketId = u.TicketId AND i.Source = 2 AND i.ChannelId = 1 AND NULLIF(i.GenesysAgentId, N'') IS NOT NULL AND i.HandledByUserId IS NULL);
-- (this UPDATE touches only the session temp table #U)

UPDATE #U SET Class =
    CASE WHEN PendingApproval = 1 THEN 'PENDING_APPROVAL'
         WHEN LeftCc = 1 AND DeptId <> ISNULL(@CcId, -1) THEN 'TRANSFERRED_OUT_OF_CC'
         WHEN DeptId = ISNULL(@CcId, -1) AND ReleasedByPerson = 1 THEN 'RELEASED_BY_PERSON'
         WHEN AnsweringEmployeeId IS NOT NULL THEN 'HUMAN_ANSWERED_MAPPED'
         WHEN PhoneUnmappedAgent = 1 THEN 'HUMAN_ANSWERED_UNMAPPED'
         WHEN RequiresHuman = 1 OR OpenHandoff = 1 OR HumanEvidence = 1 THEN CASE WHEN DeptId = ISNULL(@CcId, -1) THEN 'HUMAN_REQUIRED_IN_CC' ELSE 'HUMAN_REQUIRED_NOT_ROUTED' END
         WHEN HasGenesys = 0 THEN CASE WHEN DeptId = ISNULL(@CcId, -1) THEN 'HUMAN_REQUIRED_IN_CC' ELSE 'MANUAL_OR_INTEGRATION' END
         WHEN LiveGenesys = 0 THEN 'AI_ENDED'
         WHEN LastActivityUtc >= DATEADD(hour, -@StaleAfterHours, @Now) THEN 'AI_ACTIVE'
         ELSE 'AI_STALE_NO_END_EVENT' END;
-- A ticket younger than @MinTicketAgeMinutes is still inside its own creation flow.
UPDATE #U SET Class = 'TOO_NEW_STILL_CREATING' WHERE CreatedAtUtc > DATEADD(minute, -@MinTicketAgeMinutes, @Now) AND Class IN ('MANUAL_OR_INTEGRATION', 'HUMAN_REQUIRED_NOT_ROUTED', 'HUMAN_REQUIRED_IN_CC', 'HUMAN_ANSWERED_MAPPED', 'HUMAN_ANSWERED_UNMAPPED');

-- ===========================================================================================================================
-- 1. HEADLINE
-- ===========================================================================================================================
SELECT @@SERVERNAME AS [Server], DB_NAME() AS [Database], @Now AS DiagnosedAtUtc,
       (SELECT COUNT(*) FROM dbo.Tickets WHERE TicketStatus = 1) AS OpenTickets,
       (SELECT COUNT(*) FROM dbo.Tickets WHERE TicketStatus = 1 AND CurrentOwnerEmployeeId IS NOT NULL) AS OpenOwned_NeverTouchedByAnyFix,
       (SELECT COUNT(*) FROM #U) AS OpenUnowned_Examined,
       @CcId AS CallCenterDepartmentId, (SELECT COUNT(*) FROM dbo.Departments WHERE Code = N'CC' AND IsActive = 1) AS ActiveCallCenterDepartments,
       @BatchAtUtc AS BacklogDistributionBatchAtUtc,
       (SELECT COUNT(*) FROM #U WHERE @BatchAtUtc IS NOT NULL AND CreatedAtUtc > @BatchAtUtc) AS UnownedCreatedAfterBulk,
       (SELECT COUNT(*) FROM #U WHERE @BatchAtUtc IS NOT NULL AND CreatedAtUtc <= @BatchAtUtc) AS UnownedCreatedBeforeBulk,
       (SELECT COUNT(*) FROM #U WHERE HasCurrentAssignmentRow = 1) AS UnownedButHasCurrentAssignmentRow_DataDrift;

-- ===========================================================================================================================
-- 2. COUNTS BY ROOT CAUSE (the answer): class x created before/after the bulk batch
-- ===========================================================================================================================
SELECT Class,
       COUNT(*) AS Tickets,
       SUM(CASE WHEN @BatchAtUtc IS NOT NULL AND CreatedAtUtc > @BatchAtUtc THEN 1 ELSE 0 END) AS CreatedAfterBulk,
       SUM(CASE WHEN @BatchAtUtc IS NULL OR CreatedAtUtc <= @BatchAtUtc THEN 1 ELSE 0 END) AS CreatedBeforeBulk,
       CASE Class
         WHEN 'PENDING_APPROVAL'            THEN 'HELD: approval process decides'
         WHEN 'TRANSFERRED_OUT_OF_CC'       THEN 'BY DESIGN: Call Center transferred it onward; destination queue (check the destination has eligible members)'
         WHEN 'RELEASED_BY_PERSON'          THEN 'LEFT: a person released it to the queue'
         WHEN 'HUMAN_ANSWERED_MAPPED'       THEN 'FIX: assign to the agent who answered'
         WHEN 'HUMAN_ANSWERED_UNMAPPED'     THEN 'FIX: Round Robin (and map the Genesys user id)'
         WHEN 'HUMAN_REQUIRED_NOT_ROUTED'   THEN 'FIX: failed/never-run Genesys routing -> Call Center Round Robin'
         WHEN 'HUMAN_REQUIRED_IN_CC'        THEN 'FIX: in Call Center, nobody picked -> Round Robin'
         WHEN 'MANUAL_OR_INTEGRATION'       THEN 'FIX: human work stuck in a department queue -> Call Center Round Robin'
         WHEN 'TOO_NEW_STILL_CREATING'      THEN 'WAIT: younger than @MinTicketAgeMinutes, its own creation flow may still route it'
         WHEN 'AI_ACTIVE'                   THEN 'PRESERVE: active AI conversation'
         WHEN 'AI_STALE_NO_END_EVENT'       THEN 'REPORT: AI ticket whose end event never arrived (not human work by itself)'
         WHEN 'AI_ENDED'                    THEN 'PRESERVE: completed AI ticket' END AS Action
FROM #U GROUP BY Class ORDER BY Tickets DESC;

-- ===========================================================================================================================
-- 3. BY DEPARTMENT (current queue) x class, and by originating channel
-- ===========================================================================================================================
SELECT d.Code AS Department, u.Class, COUNT(*) AS Tickets
FROM #U u JOIN dbo.Departments d ON d.DepartmentId = u.DeptId GROUP BY d.Code, u.Class ORDER BY d.Code, Tickets DESC;

SELECT ISNULL(c.Name, N'(no interaction row)') AS FirstInteractionChannel, u.Class, COUNT(*) AS Tickets
FROM #U u LEFT JOIN dbo.Channels c ON c.ChannelId = u.ChannelId GROUP BY c.Name, u.Class ORDER BY FirstInteractionChannel, Tickets DESC;

-- ===========================================================================================================================
-- 4. THE ENVIRONMENT THAT DECIDES WHETHER ROUTING CAN WORK (why a human ticket would have stayed unassigned at creation)
-- ===========================================================================================================================
CREATE TABLE #Pool (EmployeeId uniqueidentifier NOT NULL PRIMARY KEY, CsAgent bit NOT NULL);
INSERT #Pool
SELECT e.EmployeeId,
       CASE WHEN EXISTS (SELECT 1 FROM dbo.AspNetUserRoles ur JOIN dbo.AspNetRoles r ON r.Id = ur.RoleId WHERE ur.UserId = e.EmployeeId AND r.Name = N'CS Agent') THEN 1 ELSE 0 END
FROM dbo.Employees e JOIN dbo.AspNetUsers u ON u.Id = e.EmployeeId
WHERE @CcId IS NOT NULL AND e.DeactivatedAtUtc IS NULL
  AND EXISTS (SELECT 1 FROM dbo.UserDepartmentAssignments a WHERE a.EmployeeId = e.EmployeeId AND a.DepartmentId = @CcId)
  AND NOT (u.LockoutEnabled = 1 AND u.LockoutEnd IS NOT NULL AND u.LockoutEnd > SYSDATETIMEOFFSET())
  AND EXISTS (SELECT 1 FROM dbo.AspNetUserRoles ur JOIN dbo.AspNetRoles r ON r.Id = ur.RoleId WHERE ur.UserId = e.EmployeeId AND r.Name IN (N'CS Agent', N'Department Employee', N'Department Head'));

SELECT (SELECT COUNT(*) FROM dbo.UserDepartmentAssignments WHERE DepartmentId = @CcId) AS CcMembers_All,
       (SELECT COUNT(*) FROM #Pool) AS CcMembers_EligibleForRoundRobin_RoleAndAccount,
       (SELECT COUNT(*) FROM #Pool WHERE CsAgent = 1) AS CcMembers_EligibleAndCsAgentRole,
       ISNULL((SELECT AllowAssignment FROM dbo.DepartmentWorkflowSettings WHERE DepartmentId = @CcId), 1) AS CcAllowAssignment,
       (SELECT LastAssignedEmployeeId FROM dbo.DepartmentAssignmentRotations WHERE DepartmentId = @CcId) AS CcRotationCursor,
       (SELECT COUNT(*) FROM dbo.AspNetUsers WHERE NULLIF(GenesysUserId, N'') IS NOT NULL) AS UsersWithGenesysUserIdMapped,
       (SELECT COUNT(*) FROM #Pool p JOIN dbo.AspNetUsers u ON u.Id = p.EmployeeId WHERE NULLIF(u.GenesysUserId, N'') IS NULL) AS EligibleCcAgents_WithNoGenesysUserId;

-- departments that received transferred tickets but have no one Round Robin could ever pick (every transferred ticket there stays queued)
SELECT d.Code AS Department,
       (SELECT COUNT(*) FROM dbo.UserDepartmentAssignments a JOIN dbo.Employees e ON e.EmployeeId = a.EmployeeId
         WHERE a.DepartmentId = d.DepartmentId AND e.DeactivatedAtUtc IS NULL
           AND EXISTS (SELECT 1 FROM dbo.AspNetUserRoles ur JOIN dbo.AspNetRoles r ON r.Id = ur.RoleId WHERE ur.UserId = e.EmployeeId AND r.Name IN (N'CS Agent', N'Department Employee', N'Department Head'))) AS EligibleMembers,
       ISNULL(ws.AllowAssignment, 1) AS AllowAssignment,
       (SELECT COUNT(*) FROM #U u WHERE u.DeptId = d.DepartmentId AND u.Class = 'TRANSFERRED_OUT_OF_CC') AS TransferredOutTicketsQueued
FROM dbo.Departments d LEFT JOIN dbo.DepartmentWorkflowSettings ws ON ws.DepartmentId = d.DepartmentId
WHERE d.DepartmentId <> ISNULL(@CcId, -1) AND EXISTS (SELECT 1 FROM #U u WHERE u.DeptId = d.DepartmentId)
ORDER BY d.Code;

-- Phone calls answered by an agent id that maps to nobody: the exact ids to add to AspNetUsers.GenesysUserId (missing agent mapping)
SELECT UnmappedGenesysAgentId, COUNT(*) AS Tickets FROM #U WHERE Class = 'HUMAN_ANSWERED_UNMAPPED' GROUP BY UnmappedGenesysAgentId ORDER BY Tickets DESC;

-- failed Genesys routing evidence
SELECT (SELECT COUNT(*) FROM #U WHERE FailedOrCancelledHandoff = 1) AS UnownedWithCancelledHandoff,
       (SELECT COUNT(*) FROM #U WHERE FlaggedManualAssignment = 1) AS UnownedFlaggedManualAssignmentRequired,
       (SELECT COUNT(*) FROM #U WHERE Class = 'HUMAN_REQUIRED_NOT_ROUTED' AND RequiresHuman = 1) AS HumanFlagSetButNeverReachedCallCenter,
       (SELECT COUNT(*) FROM #U WHERE Class = 'HUMAN_REQUIRED_NOT_ROUTED' AND OpenHandoff = 1) AS OpenHandoffButNeverReachedCallCenter;

-- end-event health (is "never ended" the norm - an end feed / inactivity job that does not run - or recent?)
SELECT CONVERT(char(7), i.CreatedAtUtc, 120) AS InteractionMonth, COUNT(*) AS GenesysInteractions,
       SUM(CASE WHEN i.EndedAtUtc IS NOT NULL THEN 1 ELSE 0 END) AS Ended, SUM(CASE WHEN i.InactivityClosedAtUtc IS NOT NULL THEN 1 ELSE 0 END) AS InactivityClosed,
       SUM(CASE WHEN i.EndedAtUtc IS NULL AND i.InactivityClosedAtUtc IS NULL THEN 1 ELSE 0 END) AS NeverEnded
FROM dbo.TicketInteractions i WHERE i.Source = 2 GROUP BY CONVERT(char(7), i.CreatedAtUtc, 120) ORDER BY 1;

-- ===========================================================================================================================
-- 5. WHAT THE PROPOSED SAFE CORRECTION WOULD TOUCH (read-only projection; nothing is written)
-- ===========================================================================================================================
SELECT (SELECT COUNT(*) FROM #U WHERE Class IN ('HUMAN_ANSWERED_MAPPED')) AS ToAnsweringAgent,
       (SELECT COUNT(*) FROM #U WHERE Class IN ('HUMAN_ANSWERED_UNMAPPED', 'HUMAN_REQUIRED_NOT_ROUTED', 'HUMAN_REQUIRED_IN_CC', 'MANUAL_OR_INTEGRATION')) AS ToRoundRobin,
       (SELECT COUNT(*) FROM #U WHERE Class IN ('HUMAN_ANSWERED_MAPPED', 'HUMAN_ANSWERED_UNMAPPED', 'HUMAN_REQUIRED_NOT_ROUTED', 'HUMAN_REQUIRED_IN_CC', 'MANUAL_OR_INTEGRATION')) AS TotalWouldBeAssigned,
       (SELECT COUNT(*) FROM #U WHERE Class IN ('HUMAN_ANSWERED_MAPPED', 'HUMAN_ANSWERED_UNMAPPED', 'HUMAN_REQUIRED_NOT_ROUTED', 'HUMAN_REQUIRED_IN_CC', 'MANUAL_OR_INTEGRATION') AND DeptId <> ISNULL(@CcId, -1)) AS ofWhichMovedToCallCenter,
       (SELECT COUNT(*) FROM #U WHERE Class IN ('AI_ACTIVE', 'AI_ENDED', 'AI_STALE_NO_END_EVENT', 'PENDING_APPROVAL', 'TRANSFERRED_OUT_OF_CC', 'RELEASED_BY_PERSON', 'TOO_NEW_STILL_CREATING')) AS StayExactlyAsTheyAre,
       (SELECT COUNT(*) FROM #Pool) AS EligibleAgents, @@SERVERNAME AS [Server];

-- ===========================================================================================================================
-- 6. EVERY EXAMINED TICKET (ticket numbers only - no customer data)
-- ===========================================================================================================================
SELECT u.TicketNumber, d.Code AS CurrentDepartment, od.Code AS OriginatingDepartment, u.Class,
       CASE WHEN @BatchAtUtc IS NOT NULL AND u.CreatedAtUtc > @BatchAtUtc THEN 'AFTER_BULK' ELSE 'BEFORE_BULK' END AS CreatedRelativeToBulk,
       u.CreatedAtUtc, u.LastActivityUtc, CAST(DATEDIFF(minute, u.LastActivityUtc, @Now) / 60.0 AS decimal(12, 1)) AS SilentHours,
       u.HasGenesys, u.LiveGenesys, u.RequiresHuman, u.OpenHandoff, u.HumanEvidence, u.PhoneInteraction, u.UnmappedGenesysAgentId, u.HasCurrentAssignmentRow
FROM #U u JOIN dbo.Departments d ON d.DepartmentId = u.DeptId JOIN dbo.Departments od ON od.DepartmentId = u.OriginDeptId
ORDER BY CASE WHEN u.Class LIKE 'HUMAN%' OR u.Class = 'MANUAL_OR_INTEGRATION' THEN 0 ELSE 1 END, u.CreatedAtUtc, u.TicketId;

-- the three tickets named in the incident, wherever they are
SELECT t.TicketNumber, t.TicketStatus, t.CurrentDepartmentId, t.CurrentOwnerEmployeeId, u.Class
FROM dbo.Tickets t LEFT JOIN #U u ON u.TicketId = t.TicketId
WHERE t.TicketNumber IN (N'TG-LCS-20261102-0021', N'TG-REG-20261102-0026', N'TG-ACC-20261102-0023');

DROP TABLE #Pool; DROP TABLE #U;
