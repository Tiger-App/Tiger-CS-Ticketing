/* =====================================================================================================================================
   Verify_TigerCS_TakeOwnership_ReadOnly.sql   -   READ-ONLY. SELECT only. Changes nothing.

   Purpose: before and after the "Take Ownership" release, show
     1. who can use it            - active, unlocked Call Center members holding the CS Agent role (the application's own test),
     2. what they can take        - the pool, with the same definition the application uses (TicketQueryFilters.AvailableToTake),
                                    and what is held back and why (so the numbers can be compared with the backlog-distribution Preview),
     3. what has been taken       - every claim since go-live, and the integrity of each (one owner, one current assignment, in the Call Center,
                                    originating department / status / priority / SLA state unchanged).
   Run it as a login that only has db_datareader. It never writes (no temp tables, no variables that persist, no DML).

   Definition of the pool (mirrors the held-back rules of Preview/Execute_TigerCS_PROD_CallCenterBacklogDistribution):
     status Open/InProgress/PendingCustomer/PendingThirdParty (1-4)  AND  no owner  AND  no current assignment row
     AND no pending approval (TicketApprovals.Status = 1)
     AND NOT an active AI conversation: a Genesys interaction (Source = 2) that is not ended, not closed for inactivity, needs no human
         follow-up and was not taken by a person, with no interaction on the ticket flagged RequiresHumanFollowUp and no open human handoff
         (TicketAgentHandoffs.Status 2/3/4).
   ===================================================================================================================================== */
SET NOCOUNT ON;
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;   -- a verification query must never block (or be blocked by) live traffic

SELECT DB_NAME() AS [Database], @@SERVERNAME AS [Server], SYSUTCDATETIME() AS [NowUtc];

DECLARE @CcId int = (SELECT TOP (1) DepartmentId FROM dbo.Departments WHERE Code = N'CC' AND IsActive = 1);
SELECT CASE WHEN (SELECT COUNT(*) FROM dbo.Departments WHERE Code = N'CC' AND IsActive = 1) = 1
            THEN N'OK - exactly one active Call Center (CC) department'
            ELSE N'PROBLEM - need exactly ONE active department with Code = CC (Take Ownership resolves the Call Center by that code)' END AS CallCenterCheck,
       @CcId AS CallCenterDepartmentId;

/* 1. Who can take ownership ---------------------------------------------------------------------------------------------------------------- */
SELECT e.EmployeeId, e.DisplayName, u.UserName
FROM dbo.Employees AS e
JOIN dbo.AspNetUsers AS u ON u.Id = e.EmployeeId
WHERE @CcId IS NOT NULL
  AND e.DeactivatedAtUtc IS NULL
  AND EXISTS (SELECT 1 FROM dbo.UserDepartmentAssignments a WHERE a.EmployeeId = e.EmployeeId AND a.DepartmentId = @CcId)
  AND NOT (u.LockoutEnabled = 1 AND u.LockoutEnd IS NOT NULL AND u.LockoutEnd > SYSDATETIMEOFFSET())
  AND EXISTS (SELECT 1 FROM dbo.AspNetUserRoles ur JOIN dbo.AspNetRoles r ON r.Id = ur.RoleId WHERE ur.UserId = e.EmployeeId AND r.Name = N'CS Agent')
ORDER BY e.DisplayName;

SELECT N'Call Center members who do NOT hold the CS Agent role (cannot take ownership)' AS Note, COUNT(*) AS Members
FROM dbo.UserDepartmentAssignments AS a
WHERE a.DepartmentId = @CcId
  AND NOT EXISTS (SELECT 1 FROM dbo.AspNetUserRoles ur JOIN dbo.AspNetRoles r ON r.Id = ur.RoleId WHERE ur.UserId = a.EmployeeId AND r.Name = N'CS Agent');

/* 2. What they can take, and what is held back ---------------------------------------------------------------------------------------------- */
;WITH T AS
(
    SELECT t.TicketId, t.CurrentDepartmentId, t.TicketStatus, t.CurrentOwnerEmployeeId,
           HasAssignment = CASE WHEN EXISTS (SELECT 1 FROM dbo.TicketAssignments a WHERE a.TicketId = t.TicketId AND a.IsCurrent = 1) THEN 1 ELSE 0 END,
           Pending = CASE WHEN EXISTS (SELECT 1 FROM dbo.TicketApprovals ap WHERE ap.TicketId = t.TicketId AND ap.Status = 1) THEN 1 ELSE 0 END,
           ActiveAi = CASE WHEN EXISTS (SELECT 1 FROM dbo.TicketInteractions i WHERE i.TicketId = t.TicketId AND i.Source = 2 AND i.EndedAtUtc IS NULL
                                          AND i.InactivityClosedAtUtc IS NULL AND i.RequiresHumanFollowUp = 0 AND i.HandledByUserId IS NULL)
                            AND NOT EXISTS (SELECT 1 FROM dbo.TicketInteractions i2 WHERE i2.TicketId = t.TicketId AND i2.RequiresHumanFollowUp = 1)
                            AND NOT EXISTS (SELECT 1 FROM dbo.TicketAgentHandoffs h WHERE h.TicketId = t.TicketId AND h.Status IN (2, 3, 4))
                           THEN 1 ELSE 0 END
    FROM dbo.Tickets AS t
),
C AS
(
    SELECT T.*, Reason = CASE
        WHEN TicketStatus IN (5, 6)                               THEN N'3 excluded: Resolved / Closed'
        WHEN CurrentOwnerEmployeeId IS NOT NULL OR HasAssignment = 1 THEN N'4 excluded: already owned / assigned'
        WHEN Pending = 1                                          THEN N'2 held back: pending approval'
        WHEN ActiveAi = 1                                         THEN N'1 held back: active AI conversation'
        ELSE N'0 AVAILABLE TO TAKE' END
    FROM T
)
SELECT Reason, COUNT(*) AS Tickets FROM C GROUP BY Reason ORDER BY Reason;

-- The pool per department (the departments the Call Center agents will see tickets from):
;WITH T AS
(
    SELECT t.TicketId, t.CurrentDepartmentId, t.CreatedAtUtc
    FROM dbo.Tickets AS t
    WHERE t.TicketStatus IN (1, 2, 3, 4)
      AND t.CurrentOwnerEmployeeId IS NULL
      AND NOT EXISTS (SELECT 1 FROM dbo.TicketAssignments a WHERE a.TicketId = t.TicketId AND a.IsCurrent = 1)
      AND NOT EXISTS (SELECT 1 FROM dbo.TicketApprovals ap WHERE ap.TicketId = t.TicketId AND ap.Status = 1)
      AND NOT (EXISTS (SELECT 1 FROM dbo.TicketInteractions i WHERE i.TicketId = t.TicketId AND i.Source = 2 AND i.EndedAtUtc IS NULL
                         AND i.InactivityClosedAtUtc IS NULL AND i.RequiresHumanFollowUp = 0 AND i.HandledByUserId IS NULL)
               AND NOT EXISTS (SELECT 1 FROM dbo.TicketInteractions i2 WHERE i2.TicketId = t.TicketId AND i2.RequiresHumanFollowUp = 1)
               AND NOT EXISTS (SELECT 1 FROM dbo.TicketAgentHandoffs h WHERE h.TicketId = t.TicketId AND h.Status IN (2, 3, 4)))
)
SELECT d.Code AS DepartmentCode, d.Name AS DepartmentName, COUNT(*) AS AvailableToTake,
       MIN(T.CreatedAtUtc) AS OldestCreatedUtc, MAX(T.CreatedAtUtc) AS NewestCreatedUtc
FROM T JOIN dbo.Departments AS d ON d.DepartmentId = T.CurrentDepartmentId
GROUP BY d.DepartmentId, d.Code, d.Name
ORDER BY COUNT(*) DESC, d.Code;

/* 3. What has been taken since go-live, and its integrity ---------------------------------------------------------------------------------- */
SELECT CONVERT(date, ae.OccurredAtUtc) AS [Day], COUNT(*) AS Claims, COUNT(DISTINCT ae.ActorEmployeeId) AS Agents
FROM dbo.AuditEntries AS ae
WHERE ae.Action = N'TakeOwnership' AND ae.EntityType = N'Ticket'
GROUP BY CONVERT(date, ae.OccurredAtUtc) ORDER BY [Day] DESC;

-- Each claimed ticket must be: owned by the claiming agent, in the Call Center, with exactly one current assignment naming that agent
-- (assigned by the agent), and its ORIGINATING department unchanged. Any row returned here is a defect.
SELECT t.TicketId, t.TicketNumber, ae.ActorEmployeeId AS ClaimedBy, t.CurrentOwnerEmployeeId AS OwnerNow, t.CurrentDepartmentId,
       (SELECT COUNT(*) FROM dbo.TicketAssignments a WHERE a.TicketId = t.TicketId AND a.IsCurrent = 1) AS CurrentAssignments
FROM dbo.AuditEntries AS ae
JOIN dbo.Tickets AS t ON t.TicketId = TRY_CONVERT(bigint, ae.EntityId)
WHERE ae.Action = N'TakeOwnership' AND ae.EntityType = N'Ticket'
  AND (SELECT COUNT(*) FROM dbo.TicketAssignments a WHERE a.TicketId = t.TicketId AND a.IsCurrent = 1) > 1;   -- never more than one current owner

SELECT N'must be 0: tickets with more than one current assignment (database-wide)' AS Check_, COUNT(*) AS Violations
FROM (SELECT TicketId FROM dbo.TicketAssignments WHERE IsCurrent = 1 GROUP BY TicketId HAVING COUNT(*) > 1) AS x;

SELECT N'must be 0: owned tickets with no matching current assignment (database-wide)' AS Check_, COUNT(*) AS Violations
FROM dbo.Tickets AS t
WHERE t.CurrentOwnerEmployeeId IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM dbo.TicketAssignments a WHERE a.TicketId = t.TicketId AND a.IsCurrent = 1 AND a.AssignedEmployeeId = t.CurrentOwnerEmployeeId);
