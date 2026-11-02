/*
================================================================================
 Preview_TigerCS_PROD_CallCenterBacklogDistribution_ReadOnly.sql      (DRY RUN - NO WRITES)
 Database : TigerCS_Ticketing_Prod

 READ-ONLY. No INSERT/UPDATE/DELETE/DDL against the database; only session temp tables (tempdb) and SELECTs. Run from a read-only login.
 READ UNCOMMITTED, so it never blocks the application. Safe to re-run any time.

 BUSINESS RULE (confirmed): every existing UNOWNED Open / InProgress / Pending ticket, from EVERY department, is moved into the Call Center
 (CC) and given to exactly ONE eligible Call Center agent by Round Robin. One-time backlog redistribution; future tickets are unaffected.

 A ticket is ELIGIBLE when ALL hold:
   * TicketStatus IN (1 Open, 2 InProgress, 3 PendingCustomer, 4 PendingThirdParty-legacy)   (Resolved 5 / Closed 6 excluded; the schema has no 'Cancelled')
   * Tickets.CurrentOwnerEmployeeId IS NULL                                                 (never overwrite an employee's ownership)
   * no TicketAssignments row with IsCurrent = 1                                            (never overwrite/duplicate an active assignment)
   * any CurrentDepartmentId (active OR inactive department)
 What the run would change per eligible ticket:  CurrentDepartmentId -> CC (if not already CC) and CurrentOwnerEmployeeId -> the dealt agent.
 NEVER changed: OriginatingDepartmentId, RequestTypeId, CategoryId, PriorityId, TicketStatus, SlaState, history, notes, approvals, interactions, notifications.

 ROUND ROBIN (reproduces TicketAutoAssignmentService.AssignRoundRobinAsync / FilterEligibleAsync / DepartmentAssignmentRotation.PickNext):
   * pool       = members of the active CC department who are: not deactivated, not locked out, not a service identity, and hold at least one of
                  CS Agent / Department Employee / Department Head (so read-only roles, supervisors, managers and admins are never dealt tickets)
   * order      = ascending .NET Guid order == ordinal order of the canonical GUID text, so the script sorts on CONVERT(char(36), id) with a binary
                  collation (SQL Server's own uniqueidentifier ordering is different and is deliberately not used)
   * next agent = first eligible id greater than the persisted cursor (DepartmentAssignmentRotations.LastAssignedEmployeeId), wrapping to the first
   * tickets are dealt OLDEST FIRST (CreatedAtUtc, TicketId) so per-agent counts differ by at most 1.
================================================================================
*/
SET NOCOUNT ON;
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

DECLARE @ExpectedDb sysname = N'TigerCS_Ticketing_Prod';
IF DB_NAME() <> @ExpectedDb
BEGIN
    DECLARE @m nvarchar(400) = N'ABORTED: connected to [' + ISNULL(DB_NAME(), N'?') + N'], this preview is for [' + @ExpectedDb + N'] only.';
    THROW 50200, @m, 1;
END;

-- Service accounts are configured in appsettings, not in SQL. Paste every id from BOTH settings (one INSERT per id):
--   Authorization:ServiceIdentity:EmployeeIds      and      Collections:Authorization:IntegrationEmployeeIds
-- Edits the variable table only; nothing persists.
DECLARE @ServiceIdentity TABLE (EmployeeId uniqueidentifier PRIMARY KEY);
-- INSERT INTO @ServiceIdentity VALUES ('00000000-0000-0000-0000-000000000000');
DECLARE @ServiceIdsConfirmed bit = 0;      -- set 1 once the list above is complete
DECLARE @NoServiceAccountsExist bit = 0;   -- set 1 ONLY if you have confirmed there are no service/integration accounts at all (empty list)

-- ---------------------------------------------------------------------------------------------------------------------------
-- 0. Call Center department + rotation cursor
-- ---------------------------------------------------------------------------------------------------------------------------
DECLARE @CcCount int = (SELECT COUNT(*) FROM dbo.Departments WHERE Code = N'CC' AND IsActive = 1);
DECLARE @CcId int = (SELECT TOP (1) DepartmentId FROM dbo.Departments WHERE Code = N'CC' AND IsActive = 1);
DECLARE @HasRotationTable bit = CASE WHEN OBJECT_ID(N'dbo.DepartmentAssignmentRotations', N'U') IS NULL THEN 0 ELSE 1 END;
DECLARE @Cursor uniqueidentifier = NULL;
IF @HasRotationTable = 1 AND @CcId IS NOT NULL
    SELECT @Cursor = LastAssignedEmployeeId FROM dbo.DepartmentAssignmentRotations WHERE DepartmentId = @CcId;
DECLARE @AssignmentAllowed bit = ISNULL((SELECT AllowAssignment FROM dbo.DepartmentWorkflowSettings WHERE DepartmentId = @CcId), 1);

-- ---------------------------------------------------------------------------------------------------------------------------
-- 1. Eligible agents, in Round Robin order
-- ---------------------------------------------------------------------------------------------------------------------------
CREATE TABLE #Agents (
    Rn int NOT NULL PRIMARY KEY, EmployeeId uniqueidentifier NOT NULL, DisplayName nvarchar(200) NULL, UserName nvarchar(256) NULL,
    SortKey char(36) COLLATE Latin1_General_BIN2 NOT NULL);

INSERT #Agents (Rn, EmployeeId, DisplayName, UserName, SortKey)
SELECT ROW_NUMBER() OVER (ORDER BY CONVERT(char(36), e.EmployeeId) COLLATE Latin1_General_BIN2),
       e.EmployeeId, e.DisplayName, u.UserName, CONVERT(char(36), e.EmployeeId) COLLATE Latin1_General_BIN2
FROM dbo.Employees AS e
JOIN dbo.AspNetUsers AS u ON u.Id = e.EmployeeId
WHERE @CcId IS NOT NULL
  AND e.DeactivatedAtUtc IS NULL                                                                   -- deactivated employees excluded
  AND EXISTS (SELECT 1 FROM dbo.UserDepartmentAssignments a WHERE a.EmployeeId = e.EmployeeId AND a.DepartmentId = @CcId)
  AND NOT (u.LockoutEnabled = 1 AND u.LockoutEnd IS NOT NULL AND u.LockoutEnd > SYSDATETIMEOFFSET())  -- locked accounts excluded
  AND NOT EXISTS (SELECT 1 FROM @ServiceIdentity s WHERE s.EmployeeId = e.EmployeeId)             -- service accounts excluded
  AND EXISTS (SELECT 1 FROM dbo.AspNetUserRoles ur JOIN dbo.AspNetRoles r ON r.Id = ur.RoleId     -- read-only / non-operational excluded
              WHERE ur.UserId = e.EmployeeId AND r.Name IN (N'CS Agent', N'Department Employee', N'Department Head'));

DECLARE @N int = (SELECT COUNT(*) FROM #Agents);

-- ---------------------------------------------------------------------------------------------------------------------------
-- 2. Eligible tickets (ALL departments)
-- ---------------------------------------------------------------------------------------------------------------------------
CREATE TABLE #Cand (Seq int NOT NULL PRIMARY KEY, TicketId bigint NOT NULL, TicketNumber nvarchar(40) NULL, TicketStatus tinyint NOT NULL,
    CreatedAtUtc datetime2 NOT NULL, FromDepartmentId int NOT NULL, OriginatingDepartmentId int NOT NULL, IsMove bit NOT NULL);

INSERT #Cand (Seq, TicketId, TicketNumber, TicketStatus, CreatedAtUtc, FromDepartmentId, OriginatingDepartmentId, IsMove)
SELECT ROW_NUMBER() OVER (ORDER BY t.CreatedAtUtc, t.TicketId), t.TicketId, t.TicketNumber, t.TicketStatus, t.CreatedAtUtc,
       t.CurrentDepartmentId, t.OriginatingDepartmentId, CASE WHEN t.CurrentDepartmentId <> @CcId THEN 1 ELSE 0 END
FROM dbo.Tickets AS t
WHERE @CcId IS NOT NULL
  AND t.TicketStatus IN (1, 2, 3, 4)                                   -- Open, InProgress, Pending*
  AND t.CurrentOwnerEmployeeId IS NULL                                 -- no owner
  AND NOT EXISTS (SELECT 1 FROM dbo.TicketAssignments a WHERE a.TicketId = t.TicketId AND a.IsCurrent = 1);   -- and no active assignment

DECLARE @Cand int = (SELECT COUNT(*) FROM #Cand);
DECLARE @Moved int = (SELECT COUNT(*) FROM #Cand WHERE IsMove = 1);

-- ---------------------------------------------------------------------------------------------------------------------------
-- 3. The Round Robin deal
-- ---------------------------------------------------------------------------------------------------------------------------
DECLARE @Start int = 1;
IF @Cursor IS NOT NULL
    SELECT @Start = ISNULL(MIN(Rn), 1) FROM #Agents WHERE SortKey > CONVERT(char(36), @Cursor) COLLATE Latin1_General_BIN2;

CREATE TABLE #Plan (Seq int NOT NULL PRIMARY KEY, TicketId bigint NOT NULL, AgentRn int NOT NULL);
IF @N > 0
    INSERT #Plan (Seq, TicketId, AgentRn)
    SELECT c.Seq, c.TicketId, ((@Start - 1 + c.Seq - 1) % @N) + 1 FROM #Cand AS c;

-- ===========================================================================================================================
-- RESULT SET 1 - Verdict
-- ===========================================================================================================================
SELECT @@SERVERNAME AS [Server], DB_NAME() AS [Database], SYSUTCDATETIME() AS PreviewAtUtc,
       @CcId AS CallCenterDepartmentId, @N AS EligibleAgents,
       @Cand AS EligibleTickets_Total, @Moved AS TicketsMovedIntoCC, @Cand - @Moved AS TicketsAlreadyInCC_AssignOnly,
       @Cursor AS RotationCursor_LastAssignedEmployeeId,
       CASE
           WHEN @CcCount <> 1                                    THEN 'BLOCKER: expected exactly one active department with Code=CC, found ' + CONVERT(varchar(10), @CcCount)
           WHEN @HasRotationTable = 0                            THEN 'BLOCKER: dbo.DepartmentAssignmentRotations is missing - apply Deploy_TigerCS_Prod_Migrations.sql first'
           WHEN @AssignmentAllowed = 0                           THEN 'BLOCKER: Call Center workflow settings disable assignment (the engine would leave tickets in the queue)'
           WHEN @N = 0                                           THEN 'BLOCKER: no eligible Call Center agent'
           WHEN @ServiceIdsConfirmed = 0                         THEN 'BLOCKER: service-account exclusions not confirmed (@ServiceIdsConfirmed = 0) - a service account could be listed as an agent below'
           WHEN NOT EXISTS (SELECT 1 FROM @ServiceIdentity) AND @NoServiceAccountsExist = 0
                                                                 THEN 'BLOCKER: service-account list is empty - list the ids, or set @NoServiceAccountsExist = 1 if there are none'
           WHEN EXISTS (SELECT 1 FROM @ServiceIdentity s WHERE NOT EXISTS (SELECT 1 FROM dbo.Employees e WHERE e.EmployeeId = s.EmployeeId))
                                                                 THEN 'BLOCKER: a listed service-account id is not an employee (typo?) - see ServiceIdsNotFound below'
           WHEN @N = 1                                           THEN 'WARNING: only one eligible agent - every ticket goes to that person'
           WHEN @Cand = 0                                        THEN 'NOTHING TO DO: no eligible tickets'
           ELSE 'READY' END AS Verdict;

SELECT s.EmployeeId AS ServiceIdsNotFound FROM @ServiceIdentity s WHERE NOT EXISTS (SELECT 1 FROM dbo.Employees e WHERE e.EmployeeId = s.EmployeeId);

-- ===========================================================================================================================
-- RESULT SET 2 - ELIGIBLE TICKETS BY ORIGINAL (CURRENT) DEPARTMENT AND STATUS   <- the move being approved
-- ===========================================================================================================================
SELECT d.Code AS OriginalDepartmentCode, d.Name AS OriginalDepartmentName, d.IsActive AS DepartmentIsActive,
       SUM(CASE WHEN c.TicketStatus = 1 THEN 1 ELSE 0 END) AS [Open],
       SUM(CASE WHEN c.TicketStatus = 2 THEN 1 ELSE 0 END) AS InProgress,
       SUM(CASE WHEN c.TicketStatus IN (3, 4) THEN 1 ELSE 0 END) AS Pending,
       COUNT(*) AS Eligible,
       CASE WHEN d.DepartmentId = @CcId THEN 'already in CC - assign only' ELSE 'MOVE to CC + assign' END AS Action
FROM #Cand AS c
JOIN dbo.Departments AS d ON d.DepartmentId = c.FromDepartmentId
GROUP BY d.DepartmentId, d.Code, d.Name, d.IsActive
UNION ALL
SELECT N'** TOTAL **', NULL, NULL, SUM(CASE WHEN TicketStatus = 1 THEN 1 ELSE 0 END), SUM(CASE WHEN TicketStatus = 2 THEN 1 ELSE 0 END),
       SUM(CASE WHEN TicketStatus IN (3, 4) THEN 1 ELSE 0 END), COUNT(*), NULL
FROM #Cand
ORDER BY Eligible DESC;

-- ===========================================================================================================================
-- RESULT SET 3 - What is EXCLUDED / never touched (for the sign-off)
-- ===========================================================================================================================
SELECT
    (SELECT COUNT(*) FROM dbo.Tickets WHERE TicketStatus IN (5, 6) AND CurrentOwnerEmployeeId IS NULL)                         AS UnownedResolvedOrClosed_Excluded,
    (SELECT COUNT(*) FROM dbo.Tickets WHERE TicketStatus IN (1, 2, 3, 4) AND CurrentOwnerEmployeeId IS NOT NULL)               AS AlreadyOwned_NeverTouched,
    (SELECT COUNT(*) FROM dbo.Tickets t WHERE t.TicketStatus IN (1, 2, 3, 4) AND t.CurrentOwnerEmployeeId IS NULL
        AND EXISTS (SELECT 1 FROM dbo.TicketAssignments a WHERE a.TicketId = t.TicketId AND a.IsCurrent = 1))                  AS NoOwnerButHasCurrentAssignment_DataDrift_Excluded,
    (SELECT COUNT(*) FROM dbo.Tickets t WHERE t.TicketStatus IN (1, 2, 3, 4) AND t.CurrentOwnerEmployeeId IS NOT NULL
        AND NOT EXISTS (SELECT 1 FROM dbo.TicketAssignments a WHERE a.TicketId = t.TicketId AND a.IsCurrent = 1))              AS OwnerButNoAssignmentRow_Legacy_NeverTouched,
    (SELECT COUNT(*) FROM (SELECT TicketId FROM dbo.TicketAssignments WHERE IsCurrent = 1 GROUP BY TicketId HAVING COUNT(*) > 1) x) AS TicketsWithMoreThanOneCurrentAssignment_MustBe0;

-- Informational flags on the ELIGIBLE tickets (they will still be moved - this is what the Call Center inherits):
SELECT
    (SELECT COUNT(*) FROM #Cand c JOIN dbo.Tickets t ON t.TicketId = c.TicketId WHERE t.RequestTypeId IS NOT NULL)                       AS WithRequestType_Preserved,
    (SELECT COUNT(*) FROM #Cand c JOIN dbo.Tickets t ON t.TicketId = c.TicketId WHERE t.CategoryId IS NULL)                                AS Unclassified,
    (SELECT COUNT(*) FROM #Cand c JOIN dbo.Tickets t ON t.TicketId = c.TicketId WHERE t.SlaState = 4)                                    AS SlaBreached_ClockUntouched,
    (SELECT COUNT(*) FROM #Cand c WHERE EXISTS (SELECT 1 FROM dbo.TicketApprovals ap WHERE ap.TicketId = c.TicketId AND ap.IsCurrent = 1 AND ap.Status = 1)) AS WithPendingApproval_ApprovalKept,
    (SELECT COUNT(*) FROM #Cand c WHERE c.OriginatingDepartmentId <> c.FromDepartmentId)                                                    AS AlreadyTransferredBefore_OriginatingDeptKept;

-- ===========================================================================================================================
-- RESULT SET 4 - Agents (all Call Center members, with the reason each is in or out), in rotation order
-- ===========================================================================================================================
SELECT ISNULL(CONVERT(varchar(10), a.Rn), '-') AS RotationPosition, e.DisplayName, u.UserName, e.EmployeeId,
       ISNULL((SELECT STRING_AGG(r.Name, N', ') WITHIN GROUP (ORDER BY r.Name)
                 FROM dbo.AspNetUserRoles ur JOIN dbo.AspNetRoles r ON r.Id = ur.RoleId WHERE ur.UserId = e.EmployeeId), N'(no role)') AS Roles,
       CASE WHEN a.EmployeeId IS NOT NULL THEN 'ELIGIBLE'
            WHEN e.DeactivatedAtUtc IS NOT NULL THEN 'excluded: deactivated'
            WHEN u.LockoutEnabled = 1 AND u.LockoutEnd IS NOT NULL AND u.LockoutEnd > SYSDATETIMEOFFSET() THEN 'excluded: locked out'
            WHEN EXISTS (SELECT 1 FROM @ServiceIdentity s WHERE s.EmployeeId = e.EmployeeId) THEN 'excluded: service account'
            ELSE 'excluded: no operational role (read-only / supervisory / admin)' END AS RoundRobin,
       CASE WHEN a.EmployeeId IS NOT NULL AND a.EmployeeId = @Cursor THEN '<- last assigned (cursor)' ELSE '' END AS CursorMark,
       (SELECT COUNT(*) FROM dbo.Tickets t WHERE t.CurrentOwnerEmployeeId = e.EmployeeId AND t.TicketStatus IN (1, 2, 3, 4)) AS ActiveTicketsOwnedNow
FROM dbo.UserDepartmentAssignments AS uda
JOIN dbo.Employees AS e ON e.EmployeeId = uda.EmployeeId
JOIN dbo.AspNetUsers AS u ON u.Id = e.EmployeeId
LEFT JOIN #Agents AS a ON a.EmployeeId = e.EmployeeId
WHERE uda.DepartmentId = @CcId
ORDER BY CASE WHEN a.Rn IS NULL THEN 1 ELSE 0 END, a.Rn, e.DisplayName;

-- Heuristic only (not a rule): CC members whose name looks like an integration/bot account - confirm against the service-account list.
SELECT u.UserName, e.DisplayName, e.EmployeeId AS PossibleServiceAccount_ReviewManually
FROM dbo.UserDepartmentAssignments AS uda
JOIN dbo.Employees AS e ON e.EmployeeId = uda.EmployeeId
JOIN dbo.AspNetUsers AS u ON u.Id = e.EmployeeId
WHERE uda.DepartmentId = @CcId AND e.DeactivatedAtUtc IS NULL
  AND (u.UserName LIKE N'%genesys%' OR u.UserName LIKE N'%integration%' OR u.UserName LIKE N'%service%' OR u.UserName LIKE N'%bot%' OR u.UserName LIKE N'%api%'
    OR e.DisplayName LIKE N'%genesys%' OR e.DisplayName LIKE N'%integration%' OR e.DisplayName LIKE N'%service%' OR e.DisplayName LIKE N'%bot%');

-- ===========================================================================================================================
-- RESULT SET 5 - PROJECTED DISTRIBUTION PER CALL CENTER AGENT  (this is the table to approve)
-- ===========================================================================================================================
SELECT a.Rn AS RotationPosition, a.DisplayName, a.UserName,
       COUNT(p.TicketId) AS TicketsToReceive,
       SUM(CASE WHEN c.IsMove = 1 THEN 1 ELSE 0 END) AS FromOtherDepartments,
       SUM(CASE WHEN c.IsMove = 0 THEN 1 ELSE 0 END) AS AlreadyInCC,
       SUM(CASE WHEN c.TicketStatus = 1 THEN 1 ELSE 0 END) AS Open_,
       SUM(CASE WHEN c.TicketStatus = 2 THEN 1 ELSE 0 END) AS InProgress_,
       SUM(CASE WHEN c.TicketStatus IN (3, 4) THEN 1 ELSE 0 END) AS Pending_,
       (SELECT COUNT(*) FROM dbo.Tickets t WHERE t.CurrentOwnerEmployeeId = a.EmployeeId AND t.TicketStatus IN (1, 2, 3, 4)) AS ActiveOwnedBefore,
       (SELECT COUNT(*) FROM dbo.Tickets t WHERE t.CurrentOwnerEmployeeId = a.EmployeeId AND t.TicketStatus IN (1, 2, 3, 4)) + COUNT(p.TicketId) AS ActiveOwnedAfter,
       CASE WHEN a.Rn = @Start AND @Cand > 0 THEN '<- first to receive' ELSE '' END AS Note
FROM #Agents AS a
LEFT JOIN #Plan AS p ON p.AgentRn = a.Rn
LEFT JOIN #Cand AS c ON c.TicketId = p.TicketId
GROUP BY a.Rn, a.DisplayName, a.UserName, a.EmployeeId
ORDER BY a.Rn;

SELECT MIN(cnt) AS MinPerAgent, MAX(cnt) AS MaxPerAgent, MAX(cnt) - MIN(cnt) AS Spread_MustBeAtMost1,
       @Cand AS TotalTickets, @N AS Agents, @Cand / NULLIF(@N, 0) AS FloorPerAgent, @Cand % NULLIF(@N, 0) AS AgentsGettingOneExtra
FROM (SELECT a.Rn, COUNT(p.TicketId) AS cnt FROM #Agents a LEFT JOIN #Plan p ON p.AgentRn = a.Rn GROUP BY a.Rn) x;

-- Original department x receiving agent (how each department's backlog is spread across the Call Center)
SELECT d.Code AS OriginalDepartmentCode, a.DisplayName AS ReceivingAgent, COUNT(*) AS Tickets
FROM #Plan p
JOIN #Cand c ON c.TicketId = p.TicketId
JOIN dbo.Departments d ON d.DepartmentId = c.FromDepartmentId
JOIN #Agents a ON a.Rn = p.AgentRn
GROUP BY d.Code, a.Rn, a.DisplayName
ORDER BY d.Code, a.Rn;

-- ===========================================================================================================================
-- RESULT SET 6 - APPROVAL VALUES: copy these three numbers into the execution script's commit gates
-- ===========================================================================================================================
SELECT @Cand AS ApprovedTicketCount, @Moved AS ApprovedMovedTicketCount, @N AS ApprovedAgentCount;

-- ===========================================================================================================================
-- RESULT SET 7 - Exact plan: ticket -> original department -> receiving agent (ticket numbers only; no customer data)
-- ===========================================================================================================================
SELECT c.Seq AS DealOrder, c.TicketId, c.TicketNumber,
       CASE c.TicketStatus WHEN 1 THEN 'Open' WHEN 2 THEN 'InProgress' WHEN 3 THEN 'PendingCustomer' WHEN 4 THEN 'PendingThirdParty' END AS [Status],
       c.CreatedAtUtc, od.Code AS FromDepartment, CASE WHEN c.IsMove = 1 THEN 'CC' ELSE 'CC (no move)' END AS ToDepartment,
       a.DisplayName AS WouldBeAssignedTo, a.UserName
FROM #Cand AS c
JOIN #Plan AS p ON p.TicketId = c.TicketId
JOIN #Agents AS a ON a.Rn = p.AgentRn
JOIN dbo.Departments AS od ON od.DepartmentId = c.FromDepartmentId
ORDER BY c.Seq;

-- Where the cursor would end up after the run:
SELECT TOP (1) a.DisplayName AS CursorWouldMoveTo, a.EmployeeId
FROM #Plan p JOIN #Agents a ON a.Rn = p.AgentRn ORDER BY p.Seq DESC;

DROP TABLE #Plan; DROP TABLE #Cand; DROP TABLE #Agents;
