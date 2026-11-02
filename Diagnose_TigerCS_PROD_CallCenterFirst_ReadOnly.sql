/*
================================================================================
 Diagnose_TigerCS_PROD_CallCenterFirst_ReadOnly.sql          (READ-ONLY - SELECT only)
 Database : TigerCS_Ticketing_Prod

 Answers, from Production data alone (nothing is written, nothing is locked - READ UNCOMMITTED):
   A. Is the Call Center-first build (and its schema) live in Production?             -> result sets 1-3
   B. How was ticket TG-ACC-20261102-0021 created and routed, step by step?            -> result sets 4-8
   C. Which tickets created since the release date skipped the Call Center, and why?  -> result sets 9-10

 HOW TO READ THE ANSWER
   Result set 1 (Verdict) gives one line. The fingerprints behind it:
     * Schema      : __EFMigrationsHistory has 20261031094748_AddDepartmentAssignmentRotation (+ TransferTracking, InteractionHandlers, CreatedBy).
                     Without it the Call Center-first code cannot run at all (it reads dbo.DepartmentAssignmentRotations inside ticket creation).
     * Build       : the Call Center-first build writes, for every human-handled new ticket, a 'Transfer' audit row whose text contains
                     'routed to Call Center first' and an 'AutoAssign' row with 'Trigger=CallCenterIntake' (or 'Status=Unassigned').
                     An older build writes neither - ever. Zero rows after the schema date = the new build was never live.
     * Department  : exactly one ACTIVE department with Code = 'CC' must exist, otherwise the new build silently falls back to the old routing.
     * Channel     : a Genesys ticket created WITHOUT humanFollowUpRequired=true is by design NOT moved to the Call Center (AI-handled) -
                     result set 5 shows RequiresHumanFollowUp for the ticket's originating interaction.
================================================================================
*/
SET NOCOUNT ON;
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

DECLARE @ExpectedDb      sysname      = N'TigerCS_Ticketing_Prod';
DECLARE @TicketNumber    nvarchar(40) = N'TG-ACC-20261102-0021';
DECLARE @ReleaseDateUtc  datetime2    = '2026-11-02T00:00:00';      -- tickets created on/after this date are checked in result sets 9-10

IF DB_NAME() <> @ExpectedDb
BEGIN
    DECLARE @m nvarchar(400) = N'ABORTED: connected to [' + ISNULL(DB_NAME(), N'?') + N'], this script is for [' + @ExpectedDb + N'] only.';
    THROW 50500, @m, 1;
END;

DECLARE @HasRotation bit = CASE WHEN OBJECT_ID(N'dbo.DepartmentAssignmentRotations', N'U') IS NULL THEN 0 ELSE 1 END;
DECLARE @HasOverrides bit = CASE WHEN OBJECT_ID(N'dbo.UserPermissionOverrides', N'U') IS NULL THEN 0 ELSE 1 END;
DECLARE @HasTransferCols bit = CASE WHEN COL_LENGTH(N'dbo.TicketWorkflowEvents', N'TransferReasonCode') IS NULL THEN 0 ELSE 1 END;
DECLARE @HasHandlers bit = CASE WHEN OBJECT_ID(N'dbo.TicketInteractionHandlers', N'U') IS NULL THEN 0 ELSE 1 END;
DECLARE @HasCreatedBy bit = CASE WHEN COL_LENGTH(N'dbo.Tickets', N'CreatedByEmployeeId') IS NULL THEN 0 ELSE 1 END;
DECLARE @CcActive int = (SELECT COUNT(*) FROM dbo.Departments WHERE Code = N'CC' AND IsActive = 1);
DECLARE @CcId int = (SELECT TOP (1) DepartmentId FROM dbo.Departments WHERE Code = N'CC' AND IsActive = 1);
DECLARE @MigRotation datetime2 = NULL;   -- (EF history has no timestamp; the rotation table's first cursor write is the best available date)
IF @HasRotation = 1 SELECT @MigRotation = MIN(UpdatedAtUtc) FROM dbo.DepartmentAssignmentRotations;

DECLARE @CcFirstTransfers int = (SELECT COUNT(*) FROM dbo.AuditEntries WHERE Action = N'Transfer' AND AfterValue LIKE N'%routed to Call Center first%');
DECLARE @CcFirstAssigns   int = (SELECT COUNT(*) FROM dbo.AuditEntries WHERE Action = N'AutoAssign' AND AfterValue LIKE N'%Trigger=CallCenterIntake%');
DECLARE @LastCcFirst datetime2 = (SELECT MAX(OccurredAtUtc) FROM dbo.AuditEntries
                                  WHERE (Action = N'Transfer' AND AfterValue LIKE N'%routed to Call Center first%')
                                     OR (Action = N'AutoAssign' AND AfterValue LIKE N'%Trigger=CallCenterIntake%'));

-- ===========================================================================================================================
-- 1. VERDICT
-- ===========================================================================================================================
SELECT @@SERVERNAME AS [Server], DB_NAME() AS [Database], SYSUTCDATETIME() AS CheckedAtUtc,
       @HasRotation AS Schema_DepartmentAssignmentRotations, @HasTransferCols AS Schema_TransferTracking, @HasHandlers AS Schema_InteractionHandlers,
       @HasCreatedBy AS Schema_TicketCreatedBy, @HasOverrides AS Schema_UserPermissionOverrides,
       @CcActive AS ActiveDepartmentsWithCodeCC, @CcFirstTransfers AS CallCenterFirstTransferAuditRows, @CcFirstAssigns AS CallCenterIntakeAssignAuditRows,
       @LastCcFirst AS LastCallCenterFirstActivityUtc,
       CASE
           WHEN @HasRotation = 0 AND @CcFirstTransfers = 0
                THEN 'NOT DEPLOYED: no DepartmentAssignmentRotations table and no Call Center-first audit rows - Production runs the pre-Call-Center-first build (new tickets stay in their category department).'
           WHEN @HasRotation = 0
                THEN 'INCONSISTENT: Call Center-first rows exist but the rotation table is missing - investigate before anything else.'
           WHEN @CcActive <> 1
                THEN 'MISCONFIGURED: expected exactly one ACTIVE department with Code = CC, found ' + CONVERT(varchar(10), @CcActive) + ' - the new build silently falls back to old routing.'
           WHEN @CcFirstTransfers = 0 AND @CcFirstAssigns = 0
                THEN 'SCHEMA PRESENT, BUILD NOT LIVE: the migrations are applied but no ticket has ever been routed Call Center-first - the application files (Api AND Web) are older than the Call Center-first commit.'
           WHEN @HasOverrides = 0
                THEN 'PARTIAL: Call Center-first is live but dbo.UserPermissionOverrides is missing - the current build reads it on Transfer/Resolve/Close permission checks.'
           ELSE 'LIVE: Call Center-first activity exists. Tickets that still skip the Call Center are channel/flag cases - see result sets 9-10.'
       END AS Verdict;

-- ===========================================================================================================================
-- 2. EF migrations the Call Center-first / permissions release depends on
-- ===========================================================================================================================
SELECT v.MigrationId, CASE WHEN h.MigrationId IS NULL THEN 'MISSING' ELSE 'applied' END AS [State]
FROM (VALUES (N'20261030061255_AddTicketInteractionHandlers'), (N'20261030062642_AddTicketTransferTracking'), (N'20261030073857_AddTicketCreatedBy'),
             (N'20261031094748_AddDepartmentAssignmentRotation'), (N'20261102065459_AddUserPermissionOverrides')) v(MigrationId)
LEFT JOIN dbo.__EFMigrationsHistory h ON h.MigrationId = v.MigrationId
ORDER BY v.MigrationId;

-- ===========================================================================================================================
-- 3. Call Center department and Round Robin state
-- ===========================================================================================================================
-- The rotation table exists only once the release is deployed, so its columns are added through dynamic SQL (otherwise this statement
-- fails with Msg 208 exactly in the "not deployed" case this script must diagnose).
DECLARE @sql3 nvarchar(max) = N'SELECT d.DepartmentId, d.Code, d.Name, d.IsActive,
       (SELECT AllowAssignment FROM dbo.DepartmentWorkflowSettings s WHERE s.DepartmentId = d.DepartmentId) AS AllowAssignment_NULL_means_default_true,
       (SELECT COUNT(*) FROM dbo.UserDepartmentAssignments a WHERE a.DepartmentId = d.DepartmentId) AS Members'
    + CASE WHEN @HasRotation = 1 THEN N',
       (SELECT LastAssignedEmployeeId FROM dbo.DepartmentAssignmentRotations r WHERE r.DepartmentId = d.DepartmentId) AS RotationCursor,
       (SELECT UpdatedAtUtc FROM dbo.DepartmentAssignmentRotations r WHERE r.DepartmentId = d.DepartmentId) AS CursorUpdatedAtUtc' ELSE N',
       CAST(NULL AS uniqueidentifier) AS RotationCursor, CAST(NULL AS datetime2) AS CursorUpdatedAtUtc' END
    + N'
FROM dbo.Departments d WHERE d.Code = N''CC'' OR d.Name LIKE N''%Call Center%'';';
EXEC sp_executesql @sql3;

-- ===========================================================================================================================
-- 4-8. THE TICKET: TG-ACC-20261102-0021
-- ===========================================================================================================================
DECLARE @TicketId bigint = (SELECT TicketId FROM dbo.Tickets WHERE TicketNumber = @TicketNumber);
IF @TicketId IS NULL
    SELECT 'Ticket ' + @TicketNumber + ' not found in this database.' AS TicketTrace;
ELSE
BEGIN
    -- 4. the ticket as it is now
    SELECT t.TicketId, t.TicketNumber, CASE t.TicketStatus WHEN 1 THEN 'Open' WHEN 2 THEN 'InProgress' WHEN 3 THEN 'PendingCustomer' WHEN 4 THEN 'PendingThirdParty'
                                                           WHEN 5 THEN 'Resolved' WHEN 6 THEN 'Closed' END AS [Status],
           od.Code AS OriginatingDept, cd.Code AS CurrentDept, t.CurrentOwnerEmployeeId AS Owner_NULL_means_unassigned,
           t.CategoryId, t.PriorityId, t.RequestTypeId, rt.Name AS RequestType, rtd.Code AS RequestTypeDept, t.CreatedAtUtc
    FROM dbo.Tickets t
    JOIN dbo.Departments od ON od.DepartmentId = t.OriginatingDepartmentId
    JOIN dbo.Departments cd ON cd.DepartmentId = t.CurrentDepartmentId
    LEFT JOIN dbo.RequestTypes rt ON rt.RequestTypeId = t.RequestTypeId
    LEFT JOIN dbo.Departments rtd ON rtd.DepartmentId = rt.DepartmentId
    WHERE t.TicketId = @TicketId;

    -- 5. channel / how it came in. Genesys ticket + RequiresHumanFollowUp = 0 => AI-handled by design: it stays in its department.
    SELECT i.TicketInteractionId, i.IsOriginatingInteraction, CASE i.Source WHEN 1 THEN 'Ticketing (manual UI/API)' WHEN 2 THEN 'Genesys' END AS Source,
           ch.Code AS ChannelCode, ch.Name AS Channel, i.GenesysConversationId, i.GenesysQueueName, i.RequiresHumanFollowUp, i.HandledByUserId,
           i.EndedAtUtc, i.InactivityClosedAtUtc, i.CreatedAtUtc
    FROM dbo.TicketInteractions i LEFT JOIN dbo.Channels ch ON ch.ChannelId = i.ChannelId
    WHERE i.TicketId = @TicketId ORDER BY i.TicketInteractionId;

    -- 6. audit trail in order: a Call Center-first ticket has a 'Transfer ... routed to Call Center first' row and an AutoAssign (Trigger=CallCenterIntake)
    SELECT a.AuditEntryId, a.OccurredAtUtc, a.Action, a.ActorEmployeeId, a.BeforeValue, a.AfterValue, a.CorrelationId
    FROM dbo.AuditEntries a WHERE a.EntityType = N'Ticket' AND a.EntityId = CONVERT(nvarchar(100), @TicketId) ORDER BY a.AuditEntryId;

    -- 7. assignments and transfers
    SELECT 'assignment' AS Kind, ta.AssignedAtUtc AS AtUtc, ta.AssignedEmployeeId, ta.AssignedDepartmentId AS DeptId, ta.IsCurrent, ta.AssigningActorEmployeeId
    FROM dbo.TicketAssignments ta WHERE ta.TicketId = @TicketId ORDER BY ta.AssignedAtUtc;
    -- transfer events exist only once the TransferTracking migration is applied (dynamic SQL so this script still compiles on an older schema)
    IF @HasTransferCols = 1
        EXEC sp_executesql N'SELECT e.OccurredAtUtc, e.FromDepartmentId, e.ToDepartmentId, e.ToEmployeeId, e.TransferReasonCode, e.ActorEmployeeId, e.Note
                             FROM dbo.TicketWorkflowEvents e WHERE e.TicketId = @id AND e.EventType = 17 ORDER BY e.OccurredAtUtc;', N'@id bigint', @id = @TicketId;

    -- 8. the single most useful line: which rule explains the routing
    SELECT CASE
        WHEN @CcFirstTransfers = 0 AND @CcFirstAssigns = 0
            THEN 'Created by a build WITHOUT Call Center-first (no ticket anywhere has ever been routed Call Center-first): category department + no assignment rule = department queue.'
        WHEN EXISTS (SELECT 1 FROM dbo.TicketInteractions i WHERE i.TicketId = @TicketId AND i.IsOriginatingInteraction = 1 AND i.Source = 2 AND i.RequiresHumanFollowUp = 0)
            THEN 'Genesys ticket created with humanFollowUpRequired = false/absent: by design the AI-handled path does not enter the Call Center (needsHuman = false). Fix is in the Genesys flow or a business decision - not a missing deployment.'
        WHEN @CcActive <> 1
            THEN 'No single active department with Code CC: the new build fell back to old routing.'
        WHEN NOT EXISTS (SELECT 1 FROM dbo.AuditEntries a WHERE a.EntityType = N'Ticket' AND a.EntityId = CONVERT(nvarchar(100), @TicketId) AND a.Action = N'Transfer')
            THEN 'Call Center-first is live elsewhere but THIS ticket has no Transfer audit row: created before the deployment cut-over, or by a path that does not call TicketCreationAppService (see result set 6 timestamps vs LastCallCenterFirstActivityUtc).'
        ELSE 'Routed Call Center-first, then moved/left - read result set 6.' END AS WhyThisTicketLooksLikeThis;
END;

-- ===========================================================================================================================
-- 9. Tickets created since the release date that are NOT in the Call Center and have no owner - grouped by how they were created
--    (SQL Server forbids an aggregate over a subquery, so "routed Call Center-first" is computed per ticket in the CTE and the outer query only aggregates plain columns)
-- ===========================================================================================================================
;WITH T AS (
    SELECT t.TicketId, t.CurrentDepartmentId, i.Source, i.ChannelId, i.RequiresHumanFollowUp,
           CASE WHEN EXISTS (SELECT 1 FROM dbo.AuditEntries a WHERE a.EntityType = N'Ticket' AND a.EntityId = CONVERT(nvarchar(100), t.TicketId)
                             AND a.Action = N'Transfer' AND a.AfterValue LIKE N'%routed to Call Center first%') THEN 1 ELSE 0 END AS RoutedFlag
    FROM dbo.Tickets t
    LEFT JOIN dbo.TicketInteractions i ON i.TicketId = t.TicketId AND i.IsOriginatingInteraction = 1
    WHERE t.CreatedAtUtc >= @ReleaseDateUtc AND t.CurrentOwnerEmployeeId IS NULL AND t.TicketStatus IN (1, 2, 3, 4)
      AND t.CurrentDepartmentId <> ISNULL(@CcId, -1)
)
SELECT CASE x.Source WHEN 1 THEN 'Ticketing (manual UI/API)' WHEN 2 THEN 'Genesys' ELSE 'no interaction row' END AS CreatedVia,
       ISNULL(ch.Code, '-') AS ChannelCode,
       CASE WHEN x.RequiresHumanFollowUp = 1 THEN 'human follow-up requested' ELSE 'no human follow-up flag' END AS HumanFlag,
       cd.Code AS CurrentDept, COUNT(*) AS Tickets,
       SUM(x.RoutedFlag) AS RoutedCallCenterFirst
FROM T x
JOIN dbo.Departments cd ON cd.DepartmentId = x.CurrentDepartmentId
LEFT JOIN dbo.Channels ch ON ch.ChannelId = x.ChannelId
GROUP BY x.Source, ch.Code, x.RequiresHumanFollowUp, cd.Code
ORDER BY COUNT(*) DESC;

-- 10. Hour-by-hour: when did the Call Center-first routing start (first/last routed ticket) versus tickets that were not routed
;WITH T AS (
    SELECT t.TicketId, t.CreatedAtUtc, t.CurrentOwnerEmployeeId,
           CASE WHEN EXISTS (SELECT 1 FROM dbo.AuditEntries a WHERE a.EntityType = N'Ticket' AND a.EntityId = CONVERT(nvarchar(100), t.TicketId)
                             AND a.Action = N'Transfer' AND a.AfterValue LIKE N'%routed to Call Center first%') THEN 1 ELSE 0 END AS RoutedFlag
    FROM dbo.Tickets t
    WHERE t.CreatedAtUtc >= @ReleaseDateUtc
)
SELECT CONVERT(varchar(13), x.CreatedAtUtc, 120) + ':00' AS HourUtc, COUNT(*) AS TicketsCreated,
       SUM(x.RoutedFlag) AS RoutedCallCenterFirst,
       SUM(CASE WHEN x.CurrentOwnerEmployeeId IS NULL THEN 1 ELSE 0 END) AS StillUnowned
FROM T x
GROUP BY CONVERT(varchar(13), x.CreatedAtUtc, 120)
ORDER BY 1;
