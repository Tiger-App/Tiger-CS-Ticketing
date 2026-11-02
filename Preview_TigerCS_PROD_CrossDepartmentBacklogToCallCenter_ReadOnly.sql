/*
================================================================================
 Preview_TigerCS_PROD_CrossDepartmentBacklogToCallCenter_ReadOnly.sql
 Database : TigerCS_Ticketing_Prod          *** READ ONLY - SELECT + session temp tables only. NOTHING IS WRITTEN. ***

 Sizes requirement 3 of the "Call Center first" fix: existing Open / InProgress / PendingCustomer tickets that are UNASSIGNED in a
 department other than the Call Center (CC) and would be moved to CC, then dealt to CC agents by the approved Round Robin backlog process
 (Execute_TigerCS_PROD_CallCenterBacklogDistribution.sql, which today only deals tickets that are ALREADY in CC - see Runbook §2 decision 1).

 Classification of every unowned, active, non-CC ticket (exactly one bucket each):
   MOVE_TO_CC        no owner, no current assignment, and not an active AI-only conversation  -> would be moved, then distributed
   HOLD_AI_ACTIVE    Genesys-sourced ticket whose AI conversation never asked for a person (no RequiresHumanFollowUp, no hand-off row)
                     -> left exactly where it is (requirement 5); it reaches CC if/when a person is requested
 Never in scope (not even listed as candidates): tickets with an owner or an active assignment (human ownership is never overwritten),
   Resolved / Closed tickets, tickets already in CC.

 What a later move must preserve (and what this preview lets you verify beforehand): OriginatingDepartmentId, RequestTypeId, TicketStatus,
 SLA instances, status history, audit history (append-only: one 'Transfer' + one 'AutoAssign' per ticket), single current owner.
 Send the full result sets back; they become the approval document. No count in this file is a Production number until you run it.
================================================================================
*/
SET NOCOUNT ON;
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

DECLARE @ExpectedDb sysname = N'TigerCS_Ticketing_Prod';
IF DB_NAME() <> @ExpectedDb
BEGIN
    DECLARE @msg nvarchar(2000) = N'ABORTED: connected to [' + ISNULL(DB_NAME(), N'?') + N']; this script only runs against [' + @ExpectedDb + N'].';
    THROW 50400, @msg, 1;
END;

DECLARE @CcId int = (SELECT DepartmentId FROM dbo.Departments WHERE Code = N'CC' AND IsActive = 1);
IF @CcId IS NULL OR (SELECT COUNT(*) FROM dbo.Departments WHERE Code = N'CC' AND IsActive = 1) <> 1
    THROW 50401, 'ABORTED: expected exactly one ACTIVE department with Code = CC.', 1;

CREATE TABLE #Scope (
    TicketId bigint NOT NULL PRIMARY KEY, TicketNumber nvarchar(40) NULL, TicketStatus tinyint NOT NULL,
    CurrentDepartmentId int NOT NULL, OriginatingDepartmentId int NULL, RequestTypeId int NULL, CreatedAtUtc datetime2 NOT NULL,
    IsGenesys bit NOT NULL, HumanRequested bit NOT NULL, Bucket varchar(16) NOT NULL);

INSERT #Scope (TicketId, TicketNumber, TicketStatus, CurrentDepartmentId, OriginatingDepartmentId, RequestTypeId, CreatedAtUtc, IsGenesys, HumanRequested, Bucket)
SELECT t.TicketId, t.TicketNumber, t.TicketStatus, t.CurrentDepartmentId, t.OriginatingDepartmentId, t.RequestTypeId, t.CreatedAtUtc,
       g.IsGenesys, h.HumanRequested,
       CASE WHEN g.IsGenesys = 1 AND h.HumanRequested = 0 THEN 'HOLD_AI_ACTIVE' ELSE 'MOVE_TO_CC' END
FROM dbo.Tickets AS t
CROSS APPLY (SELECT CAST(CASE WHEN EXISTS (SELECT 1 FROM dbo.TicketInteractions i
                                           WHERE i.TicketId = t.TicketId AND i.GenesysConversationId IS NOT NULL) THEN 1 ELSE 0 END AS bit) AS IsGenesys) g
CROSS APPLY (SELECT CAST(CASE WHEN EXISTS (SELECT 1 FROM dbo.TicketInteractions i WHERE i.TicketId = t.TicketId AND i.RequiresHumanFollowUp = 1)
                                OR EXISTS (SELECT 1 FROM dbo.TicketAgentHandoffs ah WHERE ah.TicketId = t.TicketId)
                              THEN 1 ELSE 0 END AS bit) AS HumanRequested) h
WHERE t.CurrentDepartmentId <> @CcId
  AND t.TicketStatus IN (1, 2, 3, 4)                                   -- Open, InProgress, PendingCustomer, PendingThirdParty (legacy)
  AND t.CurrentOwnerEmployeeId IS NULL                                 -- no owner
  AND NOT EXISTS (SELECT 1 FROM dbo.TicketAssignments a WHERE a.TicketId = t.TicketId AND a.IsCurrent = 1);

-- 1. Headline
SELECT (SELECT COUNT(*) FROM #Scope WHERE Bucket = 'MOVE_TO_CC')       AS WouldMoveToCallCenter,
       (SELECT COUNT(*) FROM #Scope WHERE Bucket = 'HOLD_AI_ACTIVE')   AS HeldActiveAiConversations,
       (SELECT COUNT(*) FROM dbo.Tickets WHERE CurrentDepartmentId <> @CcId AND TicketStatus IN (1, 2, 3, 4) AND CurrentOwnerEmployeeId IS NOT NULL)
                                                                       AS HumanOwnedNonCc_NeverTouched,
       (SELECT COUNT(*) FROM dbo.Tickets WHERE CurrentDepartmentId = @CcId AND TicketStatus IN (1, 2, 3, 4) AND CurrentOwnerEmployeeId IS NULL
          AND NOT EXISTS (SELECT 1 FROM dbo.TicketAssignments a WHERE a.TicketId = dbo.Tickets.TicketId AND a.IsCurrent = 1))
                                                                       AS AlreadyInCcUnassigned_HandledByExistingScript;

-- 2. By current department and status (the reconciliation table to approve)
SELECT d.Code AS CurrentDepartment, d.Name,
       SUM(CASE WHEN s.Bucket = 'MOVE_TO_CC' THEN 1 ELSE 0 END)     AS MoveToCc,
       SUM(CASE WHEN s.Bucket = 'MOVE_TO_CC' AND s.TicketStatus = 1 THEN 1 ELSE 0 END)        AS Move_Open,
       SUM(CASE WHEN s.Bucket = 'MOVE_TO_CC' AND s.TicketStatus = 2 THEN 1 ELSE 0 END)        AS Move_InProgress,
       SUM(CASE WHEN s.Bucket = 'MOVE_TO_CC' AND s.TicketStatus IN (3, 4) THEN 1 ELSE 0 END)  AS Move_Pending,
       SUM(CASE WHEN s.Bucket = 'HOLD_AI_ACTIVE' THEN 1 ELSE 0 END) AS HeldAi
FROM #Scope s JOIN dbo.Departments d ON d.DepartmentId = s.CurrentDepartmentId
GROUP BY d.Code, d.Name ORDER BY MoveToCc DESC;

-- 3. Integrity facts a move must not break (all expected to be 0 rows / 0)
SELECT COUNT(*) AS MoveCandidates_WithNullOriginatingDepartment
FROM #Scope WHERE Bucket = 'MOVE_TO_CC' AND OriginatingDepartmentId IS NULL;
SELECT TOP (50) 'ActiveTicketWithoutOwnerButWithCurrentAssignment' AS Anomaly, t.TicketId, t.TicketNumber
FROM dbo.Tickets t
WHERE t.TicketStatus IN (1, 2, 3, 4) AND t.CurrentOwnerEmployeeId IS NULL
  AND EXISTS (SELECT 1 FROM dbo.TicketAssignments a WHERE a.TicketId = t.TicketId AND a.IsCurrent = 1);

-- 4. The tickets themselves (oldest first = the order a Round Robin deal would use). Includes the incident ticket if still unassigned.
SELECT TOP (500) s.Bucket, s.TicketNumber, s.TicketStatus, d.Code AS CurrentDepartment, od.Code AS OriginatingDepartment, s.RequestTypeId,
       s.IsGenesys, s.HumanRequested, s.CreatedAtUtc
FROM #Scope s
JOIN dbo.Departments d ON d.DepartmentId = s.CurrentDepartmentId
LEFT JOIN dbo.Departments od ON od.DepartmentId = s.OriginatingDepartmentId
ORDER BY s.Bucket, s.CreatedAtUtc, s.TicketId;

DROP TABLE #Scope;
