/*
================================================================================
 Verify_TigerCS_GenesysAnswerRouting_ReadOnly.sql
 READ-ONLY. SELECT statements only: no INSERT / UPDATE / DELETE / DDL. Safe from a db_datareader login.

 Supports the "Genesys call answered by a Call Center agent -> that agent owns the ticket" rule (Genesys:AssignOnAnswer).
 Run it BEFORE the deployment (sections 1-2: are the agents mapped?) and AFTER (sections 3-6: is it working, is anything waiting?).
 The rule needs NO schema change - this script only reads existing tables.

   1. Call Center agents and whether their Genesys User ID is mapped (AspNetUsers.GenesysUserId). An agent who answers a call but is
      not mapped CANNOT be given the ticket: it waits unowned in the Call Center queue, flagged for a supervisor.
   2. Mapping hygiene: eligible agents with NO mapping, and mapped users that are deactivated / locked (they would be flagged too).
   3. Tickets WAITING FOR MANUAL ASSIGNMENT: flagged ManualAssignmentRequired (event type 20) and still unowned and not closed.
   4. How the rule has been used: direct assignments ('AssignOnAnswer') vs flags ('AnswerAssignmentSkipped'), per day.
   5. Invariant checks (every count must be 0): a ticket with two current assignments, an owner that differs from its current assignment,
      an unowned ticket that has a current assignment.
   6. Round Robin is untouched by direct assignments: AutoAssign (rotation) entries vs AssignOnAnswer entries.
================================================================================
*/
SET NOCOUNT ON;
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

-- 1. Call Center agents and their Genesys mapping -----------------------------------------------------------------------------
SELECT d.Code AS Department, e.DisplayName, u.UserName,
       ISNULL(u.GenesysUserId, N'(NOT MAPPED)') AS GenesysUserId,
       CASE WHEN e.DeactivatedAtUtc IS NOT NULL THEN 'deactivated'
            WHEN u.LockoutEnabled = 1 AND u.LockoutEnd IS NOT NULL AND u.LockoutEnd > SYSDATETIMEOFFSET() THEN 'locked out'
            WHEN u.GenesysUserId IS NULL THEN 'NOT MAPPED - a call this person answers will be flagged for manual assignment'
            ELSE 'ready' END AS Status,
       ISNULL((SELECT STRING_AGG(r.Name, N', ') WITHIN GROUP (ORDER BY r.Name)
                 FROM dbo.AspNetUserRoles ur JOIN dbo.AspNetRoles r ON r.Id = ur.RoleId WHERE ur.UserId = e.EmployeeId), N'(no role)') AS Roles
FROM dbo.Departments AS d
JOIN dbo.UserDepartmentAssignments AS a ON a.DepartmentId = d.DepartmentId
JOIN dbo.Employees AS e ON e.EmployeeId = a.EmployeeId
JOIN dbo.AspNetUsers AS u ON u.Id = e.EmployeeId
WHERE d.Code = N'CC'
ORDER BY CASE WHEN u.GenesysUserId IS NULL THEN 0 ELSE 1 END, e.DisplayName;

-- 2. Summary: how many are ready ---------------------------------------------------------------------------------------------
SELECT COUNT(*) AS CallCenterMembers,
       SUM(CASE WHEN e.DeactivatedAtUtc IS NULL AND u.GenesysUserId IS NOT NULL
                 AND NOT (u.LockoutEnabled = 1 AND u.LockoutEnd IS NOT NULL AND u.LockoutEnd > SYSDATETIMEOFFSET()) THEN 1 ELSE 0 END) AS ReadyToReceiveAnsweredCalls,
       SUM(CASE WHEN e.DeactivatedAtUtc IS NULL AND u.GenesysUserId IS NULL THEN 1 ELSE 0 END) AS ActiveButNotMapped_FixBeforeGoLive,
       SUM(CASE WHEN u.GenesysUserId IS NOT NULL AND e.DeactivatedAtUtc IS NOT NULL THEN 1 ELSE 0 END) AS MappedButDeactivated
FROM dbo.Departments AS d
JOIN dbo.UserDepartmentAssignments AS a ON a.DepartmentId = d.DepartmentId
JOIN dbo.Employees AS e ON e.EmployeeId = a.EmployeeId
JOIN dbo.AspNetUsers AS u ON u.Id = e.EmployeeId
WHERE d.Code = N'CC';

-- 3. Tickets waiting for manual assignment (flagged, still unowned, not closed) ------------------------------------------------
SELECT t.TicketNumber, dep.Code AS CurrentDepartment, t.TicketStatus, w.OccurredAtUtc AS FlaggedAtUtc, w.Note
FROM dbo.TicketWorkflowEvents AS w
JOIN dbo.Tickets AS t ON t.TicketId = w.TicketId
JOIN dbo.Departments AS dep ON dep.DepartmentId = t.CurrentDepartmentId
WHERE w.EventType = 20                                   -- ManualAssignmentRequired
  AND t.CurrentOwnerEmployeeId IS NULL
  AND t.TicketStatus <> 6                                -- 6 = Closed
ORDER BY w.OccurredAtUtc;

-- 4. Usage per day ------------------------------------------------------------------------------------------------------------
SELECT CONVERT(date, a.OccurredAtUtc) AS [Day],
       SUM(CASE WHEN a.Action = N'AssignOnAnswer' THEN 1 ELSE 0 END) AS DirectToAnsweringAgent,
       SUM(CASE WHEN a.Action = N'AnswerAssignmentSkipped' THEN 1 ELSE 0 END) AS FlaggedForManualAssignment,
       SUM(CASE WHEN a.Action = N'Unassign' AND a.AfterValue LIKE N'%answered%' THEN 1 ELSE 0 END) AS AutomaticOwnerReleased
FROM dbo.AuditEntries AS a
WHERE a.Action IN (N'AssignOnAnswer', N'AnswerAssignmentSkipped', N'Unassign') AND a.EntityType = N'Ticket'
  AND a.OccurredAtUtc >= DATEADD(day, -30, SYSUTCDATETIME())
GROUP BY CONVERT(date, a.OccurredAtUtc)
ORDER BY [Day] DESC;

-- 5. Invariants: every number must be 0 ---------------------------------------------------------------------------------------
SELECT
    (SELECT COUNT(*) FROM (SELECT TicketId FROM dbo.TicketAssignments WHERE IsCurrent = 1 GROUP BY TicketId HAVING COUNT(*) > 1) x)
        AS TicketsWithTwoCurrentAssignments,
    (SELECT COUNT(*) FROM dbo.Tickets t JOIN dbo.TicketAssignments a ON a.TicketId = t.TicketId AND a.IsCurrent = 1
      WHERE t.CurrentOwnerEmployeeId IS NOT NULL AND t.CurrentOwnerEmployeeId <> a.AssignedEmployeeId)
        AS OwnerDiffersFromCurrentAssignment,
    (SELECT COUNT(*) FROM dbo.Tickets t JOIN dbo.TicketAssignments a ON a.TicketId = t.TicketId AND a.IsCurrent = 1
      WHERE t.CurrentOwnerEmployeeId IS NULL)
        AS UnownedButHasCurrentAssignment;

-- 6. Round Robin is not consumed by direct assignments -------------------------------------------------------------------------
SELECT SUM(CASE WHEN Action = N'AutoAssign' AND AfterValue LIKE N'%Mode=RoundRobin%' THEN 1 ELSE 0 END) AS RoundRobinAssignments,
       SUM(CASE WHEN Action = N'AssignOnAnswer' THEN 1 ELSE 0 END) AS DirectAssignmentsToAnsweringAgent
FROM dbo.AuditEntries
WHERE EntityType = N'Ticket' AND Action IN (N'AutoAssign', N'AssignOnAnswer')
  AND OccurredAtUtc >= DATEADD(day, -30, SYSUTCDATETIME());
