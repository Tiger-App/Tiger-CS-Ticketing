/*
================================================================================
 Diagnose_TigerCS_PROD_UnownedOpenTickets_ReadOnly.sql      (READ-ONLY - SELECT only)
 Database : TigerCS_Ticketing_Prod

 Why does an Open ticket sit in a department queue with NO owner?  One row per ticket, with the FIRST cause that applies, in the order the
 application evaluates routing. Nothing is written; temp tables live in tempdb only. Run from a read-only login.

 Put the ticket numbers to inspect into #Numbers below (pre-filled with the four reported examples). Result set 2 does the same for EVERY unowned Open ticket.

 Cause codes
   NOT_OPEN / HAS_OWNER     a listed ticket that is not Open, or already has an owner (so it is not an unowned-Open case).
   NOT_IN_CC_AI_ACTIVE      active AI conversation (no human-follow-up flag, no handoff). By design the ticket stays where it is until a person is
                            requested; only a Genesys update with humanFollowUpRequired / handoff.required / a connected mapped agent moves it.
   NOT_IN_CC_PENDING_APPR   a pending approval holds it (never moved by routing or by the backlog script).
   NOT_IN_CC_LEGACY         created before the first Call Center-first audit row existed (old build, or cut-over): never routed; only the backlog script reaches it.
   NOT_IN_CC_NO_FLAG        created after the cut-over, human-handled signal absent on every interaction (Genesys sent no humanFollowUpRequired).
   NOT_IN_CC_MANUAL_OR_NO_AUDIT  no Genesys interaction and no 'routed to Call Center first' audit: created by an older build or outside the service.
   IN_CC_NO_ELIGIBLE_AGENT  ticket IS in the Call Center, unowned: the rotation found no eligible agent (CC members must be active, unlocked, not service
                            accounts, and hold the CS Agent role) or Call Center workflow settings disable assignment.
   IN_CC_AWAITING_MANUAL    in the Call Center and flagged ManualAssignmentRequired (the agent who answered could not take it).
   IN_CC_OTHER              in the Call Center, unowned, none of the above: inspect its AuditEntries.
================================================================================
*/
SET NOCOUNT ON;
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

DECLARE @ExpectedDb sysname = N'TigerCS_Ticketing_Prod';
IF DB_NAME() <> @ExpectedDb
BEGIN
    DECLARE @m nvarchar(400) = N'ABORTED: connected to [' + ISNULL(DB_NAME(), N'?') + N'], this script is for [' + @ExpectedDb + N'] only.';
    THROW 50510, @m, 1;
END;

CREATE TABLE #Numbers (TicketNumber nvarchar(40) NOT NULL PRIMARY KEY);
INSERT #Numbers (TicketNumber) VALUES
    (N'TG-ACC-20261102-0021'),
    (N'TG-LCS-20261102-0021'),
    (N'TG-REG-20261102-0026'),
    (N'TG-ACC-20261102-0023');   -- add further examples as more VALUES rows

DECLARE @CcId int = (SELECT TOP (1) DepartmentId FROM dbo.Departments WHERE Code = N'CC' AND IsActive = 1);
DECLARE @CcAssignmentAllowed bit = ISNULL((SELECT AllowAssignment FROM dbo.DepartmentWorkflowSettings WHERE DepartmentId = @CcId), 1);
DECLARE @FirstCcFirstUtc datetime2 = (SELECT MIN(OccurredAtUtc) FROM dbo.AuditEntries
                                      WHERE Action = N'Transfer' AND AfterValue LIKE N'%routed to Call Center first%');

SELECT @CcId AS CallCenterDepartmentId, @CcAssignmentAllowed AS CallCenterAllowsAssignment, @FirstCcFirstUtc AS FirstCallCenterFirstAuditUtc,
       (SELECT COUNT(*) FROM dbo.Departments WHERE Code = N'CC' AND IsActive = 1) AS ActiveDepartmentsWithCodeCC;   -- must be exactly 1

CREATE TABLE #R (TicketId bigint PRIMARY KEY, TicketNumber nvarchar(40), CreatedAtUtc datetime2, CurrentDepartmentId int, OriginatingDepartmentId int,
                 Cause varchar(40), TicketStatus tinyint, OwnerId uniqueidentifier NULL, HasGenesys bit, HumanFlag bit, ActiveAi bit, PendingApproval bit, RoutedAudit bit, Flagged bit, FromList bit);

INSERT #R
SELECT t.TicketId, t.TicketNumber, t.CreatedAtUtc, t.CurrentDepartmentId, t.OriginatingDepartmentId, '', t.TicketStatus, t.CurrentOwnerEmployeeId, x.HasGenesys, x.HumanFlag, x.ActiveAi,
       x.PendingApproval, x.RoutedAudit, x.Flagged, CASE WHEN n.TicketNumber IS NULL THEN 0 ELSE 1 END
FROM dbo.Tickets t
LEFT JOIN #Numbers n ON n.TicketNumber = t.TicketNumber
CROSS APPLY (SELECT
    CASE WHEN EXISTS (SELECT 1 FROM dbo.TicketInteractions i WHERE i.TicketId = t.TicketId AND i.Source = 2) THEN 1 ELSE 0 END AS HasGenesys,
    CASE WHEN EXISTS (SELECT 1 FROM dbo.TicketInteractions i WHERE i.TicketId = t.TicketId AND i.RequiresHumanFollowUp = 1)
           OR EXISTS (SELECT 1 FROM dbo.TicketAgentHandoffs h WHERE h.TicketId = t.TicketId AND h.Status IN (2, 3, 4)) THEN 1 ELSE 0 END AS HumanFlag,
    CASE WHEN EXISTS (SELECT 1 FROM dbo.TicketInteractions i WHERE i.TicketId = t.TicketId AND i.Source = 2 AND i.EndedAtUtc IS NULL
                      AND i.InactivityClosedAtUtc IS NULL AND i.RequiresHumanFollowUp = 0 AND i.HandledByUserId IS NULL)
          AND NOT EXISTS (SELECT 1 FROM dbo.TicketInteractions i WHERE i.TicketId = t.TicketId AND i.RequiresHumanFollowUp = 1)
          AND NOT EXISTS (SELECT 1 FROM dbo.TicketAgentHandoffs h WHERE h.TicketId = t.TicketId AND h.Status IN (2, 3, 4)) THEN 1 ELSE 0 END AS ActiveAi,
    CASE WHEN EXISTS (SELECT 1 FROM dbo.TicketApprovals ap WHERE ap.TicketId = t.TicketId AND ap.Status = 1) THEN 1 ELSE 0 END AS PendingApproval,
    CASE WHEN EXISTS (SELECT 1 FROM dbo.AuditEntries a WHERE a.EntityType = N'Ticket' AND a.EntityId = CONVERT(nvarchar(100), t.TicketId)
                      AND a.Action = N'Transfer' AND a.AfterValue LIKE N'%routed to Call Center first%') THEN 1 ELSE 0 END AS RoutedAudit,
    CASE WHEN EXISTS (SELECT 1 FROM dbo.AuditEntries a WHERE a.EntityType = N'Ticket' AND a.EntityId = CONVERT(nvarchar(100), t.TicketId)
                      AND a.Action = N'AnswerAssignmentSkipped') THEN 1 ELSE 0 END AS Flagged) x
WHERE n.TicketNumber IS NOT NULL
   OR (t.TicketStatus = 1 AND t.CurrentOwnerEmployeeId IS NULL);

UPDATE r SET Cause = CASE
    WHEN r.TicketStatus <> 1                                                              THEN 'NOT_OPEN'
    WHEN r.OwnerId IS NOT NULL                                                            THEN 'HAS_OWNER'
    WHEN r.CurrentDepartmentId = @CcId AND r.Flagged = 1                                  THEN 'IN_CC_AWAITING_MANUAL'
    WHEN r.CurrentDepartmentId = @CcId AND (@CcAssignmentAllowed = 0 OR r.RoutedAudit = 1 OR r.HumanFlag = 1 OR r.HasGenesys = 0)
                                                                                         THEN 'IN_CC_NO_ELIGIBLE_AGENT'
    WHEN r.CurrentDepartmentId = @CcId                                                    THEN 'IN_CC_OTHER'
    WHEN r.PendingApproval = 1                                                           THEN 'NOT_IN_CC_PENDING_APPR'
    WHEN r.ActiveAi = 1                                                                  THEN 'NOT_IN_CC_AI_ACTIVE'
    WHEN @FirstCcFirstUtc IS NULL OR r.CreatedAtUtc < @FirstCcFirstUtc                    THEN 'NOT_IN_CC_LEGACY'
    WHEN r.HasGenesys = 1 AND r.HumanFlag = 0                                            THEN 'NOT_IN_CC_NO_FLAG'
    ELSE 'NOT_IN_CC_MANUAL_OR_NO_AUDIT' END
FROM #R r;

-- RESULT SET 1: the listed tickets, with the evidence behind each verdict
SELECT r.TicketNumber, r.Cause, CASE r.TicketStatus WHEN 1 THEN 'Open' WHEN 2 THEN 'InProgress' WHEN 3 THEN 'PendingCustomer' WHEN 4 THEN 'PendingThirdParty' WHEN 5 THEN 'Resolved' WHEN 6 THEN 'Closed' ELSE CONVERT(varchar(5), r.TicketStatus) END AS TicketStatus, r.OwnerId AS CurrentOwnerEmployeeId, d.Code AS CurrentDepartment, od.Code AS OriginatingDepartment, r.CreatedAtUtc,
       r.HasGenesys, r.HumanFlag AS HumanFollowUpOrHandoff, r.ActiveAi, r.PendingApproval, r.RoutedAudit AS RoutedToCcFirstAudit, r.Flagged AS ManualAssignmentFlag
FROM #R r JOIN dbo.Departments d ON d.DepartmentId = r.CurrentDepartmentId JOIN dbo.Departments od ON od.DepartmentId = r.OriginatingDepartmentId
WHERE r.FromList = 1 ORDER BY r.TicketNumber;

-- RESULT SET 1b: listed numbers that matched NO ticket (typo, wrong database, or not yet created)
SELECT n.TicketNumber AS NotFound FROM #Numbers n WHERE NOT EXISTS (SELECT 1 FROM dbo.Tickets t WHERE t.TicketNumber = n.TicketNumber);

-- RESULT SET 2: every unowned Open ticket by cause and department
SELECT r.Cause, d.Code AS CurrentDepartment, COUNT(*) AS Tickets, MIN(r.CreatedAtUtc) AS Oldest, MAX(r.CreatedAtUtc) AS Newest
FROM #R r JOIN dbo.Departments d ON d.DepartmentId = r.CurrentDepartmentId
GROUP BY r.Cause, d.Code ORDER BY Tickets DESC;

-- RESULT SET 3: the Call Center pool exactly as the application evaluates it (CS Agent role required; read-only here, no writes)
SELECT e.EmployeeId, u.UserName,
       CASE WHEN u.LockoutEnabled = 1 AND u.LockoutEnd IS NOT NULL AND u.LockoutEnd > SYSDATETIMEOFFSET() THEN 'LOCKED OUT'
            WHEN NOT EXISTS (SELECT 1 FROM dbo.AspNetUserRoles ur JOIN dbo.AspNetRoles ro ON ro.Id = ur.RoleId
                             WHERE ur.UserId = e.EmployeeId AND ro.Name = N'CS Agent') THEN 'NO CS AGENT ROLE - not in rotation'
            ELSE 'in rotation' END AS RotationStatus,
       (SELECT STRING_AGG(ro.Name, N', ') FROM dbo.AspNetUserRoles ur JOIN dbo.AspNetRoles ro ON ro.Id = ur.RoleId WHERE ur.UserId = e.EmployeeId) AS Roles
FROM dbo.UserDepartmentAssignments uda
JOIN dbo.Employees e ON e.EmployeeId = uda.EmployeeId
JOIN dbo.AspNetUsers u ON u.Id = e.EmployeeId
WHERE uda.DepartmentId = @CcId
ORDER BY RotationStatus, u.UserName;

DROP TABLE #R; DROP TABLE #Numbers;
