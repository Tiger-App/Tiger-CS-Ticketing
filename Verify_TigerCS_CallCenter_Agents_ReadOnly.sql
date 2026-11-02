/*
================================================================================
 Verify_TigerCS_CallCenter_Agents_ReadOnly.sql
 READ-ONLY. SELECT statements only: no INSERT / UPDATE / DELETE / DDL / EXEC of anything that writes.
 Lists who Call Center Auto Distribution (Round Robin) can hand a new ticket to.

 Mirrors TicketAutoAssignmentService.FilterEligibleAsync + IUserDepartmentAssignmentRepository.GetByDepartmentIdAsync:
   * member of the department with Code = 'CC'                      (UserDepartmentAssignments)
   * employee not deactivated                                        (Employees.DeactivatedAtUtc IS NULL)
   * account not locked out                                          (AspNetUsers.LockoutEnabled = 1 AND LockoutEnd > now  => locked)
   * not a service identity (the Genesys integration account)        (fill @ServiceIdentity below - it lives in appsettings, not in SQL)
   * holds at least one of: CS Agent, Department Employee, Department Head
   * not read-only only (Chairman/CEO, Reporting User alone never count)
 CS Supervisor / CS Manager / General Manager / System Administrator are NOT handed work by rotation, even if CC members.

 Run it on the Production database from a read-only login. If the result of section 3 is not "READY", every new ticket will wait
 UNASSIGNED in the Call Center queue (it is never lost and never given to a non-eligible person).
================================================================================
*/
SET NOCOUNT ON;

-- Fill from appsettings: ServiceIdentity:EmployeeIds and Collections:Authorization:IntegrationEmployeeIds (one row per id).
DECLARE @ServiceIdentity TABLE (EmployeeId uniqueidentifier PRIMARY KEY);
-- INSERT INTO @ServiceIdentity VALUES ('00000000-0000-0000-0000-000000000000');   -- (edit the variable table only; nothing persists)

-- 1. The Call Center department -------------------------------------------------------------------------------------------
SELECT d.DepartmentId, d.Name, d.Code, d.IsActive,
       CASE WHEN d.IsActive = 1 THEN 'OK' ELSE 'BLOCKER: CC department is inactive - Call Center first falls back to the old routing' END AS Check1
FROM dbo.Departments AS d
WHERE d.Code = N'CC';
-- (zero rows above = BLOCKER: no CC department; new tickets keep their category department)

-- 2. Every CC member and why they are / are not eligible ---------------------------------------------------------------------
;WITH CcMembers AS (
    SELECT a.EmployeeId
    FROM dbo.UserDepartmentAssignments AS a
    JOIN dbo.Departments AS d ON d.DepartmentId = a.DepartmentId AND d.Code = N'CC' AND d.IsActive = 1
),
Roles AS (
    SELECT ur.UserId, STRING_AGG(r.Name, N', ') WITHIN GROUP (ORDER BY r.Name) AS RoleList,
           MAX(CASE WHEN r.Name IN (N'CS Agent', N'Department Employee', N'Department Head') THEN 1 ELSE 0 END) AS HasOperationalRole,
           MIN(CASE WHEN r.Name IN (N'Chairman/CEO', N'Reporting User') THEN 1 ELSE 0 END) AS OnlyReadOnlyRoles
    FROM dbo.AspNetUserRoles AS ur
    JOIN dbo.AspNetRoles AS r ON r.Id = ur.RoleId
    GROUP BY ur.UserId
)
SELECT u.UserName, u.Email, e.DisplayName, e.EmployeeId,
       ISNULL(ro.RoleList, N'(no role)') AS Roles,
       CASE WHEN e.DeactivatedAtUtc IS NULL THEN 1 ELSE 0 END AS IsActive,
       CASE WHEN u.LockoutEnabled = 1 AND u.LockoutEnd IS NOT NULL AND u.LockoutEnd > SYSDATETIMEOFFSET() THEN 1 ELSE 0 END AS IsLockedOut,
       CASE WHEN EXISTS (SELECT 1 FROM @ServiceIdentity s WHERE s.EmployeeId = e.EmployeeId) THEN 1 ELSE 0 END AS IsServiceIdentity,
       ISNULL(ro.HasOperationalRole, 0) AS HasOperationalRole,
       CASE WHEN e.DeactivatedAtUtc IS NULL
             AND NOT (u.LockoutEnabled = 1 AND u.LockoutEnd IS NOT NULL AND u.LockoutEnd > SYSDATETIMEOFFSET())
             AND NOT EXISTS (SELECT 1 FROM @ServiceIdentity s WHERE s.EmployeeId = e.EmployeeId)
             AND ISNULL(ro.HasOperationalRole, 0) = 1
            THEN 'ELIGIBLE' ELSE 'not eligible' END AS RoundRobin
FROM CcMembers AS m
JOIN dbo.Employees AS e ON e.EmployeeId = m.EmployeeId
JOIN dbo.AspNetUsers AS u ON u.Id = e.EmployeeId
LEFT JOIN Roles AS ro ON ro.UserId = e.EmployeeId
ORDER BY RoundRobin DESC, e.DisplayName;     -- display order only; the app rotates in .NET Guid order

-- 3. Verdict ---------------------------------------------------------------------------------------------------------------------
;WITH Eligible AS (
    SELECT e.EmployeeId
    FROM dbo.UserDepartmentAssignments AS a
    JOIN dbo.Departments AS d ON d.DepartmentId = a.DepartmentId AND d.Code = N'CC' AND d.IsActive = 1
    JOIN dbo.Employees AS e ON e.EmployeeId = a.EmployeeId AND e.DeactivatedAtUtc IS NULL
    JOIN dbo.AspNetUsers AS u ON u.Id = e.EmployeeId
         AND NOT (u.LockoutEnabled = 1 AND u.LockoutEnd IS NOT NULL AND u.LockoutEnd > SYSDATETIMEOFFSET())
    WHERE NOT EXISTS (SELECT 1 FROM @ServiceIdentity s WHERE s.EmployeeId = e.EmployeeId)
      AND EXISTS (SELECT 1 FROM dbo.AspNetUserRoles ur JOIN dbo.AspNetRoles r ON r.Id = ur.RoleId
                  WHERE ur.UserId = e.EmployeeId AND r.Name IN (N'CS Agent', N'Department Employee', N'Department Head'))
)
SELECT COUNT(*) AS EligibleCallCenterAgents,
       CASE WHEN COUNT(*) >= 2 THEN 'READY'
            WHEN COUNT(*) = 1 THEN 'WARNING: only one eligible agent - every ticket goes to that person'
            ELSE 'BLOCKER: no eligible Call Center agent - all new tickets wait UNASSIGNED in the CC queue' END AS Verdict
FROM Eligible;

-- 4. Context (read-only): rotation cursor and the current unassigned Call Center queue --------------------------------------------
IF OBJECT_ID(N'dbo.DepartmentAssignmentRotations', N'U') IS NULL
    SELECT 'dbo.DepartmentAssignmentRotations does not exist yet - apply Deploy_TigerCS_Prod_Migrations.sql first' AS RotationTable;
ELSE
    SELECT r.DepartmentId, r.LastAssignedEmployeeId, r.UpdatedAtUtc
    FROM dbo.DepartmentAssignmentRotations AS r JOIN dbo.Departments AS d ON d.DepartmentId = r.DepartmentId AND d.Code = N'CC';

SELECT COUNT(*) AS UnassignedCallCenterTickets
FROM dbo.Tickets AS t JOIN dbo.Departments AS d ON d.DepartmentId = t.CurrentDepartmentId AND d.Code = N'CC'
WHERE t.CurrentOwnerEmployeeId IS NULL AND t.TicketStatus <> 6;   -- 6 = Closed (TicketStatus enum: 1 Open, 2 InProgress, 3 PendingCustomer, 4 PendingThirdParty(legacy), 5 Resolved, 6 Closed)
