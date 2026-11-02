-- READ-ONLY verification for Production. SELECT only. Counts/ids only: no customer PII.
-- Run with a db_datareader login. SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED avoids blocking the app.
SET NOCOUNT ON;
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

-- 7. Migration status
SELECT TOP 5 MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId DESC;
SELECT HasRotationMigration = CASE WHEN EXISTS (SELECT 1 FROM __EFMigrationsHistory
       WHERE MigrationId = '20261031094748_AddDepartmentAssignmentRotation') THEN 1 ELSE 0 END,
       HasRotationTable = CASE WHEN OBJECT_ID('dbo.DepartmentAssignmentRotations','U') IS NOT NULL THEN 1 ELSE 0 END;
SELECT c.name, t.name AS type, c.is_nullable FROM sys.columns c JOIN sys.types t ON t.user_type_id=c.user_type_id
 WHERE c.object_id = OBJECT_ID('dbo.DepartmentAssignmentRotations');
SELECT COUNT(*) AS RotationRows FROM dbo.DepartmentAssignmentRotations;   -- fails if table missing (itself a finding)

-- 1 / 6. Unique filtered index on current assignment present?
SELECT i.name, i.is_unique, i.filter_definition FROM sys.indexes i
 WHERE i.object_id = OBJECT_ID('dbo.TicketAssignments') AND i.has_filter = 1;

-- 1 / 6. Tickets with more than one current assignment (expect 0 rows)
SELECT TicketId, COUNT(*) AS CurrentRows FROM dbo.TicketAssignments
 WHERE IsCurrent = 1 GROUP BY TicketId HAVING COUNT(*) > 1;

-- 6. Open tickets with no current assignment row (expect 0 rows; unassigned should still have a dept row)
SELECT COUNT(*) AS OpenTicketsWithoutCurrentAssignment FROM dbo.Tickets t
 WHERE t.Status NOT IN ('Closed','Cancelled')
   AND NOT EXISTS (SELECT 1 FROM dbo.TicketAssignments a WHERE a.TicketId=t.TicketId AND a.IsCurrent=1);

-- 6. Current assignees who are inactive or not members of the assigned department
SELECT COUNT(*) AS CurrentAssigneeInactive FROM dbo.TicketAssignments a
 JOIN dbo.Employees e ON e.EmployeeId = a.AssignedEmployeeId
 WHERE a.IsCurrent=1 AND e.IsActive=0;

-- 6. Current assignment distribution per department (counts only)
SELECT d.Code, COUNT(*) AS CurrentTickets, SUM(CASE WHEN a.AssignedEmployeeId IS NULL THEN 1 ELSE 0 END) AS Unassigned
  FROM dbo.TicketAssignments a JOIN dbo.Departments d ON d.DepartmentId=a.AssignedDepartmentId
 WHERE a.IsCurrent=1 GROUP BY d.Code ORDER BY d.Code;

-- 4. Call Center tickets still sitting in CC and owned (counts), for transfer-rule review
SELECT COUNT(*) AS OpenInCallCenter, SUM(CASE WHEN a.AssignedEmployeeId IS NOT NULL THEN 1 ELSE 0 END) AS OwnedInCC
  FROM dbo.TicketAssignments a JOIN dbo.Departments d ON d.DepartmentId=a.AssignedDepartmentId
  JOIN dbo.Tickets t ON t.TicketId=a.TicketId
 WHERE a.IsCurrent=1 AND d.Code='CC' AND t.Status NOT IN ('Closed','Cancelled');

-- 5. Roles / department memberships (counts only; adjust table names if they differ)
SELECT d.Code, COUNT(*) AS ActiveEmployees FROM dbo.Employees e
  JOIN dbo.Departments d ON d.DepartmentId=e.DepartmentId WHERE e.IsActive=1 GROUP BY d.Code;
SELECT COUNT(*) AS ActiveEmployeesWithNoDepartment FROM dbo.Employees WHERE IsActive=1 AND DepartmentId IS NULL;
-- Role distribution: inspect the role/user-role tables, e.g.
-- SELECT r.Name, COUNT(*) FROM dbo.UserRoles ur JOIN dbo.Roles r ON r.RoleId=ur.RoleId GROUP BY r.Name;
