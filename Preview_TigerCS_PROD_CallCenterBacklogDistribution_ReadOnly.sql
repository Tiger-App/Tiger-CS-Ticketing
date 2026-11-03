/*
================================================================================
 Preview_TigerCS_PROD_CallCenterBacklogDistribution_ReadOnly.sql      (DRY RUN - NO WRITES)
 Database : TigerCS_Ticketing_Prod

 READ-ONLY. No INSERT/UPDATE/DELETE/DDL against the database; only session temp tables (tempdb) and SELECTs. Run from a read-only login.
 READ UNCOMMITTED, so it never blocks the application. Safe to re-run any time.

 BUSINESS RULE (explicitly approved, ONE-TIME): EVERY existing ticket whose Status = OPEN, from EVERY department and whether it is currently
 UNOWNED or OWNED by another employee, is moved into the Call Center (CC) and given to exactly ONE eligible Call Center agent by Round Robin.
 The previous owner is REPLACED (not deleted): the old assignment row is kept as superseded history and the previous owner is written to the audit
 trail and the workflow event. Future tickets are unaffected. (This replaces the narrower scope of PR #143/#144: unowned only, Open/InProgress/Pending.)

 A ticket is ELIGIBLE when ALL hold:
   * TicketStatus = 1 (Open).  InProgress, PendingCustomer, PendingThirdParty-legacy, Resolved and Closed are NEVER touched (the schema has no 'Cancelled').
   * ANY owner situation: no owner / owned by anyone (active, deactivated, locked, service account, other department) / owner without an assignment
     row (legacy) / no owner but a current assignment row (data drift).  Exactly one current assignment row exists afterwards.
   * NO pending approval  (TicketApprovals.Status = 1)                                      -> HELD BACK until its approval process completes, listed separately
   * NO active AI conversation: a Genesys interaction that is not ended, not inactivity-closed, needs no human follow-up and was not taken by a person,
     on a ticket with no interaction flagged RequiresHumanFollowUp and no open human handoff (AgentHandoff Waiting/Assigned/InProgress)  -> HELD BACK, listed separately
   * NO recorded human handler: the person who actually received / accepted / answered the interaction is on record (dbo.TicketInteractionHandlers row,
     TicketInteractions.HandledByUserId, or a HumanAgent transcript line). BUSINESS RULE: that person owns the ticket, so Round Robin never re-deals it -> HELD BACK,
     listed separately with the recorded handler ids (no switch lifts this). Precedence: pending approval, then recorded human handler, then active AI.
   * any CurrentDepartmentId (active OR inactive department)
   * (only when @ReassignTicketsAlreadyOwnedByEligibleCcAgent = 0) not already in CC and owned by an agent of the pool - default 1 follows the
     approved text literally ("regardless of ... including tickets currently assigned to another employee"): such a ticket is dealt again.
 HELD-BACK tickets are NEVER touched: not moved, not assigned, no audit row, no event.
 What the run would change per eligible ticket:  CurrentDepartmentId -> CC (if not already CC) and CurrentOwnerEmployeeId -> the dealt agent, the previous
 current assignment row -> IsCurrent = 0 (kept), + one new current assignment row.
 NEVER changed: OriginatingDepartmentId, RequestTypeId, CategoryId, PriorityId, TicketStatus, SlaState, EscalationLevel, history, notes, approvals, interactions, notifications.

 ROUND ROBIN (reproduces TicketAutoAssignmentService.AssignRoundRobinAsync / FilterEligibleAsync / DepartmentAssignmentRotation.PickNext):
   * pool       = members of the active CC department who are: not deactivated, not locked out, not a service identity, and hold at least one of
                  CS Agent / Department Employee / Department Head (so read-only roles, supervisors, managers and admins are never dealt tickets);
                  with @CsAgentRoleOnly = 1 (default) additionally the CS Agent role - the role that can Transfer as owner;
                  with @ExcludeAgentsWithTicketDenials = 1 (default) not an employee with an explicit DENY of Tickets.View / Tickets.Resolve / Tickets.Close
                  (the assignee could not work or close the ticket; a denial is never overridden). The live engine does not look at denials.
   * order      = ascending .NET Guid order == ordinal order of the canonical GUID text (sorted on CONVERT(char(36), id) with a binary collation;
                  SQL Server's own uniqueidentifier ordering is different and is deliberately not used)
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

IF COL_LENGTH(N'dbo.TicketInteractions', N'RequiresHumanFollowUp') IS NULL OR OBJECT_ID(N'dbo.TicketAgentHandoffs', N'U') IS NULL OR OBJECT_ID(N'dbo.TicketApprovals', N'U') IS NULL
   OR OBJECT_ID(N'dbo.TicketInteractionHandlers', N'U') IS NULL OR OBJECT_ID(N'dbo.TicketInteractionMessages', N'U') IS NULL
    THROW 50201, N'ABORTED: dbo.TicketInteractions.RequiresHumanFollowUp / dbo.TicketAgentHandoffs / dbo.TicketApprovals not found - the hold-back rules cannot be evaluated; wrong database or a missing migration.', 1;

-- Service accounts are configured in appsettings, not in SQL. Paste every id from BOTH settings (one INSERT per id):
--   Authorization:ServiceIdentity:EmployeeIds      and      Collections:Authorization:IntegrationEmployeeIds
-- Edits the variable table only; nothing persists.
DECLARE @ServiceIdentity TABLE (EmployeeId uniqueidentifier PRIMARY KEY);
-- INSERT INTO @ServiceIdentity VALUES ('00000000-0000-0000-0000-000000000000');
DECLARE @ServiceIdsConfirmed bit = 0;      -- set 1 once the list above is complete (only needed when ids are listed)
DECLARE @NoServiceAccountsExist bit = 0;   -- set 1 ONLY if you have confirmed there are no service/integration accounts at all (empty list)
DECLARE @IncludeLiveAiConversations bit = 0;   -- MUST equal the Execute script's value. 0 = tickets with an ACTIVE AI conversation are held back (listed below)
DECLARE @CsAgentRoleOnly bit = 1;              -- MUST equal the Execute script's value. 1 = only holders of the CS Agent role receive tickets (the role that can Transfer as owner)
DECLARE @ReassignTicketsAlreadyOwnedByEligibleCcAgent bit = 1;  -- MUST equal the Execute script's value. 1 = literal rule (deal EVERY Open ticket again). 0 = leave a CC ticket that an eligible CC agent already owns
DECLARE @StaleAiAfterHours int = NULL;                           -- MUST equal the Execute script's value. NULL = off (default). e.g. 24 = an "active AI" conversation silent for more than 24 h with no human evidence is treated as ended
DECLARE @TreatHumanTouchedAiAsHistorical bit = 0;                -- MUST equal the Execute script's value. 0 = off (default). 1 = an "AI" ticket where a person demonstrably took part is human work, not held back
DECLARE @ExcludeAgentsWithTicketDenials bit = 1;                -- MUST equal the Execute script's value. 1 = an agent with an explicit DENY of Tickets.View / Resolve / Close is not dealt tickets

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
-- explicit permission DENIALS (dbo.UserPermissionOverrides, Effect 2) of the three permissions an owner needs to see / resolve / close the ticket.
-- The table is created by AddUserPermissionOverrides (PR #139/#142); where it does not exist yet nobody has a denial.
CREATE TABLE #Deny (EmployeeId uniqueidentifier NOT NULL, PermissionKey nvarchar(100) NOT NULL);
IF OBJECT_ID(N'dbo.UserPermissionOverrides', N'U') IS NOT NULL
    EXEC sp_executesql N'INSERT #Deny (EmployeeId, PermissionKey) SELECT EmployeeId, PermissionKey FROM dbo.UserPermissionOverrides
                         WHERE Effect = 2 AND PermissionKey IN (N''Tickets.View'', N''Tickets.Resolve'', N''Tickets.Close'')';

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
  AND (@ExcludeAgentsWithTicketDenials = 0 OR NOT EXISTS (SELECT 1 FROM #Deny d WHERE d.EmployeeId = e.EmployeeId))   -- explicit denial of View / Resolve / Close
  AND EXISTS (SELECT 1 FROM dbo.AspNetUserRoles ur JOIN dbo.AspNetRoles r ON r.Id = ur.RoleId     -- read-only / non-operational excluded
              WHERE ur.UserId = e.EmployeeId AND r.Name IN (N'CS Agent', N'Department Employee', N'Department Head'))
  AND (@CsAgentRoleOnly = 0 OR EXISTS (SELECT 1 FROM dbo.AspNetUserRoles ur JOIN dbo.AspNetRoles r ON r.Id = ur.RoleId
                                       WHERE ur.UserId = e.EmployeeId AND r.Name = N'CS Agent'));

DECLARE @N int = (SELECT COUNT(*) FROM #Agents);

-- ---------------------------------------------------------------------------------------------------------------------------
-- 2. Eligible tickets (ALL departments, Open only, owned or not)
-- ---------------------------------------------------------------------------------------------------------------------------
-- ---------------------------------------------------------------------------------------------------------------------------
-- Proposed correction for STALE / HUMAN-TOUCHED "active AI" conversations (see Diagnose_TigerCS_PROD_ActiveAiConversations_ReadOnly.sql).
-- Computed in memory; NO TicketInteractions row is modified. Both switches are OFF by default, so the protection is unchanged unless approved.
--   @StaleAiAfterHours             a live-looking AI conversation silent for MORE than this many hours, with no human evidence, is treated as an ended historical one
--   @TreatHumanTouchedAiAsHistorical  a person took part (HumanAgent line, interaction handler, first human response, handoff row, agent event) but the row still says AI: treated as human work
-- Last activity = latest interaction stamp / transcript message / handoff-or-Genesys workflow event / ticket creation (identical to the diagnostic).
-- ---------------------------------------------------------------------------------------------------------------------------
CREATE TABLE #StaleAi (TicketId bigint NOT NULL PRIMARY KEY, Reason varchar(20) NOT NULL, LastActivityUtc datetime2 NOT NULL);
IF @StaleAiAfterHours IS NOT NULL OR @TreatHumanTouchedAiAsHistorical = 1
    INSERT #StaleAi (TicketId, Reason, LastActivityUtc)
    SELECT x.TicketId, CASE WHEN x.Human = 1 THEN 'HUMAN_TOUCHED' ELSE 'STALE' END, x.LastActivityUtc
    FROM (SELECT t.TicketId,
                 CASE WHEN EXISTS (SELECT 1 FROM dbo.TicketInteractions i JOIN dbo.TicketInteractionMessages mm ON mm.TicketInteractionId = i.TicketInteractionId WHERE i.TicketId = t.TicketId AND mm.Sender = 2)
                        OR EXISTS (SELECT 1 FROM dbo.TicketInteractionHandlers h WHERE h.TicketId = t.TicketId)
                        OR t.FirstHumanResponseAtUtc IS NOT NULL
                        OR EXISTS (SELECT 1 FROM dbo.TicketAgentHandoffs h WHERE h.TicketId = t.TicketId)
                        OR EXISTS (SELECT 1 FROM dbo.TicketWorkflowEvents e WHERE e.TicketId = t.TicketId AND e.EventType IN (11, 12, 18, 19)) THEN 1 ELSE 0 END AS Human,
                 (SELECT MAX(v) FROM (VALUES
                     ((SELECT MAX(z.v) FROM dbo.TicketInteractions i CROSS APPLY (VALUES (i.CreatedAtUtc), (i.InteractionStartedAtUtc), (i.LastCustomerInteractionAtUtc), (i.AwaitingCustomerReplySinceUtc)) z(v) WHERE i.TicketId = t.TicketId)),
                     ((SELECT MAX(mm.SentAtUtc) FROM dbo.TicketInteractions i JOIN dbo.TicketInteractionMessages mm ON mm.TicketInteractionId = i.TicketInteractionId WHERE i.TicketId = t.TicketId)),
                     ((SELECT MAX(e.OccurredAtUtc) FROM dbo.TicketWorkflowEvents e WHERE e.TicketId = t.TicketId AND e.EventType BETWEEN 10 AND 19)),
                     (t.CreatedAtUtc)) a(v)) AS LastActivityUtc
          FROM dbo.Tickets t
          WHERE t.TicketStatus = 1
            AND (EXISTS (SELECT 1 FROM dbo.TicketInteractions i WHERE i.TicketId = t.TicketId AND i.Source = 2 AND i.EndedAtUtc IS NULL
                         AND i.InactivityClosedAtUtc IS NULL AND i.RequiresHumanFollowUp = 0 AND i.HandledByUserId IS NULL)
                 AND NOT EXISTS (SELECT 1 FROM dbo.TicketInteractions i2 WHERE i2.TicketId = t.TicketId AND i2.RequiresHumanFollowUp = 1)
                 AND NOT EXISTS (SELECT 1 FROM dbo.TicketAgentHandoffs h WHERE h.TicketId = t.TicketId AND h.Status IN (2, 3, 4)))) x
    WHERE (x.Human = 1 AND @TreatHumanTouchedAiAsHistorical = 1)
       OR (x.Human = 0 AND @StaleAiAfterHours IS NOT NULL AND x.LastActivityUtc < DATEADD(hour, -@StaleAiAfterHours, SYSUTCDATETIME()));

-- every Open ticket with its current owner / current assignment row and the two hold-back flags
CREATE TABLE #Base (TicketId bigint NOT NULL PRIMARY KEY, TicketNumber nvarchar(80) NULL, CreatedAtUtc datetime2 NOT NULL,
    FromDepartmentId int NOT NULL, OriginatingDepartmentId int NOT NULL, PrevOwnerId uniqueidentifier NULL, PrevAssignmentId bigint NULL,
    PendingApproval bit NOT NULL, ActiveAi bit NOT NULL, KeptInPlace bit NOT NULL, HumanHandled bit NOT NULL);

INSERT #Base (TicketId, TicketNumber, CreatedAtUtc, FromDepartmentId, OriginatingDepartmentId, PrevOwnerId, PrevAssignmentId, PendingApproval, ActiveAi, KeptInPlace, HumanHandled)
SELECT t.TicketId, t.TicketNumber, t.CreatedAtUtc, t.CurrentDepartmentId, t.OriginatingDepartmentId, t.CurrentOwnerEmployeeId,
       (SELECT TOP (1) a.TicketAssignmentId FROM dbo.TicketAssignments a WHERE a.TicketId = t.TicketId AND a.IsCurrent = 1),
       CASE WHEN EXISTS (SELECT 1 FROM dbo.TicketApprovals ap WHERE ap.TicketId = t.TicketId AND ap.Status = 1) THEN 1 ELSE 0 END,
       CASE WHEN (EXISTS (SELECT 1 FROM dbo.TicketInteractions i WHERE i.TicketId = t.TicketId AND i.Source = 2 AND i.EndedAtUtc IS NULL
                     AND i.InactivityClosedAtUtc IS NULL AND i.RequiresHumanFollowUp = 0 AND i.HandledByUserId IS NULL)
       AND NOT EXISTS (SELECT 1 FROM dbo.TicketInteractions i2 WHERE i2.TicketId = t.TicketId AND i2.RequiresHumanFollowUp = 1)
       AND NOT EXISTS (SELECT 1 FROM dbo.TicketAgentHandoffs h WHERE h.TicketId = t.TicketId AND h.Status IN (2, 3, 4)) AND NOT EXISTS (SELECT 1 FROM #StaleAi sa WHERE sa.TicketId = t.TicketId)) THEN 1 ELSE 0 END,
       CASE WHEN @ReassignTicketsAlreadyOwnedByEligibleCcAgent = 0 AND t.CurrentDepartmentId = @CcId
                 AND EXISTS (SELECT 1 FROM #Agents ag WHERE ag.EmployeeId = t.CurrentOwnerEmployeeId) THEN 1 ELSE 0 END,
       -- RECORDED HUMAN HANDLER: the person who actually received / accepted / answered the interaction is on record
       -- (TicketInteractionHandlers row, TicketInteractions.HandledByUserId, or a HumanAgent transcript line). That person owns the ticket:
       -- Round Robin must never hand it to somebody else, so it is held back.
       CASE WHEN (EXISTS (SELECT 1 FROM dbo.TicketInteractionHandlers hh WHERE hh.TicketId = t.TicketId)
            OR EXISTS (SELECT 1 FROM dbo.TicketInteractions hi WHERE hi.TicketId = t.TicketId AND hi.HandledByUserId IS NOT NULL)
            OR EXISTS (SELECT 1 FROM dbo.TicketInteractions hj JOIN dbo.TicketInteractionMessages hm ON hm.TicketInteractionId = hj.TicketInteractionId
                       WHERE hj.TicketId = t.TicketId AND hm.Sender = 2)) THEN 1 ELSE 0 END
FROM dbo.Tickets AS t
WHERE @CcId IS NOT NULL
  AND t.TicketStatus = 1;                                              -- OPEN only

CREATE TABLE #Cand (Seq int NOT NULL PRIMARY KEY, TicketId bigint NOT NULL, TicketNumber nvarchar(80) NULL,
    CreatedAtUtc datetime2 NOT NULL, FromDepartmentId int NOT NULL, OriginatingDepartmentId int NOT NULL, IsMove bit NOT NULL,
    PrevOwnerId uniqueidentifier NULL, PrevAssignmentId bigint NULL);

INSERT #Cand (Seq, TicketId, TicketNumber, CreatedAtUtc, FromDepartmentId, OriginatingDepartmentId, IsMove, PrevOwnerId, PrevAssignmentId)
SELECT ROW_NUMBER() OVER (ORDER BY b.CreatedAtUtc, b.TicketId), b.TicketId, b.TicketNumber, b.CreatedAtUtc,
       b.FromDepartmentId, b.OriginatingDepartmentId, CASE WHEN b.FromDepartmentId <> @CcId THEN 1 ELSE 0 END, b.PrevOwnerId, b.PrevAssignmentId
FROM #Base AS b
WHERE b.PendingApproval = 0 AND b.HumanHandled = 0 AND (b.ActiveAi = 0 OR @IncludeLiveAiConversations = 1) AND b.KeptInPlace = 0;   -- held-back tickets never enter the plan

DECLARE @Open int = (SELECT COUNT(*) FROM #Base);
DECLARE @OpenOwned int = (SELECT COUNT(*) FROM #Base WHERE PrevOwnerId IS NOT NULL);
DECLARE @Cand int = (SELECT COUNT(*) FROM #Cand);
DECLARE @Moved int = (SELECT COUNT(*) FROM #Cand WHERE IsMove = 1);
DECLARE @Reassigned int = (SELECT COUNT(*) FROM #Cand WHERE PrevOwnerId IS NOT NULL);                  -- eligible tickets that currently HAVE an owner (the owner is replaced)
DECLARE @HeldApproval int = (SELECT COUNT(*) FROM #Base WHERE PendingApproval = 1);                    -- pending approval (whether or not an AI conversation is also active)
DECLARE @HeldHuman    int = (SELECT COUNT(*) FROM #Base WHERE PendingApproval = 0 AND HumanHandled = 1);          -- a recorded human handler, no pending approval
DECLARE @HeldAiOnly   int = (SELECT COUNT(*) FROM #Base WHERE PendingApproval = 0 AND HumanHandled = 0 AND ActiveAi = 1 AND @IncludeLiveAiConversations = 0);  -- active AI conversation, no approval, no recorded human  -- active AI conversation, no pending approval
DECLARE @StaleAiReclassified int = (SELECT COUNT(*) FROM #StaleAi sa JOIN #Base b ON b.TicketId = sa.TicketId WHERE sa.Reason = 'STALE');
DECLARE @HumanAiReclassified int = (SELECT COUNT(*) FROM #StaleAi sa JOIN #Base b ON b.TicketId = sa.TicketId WHERE sa.Reason = 'HUMAN_TOUCHED');
DECLARE @KeptInPlace  int = (SELECT COUNT(*) FROM #Base WHERE PendingApproval = 0 AND HumanHandled = 0 AND (ActiveAi = 0 OR @IncludeLiveAiConversations = 1) AND KeptInPlace = 1);

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
       @Open AS TotalOpenTickets, @OpenOwned AS OpenCurrentlyOwned, @Open - @OpenOwned AS OpenCurrentlyUnowned,
       @Cand AS EligibleTickets_Total, @Moved AS TicketsMovedIntoCC, @Cand - @Moved AS TicketsAlreadyInCC,
       @Reassigned AS EligibleCurrentlyOwned_OwnerReplaced, @Cand - @Reassigned AS EligibleCurrentlyUnowned,
       @HeldApproval AS HeldBack_PendingApproval, @HeldAiOnly AS HeldBack_ActiveAiConversation, @HeldHuman AS HeldBack_RecordedHumanHandler, @KeptInPlace AS KeptInPlace_OwnedByEligibleCcAgent,
       @Cursor AS RotationCursor_LastAssignedEmployeeId,
       CASE
           WHEN @CcCount <> 1                                    THEN 'BLOCKER: expected exactly one active department with Code=CC, found ' + CONVERT(varchar(10), @CcCount)
           WHEN @HasRotationTable = 0                            THEN 'BLOCKER: dbo.DepartmentAssignmentRotations is missing - apply Deploy_TigerCS_Prod_Migrations.sql first'
           WHEN @AssignmentAllowed = 0                           THEN 'BLOCKER: Call Center workflow settings disable assignment (the engine would leave tickets in the queue)'
           WHEN @N = 0                                           THEN 'BLOCKER: no eligible Call Center agent'
           WHEN EXISTS (SELECT 1 FROM @ServiceIdentity) AND @NoServiceAccountsExist = 1
                                                                 THEN 'BLOCKER: contradictory service-account gates - ids are listed but @NoServiceAccountsExist = 1'
           WHEN EXISTS (SELECT 1 FROM @ServiceIdentity) AND @ServiceIdsConfirmed = 0
                                                                 THEN 'BLOCKER: service-account exclusions not confirmed (@ServiceIdsConfirmed = 0) - a service account could be listed as an agent below'
           WHEN NOT EXISTS (SELECT 1 FROM @ServiceIdentity) AND @NoServiceAccountsExist = 0
                                                                 THEN 'BLOCKER: service-account list is empty - list the ids, or set @NoServiceAccountsExist = 1 if there are none'
           WHEN EXISTS (SELECT 1 FROM @ServiceIdentity s WHERE NOT EXISTS (SELECT 1 FROM dbo.Employees e WHERE e.EmployeeId = s.EmployeeId))
                                                                 THEN 'BLOCKER: a listed service-account id is not an employee (typo?) - see ServiceIdsNotFound below'
           WHEN @N = 1                                           THEN 'WARNING: only one eligible agent - every ticket goes to that person'
           WHEN @Cand = 0                                        THEN 'NOTHING TO DO: no eligible tickets'
           ELSE 'READY' END AS Verdict,
       @StaleAiAfterHours AS StaleAiAfterHours, @TreatHumanTouchedAiAsHistorical AS TreatHumanTouchedAiAsHistorical,
       @StaleAiReclassified AS StaleAiTreatedAsEnded, @HumanAiReclassified AS HumanTouchedAiTreatedAsHuman;

-- Tickets whose "active AI" protection was lifted by the proposed correction (empty while both switches are off) - review before approving
SELECT b.TicketNumber, d.Code AS CurrentDepartment, sa.Reason AS ReclassifiedAs, sa.LastActivityUtc,
       CAST(DATEDIFF(minute, sa.LastActivityUtc, SYSUTCDATETIME()) / 60.0 AS decimal(12, 1)) AS SilentHours,
       CASE WHEN b.PendingApproval = 1 THEN 'still held back: PENDING APPROVAL' WHEN b.KeptInPlace = 1 THEN 'kept in place (owned by a pool agent)' ELSE 'now eligible' END AS Outcome
FROM #StaleAi sa JOIN #Base b ON b.TicketId = sa.TicketId JOIN dbo.Departments d ON d.DepartmentId = b.FromDepartmentId
ORDER BY sa.Reason, sa.LastActivityUtC, b.TicketNumber;

SELECT s.EmployeeId AS ServiceIdsNotFound FROM @ServiceIdentity s WHERE NOT EXISTS (SELECT 1 FROM dbo.Employees e WHERE e.EmployeeId = s.EmployeeId);

-- ===========================================================================================================================
-- RESULT SET 2 - ELIGIBLE TICKETS BY ORIGINAL (CURRENT) DEPARTMENT: owned vs unowned   <- the move being approved
-- ===========================================================================================================================
SELECT d.Code AS OriginalDepartmentCode, d.Name AS OriginalDepartmentName, d.IsActive AS DepartmentIsActive,
       SUM(CASE WHEN c.PrevOwnerId IS NULL THEN 1 ELSE 0 END) AS CurrentlyUnowned,
       SUM(CASE WHEN c.PrevOwnerId IS NOT NULL THEN 1 ELSE 0 END) AS CurrentlyOwned_OwnerReplaced,
       COUNT(*) AS Eligible,
       CASE WHEN d.DepartmentId = @CcId THEN 'already in CC - (re)assign only' ELSE 'MOVE to CC + assign' END AS Action
FROM #Cand AS c
JOIN dbo.Departments AS d ON d.DepartmentId = c.FromDepartmentId
GROUP BY d.DepartmentId, d.Code, d.Name, d.IsActive
UNION ALL
SELECT N'** TOTAL **', NULL, NULL, SUM(CASE WHEN PrevOwnerId IS NULL THEN 1 ELSE 0 END), SUM(CASE WHEN PrevOwnerId IS NOT NULL THEN 1 ELSE 0 END), COUNT(*), NULL
FROM #Cand
ORDER BY Eligible DESC;

-- ===========================================================================================================================
-- RESULT SET 2b - HELD BACK (not moved, not assigned, not touched): pending approvals and active AI conversations
-- ===========================================================================================================================
SELECT d.Code AS DepartmentCode, d.Name AS DepartmentName,
       SUM(CASE WHEN b.PendingApproval = 1 THEN 1 ELSE 0 END) AS HeldBack_PendingApproval,
       SUM(CASE WHEN b.PendingApproval = 0 AND b.HumanHandled = 1 THEN 1 ELSE 0 END) AS HeldBack_RecordedHumanHandler,
       SUM(CASE WHEN b.PendingApproval = 0 AND b.HumanHandled = 0 AND b.ActiveAi = 1 AND @IncludeLiveAiConversations = 0 THEN 1 ELSE 0 END) AS HeldBack_ActiveAiOnly,
       COUNT(*) AS HeldBackTotal,
       SUM(CASE WHEN b.PrevOwnerId IS NOT NULL THEN 1 ELSE 0 END) AS OfWhich_CurrentlyOwned
FROM #Base AS b JOIN dbo.Departments AS d ON d.DepartmentId = b.FromDepartmentId
WHERE b.PendingApproval = 1 OR b.HumanHandled = 1 OR (b.ActiveAi = 1 AND @IncludeLiveAiConversations = 0)
GROUP BY d.Code, d.Name
UNION ALL
SELECT N'** TOTAL **', NULL, @HeldApproval, @HeldHuman, @HeldAiOnly, @HeldApproval + @HeldHuman + @HeldAiOnly,
       (SELECT COUNT(*) FROM #Base WHERE (PendingApproval = 1 OR HumanHandled = 1 OR (ActiveAi = 1 AND @IncludeLiveAiConversations = 0)) AND PrevOwnerId IS NOT NULL)
ORDER BY HeldBackTotal DESC;

-- Each held-back ticket, with the reason (a ticket with BOTH a pending approval and an AI conversation is listed once, under the approval)
SELECT b.TicketId, b.TicketNumber, d.Code AS CurrentDepartment,
       CASE WHEN b.PendingApproval = 1 THEN 'PENDING APPROVAL' WHEN b.HumanHandled = 1 THEN 'RECORDED HUMAN HANDLER' ELSE 'ACTIVE AI CONVERSATION' END AS HeldBackBecause,
       CASE WHEN b.ActiveAi = 1 AND b.PendingApproval = 1 THEN 'also has an active AI conversation' WHEN b.PendingApproval = 1 AND b.HumanHandled = 1 THEN 'also has a recorded human handler' ELSE '' END AS Also,
       (SELECT STRING_AGG(CONVERT(nvarchar(36), x.UserId), N', ') FROM (SELECT h.HandledByUserId AS UserId FROM dbo.TicketInteractionHandlers h WHERE h.TicketId = b.TicketId
            UNION SELECT i.HandledByUserId FROM dbo.TicketInteractions i WHERE i.TicketId = b.TicketId AND i.HandledByUserId IS NOT NULL) x) AS RecordedHandlerEmployeeIds,
       (SELECT COUNT(*) FROM dbo.TicketInteractions i JOIN dbo.TicketInteractionMessages m ON m.TicketInteractionId = i.TicketInteractionId WHERE i.TicketId = b.TicketId AND m.Sender = 2) AS HumanTranscriptLines,
       ISNULL(o.DisplayName, CASE WHEN b.PrevOwnerId IS NULL THEN N'(unowned)' ELSE N'(unknown employee)' END) AS CurrentOwner_StaysUnchanged,
       (SELECT STRING_AGG(CASE ap.ApprovalType WHEN 1 THEN 'Accounting' WHEN 2 THEN 'CustomerService' ELSE 'Type' + CONVERT(varchar(5), ap.ApprovalType) END
                          + ' (since ' + CONVERT(varchar(16), ap.RequestedAtUtc, 120) + ')', '; ')
          FROM dbo.TicketApprovals ap WHERE ap.TicketId = b.TicketId AND ap.Status = 1) AS PendingApprovals,
       b.CreatedAtUtc
FROM #Base AS b JOIN dbo.Departments AS d ON d.DepartmentId = b.FromDepartmentId
LEFT JOIN dbo.Employees o ON o.EmployeeId = b.PrevOwnerId
WHERE b.PendingApproval = 1 OR b.HumanHandled = 1 OR (b.ActiveAi = 1 AND @IncludeLiveAiConversations = 0)
ORDER BY CASE WHEN b.PendingApproval = 1 THEN 0 WHEN b.HumanHandled = 1 THEN 1 ELSE 2 END, d.Code, b.CreatedAtUtc, b.TicketId;

-- ===========================================================================================================================
-- RESULT SET 3 - What is EXCLUDED / never touched (for the sign-off)
-- ===========================================================================================================================
SELECT CASE t.TicketStatus WHEN 2 THEN 'InProgress' WHEN 3 THEN 'PendingCustomer' WHEN 4 THEN 'PendingThirdParty' WHEN 5 THEN 'Resolved' WHEN 6 THEN 'Closed'
                           ELSE 'Status ' + CONVERT(varchar(5), t.TicketStatus) END AS NonOpenStatus_NeverTouched,
       COUNT(*) AS Tickets, SUM(CASE WHEN t.CurrentOwnerEmployeeId IS NOT NULL THEN 1 ELSE 0 END) AS Owned
FROM dbo.Tickets t WHERE t.TicketStatus <> 1 GROUP BY t.TicketStatus ORDER BY t.TicketStatus;

SELECT
    (SELECT COUNT(*) FROM #Base WHERE PrevOwnerId IS NULL AND PrevAssignmentId IS NOT NULL)                                      AS OpenNoOwnerButCurrentAssignment_DataDrift_Included_RowSuperseded,
    (SELECT COUNT(*) FROM #Cand WHERE PrevOwnerId IS NOT NULL AND PrevAssignmentId IS NULL)                                      AS OpenOwnerButNoAssignmentRow_Legacy_Included,
    (SELECT COUNT(*) FROM #Cand c JOIN dbo.TicketAssignments a ON a.TicketAssignmentId = c.PrevAssignmentId WHERE a.AssignedEmployeeId <> ISNULL(c.PrevOwnerId, a.AssignedEmployeeId)) AS OwnerDiffersFromCurrentAssignmentRow_Included,
    (SELECT COUNT(*) FROM (SELECT TicketId FROM dbo.TicketAssignments WHERE IsCurrent = 1 GROUP BY TicketId HAVING COUNT(*) > 1) x) AS TicketsWithMoreThanOneCurrentAssignment_MustBe0;

-- Informational flags on the ELIGIBLE tickets (they will still be moved - this is what the Call Center inherits):
SELECT
    (SELECT COUNT(*) FROM #Cand c JOIN dbo.Tickets t ON t.TicketId = c.TicketId WHERE t.RequestTypeId IS NOT NULL)                       AS WithRequestType_Preserved,
    (SELECT COUNT(*) FROM #Cand c JOIN dbo.Tickets t ON t.TicketId = c.TicketId WHERE t.CategoryId IS NULL)                                AS Unclassified,
    (SELECT COUNT(*) FROM #Cand c JOIN dbo.Tickets t ON t.TicketId = c.TicketId WHERE t.SlaState = 4)                                    AS SlaBreached_ClockUntouched,
    (SELECT COUNT(*) FROM #Cand c JOIN dbo.Tickets t ON t.TicketId = c.TicketId WHERE t.FirstHumanResponseAtUtc IS NOT NULL)             AS AlreadyAnsweredByAPerson_StillOpen,
    (SELECT COUNT(*) FROM #Cand c WHERE EXISTS (SELECT 1 FROM dbo.TicketApprovals ap WHERE ap.TicketId = c.TicketId AND ap.Status IN (2, 3, 4))) AS WithDecidedApprovalHistory_Kept,
    (SELECT COUNT(*) FROM #Cand c WHERE c.OriginatingDepartmentId <> c.FromDepartmentId)                                                    AS AlreadyTransferredBefore_OriginatingDeptKept;

-- Departments whose workflow settings would NOT allow this move/reassignment as a normal user action. The approved one-time run does it anyway
-- (like an administrator); listed so the owners of those departments are told.
SELECT d.Code AS DepartmentCode, s.AllowTransferToOtherDepartments, s.AllowInternalReassignment, COUNT(*) AS EligibleTicketsThere
FROM #Cand c JOIN dbo.Departments d ON d.DepartmentId = c.FromDepartmentId
JOIN dbo.DepartmentWorkflowSettings s ON s.DepartmentId = c.FromDepartmentId
WHERE (s.AllowTransferToOtherDepartments = 0 AND c.IsMove = 1) OR (s.AllowInternalReassignment = 0 AND c.IsMove = 0 AND c.PrevOwnerId IS NOT NULL)
GROUP BY d.Code, s.AllowTransferToOtherDepartments, s.AllowInternalReassignment;

-- ===========================================================================================================================
-- RESULT SET 3b - WHO LOSES TICKETS: the current owners of the eligible tickets (the ownership being replaced)
-- ===========================================================================================================================
SELECT ISNULL(o.DisplayName, N'(employee row missing)') AS CurrentOwner, ou.UserName,
       CASE WHEN ag.EmployeeId IS NOT NULL THEN 'CC agent in the pool (will be dealt again)'
            WHEN o.EmployeeId IS NULL THEN 'no employee row'
            WHEN o.DeactivatedAtUtc IS NOT NULL THEN 'deactivated employee'
            WHEN ou.LockoutEnabled = 1 AND ou.LockoutEnd IS NOT NULL AND ou.LockoutEnd > SYSDATETIMEOFFSET() THEN 'locked out'
            WHEN EXISTS (SELECT 1 FROM @ServiceIdentity s WHERE s.EmployeeId = o.EmployeeId) THEN 'SERVICE ACCOUNT'
            WHEN EXISTS (SELECT 1 FROM dbo.UserDepartmentAssignments a WHERE a.EmployeeId = o.EmployeeId AND a.DepartmentId = @CcId) THEN 'CC member, not in the pool'
            ELSE 'employee of another department' END AS OwnerKind,
       (SELECT STRING_AGG(r.Name, N', ') WITHIN GROUP (ORDER BY r.Name) FROM dbo.AspNetUserRoles ur JOIN dbo.AspNetRoles r ON r.Id = ur.RoleId WHERE ur.UserId = c.PrevOwnerId) AS Roles,
       COUNT(*) AS OpenTicketsTakenAway,
       SUM(CASE WHEN c.IsMove = 1 THEN 1 ELSE 0 END) AS OfWhich_InOtherDepartments
FROM #Cand c
LEFT JOIN dbo.Employees o ON o.EmployeeId = c.PrevOwnerId
LEFT JOIN dbo.AspNetUsers ou ON ou.Id = c.PrevOwnerId
LEFT JOIN #Agents ag ON ag.EmployeeId = c.PrevOwnerId
WHERE c.PrevOwnerId IS NOT NULL
GROUP BY c.PrevOwnerId, o.DisplayName, ou.UserName, o.EmployeeId, o.DeactivatedAtUtc, ou.LockoutEnabled, ou.LockoutEnd, ag.EmployeeId
ORDER BY COUNT(*) DESC, CurrentOwner;

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
            WHEN @ExcludeAgentsWithTicketDenials = 1 AND EXISTS (SELECT 1 FROM #Deny d WHERE d.EmployeeId = e.EmployeeId)
                                      THEN 'excluded: explicit permission DENIAL (see DeniedPermissions) - could not view / resolve / close the ticket'
            WHEN @CsAgentRoleOnly = 1 AND EXISTS (SELECT 1 FROM dbo.AspNetUserRoles ur JOIN dbo.AspNetRoles r ON r.Id = ur.RoleId
                                                  WHERE ur.UserId = e.EmployeeId AND r.Name IN (N'Department Employee', N'Department Head'))
                                      THEN 'excluded by @CsAgentRoleOnly: engine-eligible but no CS Agent role (cannot Transfer as owner)'
            ELSE 'excluded: no operational role (read-only / supervisory / admin)' END AS RoundRobin,
       ISNULL((SELECT STRING_AGG(d.PermissionKey, N', ') FROM #Deny d WHERE d.EmployeeId = e.EmployeeId), N'') AS DeniedPermissions,
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
-- RESULT SET 5 - EXPECTED DISTRIBUTION PER CALL CENTER AGENT  (this is the table to approve)
-- ===========================================================================================================================
SELECT a.Rn AS RotationPosition, a.DisplayName, a.UserName,
       COUNT(p.TicketId) AS TicketsToReceive,
       SUM(CASE WHEN c.IsMove = 1 THEN 1 ELSE 0 END) AS FromOtherDepartments,
       SUM(CASE WHEN c.IsMove = 0 THEN 1 ELSE 0 END) AS AlreadyInCC,
       SUM(CASE WHEN c.PrevOwnerId IS NOT NULL THEN 1 ELSE 0 END) AS TakenFromAnotherOwner,
       SUM(CASE WHEN c.PrevOwnerId IS NULL THEN 1 ELSE 0 END) AS FromTheQueue,
       (SELECT COUNT(*) FROM dbo.Tickets t WHERE t.CurrentOwnerEmployeeId = a.EmployeeId AND t.TicketStatus IN (1, 2, 3, 4)) AS ActiveOwnedBefore,
       (SELECT COUNT(*) FROM dbo.Tickets t WHERE t.CurrentOwnerEmployeeId = a.EmployeeId AND t.TicketStatus IN (1, 2, 3, 4))
         - (SELECT COUNT(*) FROM #Cand c2 WHERE c2.PrevOwnerId = a.EmployeeId) + COUNT(p.TicketId) AS ActiveOwnedAfter,   -- minus the agent's own tickets that are dealt again, plus everything received
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
-- RESULT SET 6 - APPROVAL VALUES: copy these SEVEN numbers into the execution script's commit gates
-- ===========================================================================================================================
SELECT @Cand AS ApprovedTicketCount, @Moved AS ApprovedTransferCount, @Reassigned AS ApprovedReassignedCount, @N AS ApprovedAgentCount,
       @HeldApproval AS ApprovedHeldBackPendingApprovalCount, @HeldAiOnly AS ApprovedHeldBackActiveAiCount, @HeldHuman AS ApprovedHeldBackHumanHandledCount;

-- ===========================================================================================================================
-- RESULT SET 7 - Exact plan: ticket -> original department / previous owner -> receiving agent (ticket numbers only; no customer data)
-- ===========================================================================================================================
SELECT c.Seq AS DealOrder, c.TicketId, c.TicketNumber, c.CreatedAtUtc, od.Code AS FromDepartment,
       CASE WHEN c.IsMove = 1 THEN 'CC' ELSE 'CC (no move)' END AS ToDepartment,
       ISNULL(po.DisplayName, N'(unowned)') AS PreviousOwner,
       a.DisplayName AS WouldBeAssignedTo, a.UserName
FROM #Cand AS c
JOIN #Plan AS p ON p.TicketId = c.TicketId
JOIN #Agents AS a ON a.Rn = p.AgentRn
JOIN dbo.Departments AS od ON od.DepartmentId = c.FromDepartmentId
LEFT JOIN dbo.Employees AS po ON po.EmployeeId = c.PrevOwnerId
ORDER BY c.Seq;

-- Where the cursor would end up after the run:
SELECT TOP (1) a.DisplayName AS CursorWouldMoveTo, a.EmployeeId
FROM #Plan p JOIN #Agents a ON a.Rn = p.AgentRn ORDER BY p.Seq DESC;

DROP TABLE #Plan; DROP TABLE #Cand; DROP TABLE #Base; DROP TABLE #Agents; DROP TABLE #Deny; DROP TABLE #StaleAi;
