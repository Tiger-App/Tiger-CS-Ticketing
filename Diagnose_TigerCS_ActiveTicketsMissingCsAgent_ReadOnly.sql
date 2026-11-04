/*
================================================================================
 Diagnose_TigerCS_ActiveTicketsMissingCsAgent_ReadOnly.sql     (READ-ONLY - SELECT only)

 Business rule: every ACTIVE human-handled ticket has a CS Agent (Tickets.CurrentOwnerEmployeeId), independent of its department.
 This lists every ticket that violates it - i.e. everything the UI used to show as "<Department> Queue (Unassigned)" - and classifies why,
 so a backfill can be agreed BEFORE any historical row is changed. Nothing is written; temp tables live in tempdb only.

 Ticket status values: 1 Open, 2 InProgress, 3 PendingCustomer, 4 PendingThirdParty, 5 Resolved, 6 Closed.  "Active" = 1..4.

 Class (final business rules: every ticket needing a person gets an eligible CS Agent, whatever its Department)
   PENDING_APPROVAL   an approval is pending; actively handled by the approval process. EXCLUDED while pending.
   AI_IN_PROGRESS     a live Genesys conversation, no human requested, no human reply: AI-owned. EXCLUDED while live.
   HANDLER_KNOWN      a person demonstrably handled the interaction (handler row / handled-by user). BACKFILL: assigned to THAT agent when they are an eligible
                      Call Center CS Agent, otherwise next in the shared rotation (the application preview shows which).
   AI_ENDED_NO_HUMAN  the AI conversation ended and the ticket was never resolved/closed. BACKFILL (CsAgentBackfill:IncludeAiEndedWithoutHuman = true, the default).
   BACKFILL_CANDIDATE active, unowned, none of the above: gets the next CS Agent of the shared rotation, oldest first, WITHOUT moving the ticket.
   The application preview additionally applies what only the application can evaluate (agent lock-out, service identities, tickets younger than 3 minutes -> TOO_NEW).
================================================================================
*/
SET NOCOUNT ON;
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

DECLARE @CcId int = (SELECT TOP (1) DepartmentId FROM dbo.Departments WHERE Code = N'CC' AND IsActive = 1);

CREATE TABLE #R (TicketId bigint PRIMARY KEY, TicketNumber nvarchar(40), TicketStatus tinyint, CurrentDepartmentId int, CreatedAtUtc datetime2,
                 Class varchar(24), FailureRecorded bit);

INSERT #R
SELECT t.TicketId, t.TicketNumber, t.TicketStatus, t.CurrentDepartmentId, t.CreatedAtUtc,
       CASE
           WHEN EXISTS (SELECT 1 FROM dbo.TicketApprovals a WHERE a.TicketId = t.TicketId AND a.Status = 1) THEN 'PENDING_APPROVAL'
           WHEN EXISTS (SELECT 1 FROM dbo.TicketInteractionHandlers h WHERE h.TicketId = t.TicketId)
             OR EXISTS (SELECT 1 FROM dbo.TicketInteractions i WHERE i.TicketId = t.TicketId AND i.HandledByUserId IS NOT NULL) THEN 'HANDLER_KNOWN'
           WHEN EXISTS (SELECT 1 FROM dbo.TicketInteractions i WHERE i.TicketId = t.TicketId AND i.Source = 2)
            AND NOT EXISTS (SELECT 1 FROM dbo.TicketInteractions i WHERE i.TicketId = t.TicketId AND i.RequiresHumanFollowUp = 1)
            AND t.FirstHumanResponseAtUtc IS NULL
            AND EXISTS (SELECT 1 FROM dbo.TicketInteractions i WHERE i.TicketId = t.TicketId AND i.EndedAtUtc IS NULL AND i.InactivityClosedAtUtc IS NULL)
                                                                                                  THEN 'AI_IN_PROGRESS'
           WHEN EXISTS (SELECT 1 FROM dbo.TicketInteractions i WHERE i.TicketId = t.TicketId AND i.Source = 2)
            AND NOT EXISTS (SELECT 1 FROM dbo.TicketInteractions i WHERE i.TicketId = t.TicketId AND i.RequiresHumanFollowUp = 1)
            AND t.FirstHumanResponseAtUtc IS NULL                                                 THEN 'AI_ENDED_NO_HUMAN'
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

-- 2b. Counts by exclusion reason (excluded = left alone on purpose) versus eligible (needs a CS Agent).
SELECT CASE WHEN Class IN ('PENDING_APPROVAL','AI_IN_PROGRESS') THEN 'EXCLUDED' ELSE 'ELIGIBLE' END AS Disposition, Class AS Reason, COUNT(*) AS Tickets
FROM #R GROUP BY CASE WHEN Class IN ('PENDING_APPROVAL','AI_IN_PROGRESS') THEN 'EXCLUDED' ELSE 'ELIGIBLE' END, Class ORDER BY Disposition, Class;

-- 3. The backfill population, oldest first (the order the proposed backfill would deal them). Excludes only the two protected classes.
SELECT r.TicketId, r.TicketNumber, r.TicketStatus, d.Name AS Department, r.Class, r.CreatedAtUtc, r.FailureRecorded
FROM #R r JOIN dbo.Departments d ON d.DepartmentId = r.CurrentDepartmentId
WHERE r.Class NOT IN ('PENDING_APPROVAL','AI_IN_PROGRESS') ORDER BY r.CreatedAtUtc, r.TicketId;

-- 4. Is there anyone to deal them to? (Eligible = active member of the Call Center holding the CS Agent role; the application also excludes
--    locked-out accounts and service identities, which only the application can evaluate.)
SELECT @CcId AS CallCenterDepartmentId,
       (SELECT COUNT(*) FROM dbo.UserDepartmentAssignments uda WHERE uda.DepartmentId = @CcId) AS CallCenterMembers,
       (SELECT LastAssignedEmployeeId FROM dbo.DepartmentAssignmentRotations WHERE DepartmentId = @CcId) AS SharedRotationCursor;

-- 4b. REPORT ONLY - active tickets that HAVE an owner who is NOT a CS Agent (typically a department employee from before the shared-rotation fix).
--     They are not "unassigned", so the backfill never touches them (an existing assignment is never redistributed); whether to move them to a CS Agent is
--     a business decision. A manual/Supervisor assignment to a CS Agent is never in this list.
SELECT d.Name AS Department, t.TicketStatus, COUNT(*) AS TicketsOwnedByNonCsAgent
FROM dbo.Tickets t JOIN dbo.Departments d ON d.DepartmentId = t.CurrentDepartmentId
WHERE t.TicketStatus BETWEEN 1 AND 4 AND t.CurrentOwnerEmployeeId IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM dbo.AspNetUserRoles ur JOIN dbo.AspNetRoles r ON r.Id = ur.RoleId
                  WHERE ur.UserId = t.CurrentOwnerEmployeeId AND r.Name = N'CS Agent')
GROUP BY d.Name, t.TicketStatus ORDER BY TicketsOwnedByNonCsAgent DESC;

-- 5. Integrity: active tickets with MORE than one current assignment row (must be 0).
SELECT TicketId, COUNT(*) AS CurrentRows FROM dbo.TicketAssignments WHERE IsCurrent = 1 GROUP BY TicketId HAVING COUNT(*) > 1;

DROP TABLE #R;
