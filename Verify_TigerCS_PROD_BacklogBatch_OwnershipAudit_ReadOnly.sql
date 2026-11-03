/*
================================================================================
 Verify_TigerCS_PROD_BacklogBatch_OwnershipAudit_ReadOnly.sql
 Database : TigerCS_Ticketing_Prod                                   *** READ-ONLY - SELECT ONLY ***

 PURPOSE  Audit the tickets of the ALREADY EXECUTED one-time Call Center backlog distribution (expected: 84 tickets) against the BUSINESS RULE:
            "the actual human agent who accepted or handled the customer interaction must own the ticket".
          For every ticket of the batch it compares the CURRENT OWNER with three kinds of recorded evidence of who actually handled it:
            (1) Genesys answering agents   - TicketInteractions.HandledByUserId / GenesysAgentUserId (resolved by the app) and TicketInteractions.GenesysAgentId
                                             (the Genesys user id recorded from routing; resolved here through AspNetUsers.GenesysUserId)
            (2) human transcript senders   - TicketInteractionMessages with Sender = 2 (HumanAgent): SenderId (a Genesys user id) / SenderName
            (3) interaction handlers       - dbo.TicketInteractionHandlers rows (the per-agent handling history ticket visibility rests on)
          Evidence STRENGTH:  STRONG = (3), HandledByUserId and HumanAgent transcript lines (a person demonstrably handled it);
                              WEAK   = TicketInteractions.GenesysAgentId alone - on digital channels a top-level agent id can be an OFFERED agent, so it is
                                       shown and compared, but never counted as proof on its own.

 IT CHANGES NOTHING. No INSERT / UPDATE / DELETE / DDL against the database (only session temp tables in tempdb, so a db_datareader login works).
 It never corrects, reassigns or "fixes" a ticket. Human work, internal notes, transfers, SLA and audit history are only READ; the output lists them per ticket
 (NotesSinceBatch, PersonTransferSinceBatch, ...) so a person can decide what, if anything, to do. Customer data is never selected (ticket numbers and
 employee names only).

 HOW TO RUN  Connect with a READ-ONLY login to the Production database (or a restored copy), run the whole file in ONE batch. Leave @BatchId NULL to use the one
             batch the database knows (the script refuses to guess when there are several). Review the result sets in order:
   RS1  Summary           batch id / time, tickets found vs @ExpectedBatchTicketCount (84), and the count per classification
   RS2  MISMATCHES        owner is NOT among the people recorded as having handled the ticket (STRONG evidence) - the rule is not met
   RS3  WEAK MISMATCHES   only the recorded Genesys routing agent differs from the owner (may be an offered agent) - review, not proof
   RS4  MISSING EVIDENCE  (a) evidence exists but names ids that map to NO Ticketing user (unresolved), (b) no evidence at all - the rule cannot be checked
   RS5  MATCHES           owner is one of the recorded handlers (compact)
   RS6  Evidence detail   every evidence row behind RS2-RS5 (source, strength, employee, genesys id, when)
   RS7  Eligibility       whether each recorded handler could receive a Call Center ticket today (active, not locked, CC member, CS Agent role)
 CLASSIFICATION (one per ticket, in this order):
   MISMATCH                 STRONG evidence names at least one employee and the owner is none of them
   MATCH                    the owner is one of the employees named by the evidence (STRONG, or WEAK when there is no STRONG evidence)
   WEAK_MISMATCH            there is no STRONG evidence; the WEAK evidence names an employee who is not the owner
   MISSING_EVIDENCE_UNRESOLVED   evidence exists but resolves to no Ticketing user (e.g. an unmapped Genesys id)
   MISSING_EVIDENCE_NONE         nothing recorded at all (e.g. the ticket never had a Genesys interaction)
 A MISMATCH or WEAK_MISMATCH ticket whose HumanWorkSinceBatch = 'YES' (owner changed, a note, a person's transfer, a first response or a status change AFTER the
 batch) must NOT be changed automatically: a person already acted on it.
================================================================================
*/
SET NOCOUNT ON;
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

-- ------------------------------------------------ PARAMETERS ----------------------------------------------------------------
DECLARE @BatchId                 uniqueidentifier = NULL;   -- NULL = the single BacklogDistributionBatch known to the database
DECLARE @ExpectedBatchTicketCount int             = 84;     -- the number reported when the batch was executed; a different count is REPORTED, never "fixed"
DECLARE @ExpectedDb              sysname          = N'TigerCS_Ticketing_Prod';
-- ----------------------------------------------------------------------------------------------------------------------------

IF DB_NAME() <> @ExpectedDb
    PRINT N'WARNING: connected to [' + ISNULL(DB_NAME(), N'?') + N'], not [' + @ExpectedDb + N']. The script is read-only, so it still runs; check this is the database you mean.';

-- the batch ---------------------------------------------------------------------------------------------------------------------
DECLARE @BatchRows int = (SELECT COUNT(*) FROM dbo.AuditEntries WHERE Action = N'BacklogDistributionBatch');
IF @BatchId IS NULL
BEGIN
    IF @BatchRows = 0
        THROW 50500, 'No BacklogDistributionBatch audit row exists in this database: the distribution was never executed here (or this is the wrong database).', 1;
    IF @BatchRows > 1
        THROW 50501, 'More than one BacklogDistributionBatch audit row exists: set @BatchId explicitly (the audit rows are listed by: SELECT * FROM dbo.AuditEntries WHERE Action = N''BacklogDistributionBatch'').', 1;
    SELECT @BatchId = CorrelationId FROM dbo.AuditEntries WHERE Action = N'BacklogDistributionBatch';
END;

DECLARE @BatchAt datetime2, @BatchRowAfter nvarchar(max), @CcId int = (SELECT DepartmentId FROM dbo.Departments WHERE Code = N'CC');
SELECT @BatchAt = OccurredAtUtc, @BatchRowAfter = AfterValue FROM dbo.AuditEntries WHERE Action = N'BacklogDistributionBatch' AND CorrelationId = @BatchId;
IF @BatchAt IS NULL THROW 50502, 'The given @BatchId has no BacklogDistributionBatch audit row.', 1;

-- the batch's tickets: one 'AutoAssign' audit row per ticket carrying the batch id (written by the Execute script) -----------------
SELECT TicketId = TRY_CONVERT(bigint, a.EntityId),
       BatchAgentId = TRY_CONVERT(uniqueidentifier, SUBSTRING(a.AfterValue, CHARINDEX(N'AssignedEmployeeId=', a.AfterValue) + 19, 36)),
       PreviousOwnerId = TRY_CONVERT(uniqueidentifier, NULLIF(SUBSTRING(a.BeforeValue, CHARINDEX(N'PreviousOwnerId=', a.BeforeValue) + 16, 36), N'none')),
       Reassigned = CASE WHEN a.AfterValue LIKE N'%Reassigned=1%' THEN 1 ELSE 0 END
INTO #Batch
FROM dbo.AuditEntries a
WHERE a.Action = N'AutoAssign' AND a.EntityType = N'Ticket' AND a.CorrelationId = @BatchId AND a.AfterValue LIKE N'%Trigger=BacklogDistribution%';

DECLARE @Found int = (SELECT COUNT(*) FROM #Batch);

-- evidence ---------------------------------------------------------------------------------------------------------------------
CREATE TABLE #Ev (TicketId bigint NOT NULL, Source varchar(40) NOT NULL, Strength varchar(6) NOT NULL, EmployeeId uniqueidentifier NULL,
                  GenesysId nvarchar(64) NULL, Detail nvarchar(200) NULL, AtUtc datetime2 NULL);

-- (3) interaction handlers
INSERT #Ev SELECT h.TicketId, 'InteractionHandlerRow', 'STRONG', h.HandledByUserId, h.GenesysAgentUserId, N'TicketInteractionHandlers', h.RecordedAtUtc
FROM dbo.TicketInteractionHandlers h JOIN #Batch b ON b.TicketId = h.TicketId;

-- (1) Genesys answering agent resolved by the application
INSERT #Ev SELECT i.TicketId, 'InteractionHandledBy', 'STRONG', i.HandledByUserId, i.GenesysAgentUserId, N'TicketInteractions.HandledByUserId', i.CreatedAtUtc
FROM dbo.TicketInteractions i JOIN #Batch b ON b.TicketId = i.TicketId WHERE i.HandledByUserId IS NOT NULL;

-- (1) the recorded Genesys routing agent id, resolved through the Genesys user mapping (WEAK: may be an offered agent)
INSERT #Ev SELECT i.TicketId, 'GenesysRoutingAgentId', 'WEAK', u.Id, i.GenesysAgentId,
                  N'TicketInteractions.GenesysAgentId' + ISNULL(N' (' + i.GenesysAgentName + N')', N''), i.CreatedAtUtc
FROM dbo.TicketInteractions i JOIN #Batch b ON b.TicketId = i.TicketId
LEFT JOIN dbo.AspNetUsers u ON u.GenesysUserId = i.GenesysAgentId
WHERE NULLIF(LTRIM(RTRIM(i.GenesysAgentId)), N'') IS NOT NULL;

-- (2) human transcript senders (HumanAgent lines), resolved through the Genesys user mapping; a line with no sender id is still evidence that a person wrote
INSERT #Ev SELECT i.TicketId, 'HumanTranscriptSender', 'STRONG', u.Id, m.SenderId,
                  N'HumanAgent line' + ISNULL(N' by ' + m.SenderName, N'') + N' (first of its sender)', MIN(m.SentAtUtc)
FROM dbo.TicketInteractionMessages m JOIN dbo.TicketInteractions i ON i.TicketInteractionId = m.TicketInteractionId JOIN #Batch b ON b.TicketId = i.TicketId
LEFT JOIN dbo.AspNetUsers u ON u.GenesysUserId = NULLIF(LTRIM(RTRIM(m.SenderId)), N'')
WHERE m.Sender = 2
GROUP BY i.TicketId, u.Id, m.SenderId, m.SenderName;

-- per ticket facts ---------------------------------------------------------------------------------------------------------------
SELECT b.TicketId, t.TicketNumber, t.TicketStatus, t.CurrentDepartmentId, t.CurrentOwnerEmployeeId AS CurrentOwnerId, b.BatchAgentId, b.PreviousOwnerId, b.Reassigned,
       StrongEmployees = (SELECT COUNT(DISTINCT e.EmployeeId) FROM #Ev e WHERE e.TicketId = b.TicketId AND e.Strength = 'STRONG' AND e.EmployeeId IS NOT NULL),
       WeakEmployees   = (SELECT COUNT(DISTINCT e.EmployeeId) FROM #Ev e WHERE e.TicketId = b.TicketId AND e.Strength = 'WEAK'   AND e.EmployeeId IS NOT NULL),
       OwnerInStrong   = CASE WHEN EXISTS (SELECT 1 FROM #Ev e WHERE e.TicketId = b.TicketId AND e.Strength = 'STRONG' AND e.EmployeeId = t.CurrentOwnerEmployeeId) THEN 1 ELSE 0 END,
       OwnerInWeak     = CASE WHEN EXISTS (SELECT 1 FROM #Ev e WHERE e.TicketId = b.TicketId AND e.Strength = 'WEAK'   AND e.EmployeeId = t.CurrentOwnerEmployeeId) THEN 1 ELSE 0 END,
       AnyEvidence     = (SELECT COUNT(*) FROM #Ev e WHERE e.TicketId = b.TicketId),
       -- what happened AFTER the batch (read-only indicators; a person acted if any is non-zero)
       OwnerChanged    = CASE WHEN t.CurrentOwnerEmployeeId IS NULL OR t.CurrentOwnerEmployeeId <> b.BatchAgentId THEN 1 ELSE 0 END,
       NotesSince      = (SELECT COUNT(*) FROM dbo.TicketNotes n WHERE n.TicketId = b.TicketId AND n.CreatedAtUtc > @BatchAt),
       PersonTransferSince = (SELECT COUNT(*) FROM dbo.TicketWorkflowEvents ev WHERE ev.TicketId = b.TicketId AND ev.EventType = 17 AND ev.OccurredAtUtc > @BatchAt AND ev.CorrelationId <> @BatchId AND ISNULL(ev.TransferReasonCode, 0) <> 5),
       AssignmentsSince = (SELECT COUNT(*) FROM dbo.TicketAssignments a WHERE a.TicketId = b.TicketId AND a.AssignedAtUtc > @BatchAt),
       StatusHistorySince = (SELECT COUNT(*) FROM dbo.TicketStatusHistory h WHERE h.TicketId = b.TicketId AND h.OccurredAtUtc > @BatchAt),
       FirstResponseSince = CASE WHEN t.FirstHumanResponseAtUtc > @BatchAt THEN 1 ELSE 0 END,
       NotesTotal      = (SELECT COUNT(*) FROM dbo.TicketNotes n WHERE n.TicketId = b.TicketId)
INTO #Facts
FROM #Batch b LEFT JOIN dbo.Tickets t ON t.TicketId = b.TicketId;

SELECT f.*,
       Classification = CASE
           WHEN f.StrongEmployees > 0 AND f.OwnerInStrong = 0                                   THEN 'MISMATCH'
           WHEN (f.StrongEmployees > 0 AND f.OwnerInStrong = 1) OR (f.StrongEmployees = 0 AND f.OwnerInWeak = 1) THEN 'MATCH'
           WHEN f.StrongEmployees = 0 AND f.WeakEmployees > 0                                   THEN 'WEAK_MISMATCH'
           WHEN f.AnyEvidence > 0                                                               THEN 'MISSING_EVIDENCE_UNRESOLVED'
           ELSE                                                                                      'MISSING_EVIDENCE_NONE' END,
       HumanWorkSinceBatch = CASE WHEN f.OwnerChanged = 1 OR f.NotesSince > 0 OR f.PersonTransferSince > 0 OR f.AssignmentsSince > 0 OR f.StatusHistorySince > 0 OR f.FirstResponseSince = 1
                                  THEN 'YES' ELSE 'no' END
INTO #Result
FROM #Facts f;

-- ===========================================================================================================================
-- RS1 - SUMMARY
-- ===========================================================================================================================
SELECT @@SERVERNAME AS [Server], DB_NAME() AS [Database], @BatchId AS BatchId, @BatchAt AS BatchAtUtc,
       @ExpectedBatchTicketCount AS ExpectedTickets, @Found AS TicketsFoundInBatch,
       CASE WHEN @Found = @ExpectedBatchTicketCount THEN 'count matches' ELSE 'COUNT DIFFERS from the expected number - investigate before relying on this audit' END AS CountCheck,
       (SELECT COUNT(*) FROM #Batch WHERE TicketId IS NULL OR BatchAgentId IS NULL) AS BatchRowsNotParsed,
       @BatchRowAfter AS BatchAuditRow_AfterValue;

SELECT Classification, COUNT(*) AS Tickets,
       SUM(CASE WHEN HumanWorkSinceBatch = 'YES' THEN 1 ELSE 0 END) AS OfWhich_HumanWorkSinceBatch_DoNotAutoChange,
       SUM(CASE WHEN CurrentOwnerId IS NULL THEN 1 ELSE 0 END) AS OfWhich_NowUnowned,
       SUM(CASE WHEN TicketStatus <> 1 THEN 1 ELSE 0 END) AS OfWhich_NoLongerOpen
FROM #Result GROUP BY Classification
UNION ALL SELECT '** TOTAL **', COUNT(*), SUM(CASE WHEN HumanWorkSinceBatch = 'YES' THEN 1 ELSE 0 END), SUM(CASE WHEN CurrentOwnerId IS NULL THEN 1 ELSE 0 END), SUM(CASE WHEN TicketStatus <> 1 THEN 1 ELSE 0 END) FROM #Result
ORDER BY Tickets DESC;

-- ===========================================================================================================================
-- RS2 - MISMATCHES: STRONG evidence names someone and the current owner is not among them
-- RS3 - WEAK MISMATCHES: only the recorded Genesys routing agent differs
-- ===========================================================================================================================
SELECT r.TicketNumber, r.Classification, CASE r.TicketStatus WHEN 1 THEN 'Open' WHEN 2 THEN 'InProgress' WHEN 3 THEN 'PendingCustomer' WHEN 4 THEN 'PendingThirdParty' WHEN 5 THEN 'Resolved' WHEN 6 THEN 'Closed' ELSE CONVERT(varchar(5), r.TicketStatus) END AS Status,
       d.Code AS CurrentDepartment, ISNULL(o.DisplayName, N'(unowned)') AS CurrentOwner, ISNULL(ba.DisplayName, N'(unknown)') AS BatchAssignedAgent,
       ISNULL(po.DisplayName, CASE WHEN r.PreviousOwnerId IS NULL THEN N'(was unowned)' ELSE N'(unknown employee)' END) AS PreviousOwnerBeforeBatch,
       (SELECT STRING_AGG(ISNULL(x.DisplayName, N'?') + N' [' + x.Source + N']', N'; ') FROM
            (SELECT DISTINCT ee.Source, emp.DisplayName FROM #Ev ee LEFT JOIN dbo.Employees emp ON emp.EmployeeId = ee.EmployeeId
             WHERE ee.TicketId = r.TicketId AND ee.EmployeeId IS NOT NULL AND (ee.Strength = 'STRONG' OR r.Classification = 'WEAK_MISMATCH')) x) AS RecordedHandlers,
       r.HumanWorkSinceBatch, r.OwnerChanged AS OwnerChangedSinceBatch, r.NotesSince AS NotesSinceBatch, r.NotesTotal, r.PersonTransferSince AS PersonTransferSinceBatch,
       r.AssignmentsSince AS AssignmentsSinceBatch, r.StatusHistorySince AS StatusChangesSinceBatch, r.FirstResponseSince AS FirstHumanResponseSinceBatch,
       CASE WHEN r.PreviousOwnerId IS NOT NULL AND EXISTS (SELECT 1 FROM #Ev e WHERE e.TicketId = r.TicketId AND e.EmployeeId = r.PreviousOwnerId)
            THEN 'the PREVIOUS owner (before the batch) is a recorded handler' ELSE '' END AS Hint
FROM #Result r
LEFT JOIN dbo.Departments d ON d.DepartmentId = r.CurrentDepartmentId
LEFT JOIN dbo.Employees o ON o.EmployeeId = r.CurrentOwnerId LEFT JOIN dbo.Employees ba ON ba.EmployeeId = r.BatchAgentId LEFT JOIN dbo.Employees po ON po.EmployeeId = r.PreviousOwnerId
WHERE r.Classification IN ('MISMATCH', 'WEAK_MISMATCH')
ORDER BY CASE r.Classification WHEN 'MISMATCH' THEN 0 ELSE 1 END, r.TicketNumber;

-- ===========================================================================================================================
-- RS4 - MISSING EVIDENCE (reported separately from mismatches: the rule cannot be checked for these tickets)
-- ===========================================================================================================================
SELECT r.TicketNumber, r.Classification,
       CASE r.TicketStatus WHEN 1 THEN 'Open' WHEN 2 THEN 'InProgress' WHEN 3 THEN 'PendingCustomer' WHEN 4 THEN 'PendingThirdParty' WHEN 5 THEN 'Resolved' WHEN 6 THEN 'Closed' ELSE CONVERT(varchar(5), r.TicketStatus) END AS Status,
       ISNULL(o.DisplayName, N'(unowned)') AS CurrentOwner,
       (SELECT COUNT(*) FROM dbo.TicketInteractions i WHERE i.TicketId = r.TicketId) AS Interactions,
       (SELECT COUNT(*) FROM dbo.TicketInteractions i WHERE i.TicketId = r.TicketId AND i.Source = 2) AS GenesysInteractions,
       (SELECT STRING_AGG(x.GenesysId, N', ') FROM (SELECT DISTINCT e.GenesysId FROM #Ev e WHERE e.TicketId = r.TicketId AND e.EmployeeId IS NULL AND e.GenesysId IS NOT NULL) x) AS UnresolvedGenesysIds,
       CASE r.Classification WHEN 'MISSING_EVIDENCE_UNRESOLVED' THEN 'evidence exists but the Genesys id maps to no Ticketing user (AspNetUsers.GenesysUserId missing)'
            ELSE 'no handler, no HandledByUserId, no HumanAgent line and no Genesys agent id is recorded (never had a Genesys interaction, or an AI-only conversation)' END AS Why,
       r.HumanWorkSinceBatch
FROM #Result r LEFT JOIN dbo.Employees o ON o.EmployeeId = r.CurrentOwnerId
WHERE r.Classification LIKE 'MISSING_EVIDENCE%'
ORDER BY r.Classification, r.TicketNumber;

-- ===========================================================================================================================
-- RS5 - MATCHES
-- ===========================================================================================================================
SELECT r.TicketNumber, ISNULL(o.DisplayName, N'(unowned)') AS CurrentOwner, CASE WHEN r.OwnerInStrong = 1 THEN 'STRONG evidence' ELSE 'weak evidence only' END AS MatchedBy, r.HumanWorkSinceBatch
FROM #Result r LEFT JOIN dbo.Employees o ON o.EmployeeId = r.CurrentOwnerId WHERE r.Classification = 'MATCH' ORDER BY r.TicketNumber;

-- ===========================================================================================================================
-- RS6 - EVIDENCE DETAIL (every row behind the classification)
-- ===========================================================================================================================
SELECT r.TicketNumber, e.Source, e.Strength, ISNULL(emp.DisplayName, N'(no Ticketing user)') AS Employee, e.EmployeeId, e.GenesysId, e.Detail, e.AtUtc,
       CASE WHEN e.EmployeeId = r.CurrentOwnerId THEN 'IS the current owner' ELSE '' END AS OwnerCheck
FROM #Ev e JOIN #Result r ON r.TicketId = e.TicketId LEFT JOIN dbo.Employees emp ON emp.EmployeeId = e.EmployeeId
ORDER BY r.TicketNumber, e.Strength, e.AtUtc;

-- ===========================================================================================================================
-- RS7 - Could each recorded handler receive a Call Center ticket today? (current rules: active, not locked, CC member, CS Agent role)
-- ===========================================================================================================================
SELECT emp.DisplayName AS RecordedHandler, COUNT(DISTINCT e.TicketId) AS TicketsHandled,
       CASE WHEN emp.DeactivatedAtUtc IS NOT NULL THEN 'deactivated' WHEN u.LockoutEnabled = 1 AND u.LockoutEnd > SYSDATETIMEOFFSET() THEN 'locked out'
            WHEN NOT EXISTS (SELECT 1 FROM dbo.UserDepartmentAssignments a WHERE a.EmployeeId = emp.EmployeeId AND a.DepartmentId = @CcId) THEN 'not a Call Center member'
            WHEN NOT EXISTS (SELECT 1 FROM dbo.AspNetUserRoles ur JOIN dbo.AspNetRoles ro ON ro.Id = ur.RoleId WHERE ur.UserId = emp.EmployeeId AND ro.Name = N'CS Agent') THEN 'no CS Agent role'
            ELSE 'eligible' END AS CanReceiveCallCenterTicketsToday
FROM #Ev e JOIN dbo.Employees emp ON emp.EmployeeId = e.EmployeeId LEFT JOIN dbo.AspNetUsers u ON u.Id = emp.EmployeeId
WHERE e.Strength = 'STRONG'
GROUP BY emp.DisplayName, emp.EmployeeId, emp.DeactivatedAtUtc, u.LockoutEnabled, u.LockoutEnd
ORDER BY TicketsHandled DESC;

DROP TABLE #Result; DROP TABLE #Facts; DROP TABLE #Ev; DROP TABLE #Batch;
