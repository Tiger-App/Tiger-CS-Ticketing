/*
================================================================================
 Preview_TigerCS_PROD_CallCenterBacklogDistribution_ReadOnly.sql      (DRY RUN - NO WRITES)
 Database : TigerCS_Ticketing_Prod

 READ-ONLY. No INSERT/UPDATE/DELETE/DDL against the database; only session temp tables (tempdb) and SELECTs. Run from a read-only login.
 READ UNCOMMITTED, so it never blocks the application. Safe to re-run any time.

 BUSINESS RULE (confirmed): EVERY existing OPEN ticket, from EVERY department - whether it has an owner or not - is moved into the Call Center (CC) and given
 to exactly ONE eligible Call Center agent by Round Robin. A ticket whose Genesys interaction was ANSWERED by an eligible agent goes to that answering agent instead
 (it does not consume a Round Robin turn). One-time backlog redistribution; future tickets are routed by the application itself.

 A ticket is IN THE PLAN when ALL hold:
   * TicketStatus = 1 (Open).   InProgress / Pending* / Resolved / Closed are NOT in the plan and are never touched (the schema has no 'Cancelled')
   * NO pending approval  (TicketApprovals.Status = 1)                                      -> HELD BACK, listed separately below
   * NO active AI conversation: a Genesys interaction that is not ended, not inactivity-closed, needs no human follow-up and was not taken by a person,
     on a ticket with no interaction flagged RequiresHumanFollowUp and no open human handoff (AgentHandoff Waiting/Assigned/InProgress)  -> HELD BACK, listed separately
   * any CurrentDepartmentId (active OR inactive department) and any current owner (or none)
 HELD-BACK and non-Open tickets are NEVER touched: not moved, not assigned, no audit row, no event.
 What the run would change per ticket in the plan:  CurrentDepartmentId -> CC (if not already CC) and CurrentOwnerEmployeeId -> the dealt agent. The previous CURRENT
 assignment row is retired (history kept) and a new current row is added. A ticket already in CC and already owned by the dealt agent is KEPT as is (no write).
 NEVER changed: OriginatingDepartmentId, RequestTypeId, CategoryId, PriorityId, TicketStatus, SlaState, history, notes, approvals, interactions (incl. AI conversations), notifications.
 The previous owner of a reassigned ticket stops being the owner: the application's ownership rules then remove their Resolve / Close / Transfer rights on it.

 ROUND ROBIN (reproduces TicketAutoAssignmentService.AssignRoundRobinAsync / FilterEligibleAsync / DepartmentAssignmentRotation.PickNext):
   * pool       = members of the active CC department who are: not deactivated, not locked out, not a service identity, and hold at least one of
                  CS Agent / Department Employee / Department Head (so read-only roles, supervisors, managers and admins are never dealt tickets);
                  with @CsAgentRoleOnly = 1 (default) additionally the CS Agent role - the role that can Transfer as owner (a Department Employee could
                  Resolve/Close but not Transfer a Call Center ticket)
   * order      = ascending .NET Guid order == ordinal order of the canonical GUID text, so the script sorts on CONVERT(char(36), id) with a binary
                  collation (SQL Server's own uniqueidentifier ordering is different and is deliberately not used)
   * next agent = first eligible id greater than the persisted cursor (DepartmentAssignmentRotations.LastAssignedEmployeeId), wrapping to the first
   * tickets are dealt OLDEST FIRST (CreatedAtUtc, TicketId) so the Round Robin counts differ by at most 1 per agent.
   * answering agent = the person recorded as handling the ticket's ORIGINATING Genesys interaction (TicketInteractions.HandledByUserId), if that person is in the pool above.
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

IF COL_LENGTH(N'dbo.TicketInteractions', N'RequiresHumanFollowUp') IS NULL OR OBJECT_ID(N'dbo.TicketAgentHandoffs', N'U') IS NULL OR OBJECT_ID(N'dbo.TicketApprovals', N'U') IS NULL
    THROW 50201, N'ABORTED: dbo.TicketInteractions.RequiresHumanFollowUp / dbo.TicketAgentHandoffs / dbo.TicketApprovals not found - the hold-back rules cannot be evaluated; wrong database or a missing migration.', 1;

-- Service accounts are configured in appsettings, not in SQL. Paste every id from BOTH settings (one INSERT per id):
--   Authorization:ServiceIdentity:EmployeeIds      and      Collections:Authorization:IntegrationEmployeeIds
-- Edits the variable table only; nothing persists.
DECLARE @ServiceIdentity TABLE (EmployeeId uniqueidentifier PRIMARY KEY);
-- INSERT INTO @ServiceIdentity VALUES ('00000000-0000-0000-0000-000000000000');
DECLARE @ServiceIdsConfirmed bit = 0;      -- set 1 once the list above is complete
DECLARE @NoServiceAccountsExist bit = 0;   -- set 1 ONLY if you have confirmed there are no service/integration accounts at all (empty list)
DECLARE @IncludeLiveAiConversations bit = 0;   -- MUST equal the Execute script's value. 0 = tickets with an ACTIVE AI conversation are held back (listed below)
DECLARE @CsAgentRoleOnly bit = 1;              -- MUST equal the Execute script's value. 1 = only holders of the CS Agent role receive tickets (the role that can Transfer as owner)

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
              WHERE ur.UserId = e.EmployeeId AND r.Name IN (N'CS Agent', N'Department Employee', N'Department Head'))
  AND (@CsAgentRoleOnly = 0 OR EXISTS (SELECT 1 FROM dbo.AspNetUserRoles ur JOIN dbo.AspNetRoles r ON r.Id = ur.RoleId
                                       WHERE ur.UserId = e.EmployeeId AND r.Name = N'CS Agent'));

DECLARE @N int = (SELECT COUNT(*) FROM #Agents);

-- ---------------------------------------------------------------------------------------------------------------------------
-- 2. Open tickets (ALL departments, assigned or not), with the two hold-back flags
-- ---------------------------------------------------------------------------------------------------------------------------
CREATE TABLE #Base (TicketId bigint NOT NULL PRIMARY KEY, TicketNumber nvarchar(40) NULL, TicketStatus tinyint NOT NULL, CreatedAtUtc datetime2 NOT NULL,
    FromDepartmentId int NOT NULL, OriginatingDepartmentId int NOT NULL, PrevOwner uniqueidentifier NULL, CurAssignId bigint NULL, PinnedEmployeeId uniqueidentifier NULL,
    PendingApproval bit NOT NULL, ActiveAi bit NOT NULL);

INSERT #Base (TicketId, TicketNumber, TicketStatus, CreatedAtUtc, FromDepartmentId, OriginatingDepartmentId, PrevOwner, CurAssignId, PinnedEmployeeId, PendingApproval, ActiveAi)
SELECT t.TicketId, t.TicketNumber, t.TicketStatus, t.CreatedAtUtc, t.CurrentDepartmentId, t.OriginatingDepartmentId, t.CurrentOwnerEmployeeId,
       (SELECT TOP (1) a.TicketAssignmentId FROM dbo.TicketAssignments a WHERE a.TicketId = t.TicketId AND a.IsCurrent = 1),
       (SELECT TOP (1) i.HandledByUserId FROM dbo.TicketInteractions i
         WHERE i.TicketId = t.TicketId AND i.IsOriginatingInteraction = 1 AND i.HandledByUserId IS NOT NULL ORDER BY i.TicketInteractionId),
       CASE WHEN EXISTS (SELECT 1 FROM dbo.TicketApprovals ap WHERE ap.TicketId = t.TicketId AND ap.Status = 1) THEN 1 ELSE 0 END,
       CASE WHEN (EXISTS (SELECT 1 FROM dbo.TicketInteractions i WHERE i.TicketId = t.TicketId AND i.Source = 2 AND i.EndedAtUtc IS NULL
                     AND i.InactivityClosedAtUtc IS NULL AND i.RequiresHumanFollowUp = 0 AND i.HandledByUserId IS NULL)
       AND NOT EXISTS (SELECT 1 FROM dbo.TicketInteractions i2 WHERE i2.TicketId = t.TicketId AND i2.RequiresHumanFollowUp = 1)
       AND NOT EXISTS (SELECT 1 FROM dbo.TicketAgentHandoffs h WHERE h.TicketId = t.TicketId AND h.Status IN (2, 3, 4))) THEN 1 ELSE 0 END
FROM dbo.Tickets AS t
WHERE @CcId IS NOT NULL
  AND t.TicketStatus = 1;                                              -- Open only

CREATE TABLE #Cand (Seq int NOT NULL PRIMARY KEY, TicketId bigint NOT NULL, TicketNumber nvarchar(40) NULL, TicketStatus tinyint NOT NULL,
    CreatedAtUtc datetime2 NOT NULL, FromDepartmentId int NOT NULL, OriginatingDepartmentId int NOT NULL, IsMove bit NOT NULL,
    PrevOwner uniqueidentifier NULL, CurAssignId bigint NULL, PinnedEmployeeId uniqueidentifier NULL);

INSERT #Cand (Seq, TicketId, TicketNumber, TicketStatus, CreatedAtUtc, FromDepartmentId, OriginatingDepartmentId, IsMove, PrevOwner, CurAssignId, PinnedEmployeeId)
SELECT ROW_NUMBER() OVER (ORDER BY b.CreatedAtUtc, b.TicketId), b.TicketId, b.TicketNumber, b.TicketStatus, b.CreatedAtUtc,
       b.FromDepartmentId, b.OriginatingDepartmentId, CASE WHEN b.FromDepartmentId <> @CcId THEN 1 ELSE 0 END, b.PrevOwner, b.CurAssignId, b.PinnedEmployeeId
FROM #Base AS b
WHERE b.PendingApproval = 0 AND (b.ActiveAi = 0 OR @IncludeLiveAiConversations = 1);   -- held-back tickets never enter the plan

DECLARE @Cand int = (SELECT COUNT(*) FROM #Cand);
DECLARE @Moved int = (SELECT COUNT(*) FROM #Cand WHERE IsMove = 1);
DECLARE @HeldApproval int = (SELECT COUNT(*) FROM #Base WHERE PendingApproval = 1);                    -- pending approval (whether or not an AI conversation is also active)
DECLARE @HeldAiOnly   int = (SELECT COUNT(*) FROM #Base WHERE PendingApproval = 0 AND ActiveAi = 1 AND @IncludeLiveAiConversations = 0);  -- active AI conversation, no pending approval

-- ---------------------------------------------------------------------------------------------------------------------------
-- 3. The deal: answering agent first, otherwise Round Robin
-- ---------------------------------------------------------------------------------------------------------------------------
DECLARE @Start int = 1;
IF @Cursor IS NOT NULL
    SELECT @Start = ISNULL(MIN(Rn), 1) FROM #Agents WHERE SortKey > CONVERT(char(36), @Cursor) COLLATE Latin1_General_BIN2;

CREATE TABLE #Plan (Seq int NOT NULL PRIMARY KEY, TicketId bigint NOT NULL, AgentRn int NOT NULL, IsPinned bit NOT NULL, Action varchar(8) NULL);
IF @N > 0
BEGIN
    INSERT #Plan (Seq, TicketId, AgentRn, IsPinned)
    SELECT c.Seq, c.TicketId, a.Rn, 1 FROM #Cand AS c JOIN #Agents a ON a.EmployeeId = c.PinnedEmployeeId;
    INSERT #Plan (Seq, TicketId, AgentRn, IsPinned)
    SELECT r.Seq, r.TicketId, ((@Start - 1 + r.k - 1) % @N) + 1, 0
    FROM (SELECT c.Seq, c.TicketId, ROW_NUMBER() OVER (ORDER BY c.Seq) AS k FROM #Cand c
          WHERE NOT EXISTS (SELECT 1 FROM #Agents pa WHERE pa.EmployeeId = c.PinnedEmployeeId)) r;
    UPDATE p SET Action = CASE
            WHEN c.IsMove = 0 AND c.PrevOwner = a.EmployeeId
                 AND EXISTS (SELECT 1 FROM dbo.TicketAssignments x WHERE x.TicketAssignmentId = c.CurAssignId AND x.IsCurrent = 1
                             AND x.AssignedEmployeeId = a.EmployeeId AND x.AssignedDepartmentId = @CcId) THEN 'KEEP'
            WHEN c.PrevOwner IS NULL THEN 'ASSIGN' ELSE 'REASSIGN' END
    FROM #Plan p JOIN #Cand c ON c.TicketId = p.TicketId JOIN #Agents a ON a.Rn = p.AgentRn;
END;

DECLARE @Kept int = (SELECT COUNT(*) FROM #Plan WHERE Action = 'KEEP');
DECLARE @Changed int = @Cand - @Kept;
DECLARE @Assigned int = (SELECT COUNT(*) FROM #Plan WHERE Action = 'ASSIGN');
DECLARE @Reassigned int = (SELECT COUNT(*) FROM #Plan WHERE Action = 'REASSIGN');
DECLARE @Pinned int = (SELECT COUNT(*) FROM #Plan WHERE IsPinned = 1);
DECLARE @PlanFp int = (SELECT CHECKSUM_AGG(CHECKSUM(p.TicketId, a.EmployeeId)) FROM #Plan p JOIN #Agents a ON a.Rn = p.AgentRn);
DECLARE @LastRr uniqueidentifier = (SELECT TOP (1) a.EmployeeId FROM #Plan p JOIN #Agents a ON a.Rn = p.AgentRn WHERE p.IsPinned = 0 ORDER BY p.Seq DESC);

-- ===========================================================================================================================
-- RESULT SET 1 - Verdict
-- ===========================================================================================================================
SELECT @@SERVERNAME AS [Server], DB_NAME() AS [Database], SYSUTCDATETIME() AS PreviewAtUtc,
       @CcId AS CallCenterDepartmentId, @N AS EligibleAgents,
       @Cand AS OpenTickets_InPlan, @Changed AS TicketsChanged, @Moved AS TicketsMovedIntoCC, @Kept AS TicketsAlreadyCorrect_Kept,
       @Assigned AS NoOwnerBefore_Assigned, @Reassigned AS Reassigned_FromAnotherOwner, @Pinned AS ToTheAnsweringAgent,
       @HeldApproval AS HeldBack_PendingApproval, @HeldAiOnly AS HeldBack_ActiveAiConversation,
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
           WHEN @Cand = 0                                        THEN 'NOTHING TO DO: no Open tickets in the plan'
           ELSE 'READY' END AS Verdict;

SELECT s.EmployeeId AS ServiceIdsNotFound FROM @ServiceIdentity s WHERE NOT EXISTS (SELECT 1 FROM dbo.Employees e WHERE e.EmployeeId = s.EmployeeId);

-- ===========================================================================================================================
-- RESULT SET 2 - OPEN TICKETS IN THE PLAN BY CURRENT DEPARTMENT   <- the move being approved
-- ===========================================================================================================================
SELECT d.Code AS OriginalDepartmentCode, d.Name AS OriginalDepartmentName, d.IsActive AS DepartmentIsActive,
       SUM(CASE WHEN c.PrevOwner IS NULL THEN 1 ELSE 0 END) AS NoOwnerNow,
       SUM(CASE WHEN c.PrevOwner IS NOT NULL THEN 1 ELSE 0 END) AS OwnedNow,
       COUNT(*) AS OpenTickets,
       CASE WHEN d.DepartmentId = @CcId THEN 'already in CC - (re)assign only' ELSE 'MOVE to CC + assign' END AS Action
FROM #Cand AS c
JOIN dbo.Departments AS d ON d.DepartmentId = c.FromDepartmentId
GROUP BY d.DepartmentId, d.Code, d.Name, d.IsActive
UNION ALL
SELECT N'** TOTAL **', NULL, NULL, SUM(CASE WHEN PrevOwner IS NULL THEN 1 ELSE 0 END), SUM(CASE WHEN PrevOwner IS NOT NULL THEN 1 ELSE 0 END), COUNT(*), NULL
FROM #Cand
ORDER BY OpenTickets DESC;

-- ===========================================================================================================================
-- RESULT SET 2b - HELD BACK (not moved, not assigned, not touched): pending approvals and active AI conversations (Open tickets only)
-- ===========================================================================================================================
SELECT d.Code AS DepartmentCode, d.Name AS DepartmentName,
       SUM(CASE WHEN b.PendingApproval = 1 THEN 1 ELSE 0 END) AS HeldBack_PendingApproval,
       SUM(CASE WHEN b.PendingApproval = 0 AND b.ActiveAi = 1 AND @IncludeLiveAiConversations = 0 THEN 1 ELSE 0 END) AS HeldBack_ActiveAiOnly,
       COUNT(*) AS HeldBackTotal
FROM #Base AS b JOIN dbo.Departments AS d ON d.DepartmentId = b.FromDepartmentId
WHERE b.PendingApproval = 1 OR (b.ActiveAi = 1 AND @IncludeLiveAiConversations = 0)
GROUP BY d.Code, d.Name
UNION ALL
SELECT N'** TOTAL **', NULL, @HeldApproval, @HeldAiOnly, @HeldApproval + @HeldAiOnly
ORDER BY HeldBackTotal DESC;

-- Each held-back ticket, with the reason (a ticket with BOTH a pending approval and an AI conversation is listed once, under the approval)
SELECT b.TicketId, b.TicketNumber,
       CASE b.TicketStatus WHEN 1 THEN 'Open' WHEN 2 THEN 'InProgress' WHEN 3 THEN 'PendingCustomer' WHEN 4 THEN 'PendingThirdParty' END AS [Status],
       d.Code AS CurrentDepartment,
       CASE WHEN b.PendingApproval = 1 THEN 'PENDING APPROVAL' ELSE 'ACTIVE AI CONVERSATION' END AS HeldBackBecause,
       CASE WHEN b.ActiveAi = 1 AND b.PendingApproval = 1 THEN 'also has an active AI conversation' ELSE '' END AS Also,
       (SELECT STRING_AGG(CASE ap.ApprovalType WHEN 1 THEN 'Accounting' WHEN 2 THEN 'CustomerService' ELSE 'Type' + CONVERT(varchar(5), ap.ApprovalType) END
                          + ' (since ' + CONVERT(varchar(16), ap.RequestedAtUtc, 120) + ')', '; ')
          FROM dbo.TicketApprovals ap WHERE ap.TicketId = b.TicketId AND ap.Status = 1) AS PendingApprovals,
       b.CreatedAtUtc
FROM #Base AS b JOIN dbo.Departments AS d ON d.DepartmentId = b.FromDepartmentId
WHERE b.PendingApproval = 1 OR (b.ActiveAi = 1 AND @IncludeLiveAiConversations = 0)
ORDER BY CASE WHEN b.PendingApproval = 1 THEN 0 ELSE 1 END, d.Code, b.CreatedAtUtc, b.TicketId;

-- ===========================================================================================================================
-- RESULT SET 3 - What is EXCLUDED / never touched (for the sign-off)
-- ===========================================================================================================================
SELECT
    (SELECT COUNT(*) FROM dbo.Tickets WHERE TicketStatus IN (5, 6))                                                              AS ResolvedOrClosed_Excluded,
    (SELECT COUNT(*) FROM dbo.Tickets WHERE TicketStatus IN (2, 3, 4))                                                           AS ActiveButNotOpen_Excluded_InProgressOrPending,
    (SELECT COUNT(*) FROM dbo.Tickets t WHERE t.TicketStatus = 1 AND t.CurrentOwnerEmployeeId IS NULL
        AND EXISTS (SELECT 1 FROM dbo.TicketAssignments a WHERE a.TicketId = t.TicketId AND a.IsCurrent = 1))                    AS OpenNoOwnerButHasCurrentAssignment_DataDrift_InPlan_Repaired,
    (SELECT COUNT(*) FROM dbo.Tickets t WHERE t.TicketStatus = 1 AND t.CurrentOwnerEmployeeId IS NOT NULL
        AND NOT EXISTS (SELECT 1 FROM dbo.TicketAssignments a WHERE a.TicketId = t.TicketId AND a.IsCurrent = 1))                AS OpenOwnerButNoAssignmentRow_Legacy_InPlan,
    (SELECT COUNT(*) FROM (SELECT TicketId FROM dbo.TicketAssignments WHERE IsCurrent = 1 GROUP BY TicketId HAVING COUNT(*) > 1) x) AS TicketsWithMoreThanOneCurrentAssignment_MustBe0;

-- Informational flags on the tickets in the plan (they WILL be moved - this is what the Call Center inherits):
SELECT
    (SELECT COUNT(*) FROM #Cand c JOIN dbo.Tickets t ON t.TicketId = c.TicketId WHERE t.RequestTypeId IS NOT NULL)                       AS WithRequestType_Preserved,
    (SELECT COUNT(*) FROM #Cand c JOIN dbo.Tickets t ON t.TicketId = c.TicketId WHERE t.CategoryId IS NULL)                                AS Unclassified,
    (SELECT COUNT(*) FROM #Cand c JOIN dbo.Tickets t ON t.TicketId = c.TicketId WHERE t.SlaState = 4)                                    AS SlaBreached_ClockUntouched,
    (SELECT COUNT(*) FROM #Cand c WHERE EXISTS (SELECT 1 FROM dbo.TicketApprovals ap WHERE ap.TicketId = c.TicketId AND ap.Status IN (2, 3, 4))) AS WithDecidedApprovalHistory_Kept,
    (SELECT COUNT(*) FROM #Cand c WHERE c.OriginatingDepartmentId <> c.FromDepartmentId)                                                    AS AlreadyTransferredBefore_OriginatingDeptKept;

-- ===========================================================================================================================
-- RESULT SET 3b - WHO LOSES TICKETS: current owners whose Open tickets are reassigned (they stop being the owner and lose Resolve / Close / Transfer on them)
-- ===========================================================================================================================
SELECT e.DisplayName AS PreviousOwner, u.UserName,
       ISNULL((SELECT STRING_AGG(r.Name, N', ') WITHIN GROUP (ORDER BY r.Name)
                 FROM dbo.AspNetUserRoles ur JOIN dbo.AspNetRoles r ON r.Id = ur.RoleId WHERE ur.UserId = e.EmployeeId), N'(no role)') AS Roles,
       COUNT(*) AS OpenTicketsLost,
       SUM(CASE WHEN c.IsMove = 1 THEN 1 ELSE 0 END) AS LeavingTheirDepartment,
       CASE WHEN EXISTS (SELECT 1 FROM #Agents a WHERE a.EmployeeId = e.EmployeeId) THEN 'is also a Call Center agent in the pool' ELSE '' END AS Note
FROM #Plan p JOIN #Cand c ON c.TicketId = p.TicketId
JOIN dbo.Employees e ON e.EmployeeId = c.PrevOwner JOIN dbo.AspNetUsers u ON u.Id = e.EmployeeId
WHERE p.Action = 'REASSIGN'
GROUP BY e.EmployeeId, e.DisplayName, u.UserName
ORDER BY COUNT(*) DESC, e.DisplayName;

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
-- RESULT SET 5 - PROJECTED DISTRIBUTION PER CALL CENTER AGENT  (this is the table to approve)
-- ===========================================================================================================================
SELECT a.Rn AS RotationPosition, a.DisplayName, a.UserName,
       COUNT(p.TicketId) AS OpenTicketsToOwn,
       SUM(CASE WHEN p.IsPinned = 0 THEN 1 ELSE 0 END) AS ByRoundRobin,
       SUM(CASE WHEN p.IsPinned = 1 THEN 1 ELSE 0 END) AS AsAnsweringAgent,
       SUM(CASE WHEN c.IsMove = 1 THEN 1 ELSE 0 END) AS FromOtherDepartments,
       SUM(CASE WHEN p.Action = 'KEEP' THEN 1 ELSE 0 END) AS AlreadyTheirs_Kept,
       SUM(CASE WHEN p.Action = 'REASSIGN' THEN 1 ELSE 0 END) AS TakenFromAnotherOwner,
       (SELECT COUNT(*) FROM dbo.Tickets t WHERE t.CurrentOwnerEmployeeId = a.EmployeeId AND t.TicketStatus = 1) AS OpenOwnedBefore,
       (SELECT COUNT(*) FROM dbo.Tickets t WHERE t.CurrentOwnerEmployeeId = a.EmployeeId AND t.TicketStatus = 1
           AND NOT EXISTS (SELECT 1 FROM #Plan q WHERE q.TicketId = t.TicketId)) + COUNT(p.TicketId) AS OpenOwnedAfter_Estimated,
       CASE WHEN a.Rn = @Start AND @Cand - @Pinned > 0 THEN '<- first to receive by Round Robin' ELSE '' END AS Note
FROM #Agents AS a
LEFT JOIN #Plan AS p ON p.AgentRn = a.Rn
LEFT JOIN #Cand AS c ON c.TicketId = p.TicketId
GROUP BY a.Rn, a.DisplayName, a.UserName, a.EmployeeId
ORDER BY a.Rn;

SELECT MIN(cnt) AS MinPerAgent, MAX(cnt) AS MaxPerAgent, MAX(cnt) - MIN(cnt) AS RoundRobinSpread_MustBeAtMost1,
       @Cand - @Pinned AS RoundRobinTickets, @Pinned AS AnsweringAgentTickets, @N AS Agents
FROM (SELECT a.Rn, COUNT(p.TicketId) AS cnt FROM #Agents a LEFT JOIN #Plan p ON p.AgentRn = a.Rn AND p.IsPinned = 0 GROUP BY a.Rn) x;

-- Original department x receiving agent (how each department's backlog is spread across the Call Center)
SELECT d.Code AS OriginalDepartmentCode, a.DisplayName AS ReceivingAgent, COUNT(*) AS Tickets
FROM #Plan p
JOIN #Cand c ON c.TicketId = p.TicketId
JOIN dbo.Departments d ON d.DepartmentId = c.FromDepartmentId
JOIN #Agents a ON a.Rn = p.AgentRn
GROUP BY d.Code, a.Rn, a.DisplayName
ORDER BY d.Code, a.Rn;

-- ===========================================================================================================================
-- RESULT SET 6 - APPROVAL VALUES: copy these SEVEN values into the execution script's commit gates
-- ===========================================================================================================================
SELECT @Cand AS ApprovedTicketCount, @Moved AS ApprovedTransferCount, @N AS ApprovedAgentCount,
       @HeldApproval AS ApprovedHeldBackPendingApprovalCount, @HeldAiOnly AS ApprovedHeldBackActiveAiCount,
       @Reassigned AS ApprovedReassignedCount, ISNULL(@PlanFp, 0) AS ApprovedPlanFingerprint;

-- ===========================================================================================================================
-- RESULT SET 7 - Exact plan: ticket -> current department / owner -> receiving agent (ticket numbers only; no customer data)
-- ===========================================================================================================================
SELECT c.Seq AS DealOrder, c.TicketId, c.TicketNumber,
       CASE c.TicketStatus WHEN 1 THEN 'Open' WHEN 2 THEN 'InProgress' WHEN 3 THEN 'PendingCustomer' WHEN 4 THEN 'PendingThirdParty' END AS [Status],
       c.CreatedAtUtc, od.Code AS FromDepartment, CASE WHEN c.IsMove = 1 THEN 'CC' ELSE 'CC (no move)' END AS ToDepartment,
       a.DisplayName AS WouldBeAssignedTo, a.UserName,
       p.Action AS PlanAction, CASE WHEN p.IsPinned = 1 THEN 'answering agent' ELSE 'round robin' END AS Basis,
       ISNULL(po.DisplayName, N'(none)') AS CurrentOwnerLosingIt
FROM #Cand AS c
JOIN #Plan AS p ON p.TicketId = c.TicketId
JOIN #Agents AS a ON a.Rn = p.AgentRn
JOIN dbo.Departments AS od ON od.DepartmentId = c.FromDepartmentId
LEFT JOIN dbo.Employees AS po ON po.EmployeeId = c.PrevOwner
ORDER BY c.Seq;

-- Where the cursor would end up after the run (Round Robin tickets only):
SELECT TOP (1) a.DisplayName AS CursorWouldMoveTo, a.EmployeeId
FROM #Plan p JOIN #Agents a ON a.Rn = p.AgentRn WHERE p.IsPinned = 0 ORDER BY p.Seq DESC;

DROP TABLE #Plan; DROP TABLE #Cand; DROP TABLE #Base; DROP TABLE #Agents;
