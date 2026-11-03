/*
================================================================================
 Diagnose_TigerCS_ActiveTicketsMissingCsAgent_ReadOnly.sql     (READ-ONLY - SELECT only)

 Business rule: every ACTIVE human-handled ticket has a CS Agent (Tickets.CurrentOwnerEmployeeId), independent of its department.
 This lists every ticket that violates it - i.e. everything the UI used to show as "<Department> Queue (Unassigned)" - and classifies why,
 so a backfill can be agreed BEFORE any historical row is changed. Nothing is written; temp tables live in tempdb only.

 Ticket status values: 1 Open, 2 InProgress, 3 PendingCustomer, 4 PendingThirdParty, 5 Resolved, 6 Closed.  "Active" = 1..4.

 Class
   AI_IN_PROGRESS     a live Genesys conversation with no human requested: protected by design, gets a CS Agent only at handoff. NOT part of the backfill.
   PENDING_APPROVAL   an approval is pending; the approval process decides first. NOT part of the backfill until it is decided.
   BACKFILL_CANDIDATE active, unowned, not AI-in-progress: should have a CS Agent (this is the population the proposed backfill would assign).
================================================================================
*/
SET NOCOUNT ON;
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

DECLARE @CcId int = (SELECT TOP (1) DepartmentId FROM dbo.Departments WHERE Code = N'CC' AND IsActive = 1);

CREATE TABLE #R (TicketId bigint PRIMARY KEY, TicketNumber nvarchar(40), TicketStatus tinyint, CurrentDepartmentId int, CreatedAtUtc datetime2,
                 Class varchar(20), FailureRecorded bit);

INSERT #R
SELECT t.TicketId, t.TicketNumber, t.TicketStatus, t.CurrentDepartmentId, t.CreatedAtUtc,
       CASE
           WHEN EXISTS (SELECT 1 FROM dbo.TicketApprovals a WHERE a.TicketId = t.TicketId AND a.Status = 1) THEN 'PENDING_APPROVAL'
           WHEN EXISTS (SELECT 1 FROM dbo.TicketInteractions i WHERE i.TicketId = t.TicketId AND i.Source = 2)
            AND NOT EXISTS (SELECT 1 FROM dbo.TicketInteractions i WHERE i.TicketId = t.TicketId AND i.RequiresHumanFollowUp = 1)
            AND EXISTS (SELECT 1 FROM dbo.TicketInteractions i WHERE i.TicketId = t.TicketId AND i.EndedAtUtc IS NULL AND i.InactivityClosedAtUtc IS NULL)
                                                                                                  THEN 'AI_IN_PROGRESS'
           ELSE 'BACKFILL_CANDIDATE'
       END,
       -- EventType 20 = ManualAssignmentRequired; the application writes one with the CsAgentAssignmentFailed marker per failed ticket.
       CASE WHEN EXISTS (SELECT 1 FROM dbo.TicketWorkflowEvents e
                         WHERE e.TicketId = t.TicketId AND e.EventType = 20 AND e.Note LIKE N'%CsAgentAssignmentFailed%') THEN 1 ELSE 0 END
FROM dbo.Tickets t
WHERE t.TicketStatus BETWEEN 1 AND 4
  AND t.CurrentOwnerEmployeeId IS NULL;

-- 1. Summary by class and status.
SELECT Class, TicketStatus, COUNT(*) AS Tickets, SUM(CAST(FailureRecorded AS int)) AS WithRecordedFailure
FROM #R GROUP BY Class, TicketStatus ORDER BY Class, TicketStatus;

-- 2. Summary by department (the "Facilities Management Queue (Unassigned)" view).
SELECT d.Name AS Department, r.Class, COUNT(*) AS Tickets
FROM #R r JOIN dbo.Departments d ON d.DepartmentId = r.CurrentDepartmentId
GROUP BY d.Name, r.Class ORDER BY Tickets DESC;

-- 3. The backfill population, oldest first (the order the proposed backfill would deal them).
SELECT r.TicketId, r.TicketNumber, r.TicketStatus, d.Name AS Department, r.CreatedAtUtc, r.FailureRecorded
FROM #R r JOIN dbo.Departments d ON d.DepartmentId = r.CurrentDepartmentId
WHERE r.Class = 'BACKFILL_CANDIDATE' ORDER BY r.CreatedAtUtc, r.TicketId;

-- 4. Is there anyone to deal them to? (Eligible = active member of the Call Center holding the CS Agent role; the application also excludes
--    locked-out accounts and service identities, which only the application can evaluate.)
SELECT @CcId AS CallCenterDepartmentId,
       (SELECT COUNT(*) FROM dbo.UserDepartmentAssignments uda WHERE uda.DepartmentId = @CcId) AS CallCenterMembers,
       (SELECT LastAssignedEmployeeId FROM dbo.DepartmentAssignmentRotations WHERE DepartmentId = @CcId) AS SharedRotationCursor;

-- 5. Integrity: active tickets with MORE than one current assignment row (must be 0).
SELECT TicketId, COUNT(*) AS CurrentRows FROM dbo.TicketAssignments WHERE IsCurrent = 1 GROUP BY TicketId HAVING COUNT(*) > 1;

DROP TABLE #R;
