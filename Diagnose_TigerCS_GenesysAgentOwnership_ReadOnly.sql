/*
================================================================================
 Diagnose_TigerCS_GenesysAgentOwnership_ReadOnly.sql              (READ-ONLY - SELECT only)

 Does the CS Agent on each ACTIVE ticket (status 1..4) match the agent Genesys says actually handled the interaction?
 Nothing is written or corrected - this only reports. Any correction is a separately approved, dry-run-first operation.

 "Confirmed handling agent" = a TicketInteractionHandlers row whose Source is 1 (Inquiry: the ticket-creating event named the answering agent) or
 2 (Routing: routing.agentId = the agent CONNECTED). Sources 3 (Update) and 4 (ConversationEnd) are top-level agent context, NOT an acceptance, and a
 fallback (round-robin) assignment is never a handler row. The latest confirmed handler = highest ConnectedAtUtc (else RecordedAtUtc).

 Result sets
   1. Summary counts for every section below.
   2. MAPPING        duplicate AspNetUsers.GenesysUserId (case-insensitive), and active Call Center CS Agents with NO Genesys mapping.
   3. UNMAPPED       Genesys agent ids seen on active tickets' interactions that map to nobody (the agent can never own those tickets).
   4. MISMATCH       owner <> latest confirmed handler, with whether a person (supervisor / manager / the agent) set the current owner.
   5. NO_ELIGIBLE    active tickets with no owner, or whose owner is deactivated / locked out / holds no CS Agent role.
   6. CONFLICT       tickets with 2+ distinct confirmed handlers (a transfer / consult / conference TigerCS cannot resolve - Genesys sends no transfer state),
                     tickets with more than one current assignment row (must be 0), and tickets with a recorded Genesys call-transfer event.
   7. SUPERVISOR     current assignments made by SOMEONE ELSE (supervisor / manager / administrator override) - authoritative, never overwritten by Genesys.
================================================================================
*/
SET NOCOUNT ON;
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

DECLARE @CcId int = (SELECT TOP (1) DepartmentId FROM dbo.Departments WHERE Code = N'CC' AND IsActive = 1);
DECLARE @CsAgentRole nvarchar(256) = N'CS Agent';

CREATE TABLE #Active (TicketId bigint PRIMARY KEY, TicketNumber nvarchar(40), TicketStatus tinyint, DepartmentId int, OwnerId uniqueidentifier NULL, CreatedAtUtc datetime2);
INSERT #Active
SELECT TicketId, TicketNumber, TicketStatus, CurrentDepartmentId, CurrentOwnerEmployeeId, CreatedAtUtc
FROM dbo.Tickets WHERE TicketStatus BETWEEN 1 AND 4;

-- Latest confirmed handler per ticket (Source 1/2), and how many distinct confirmed handlers.
CREATE TABLE #H (TicketId bigint PRIMARY KEY, LatestHandlerId uniqueidentifier, LatestGenesysId nvarchar(64), LatestAtUtc datetime2, DistinctHandlers int);
INSERT #H
SELECT x.TicketId, x.HandledByUserId, x.GenesysAgentUserId, x.AtUtc, d.Cnt
FROM (SELECT h.TicketId, h.HandledByUserId, h.GenesysAgentUserId, COALESCE(h.ConnectedAtUtc, h.RecordedAtUtc) AS AtUtc,
             ROW_NUMBER() OVER (PARTITION BY h.TicketId ORDER BY COALESCE(h.ConnectedAtUtc, h.RecordedAtUtc) DESC, h.TicketInteractionHandlerId DESC) AS rn
      FROM dbo.TicketInteractionHandlers h
      JOIN #Active a ON a.TicketId = h.TicketId
      WHERE h.Source IN (1, 2) AND h.HandledByUserId IS NOT NULL) x
JOIN (SELECT h.TicketId, COUNT(DISTINCT h.HandledByUserId) AS Cnt
      FROM dbo.TicketInteractionHandlers h JOIN #Active a ON a.TicketId = h.TicketId
      WHERE h.Source IN (1, 2) AND h.HandledByUserId IS NOT NULL GROUP BY h.TicketId) d ON d.TicketId = x.TicketId
WHERE x.rn = 1;

-- Eligibility of a user as a CS Agent (database-visible part only: the application also excludes service identities, which live in configuration).
CREATE TABLE #Elig (UserId uniqueidentifier PRIMARY KEY, IsActive bit, IsLockedOut bit, IsCsAgent bit);
INSERT #Elig
SELECT u.Id,
       CASE WHEN e.EmployeeId IS NOT NULL AND e.DeactivatedAtUtc IS NULL THEN 1 ELSE 0 END,
       CASE WHEN u.LockoutEnd IS NOT NULL AND u.LockoutEnd > SYSDATETIMEOFFSET() THEN 1 ELSE 0 END,
       CASE WHEN EXISTS (SELECT 1 FROM dbo.AspNetUserRoles ur JOIN dbo.AspNetRoles r ON r.Id = ur.RoleId
                         WHERE ur.UserId = u.Id AND r.Name = @CsAgentRole) THEN 1 ELSE 0 END
FROM dbo.AspNetUsers u LEFT JOIN dbo.Employees e ON e.EmployeeId = u.Id;

-- 1. Summary
SELECT 'ActiveTickets' AS Section, COUNT(*) AS Tickets FROM #Active
UNION ALL SELECT 'MISMATCH_OwnerDiffersFromLatestConfirmedHandler', COUNT(*) FROM #Active a JOIN #H h ON h.TicketId = a.TicketId WHERE a.OwnerId IS NULL OR a.OwnerId <> h.LatestHandlerId
UNION ALL SELECT 'NO_ELIGIBLE_NoOwner', COUNT(*) FROM #Active WHERE OwnerId IS NULL
UNION ALL SELECT 'NO_ELIGIBLE_OwnerIneligible', COUNT(*) FROM #Active a JOIN #Elig e ON e.UserId = a.OwnerId WHERE e.IsActive = 0 OR e.IsLockedOut = 1 OR e.IsCsAgent = 0
UNION ALL SELECT 'CONFLICT_MultipleConfirmedHandlers', COUNT(*) FROM #H WHERE DistinctHandlers > 1
UNION ALL SELECT 'CONFLICT_MoreThanOneCurrentAssignment', COUNT(*) FROM (SELECT ta.TicketId FROM dbo.TicketAssignments ta JOIN #Active a ON a.TicketId = ta.TicketId WHERE ta.IsCurrent = 1 GROUP BY ta.TicketId HAVING COUNT(*) > 1) q
UNION ALL SELECT 'SUPERVISOR_OverrideAssignments', COUNT(*) FROM dbo.TicketAssignments ta JOIN #Active a ON a.TicketId = ta.TicketId
                  WHERE ta.IsCurrent = 1 AND ta.AssigningActorEmployeeId IS NOT NULL AND ta.AssigningActorEmployeeId <> ta.AssignedEmployeeId;

-- 2. Mapping integrity
SELECT 'DUPLICATE_GenesysUserId' AS Problem, UPPER(GenesysUserId) AS GenesysUserId, COUNT(*) AS Users
FROM dbo.AspNetUsers WHERE GenesysUserId IS NOT NULL GROUP BY UPPER(GenesysUserId) HAVING COUNT(*) > 1;

SELECT 'CC_CS_AGENT_WITHOUT_GENESYS_MAPPING' AS Problem, u.Id AS UserId, u.UserName
FROM dbo.AspNetUsers u
JOIN dbo.UserDepartmentAssignments uda ON uda.EmployeeId = u.Id AND uda.DepartmentId = @CcId
JOIN #Elig e ON e.UserId = u.Id AND e.IsActive = 1 AND e.IsCsAgent = 1
WHERE u.GenesysUserId IS NULL OR LTRIM(RTRIM(u.GenesysUserId)) = N'';

-- 3. Genesys agent ids on active tickets that map to nobody
SELECT i.GenesysAgentId AS UnmappedGenesysAgentId, COUNT(DISTINCT i.TicketId) AS ActiveTickets, MIN(i.CreatedAtUtc) AS FirstSeenUtc, MAX(i.CreatedAtUtc) AS LastSeenUtc
FROM dbo.TicketInteractions i JOIN #Active a ON a.TicketId = i.TicketId
WHERE i.Source = 2 AND i.GenesysAgentId IS NOT NULL AND LTRIM(RTRIM(i.GenesysAgentId)) <> N'' AND i.HandledByUserId IS NULL
  AND NOT EXISTS (SELECT 1 FROM dbo.AspNetUsers u WHERE u.GenesysUserId = i.GenesysAgentId)
GROUP BY i.GenesysAgentId ORDER BY ActiveTickets DESC;

-- 4. Owner differs from the latest confirmed handler
SELECT a.TicketId, a.TicketNumber, a.TicketStatus, d.Name AS Department, a.OwnerId AS CurrentOwner, h.LatestHandlerId AS ConfirmedHandler,
       h.LatestGenesysId, h.LatestAtUtc AS ConfirmedAtUtc, h.DistinctHandlers,
       CASE WHEN ta.AssigningActorEmployeeId IS NULL THEN 'AUTOMATIC (round robin / rule)'
            WHEN ta.AssigningActorEmployeeId = ta.AssignedEmployeeId THEN 'SELF / ANSWERING AGENT'
            ELSE 'SET BY SOMEONE ELSE (supervisor override?)' END AS CurrentOwnerSetBy
FROM #Active a
JOIN #H h ON h.TicketId = a.TicketId
JOIN dbo.Departments d ON d.DepartmentId = a.DepartmentId
LEFT JOIN dbo.TicketAssignments ta ON ta.TicketId = a.TicketId AND ta.IsCurrent = 1
WHERE a.OwnerId IS NULL OR a.OwnerId <> h.LatestHandlerId
ORDER BY a.CreatedAtUtc, a.TicketId;

-- 5. No eligible CS Agent
SELECT a.TicketId, a.TicketNumber, a.TicketStatus, d.Name AS Department, a.OwnerId,
       CASE WHEN a.OwnerId IS NULL THEN 'NO_OWNER'
            WHEN e.UserId IS NULL THEN 'OWNER_NOT_A_USER'
            WHEN e.IsActive = 0 THEN 'OWNER_DEACTIVATED'
            WHEN e.IsLockedOut = 1 THEN 'OWNER_LOCKED_OUT'
            ELSE 'OWNER_HAS_NO_CS_AGENT_ROLE' END AS Problem,
       CASE WHEN EXISTS (SELECT 1 FROM dbo.TicketWorkflowEvents w WHERE w.TicketId = a.TicketId AND w.EventType = 20) THEN 1 ELSE 0 END AS FailureFlagged
FROM #Active a
JOIN dbo.Departments d ON d.DepartmentId = a.DepartmentId
LEFT JOIN #Elig e ON e.UserId = a.OwnerId
WHERE a.OwnerId IS NULL OR e.UserId IS NULL OR e.IsActive = 0 OR e.IsLockedOut = 1 OR e.IsCsAgent = 0
ORDER BY a.CreatedAtUtc, a.TicketId;

-- 6. Conflicting ownership evidence
SELECT 'MULTIPLE_CONFIRMED_HANDLERS' AS Conflict, h.TicketId, h.DistinctHandlers,
       (SELECT COUNT(*) FROM dbo.TicketWorkflowEvents w WHERE w.TicketId = h.TicketId AND w.EventType = 19) AS GenesysCallTransferEvents
FROM #H h WHERE h.DistinctHandlers > 1
UNION ALL
SELECT 'MORE_THAN_ONE_CURRENT_ASSIGNMENT', ta.TicketId, COUNT(*), 0
FROM dbo.TicketAssignments ta JOIN #Active a ON a.TicketId = ta.TicketId WHERE ta.IsCurrent = 1 GROUP BY ta.TicketId HAVING COUNT(*) > 1
UNION ALL
SELECT 'ANSWER_ASSIGNMENTS_TO_DIFFERENT_AGENTS', CONVERT(bigint, au.EntityId), COUNT(DISTINCT au.ActorEmployeeId), 0
FROM dbo.AuditEntries au JOIN #Active a ON CONVERT(nvarchar(40), a.TicketId) = au.EntityId
WHERE au.Action = N'AssignOnAnswer' AND au.EntityType = N'Ticket' GROUP BY au.EntityId HAVING COUNT(DISTINCT au.ActorEmployeeId) > 1;

-- 7. Supervisor / manager / administrator overrides (authoritative until a new authorized reassignment)
SELECT a.TicketId, a.TicketNumber, a.TicketStatus, d.Name AS Department, ta.AssignedEmployeeId AS Owner, ta.AssigningActorEmployeeId AS AssignedBy,
       ta.AssignedAtUtc, h.LatestHandlerId AS ConfirmedHandler,
       CASE WHEN h.LatestHandlerId IS NOT NULL AND h.LatestHandlerId <> ta.AssignedEmployeeId THEN 'DIFFERS_FROM_GENESYS_HANDLER_(OVERRIDE_STANDS)' ELSE '' END AS Note
FROM dbo.TicketAssignments ta
JOIN #Active a ON a.TicketId = ta.TicketId
JOIN dbo.Departments d ON d.DepartmentId = a.DepartmentId
LEFT JOIN #H h ON h.TicketId = a.TicketId
WHERE ta.IsCurrent = 1 AND ta.AssigningActorEmployeeId IS NOT NULL AND ta.AssigningActorEmployeeId <> ta.AssignedEmployeeId
ORDER BY ta.AssignedAtUtc;

DROP TABLE #Elig; DROP TABLE #H; DROP TABLE #Active;
