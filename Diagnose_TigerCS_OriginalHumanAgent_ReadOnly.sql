-- READ-ONLY. Shows, per ticket, who first owned it in the Call Center (the Original Human Agent), who owns it now, and the full
-- assignment chain, so a ticket that "came back as Bader instead of Kawthar" can be explained from data. Changes nothing.
-- Set @AgentName to filter to tickets whose Call Center chain mentions a person (matched on the Employees display name / user name).
DECLARE @AgentName nvarchar(200) = N'Kawthar';

;WITH Chain AS (
    SELECT a.TicketId, a.TicketAssignmentId, a.AssignedEmployeeId, a.AssignedDepartmentId, d.Code AS DepartmentCode,
           a.AssignedAtUtc, a.AssigningActorEmployeeId, a.IsCurrent,
           CASE WHEN a.AssigningActorEmployeeId IS NULL THEN 'System (rule / Round Robin / tower routing)'
                WHEN a.AssigningActorEmployeeId = a.AssignedEmployeeId THEN 'Self (creator / claim / answered)'
                ELSE 'Manual (another person)' END AS HowAssigned,
           ROW_NUMBER() OVER (PARTITION BY a.TicketId ORDER BY a.AssignedAtUtc, a.TicketAssignmentId) AS Step
    FROM TicketAssignments a
    JOIN Departments d ON d.DepartmentId = a.AssignedDepartmentId)
SELECT t.TicketId, t.TicketNumber, t.CreatedByEmployeeId, t.OriginalHumanAgentId, t.CurrentOwnerEmployeeId, c.Step, c.DepartmentCode,
       c.AssignedEmployeeId, c.AssignedAtUtc, c.HowAssigned, c.IsCurrent
FROM Tickets t
JOIN Chain c ON c.TicketId = t.TicketId
WHERE EXISTS (SELECT 1 FROM Chain x JOIN Employees e ON e.EmployeeId = x.AssignedEmployeeId
              WHERE x.TicketId = t.TicketId AND e.DisplayName LIKE N'%' + @AgentName + N'%')
ORDER BY t.TicketId, c.Step;

-- Tickets whose current Call Center owner differs from the Original Human Agent (candidates for the Kawthar -> Bader symptom).
SELECT t.TicketId, t.TicketNumber, t.OriginalHumanAgentId, t.CurrentOwnerEmployeeId
FROM Tickets t
JOIN Departments d ON d.DepartmentId = t.CurrentDepartmentId AND d.Code = N'CC'
WHERE t.OriginalAgentWorkflowEnabled = 1   -- only tickets created after the feature; legacy tickets never use the Original Human Agent workflow
  AND t.OriginalHumanAgentId IS NOT NULL AND t.CurrentOwnerEmployeeId IS NOT NULL AND t.CurrentOwnerEmployeeId <> t.OriginalHumanAgentId;
