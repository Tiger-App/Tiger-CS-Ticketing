/*
================================================================================
 Preview_TigerCS_PROD_CallCenterBacklogDistribution_ReadOnly.sql      (DRY RUN - NO WRITES)
 Database : TigerCS_Ticketing_Prod

 READ-ONLY. Contains no INSERT/UPDATE/DELETE/DDL against the database. It only creates session temp tables (tempdb) and SELECTs.
 Run it from a read-only login. It changes nothing, takes no locks that block the application (READ UNCOMMITTED), and can be re-run any time.

 It answers, BEFORE anything is executed:
   1. Which tickets are in scope (Open / InProgress / Pending, ANY department, no owner, no current assignment)? Those outside the Call
      Center would be TRANSFERRED to it first (CurrentDepartmentId only - Originating department, number, status, SLA untouched).
   2. Which tickets are held back or untouched and why (live AI conversations, owned tickets, drift anomalies, Resolved/Closed)?
   3. Which Call Center agents are eligible, in exact Round Robin order, with their current workload?
   4. How many tickets each agent WOULD receive, and the exact ticket -> agent plan.

 It reproduces the existing Round Robin engine (TicketAutoAssignmentService.AssignRoundRobinAsync / FilterEligibleAsync /
 DepartmentAssignmentRotation.PickNext):
   * pool            = active members of the ticket's CURRENT department (here: Code = 'CC', department active)
   * eligibility     = employee not deactivated, account not locked out, not a service identity, holds at least one of
                       CS Agent / Department Employee / Department Head (so Reporting User / Chairman-CEO-only users are excluded)
   * order           = ascending .NET Guid order. For a Guid that is identical to ordinal order of its canonical text, so the script
                       sorts on CONVERT(char(36), id) with a binary collation. (SQL Server's own uniqueidentifier ordering differs - not used.)
   * next agent      = first eligible id greater than the persisted cursor (DepartmentAssignmentRotations.LastAssignedEmployeeId),
                       wrapping to the first; cursor missing / no longer eligible restarts at the first.
 Tickets are dealt out oldest first (CreatedAtUtc, TicketId), so counts per agent differ by at most 1.

 TicketStatus: 1 Open, 2 InProgress, 3 PendingCustomer, 4 PendingThirdParty (legacy)  -> in scope
               5 Resolved, 6 Closed                                                   -> excluded   (there is no 'Cancelled' status in this schema)
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
DECLARE @ServiceIdsConfirmed bit = 0;   -- set to 1 once the list above is complete (an empty list with 0 is reported as a BLOCKER)
DECLARE @IncludeLiveAiConversations bit = 0;   -- must equal the Execute script's value. 0 = live Genesys AI conversations are held back (listed below)
DECLARE @CsAgentRoleOnly bit = 1;              -- must equal the Execute script's value. 1 = only holders of the CS Agent role receive work

-- ---------------------------------------------------------------------------------------------------------------------------
-- 0. Call Center department + rotation cursor
-- ---------------------------------------------------------------------------------------------------------------------------
DECLARE @CcId int = (SELECT TOP (1) DepartmentId FROM dbo.Departments WHERE Code = N'CC' AND IsActive = 1);
DECLARE @CcCount int = (SELECT COUNT(*) FROM dbo.Departments WHERE Code = N'CC' AND IsActive = 1);
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
    RoleList nvarchar(400) NULL, SortKey char(36) COLLATE Latin1_General_BIN2 NOT NULL);

INSERT #Agents (Rn, EmployeeId, DisplayName, UserName, RoleList, SortKey)
SELECT ROW_NUMBER() OVER (ORDER BY CONVERT(char(36), e.EmployeeId) COLLATE Latin1_General_BIN2),
       e.EmployeeId, e.DisplayName, u.UserName,
       (SELECT STRING_AGG(r.Name, N', ') WITHIN GROUP (ORDER BY r.Name)
          FROM dbo.AspNetUserRoles ur JOIN dbo.AspNetRoles r ON r.Id = ur.RoleId WHERE ur.UserId = e.EmployeeId),
       CONVERT(char(36), e.EmployeeId) COLLATE Latin1_General_BIN2
FROM dbo.Employees AS e
JOIN dbo.AspNetUsers AS u ON u.Id = e.EmployeeId
WHERE @CcId IS NOT NULL
  AND e.DeactivatedAtUtc IS NULL                                                                   -- deactivated employees excluded
  AND EXISTS (SELECT 1 FROM dbo.UserDepartmentAssignments a WHERE a.EmployeeId = e.EmployeeId AND a.DepartmentId = @CcId)
  AND NOT (u.LockoutEnabled = 1 AND u.LockoutEnd IS NOT NULL AND u.LockoutEnd > SYSDATETIMEOFFSET())  -- locked accounts excluded
  AND NOT EXISTS (SELECT 1 FROM @ServiceIdentity s WHERE s.EmployeeId = e.EmployeeId)             -- service accounts excluded
  AND EXISTS (SELECT 1 FROM dbo.AspNetUserRoles ur JOIN dbo.AspNetRoles r ON r.Id = ur.RoleId     -- read-only / non-operational excluded
              WHERE ur.UserId = e.EmployeeId AND r.Name IN (N'CS Agent', N'Department Employee', N'Department Head'))
  AND (@CsAgentRoleOnly = 0 OR EXISTS (SELECT 1 FROM dbo.AspNetUserRoles ur JOIN dbo.AspNetRoles r ON r.Id = ur.RoleId
                                       WHERE ur.UserId = e.EmployeeId AND r.Name = N'CS Agent'));

DECLARE @N int = (SELECT COUNT(*) FROM #Agents);

-- ---------------------------------------------------------------------------------------------------------------------------
-- 2. Candidate tickets (the only tickets that would ever be touched)
-- ---------------------------------------------------------------------------------------------------------------------------
CREATE TABLE #Cand (Seq int NOT NULL PRIMARY KEY, TicketId bigint NOT NULL, TicketNumber nvarchar(40) NULL, TicketStatus tinyint NOT NULL, CreatedAtUtc datetime2 NOT NULL,
                    SrcDeptId int NOT NULL, SrcDeptCode nvarchar(50) NULL);
CREATE TABLE #Held (TicketId bigint NOT NULL PRIMARY KEY, TicketNumber nvarchar(40) NULL, TicketStatus tinyint NOT NULL, SrcDeptCode nvarchar(50) NULL, CreatedAtUtc datetime2 NOT NULL);

-- unowned, active, no current assignment, ANY department; split into "in scope" and "held back: live AI conversation"
SELECT t.TicketId, t.TicketNumber, t.TicketStatus, t.CreatedAtUtc, t.CurrentDepartmentId, d.Code AS DeptCode,
       CASE WHEN EXISTS (SELECT 1 FROM dbo.TicketInteractions i
                         WHERE i.TicketId = t.TicketId AND i.IsOriginatingInteraction = 1 AND i.GenesysConversationId IS NOT NULL
                           AND i.RequiresHumanFollowUp = 0 AND i.EndedAtUtc IS NULL AND i.InactivityClosedAtUtc IS NULL) THEN 1 ELSE 0 END AS LiveAi
INTO #All
FROM dbo.Tickets AS t
JOIN dbo.Departments AS d ON d.DepartmentId = t.CurrentDepartmentId
WHERE @CcId IS NOT NULL
  AND t.TicketStatus IN (1, 2, 3, 4)                                   -- Open, InProgress, Pending*
  AND t.CurrentOwnerEmployeeId IS NULL                                 -- no owner
  AND NOT EXISTS (SELECT 1 FROM dbo.TicketAssignments a WHERE a.TicketId = t.TicketId AND a.IsCurrent = 1);   -- and no active assignment

INSERT #Cand (Seq, TicketId, TicketNumber, TicketStatus, CreatedAtUtc, SrcDeptId, SrcDeptCode)
SELECT ROW_NUMBER() OVER (ORDER BY CreatedAtUtc, TicketId), TicketId, TicketNumber, TicketStatus, CreatedAtUtc, CurrentDepartmentId, DeptCode
FROM #All WHERE @IncludeLiveAiConversations = 1 OR LiveAi = 0;

INSERT #Held (TicketId, TicketNumber, TicketStatus, SrcDeptCode, CreatedAtUtc)
SELECT TicketId, TicketNumber, TicketStatus, DeptCode, CreatedAtUtc FROM #All WHERE @IncludeLiveAiConversations = 0 AND LiveAi = 1;

DECLARE @Cand int = (SELECT COUNT(*) FROM #Cand);
DECLARE @Transfers int = (SELECT COUNT(*) FROM #Cand WHERE SrcDeptId <> @CcId);

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
       @CcId AS CallCenterDepartmentId, @N AS EligibleAgents, @Cand AS TicketsToAssign, @Transfers AS TicketsToTransferToCallCenter,
       @Cursor AS RotationCursor_LastAssignedEmployeeId,
       CASE
           WHEN @CcCount <> 1                                    THEN 'BLOCKER: expected exactly one active department with Code=CC, found ' + CONVERT(varchar(10), @CcCount)
           WHEN @HasRotationTable = 0                            THEN 'BLOCKER: dbo.DepartmentAssignmentRotations is missing - apply Deploy_TigerCS_Prod_Migrations.sql first'
           WHEN @AssignmentAllowed = 0                           THEN 'BLOCKER: Call Center workflow settings disable assignment (the engine would leave tickets in the queue)'
           WHEN @N = 0                                           THEN 'BLOCKER: no eligible Call Center agent'
           WHEN @ServiceIdsConfirmed = 0                         THEN 'BLOCKER: service-account list not confirmed (@ServiceIdsConfirmed = 0) - a service account could be listed as an agent below'
           WHEN @N = 1                                           THEN 'WARNING: only one eligible agent - every ticket goes to that person'
           WHEN @Cand = 0                                        THEN 'NOTHING TO DO: no eligible unowned tickets'
           ELSE 'READY' END AS Verdict;

-- ===========================================================================================================================
-- RESULT SET 2 - Scope reconciliation: every unowned non-terminal ticket, by department, and what would happen to it
-- ===========================================================================================================================
SELECT d.Code AS DepartmentCode, d.Name AS DepartmentName,
       SUM(CASE WHEN t.TicketStatus = 1 THEN 1 ELSE 0 END) AS [Open],
       SUM(CASE WHEN t.TicketStatus = 2 THEN 1 ELSE 0 END) AS InProgress,
       SUM(CASE WHEN t.TicketStatus IN (3, 4) THEN 1 ELSE 0 END) AS Pending,
       COUNT(*) AS TotalUnowned,
       (SELECT COUNT(*) FROM #Cand c WHERE c.SrcDeptId = d.DepartmentId) AS InScope,
       (SELECT COUNT(*) FROM #Held h WHERE h.SrcDeptCode = d.Code)       AS HeldBack_LiveAiConversation,
       CASE WHEN d.DepartmentId = @CcId THEN 'Call Center: assigned in place (no department change)'
            ELSE 'transferred to Call Center (CurrentDepartmentId only), then assigned' END AS WhatHappens
FROM dbo.Tickets AS t
JOIN dbo.Departments AS d ON d.DepartmentId = t.CurrentDepartmentId
WHERE t.TicketStatus IN (1, 2, 3, 4) AND t.CurrentOwnerEmployeeId IS NULL
  AND NOT EXISTS (SELECT 1 FROM dbo.TicketAssignments a WHERE a.TicketId = t.TicketId AND a.IsCurrent = 1)
GROUP BY d.DepartmentId, d.Code, d.Name
ORDER BY CASE WHEN d.DepartmentId = @CcId THEN 0 ELSE 1 END, COUNT(*) DESC;

-- Held back (NOT touched unless @IncludeLiveAiConversations = 1): live Genesys AI conversations. Moving them would switch off AI closure / inactivity rules.
SELECT TicketNumber, SrcDeptCode, CASE TicketStatus WHEN 1 THEN 'Open' WHEN 2 THEN 'InProgress' ELSE 'Pending' END AS [Status], CreatedAtUtc
FROM #Held ORDER BY CreatedAtUtc;

-- Counts of what is excluded and untouched, for the sign-off:
SELECT
    (SELECT COUNT(*) FROM dbo.Tickets WHERE TicketStatus IN (5, 6) AND CurrentOwnerEmployeeId IS NULL)  AS UnownedResolvedOrClosed_Excluded,
    (SELECT COUNT(*) FROM dbo.Tickets WHERE TicketStatus IN (1, 2, 3, 4) AND CurrentOwnerEmployeeId IS NOT NULL) AS AlreadyOwned_NeverTouched,
    (SELECT COUNT(*) FROM dbo.Tickets t WHERE t.TicketStatus IN (1, 2, 3, 4) AND t.CurrentOwnerEmployeeId IS NULL
        AND EXISTS (SELECT 1 FROM dbo.TicketAssignments a WHERE a.TicketId = t.TicketId AND a.IsCurrent = 1)) AS NoOwnerButHasCurrentAssignment_DataDrift_Excluded,
    (SELECT COUNT(*) FROM dbo.Tickets t WHERE t.TicketStatus IN (1, 2, 3, 4) AND t.CurrentOwnerEmployeeId IS NOT NULL
        AND NOT EXISTS (SELECT 1 FROM dbo.TicketAssignments a WHERE a.TicketId = t.TicketId AND a.IsCurrent = 1)) AS OwnerButNoAssignmentRow_Legacy_NeverTouched,
    (SELECT COUNT(*) FROM (SELECT TicketId FROM dbo.TicketAssignments WHERE IsCurrent = 1 GROUP BY TicketId HAVING COUNT(*) > 1) x) AS TicketsWithMoreThanOneCurrentAssignment_Existing;

-- In-scope tickets by status
SELECT CASE TicketStatus WHEN 1 THEN 'Open' WHEN 2 THEN 'InProgress' WHEN 3 THEN 'PendingCustomer' WHEN 4 THEN 'PendingThirdParty(legacy)' END AS [Status],
       COUNT(*) AS Tickets, MIN(CreatedAtUtc) AS Oldest, MAX(CreatedAtUtc) AS Newest
FROM #Cand GROUP BY TicketStatus ORDER BY TicketStatus;

-- ===========================================================================================================================
-- RESULT SET 3 - Agents (all Call Center members, with the reason each is in or out) in rotation order
-- ===========================================================================================================================
SELECT ISNULL(CONVERT(varchar(10), a.Rn), '-') AS RotationPosition, e.DisplayName, u.UserName, e.EmployeeId,
       ISNULL((SELECT STRING_AGG(r.Name, N', ') WITHIN GROUP (ORDER BY r.Name)
                 FROM dbo.AspNetUserRoles ur JOIN dbo.AspNetRoles r ON r.Id = ur.RoleId WHERE ur.UserId = e.EmployeeId), N'(no role)') AS Roles,
       CASE WHEN a.EmployeeId IS NOT NULL THEN 'ELIGIBLE'
            WHEN e.DeactivatedAtUtc IS NOT NULL THEN 'excluded: deactivated'
            WHEN u.LockoutEnabled = 1 AND u.LockoutEnd IS NOT NULL AND u.LockoutEnd > SYSDATETIMEOFFSET() THEN 'excluded: locked out'
            WHEN EXISTS (SELECT 1 FROM @ServiceIdentity s WHERE s.EmployeeId = e.EmployeeId) THEN 'excluded: service account'
            WHEN @CsAgentRoleOnly = 1 AND EXISTS (SELECT 1 FROM dbo.AspNetUserRoles ur JOIN dbo.AspNetRoles r ON r.Id = ur.RoleId
                                                  WHERE ur.UserId = e.EmployeeId AND r.Name IN (N'Department Employee', N'Department Head'))
                                      THEN 'excluded by @CsAgentRoleOnly: engine-eligible but no CS Agent role (cannot Transfer as owner)'
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
-- RESULT SET 4 - PROJECTED DISTRIBUTION PER AGENT  (this is the table to approve)
-- ===========================================================================================================================
SELECT a.Rn AS RotationPosition, a.DisplayName, a.UserName,
       COUNT(p.TicketId) AS TicketsToReceive,
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

-- Values to copy into the execution script's approval gates:
SELECT @Cand AS ApprovedTicketCount_for_execution_script, @Transfers AS ApprovedTransferCount_for_execution_script, @N AS ApprovedAgentCount_for_execution_script;

-- ===========================================================================================================================
-- RESULT SET 5 - Exact plan: ticket -> agent (ticket numbers only; no customer data)
-- ===========================================================================================================================
SELECT c.Seq AS DealOrder, c.TicketId, c.TicketNumber, c.SrcDeptCode AS FromDepartment, CASE WHEN c.SrcDeptId <> @CcId THEN 'TRANSFER to CC + assign' ELSE 'assign in CC' END AS Action,
       CASE c.TicketStatus WHEN 1 THEN 'Open' WHEN 2 THEN 'InProgress' WHEN 3 THEN 'PendingCustomer' WHEN 4 THEN 'PendingThirdParty' END AS [Status],
       c.CreatedAtUtc, a.DisplayName AS WouldBeAssignedTo, a.UserName
FROM #Cand AS c
JOIN #Plan AS p ON p.TicketId = c.TicketId
JOIN #Agents AS a ON a.Rn = p.AgentRn
ORDER BY c.Seq;

-- Where the cursor would end up after the run:
SELECT TOP (1) a.DisplayName AS CursorWouldMoveTo, a.EmployeeId
FROM #Plan p JOIN #Agents a ON a.Rn = p.AgentRn ORDER BY p.Seq DESC;

DROP TABLE #Plan; DROP TABLE #Cand; DROP TABLE #Held; DROP TABLE #All; DROP TABLE #Agents;
