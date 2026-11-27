/*
================================================================================
 Diagnose_TigerCS_PROD_AiOwnedStuckTickets_ReadOnly.sql          (SELECT ONLY - NO WRITES)
 Database : TigerCS_Ticketing_Prod

 P0: Genesys tickets stay assigned to the AI Agent in the Call Center queue and employees cannot work them.

 WHAT IT SHOWS
   1. Pool        - how many employees the Call Center Round Robin can deal to RIGHT NOW (active, not locked out, CS Agent, Call Center member).
                    0 = nothing can be assigned until staff are fixed (the job then keeps the tickets queued and retries every run).
   2. Summary     - open tickets still owned by the AI Agent (AiAgentOwnerEmployeeId set, no human owner) by class.
   3. Stuck       - the tickets the fixed VoiceCallReconciliation job will give to a person: Open, unowned (AI Agent owner only), every Genesys interaction a
                    Phone call, call ended, the interaction flagged RequiresHumanFollowUp (Genesys asked for a person, or an earlier run concluded "nobody
                    answered"), no human has answered, nobody released it to the queue, no approval pending.
   4. NotTouched  - AI-owned open tickets the job will NOT touch, with the reason (live call, AI handled it with no human need, a person answered, ...).

 THE RECOVERY IS PERFORMED BY THE APPLICATION, NOT BY SQL. Assigning from SQL would bypass the persisted, row-locked Round Robin cursor, the one-current-
 assignment index, the assignment history, the audit trail and the "assigned to you" notification. After deployment the (now live) job picks section 3's tickets
 up automatically, at most MaxActionsPerRun (50) per minute. Run this script before and after: section 3 must drain to 0 and section 2's AI-owned count fall.

 READ-ONLY: SELECTs and session temp tables only; READ UNCOMMITTED; safe from a db_datareader login. No customer name / phone / e-mail is selected.
================================================================================
*/
SET NOCOUNT ON;
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

DECLARE @ExpectedDb sysname = N'TigerCS_Ticketing_Prod';
IF DB_NAME() <> @ExpectedDb
BEGIN
    DECLARE @m nvarchar(400) = N'ABORTED: connected to [' + ISNULL(DB_NAME(), N'?') + N'], this diagnostic is for [' + @ExpectedDb + N'] only.';
    THROW 50701, @m, 1;
END;

DECLARE @CcId int = (SELECT TOP (1) DepartmentId FROM dbo.Departments WHERE Code = N'CC' AND IsActive = 1);
DECLARE @CsAgentRole nvarchar(256) = N'CS Agent';
DECLARE @Now datetime2 = SYSUTCDATETIME();

-- Eligibility as far as the database can see it (the application also excludes service identities, which live in configuration).
CREATE TABLE #Elig (UserId uniqueidentifier NOT NULL PRIMARY KEY, IsActive bit NOT NULL, IsLockedOut bit NOT NULL, IsCsAgent bit NOT NULL, IsCcMember bit NOT NULL);
INSERT #Elig
SELECT u.Id,
       CASE WHEN e.EmployeeId IS NOT NULL AND e.DeactivatedAtUtc IS NULL THEN 1 ELSE 0 END,
       CASE WHEN u.LockoutEnd IS NOT NULL AND u.LockoutEnd > SYSDATETIMEOFFSET() THEN 1 ELSE 0 END,
       CASE WHEN EXISTS (SELECT 1 FROM dbo.AspNetUserRoles ur JOIN dbo.AspNetRoles r ON r.Id = ur.RoleId WHERE ur.UserId = u.Id AND r.Name = @CsAgentRole) THEN 1 ELSE 0 END,
       CASE WHEN @CcId IS NOT NULL AND EXISTS (SELECT 1 FROM dbo.UserDepartmentAssignments uda WHERE uda.EmployeeId = u.Id AND uda.DepartmentId = @CcId) THEN 1 ELSE 0 END
FROM dbo.AspNetUsers u LEFT JOIN dbo.Employees e ON e.EmployeeId = u.Id;

-- 1. Pool ----------------------------------------------------------------------------------------------------------------------------------------------------
SELECT @CcId AS CallCenterDepartmentId,
       (SELECT COUNT(*) FROM dbo.Departments WHERE Code = N'CC' AND IsActive = 1) AS ActiveCallCenterDepartments,        -- must be exactly 1
       (SELECT COUNT(*) FROM #Elig WHERE IsActive = 1 AND IsLockedOut = 0 AND IsCsAgent = 1 AND IsCcMember = 1) AS EligibleCsAgentsInPool,   -- 0 = nobody can be dealt tickets
       (SELECT COUNT(*) FROM #Elig WHERE IsCsAgent = 1 AND IsCcMember = 1 AND (IsActive = 0 OR IsLockedOut = 1)) AS CsAgentsExcluded_InactiveOrLockedOut,
       (SELECT COUNT(*) FROM #Elig WHERE IsCsAgent = 1 AND IsCcMember = 0) AS CsAgentsNotInCallCenter;

-- Candidate rows: every open ticket the AI Agent still owns (no human owner).
CREATE TABLE #P (
    TicketId bigint NOT NULL PRIMARY KEY, TicketNumber nvarchar(80) NOT NULL, CurrentDepartmentId int NOT NULL, CreatedAtUtc datetime2 NOT NULL,
    GenesysInteractions int NOT NULL, PhoneInteractions int NOT NULL, LiveInteractions int NOT NULL, EndedPhoneInteractions int NOT NULL,
    NeedsPerson bit NOT NULL, LastEndReceivedAtUtc datetime2 NULL, HumanAnswered bit NOT NULL, ReleasedByPerson bit NOT NULL, PendingApproval bit NOT NULL,
    Class varchar(48) NULL);

INSERT #P (TicketId, TicketNumber, CurrentDepartmentId, CreatedAtUtc, GenesysInteractions, PhoneInteractions, LiveInteractions, EndedPhoneInteractions,
           NeedsPerson, LastEndReceivedAtUtc, HumanAnswered, ReleasedByPerson, PendingApproval)
SELECT t.TicketId, t.TicketNumber, t.CurrentDepartmentId, t.CreatedAtUtc,
       (SELECT COUNT(*) FROM dbo.TicketInteractions i WHERE i.TicketId = t.TicketId AND i.Source = 2),
       (SELECT COUNT(*) FROM dbo.TicketInteractions i WHERE i.TicketId = t.TicketId AND i.Source = 2 AND i.ChannelId = 1),
       (SELECT COUNT(*) FROM dbo.TicketInteractions i WHERE i.TicketId = t.TicketId AND i.Source = 2 AND i.EndedAtUtc IS NULL AND i.InactivityClosedAtUtc IS NULL),
       (SELECT COUNT(*) FROM dbo.TicketInteractions i WHERE i.TicketId = t.TicketId AND i.Source = 2 AND i.ChannelId = 1 AND i.EndedAtUtc IS NOT NULL),
       CASE WHEN EXISTS (SELECT 1 FROM dbo.TicketInteractions i WHERE i.TicketId = t.TicketId AND i.RequiresHumanFollowUp = 1) THEN 1 ELSE 0 END,
       (SELECT MAX(COALESCE(i.EndReceivedAtUtc, i.EndedAtUtc)) FROM dbo.TicketInteractions i WHERE i.TicketId = t.TicketId AND i.Source = 2 AND i.ChannelId = 1),
       CASE WHEN t.FirstHumanResponseAtUtc IS NOT NULL
              OR EXISTS (SELECT 1 FROM dbo.TicketInteractionHandlers h WHERE h.TicketId = t.TicketId)
              OR EXISTS (SELECT 1 FROM dbo.TicketWorkflowEvents e WHERE e.TicketId = t.TicketId AND e.EventType = 20 AND e.Note LIKE N'%[[]GenesysUserId=%') THEN 1 ELSE 0 END,
       CASE WHEN EXISTS (SELECT 1 FROM dbo.AuditEntries a WHERE a.Action = N'Unassign' AND a.EntityType = N'Ticket' AND a.EntityId = CONVERT(nvarchar(40), t.TicketId) AND a.ActorEmployeeId IS NOT NULL) THEN 1 ELSE 0 END,
       CASE WHEN EXISTS (SELECT 1 FROM dbo.TicketApprovals ap WHERE ap.TicketId = t.TicketId AND ap.Status = 1) THEN 1 ELSE 0 END
FROM dbo.Tickets t
WHERE t.TicketStatus = 1                                   -- Open
  AND t.CurrentOwnerEmployeeId IS NULL                     -- no human owner ...
  AND t.AiAgentOwnerEmployeeId IS NOT NULL                 -- ... the AI Agent is the recorded owner
  AND (t.IntegrationSource IS NULL OR t.IntegrationSource <> N'QueueingSystem');

-- Same precedence as VoiceCallClassifier (phone-only tickets). Digital / mixed tickets are reported separately.
UPDATE #P SET Class =
    CASE WHEN GenesysInteractions = 0 OR PhoneInteractions < GenesysInteractions THEN 'NOT_PHONE_ONLY_(DIGITAL_OR_MIXED)'
         WHEN PendingApproval = 1 THEN 'PENDING_APPROVAL'
         WHEN ReleasedByPerson = 1 THEN 'RELEASED_BY_PERSON'
         WHEN HumanAnswered = 1 THEN 'HUMAN_ANSWERED_(MAPPING_OR_ASSIGNMENT_PENDING)'
         WHEN LiveInteractions > 0 THEN 'CALL_STILL_LIVE'
         WHEN NeedsPerson = 0 THEN 'AI_HANDLED_NO_HUMAN_NEED'
         WHEN LastEndReceivedAtUtc > DATEADD(MINUTE, -5, @Now) THEN 'WAITING_FOR_NO_ANSWER_WINDOW'
         ELSE 'STUCK_NEEDS_PERSON_WILL_BE_ASSIGNED' END;

-- 2. Summary -------------------------------------------------------------------------------------------------------------------------------------------------
SELECT Class, COUNT(*) AS Tickets, MIN(CreatedAtUtc) AS OldestCreatedAtUtc
FROM #P GROUP BY Class ORDER BY Tickets DESC;

-- 3. Stuck: the tickets the fixed job gives to an eligible Call Center agent -------------------------------------------------------------------------------------
SELECT TicketId, TicketNumber, CurrentDepartmentId, CreatedAtUtc, LastEndReceivedAtUtc,
       DATEDIFF(MINUTE, LastEndReceivedAtUtc, @Now) AS MinutesSinceCallEnded,
       CASE WHEN CurrentDepartmentId = @CcId THEN 'CallCenter' ELSE 'OtherDepartment(explicit workflow)' END AS Queue
FROM #P WHERE Class = 'STUCK_NEEDS_PERSON_WILL_BE_ASSIGNED'
ORDER BY CreatedAtUtc;

-- 4. Not touched, with the reason --------------------------------------------------------------------------------------------------------------------------------
SELECT TicketId, TicketNumber, CurrentDepartmentId, CreatedAtUtc, Class
FROM #P WHERE Class <> 'STUCK_NEEDS_PERSON_WILL_BE_ASSIGNED'
ORDER BY Class, CreatedAtUtc;

-- 5. After the recovery: tickets that were stuck and were dealt (the assignment is by the system: no actor) ---------------------------------------------------------------
SELECT TOP (200) a.TicketId, t.TicketNumber, a.AssignedEmployeeId, a.AssignedAtUtc, t.TicketStatus, t.AiAgentOwnerEmployeeId
FROM dbo.TicketAssignments a JOIN dbo.Tickets t ON t.TicketId = a.TicketId
WHERE a.AssigningActorEmployeeId IS NULL AND a.AssignedAtUtc >= DATEADD(DAY, -2, @Now)
  AND EXISTS (SELECT 1 FROM dbo.AuditEntries x WHERE x.Action = N'GenesysVoiceNoAnswerConcluded' AND x.EntityId = CONVERT(nvarchar(40), a.TicketId))
ORDER BY a.AssignedAtUtc DESC;

DROP TABLE #P;
DROP TABLE #Elig;
