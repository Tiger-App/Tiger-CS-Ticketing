/*
================================================================================
 Backfill_TigerCS_PUBLISHED_ClassificationAndAssignment.sql
 Database : TigerCS_Ticketing_Prod   (guarded by @ExpectedDb). Dry run on a RESTORED COPY first; the live run needs explicit written approval.

 Applies the CURRENT classification and assignment rules to HISTORICAL tickets. It introduces NO new rule: every decision below is a set-based
 transcription of an existing application service, named next to the block that uses it.

   PHASE 1  CLASSIFICATION  = DefaultClassificationService.AssessAsync / IsDefaultAsync / ResolveForDepartmentAsync / EnsureClassifiedAsync
                              and DefaultClassificationBackfillClassifier.Decide  (the same rules as the admin "default classification" backfill).
     * A ticket with no classification, a request type only, a dangling category / request type id, or a category and request type of DIFFERENT
       departments is re-classified to  <originating department> / General / General  and that request type's PUBLISHED workflow version.
       When the originating department is unknown / inactive it is classified Customer Services / General - the existing human classification queue
       (agents re-classify it with "Classify Ticket"; the UI shows "Customer Services / General").
     * A VALID classification is never replaced; a ticket already on General stays; category-only tickets are kept unless @IncludeCategoryOnly = 1.
     * Collections tickets are never touched. A valid department WITHOUT its own General is a CONFIGURATION error: reported, nothing written
       (run EnsureDefaultGeneralClassification.sql first). Nothing is created here and the department is never swapped for Customer Services'.
     * Writes Tickets.CategoryId / RequestTypeId / WorkflowTemplateId ONLY. Applies to every status (a Closed ticket is labelled, never reopened).

   PHASE 2  ASSIGNMENT      = CsAgentBackfillClassifier.Classify + CsAgentBackfillService.TryBackfillAsync + TicketAutoAssignmentService
                              (AssignConfirmedHandlerAsync, EnsureCsAgentAsync -> AssignRoundRobinAsync with the shared Call Center CS Agent rotation)
     * Business rule: every ACTIVE (Open / InProgress / PendingCustomer / PendingThirdParty) human-handled ticket has a CS Agent, whatever its department.
     * Only tickets with NO owner are candidates; an existing owner is never replaced (owned tickets are only REPORTED, see report 9).
     * HELD BACK: pending approval, Collections, a live AI conversation nobody asked a person for, tickets younger than @MinTicketAgeMinutes.
     * Who: the confirmed handling agent when they are an eligible Call Center CS Agent (the cursor does not move); otherwise the next eligible CS Agent
       of the persisted shared rotation, oldest ticket first, continuing the live cursor (never resetting it).
     * The ticket is NOT moved: department, request type, priority, status, SLA and history are untouched.
     * Writes Tickets.CurrentOwnerEmployeeId, one superseded + one new TicketAssignments row, audit rows and the rotation cursor.

 DEPARTMENT: the department is never changed by this script. The existing workflow only moves a department through an explicit transfer / request-type
 routing event, and a historical ticket's department may be the result of one; changing it here would be a new rule. Report 9b lists tickets whose
 request-type department differs from their current department, for review only.

 NEVER WRITTEN: ticket status, SLA timestamps / instances / pauses, status history, interactions and their Genesys fields, messages, notes, resolutions,
 approvals, workflow events, notifications / outbox (no Genesys call or customer notification can result), any other ticket column. The script PROVES
 this in-transaction (row-count and protected-column checks, report 8) and aborts + rolls back on any difference. Because those checks compare whole-table
 counts, run it in a quiet window: live traffic during the run can only cause a safe false abort (nothing is written), never a wrong write.

 IDEMPOTENT / SAFE TO RERUN: a ticket that has been fixed no longer matches any candidate rule (it is now classified General / owned), so a second run
 finds nothing; tickets that cannot be fixed (configuration gap, nobody eligible) are simply reported again. Per-row guards (RowVersion, still no owner,
 same classification) plus an application lock make a concurrent writer win or the run abort - never a double write.

 LOG: every change writes AuditEntries rows (system actor, CorrelationId = @BatchId):
       'ApplyDefaultClassification'  (BeforeValue holds the previous CategoryId / RequestTypeId / WorkflowTemplateId  -> the data needed for a rollback)
       'AutoAssign' + 'BackfillAssign' (engine format, Trigger=Backfill)
       'HistoricalBackfillBatch'     (one per run: counts, cursor before / after)
 and report 10 prints the same log (it survives a dry-run's rollback).

 HOW TO RUN (SSMS, ONE batch, no GO)
   1) Full backup / restored copy.   2) Run with defaults: @DryRun = 1 -> a REAL transaction that performs every write and every check, prints the
      reports, then ROLLS BACK.   3) Review reports 3, 4, 6, 7, 9.   4) After sign-off copy the two approved counts from report 1 into
      @ApprovedClassificationCount / @ApprovedAssignmentCount, confirm the service-account gate and set @DryRun = 0.
   sqlcmd: -C -b -I -i Backfill_TigerCS_PUBLISHED_ClassificationAndAssignment.sql
 Status values: 1 Open, 2 InProgress, 3 PendingCustomer, 4 PendingThirdParty, 5 Resolved, 6 Closed.   Workflow template Status 2 = Published.
================================================================================
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;     -- required by the filtered unique index on TicketAssignments
SET ANSI_NULLS ON;
SET LOCK_TIMEOUT 20000;

-- ------------------------------------------------ PARAMETERS ----------------------------------------------------------------
DECLARE @DryRun                       bit              = 1;      -- 1 = everything, then ROLL BACK (default).  0 = COMMIT.
DECLARE @ExpectedDb                   sysname          = N'TigerCS_Ticketing_Prod';   -- confirmed Production name; the guard below refuses any other database
DECLARE @RunClassification            bit              = 1;      -- phase switches (both on by default)
DECLARE @RunAssignment                bit              = 1;
DECLARE @IncludeCategoryOnly          bit              = 0;      -- DefaultClassificationBackfillOptions.IncludeCategoryOnly (default off)
DECLARE @IncludeAiEndedWithoutHuman   bit              = 1;      -- CsAgentBackfillOptions.IncludeAiEndedWithoutHuman (default on)
DECLARE @MinTicketAgeMinutes          int              = 3;      -- CsAgentBackfillOptions.MinTicketAgeMinutes
DECLARE @ApprovedClassificationCount  int              = NULL;   -- commit gate: report 1 "ClassificationToApply"
DECLARE @ApprovedAssignmentCount      int              = NULL;   -- commit gate: report 1 "AssignmentToApply"
DECLARE @ServiceIdsConfirmed          bit              = 0;      -- commit gate: 1 only after listing ALL service-account ids below
DECLARE @NoServiceAccountsExist       bit              = 0;      -- commit gate alternative: 1 ONLY if confirmed that none exist (list stays empty)
DECLARE @BatchId                      uniqueidentifier = NEWID();

DECLARE @ServiceIdentity TABLE (EmployeeId uniqueidentifier PRIMARY KEY);
-- Authorization:ServiceIdentity:EmployeeIds  and  Collections:Authorization:IntegrationEmployeeIds  (one INSERT per id):
-- INSERT INTO @ServiceIdentity VALUES ('00000000-0000-0000-0000-000000000000');
-- ----------------------------------------------------------------------------------------------------------------------------

DECLARE @msg nvarchar(2000);
IF DB_NAME() <> @ExpectedDb
BEGIN
    SET @msg = N'ABORTED: connected to [' + ISNULL(DB_NAME(), N'?') + N'] on [' + CAST(@@SERVERNAME AS nvarchar(128)) + N']; this script only runs against ['
             + @ExpectedDb + N']. Nothing was changed.';
    THROW 50800, @msg, 1;
END;
PRINT N'Guard passed: server [' + CAST(@@SERVERNAME AS nvarchar(128)) + N'], database [' + DB_NAME() + N'], DryRun=' + CAST(@DryRun AS nvarchar(1))
    + N', BatchId=' + CONVERT(nvarchar(36), @BatchId);

-- ---------------------------------------------------- schema pre-checks (read-only) -------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.indexes i
               WHERE i.object_id = OBJECT_ID(N'dbo.TicketAssignments') AND i.is_unique = 1 AND i.has_filter = 1
                 AND i.filter_definition LIKE N'%IsCurrent%=%1%'
                 AND (SELECT COUNT(*) FROM sys.index_columns ic WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0) = 1)
    THROW 50801, 'ABORTED: the unique filtered index on dbo.TicketAssignments(TicketId) WHERE IsCurrent = 1 is missing - the duplicate-assignment guard this script relies on.', 1;

DECLARE @missing nvarchar(max) = N'';
SELECT @missing += v.Tbl + N'.' + v.Col + N' ' FROM (VALUES
    (N'Tickets', N'TicketId'), (N'Tickets', N'TicketNumber'), (N'Tickets', N'TicketStatus'), (N'Tickets', N'CategoryId'), (N'Tickets', N'RequestTypeId'),
    (N'Tickets', N'WorkflowTemplateId'), (N'Tickets', N'CurrentDepartmentId'), (N'Tickets', N'OriginatingDepartmentId'), (N'Tickets', N'CurrentOwnerEmployeeId'),
    (N'Tickets', N'PriorityId'), (N'Tickets', N'SlaState'), (N'Tickets', N'EscalationLevel'), (N'Tickets', N'CreatedAtUtc'), (N'Tickets', N'RowVersion'),
    (N'Tickets', N'FirstHumanResponseAtUtc'),
    (N'Categories', N'CategoryId'), (N'Categories', N'DepartmentId'), (N'Categories', N'Name'), (N'Categories', N'IsActive'),
    (N'RequestTypes', N'RequestTypeId'), (N'RequestTypes', N'DepartmentId'), (N'RequestTypes', N'Name'), (N'RequestTypes', N'WorkflowId'), (N'RequestTypes', N'IsActive'),
    (N'WorkflowTemplates', N'WorkflowTemplateId'), (N'WorkflowTemplates', N'WorkflowId'), (N'WorkflowTemplates', N'Status'),
    (N'Departments', N'DepartmentId'), (N'Departments', N'Code'), (N'Departments', N'IsActive'), (N'DepartmentWorkflowSettings', N'DepartmentId'),
    (N'DepartmentWorkflowSettings', N'AllowAssignment'),
    (N'TicketAssignments', N'TicketAssignmentId'), (N'TicketAssignments', N'TicketId'), (N'TicketAssignments', N'AssignedEmployeeId'),
    (N'TicketAssignments', N'AssignedDepartmentId'), (N'TicketAssignments', N'AssignedAtUtc'), (N'TicketAssignments', N'AssigningActorEmployeeId'),
    (N'TicketAssignments', N'IsCurrent'),
    (N'AuditEntries', N'ActorEmployeeId'), (N'AuditEntries', N'Action'), (N'AuditEntries', N'EntityType'), (N'AuditEntries', N'EntityId'),
    (N'AuditEntries', N'BeforeValue'), (N'AuditEntries', N'AfterValue'), (N'AuditEntries', N'CorrelationId'), (N'AuditEntries', N'OccurredAtUtc'),
    (N'TicketApprovals', N'TicketId'), (N'TicketApprovals', N'Status'), (N'TicketAgentHandoffs', N'TicketId'), (N'TicketAgentHandoffs', N'Status'),
    (N'TicketInteractions', N'TicketInteractionId'), (N'TicketInteractions', N'TicketId'), (N'TicketInteractions', N'Source'), (N'TicketInteractions', N'EndedAtUtc'),
    (N'TicketInteractions', N'InactivityClosedAtUtc'), (N'TicketInteractions', N'RequiresHumanFollowUp'), (N'TicketInteractions', N'HandledByUserId'),
    (N'TicketInteractionHandlers', N'TicketId'), (N'TicketInteractionHandlers', N'HandledByUserId'), (N'TicketInteractionHandlers', N'ConnectedAtUtc'),
    (N'TicketInteractionHandlers', N'RecordedAtUtc'),
    (N'TicketInteractionMessages', N'TicketInteractionId'), (N'TicketInteractionMessages', N'Sender'), (N'TicketInteractionMessages', N'SenderId'),
    (N'TicketInteractionMessages', N'SentAtUtc'),
    (N'DepartmentAssignmentRotations', N'DepartmentId'), (N'DepartmentAssignmentRotations', N'LastAssignedEmployeeId'), (N'DepartmentAssignmentRotations', N'UpdatedAtUtc'),
    (N'Employees', N'EmployeeId'), (N'Employees', N'DeactivatedAtUtc'), (N'UserDepartmentAssignments', N'EmployeeId'), (N'UserDepartmentAssignments', N'DepartmentId'),
    (N'AspNetUsers', N'Id'), (N'AspNetUsers', N'GenesysUserId'), (N'AspNetUsers', N'LockoutEnabled'), (N'AspNetUsers', N'LockoutEnd'),
    (N'AspNetUserRoles', N'UserId'), (N'AspNetUserRoles', N'RoleId'), (N'AspNetRoles', N'Id'), (N'AspNetRoles', N'Name')) v(Tbl, Col)
WHERE NOT EXISTS (SELECT 1 FROM sys.columns c WHERE c.object_id = OBJECT_ID(N'dbo.' + v.Tbl) AND c.name = v.Col);
IF LEN(@missing) > 0
BEGIN
    SET @msg = N'ABORTED: expected tables / columns not found: ' + @missing;
    THROW 50802, @msg, 1;
END;

-- Service-account gate (same rule as the Call Center distribution scripts).
DECLARE @ServiceListCount int = (SELECT COUNT(*) FROM @ServiceIdentity);
IF @DryRun = 0
BEGIN
    IF (@RunClassification = 1 AND @ApprovedClassificationCount IS NULL) OR (@RunAssignment = 1 AND @ApprovedAssignmentCount IS NULL)
        THROW 50803, 'ABORTED: commit gates not satisfied - set @ApprovedClassificationCount and @ApprovedAssignmentCount from the approved dry run (report 1).', 1;
    IF @RunAssignment = 1
    BEGIN
        IF @ServiceListCount > 0 AND ISNULL(@NoServiceAccountsExist, 0) = 1
            THROW 50804, 'ABORTED: contradictory service-account gates - ids are listed but @NoServiceAccountsExist = 1.', 1;
        IF @ServiceListCount > 0 AND ISNULL(@ServiceIdsConfirmed, 0) <> 1
            THROW 50805, 'ABORTED: service-account ids are listed but @ServiceIdsConfirmed <> 1.', 1;
        IF @ServiceListCount = 0 AND ISNULL(@NoServiceAccountsExist, 0) <> 1
            THROW 50806, 'ABORTED: the service-account list is empty. List every service/integration account id, or set @NoServiceAccountsExist = 1 once confirmed there are none.', 1;
        IF EXISTS (SELECT 1 FROM @ServiceIdentity s WHERE NOT EXISTS (SELECT 1 FROM dbo.Employees e WHERE e.EmployeeId = s.EmployeeId))
            THROW 50807, 'ABORTED: a listed service-account id is not an employee (typo?).', 1;
    END;
END
ELSE IF @RunAssignment = 1 AND @ServiceListCount = 0 AND ISNULL(@NoServiceAccountsExist, 0) <> 1
    PRINT N'WARNING: service-account list is empty and @NoServiceAccountsExist = 0 - a real run would be refused.';

-- ---------------------------------------------------- working structures (table variables: they survive a dry run's ROLLBACK) -----
DECLARE @Gen TABLE (DepartmentId int PRIMARY KEY, Code nvarchar(50) NOT NULL, CategoryId int NULL, RequestTypeId int NULL, WorkflowTemplateId int NULL, Gap varchar(40) NULL);
DECLARE @Cls TABLE (
    TicketId bigint PRIMARY KEY, TicketNumber nvarchar(80) NULL, TicketStatus tinyint NOT NULL, CurrentDepartmentId int NOT NULL, OriginatingDepartmentId int NOT NULL,
    OldCategoryId int NULL, OldRequestTypeId int NULL, OldWorkflowTemplateId int NULL, RowVer binary(8) NOT NULL,
    State varchar(20) NOT NULL, IsDefault bit NOT NULL, IsCollections bit NOT NULL,
    Decision varchar(40) NOT NULL, TargetDeptCode nvarchar(50) NULL, NewCategoryId int NULL, NewRequestTypeId int NULL, NewWorkflowTemplateId int NULL);
DECLARE @Asg TABLE (
    TicketId bigint PRIMARY KEY, TicketNumber nvarchar(80) NULL, TicketStatus tinyint NOT NULL, CurrentDepartmentId int NOT NULL, CreatedAtUtc datetime2 NOT NULL,
    RowVer binary(8) NOT NULL, PendingApproval bit NOT NULL, IsCollections bit NOT NULL, HumanNeeded bit NOT NULL, HasGenesys bit NOT NULL, HasLiveGenesys bit NOT NULL,
    HandlerId uniqueidentifier NULL,
    Class varchar(24) NOT NULL, Source varchar(20) NULL, AgentId uniqueidentifier NULL, RotationSeq int NULL);
DECLARE @Agents TABLE (Rn int PRIMARY KEY, EmployeeId uniqueidentifier NOT NULL, SortKey char(36) COLLATE Latin1_General_BIN2 NOT NULL);
DECLARE @Log TABLE (Seq int IDENTITY(1,1) PRIMARY KEY, TicketId bigint NOT NULL, Phase varchar(16) NOT NULL, Action nvarchar(100) NOT NULL, Detail nvarchar(1000) NOT NULL);
DECLARE @Counts TABLE (TableName sysname PRIMARY KEY, RowsBefore bigint NOT NULL, RowsAfter bigint NULL, MustBeUnchanged bit NOT NULL);
DECLARE @Check TABLE (CheckName nvarchar(200) NOT NULL, Result varchar(8) NOT NULL, Detail nvarchar(400) NULL);

DECLARE @Now datetime2 = SYSUTCDATETIME();
DECLARE @ColId int = (SELECT DepartmentId FROM dbo.Departments WHERE Code = N'COL');          -- Collections (may not exist)
DECLARE @CsId  int = (SELECT DepartmentId FROM dbo.Departments WHERE Code = N'CS' AND IsActive = 1);
DECLARE @CcId  int = (SELECT DepartmentId FROM dbo.Departments WHERE Code = N'CC' AND IsActive = 1);
DECLARE @CsGap varchar(40), @CsCount int = (SELECT COUNT(*) FROM dbo.Departments WHERE Code = N'CS' AND IsActive = 1);
DECLARE @ClsApply int = 0, @ClsGap int = 0, @AsgApply int = 0, @AsgHandler int = 0, @AsgRotation int = 0, @AsgNoAgent int = 0;
DECLARE @N int = 0, @Start int = 1, @Cursor uniqueidentifier, @LastAgent uniqueidentifier, @Updated int, @Inserted int;
DECLARE @CcCount int = (SELECT COUNT(*) FROM dbo.Departments WHERE Code = N'CC' AND IsActive = 1);

-- Protected tables: the script must not change them. Row counts before (informational + proven after).
DECLARE @t sysname, @sql nvarchar(max), @rowCnt bigint;
DECLARE watched CURSOR LOCAL FAST_FORWARD FOR
    SELECT v.n, v.w FROM (VALUES (N'Tickets', 0), (N'TicketAssignments', 0), (N'AuditEntries', 0), (N'DepartmentAssignmentRotations', 0),
        (N'TicketStatusHistory', 1), (N'TicketSlaInstances', 1), (N'TicketSlaPausePeriods', 1), (N'TicketInteractions', 1), (N'TicketInteractionMessages', 1),
        (N'TicketInteractionHandlers', 1), (N'TicketNotes', 1), (N'TicketResolutions', 1), (N'TicketPendingRecords', 1), (N'TicketApprovals', 1),
        (N'TicketAgentHandoffs', 1), (N'TicketEscalations', 1), (N'TicketWorkflowEvents', 1), (N'Notifications', 1), (N'OutboxMessages', 1),
        (N'Employees', 1), (N'AspNetUsers', 1), (N'AspNetUserRoles', 1), (N'Categories', 1), (N'RequestTypes', 1), (N'WorkflowTemplates', 1), (N'Departments', 1)) v(n, w)
    WHERE OBJECT_ID(N'dbo.' + v.n, N'U') IS NOT NULL;
DECLARE @mustNotChange bit;
OPEN watched; FETCH NEXT FROM watched INTO @t, @mustNotChange;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @sql = N'SELECT @c = COUNT_BIG(*) FROM dbo.' + QUOTENAME(@t) + N' WITH (NOLOCK);';
    EXEC sp_executesql @sql, N'@c bigint OUTPUT', @c = @rowCnt OUTPUT;
    INSERT @Counts (TableName, RowsBefore, MustBeUnchanged) VALUES (@t, @rowCnt, @mustNotChange);
    FETCH NEXT FROM watched INTO @t, @mustNotChange;
END;
CLOSE watched; DEALLOCATE watched;

-- Protected ticket columns, captured before any write: they must be identical afterwards for EVERY ticket (compared as one checksum + per-column counts).
DECLARE @ProtectedBefore bigint, @ProtectedAfter bigint;
SELECT @ProtectedBefore = CHECKSUM_AGG(CHECKSUM(TicketId, TicketNumber, TicketStatus, CurrentDepartmentId, OriginatingDepartmentId, PriorityId, SlaState,
                                                EscalationLevel, CreatedAtUtc, FirstHumanResponseAtUtc))
FROM dbo.Tickets WITH (NOLOCK);

BEGIN TRY
    BEGIN TRANSACTION;

    -- (a) one run at a time -----------------------------------------------------------------------------------------------
    DECLARE @lock int;
    EXEC @lock = sp_getapplock @Resource = N'TigerCS.HistoricalClassificationAssignmentBackfill', @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 0;
    IF @lock < 0 THROW 50810, 'ABORTED: another backfill run holds the application lock.', 1;

    ------------------------------------------------------------------------------------------------------------------------
    -- PHASE 1 - CLASSIFICATION
    ------------------------------------------------------------------------------------------------------------------------
    -- General / General of every active non-Collections department: exactly one active General category, exactly one active General request type,
    -- exactly one Published workflow version (DefaultClassificationService.ResolveInAsync). Anything else is a reported gap, never guessed.
    INSERT @Gen (DepartmentId, Code, CategoryId, RequestTypeId, WorkflowTemplateId, Gap)
    SELECT d.DepartmentId, d.Code, cat.CategoryId, rt.RequestTypeId, tpl.WorkflowTemplateId,
           CASE WHEN cat.n = 0 THEN 'CategoryMissing'    WHEN cat.n > 1 THEN 'CategoryAmbiguous'
                WHEN rt.n  = 0 THEN 'RequestTypeMissing' WHEN rt.n  > 1 THEN 'RequestTypeAmbiguous'
                WHEN tpl.n = 0 THEN 'WorkflowNotPublished' WHEN tpl.n > 1 THEN 'WorkflowAmbiguous' END
    FROM dbo.Departments d
    OUTER APPLY (SELECT COUNT(*) AS n, MIN(c.CategoryId) AS CategoryId FROM dbo.Categories c
                 WHERE c.DepartmentId = d.DepartmentId AND c.IsActive = 1 AND LTRIM(RTRIM(c.Name)) COLLATE Latin1_General_CI_AS = N'General') cat
    OUTER APPLY (SELECT COUNT(*) AS n, MIN(r.RequestTypeId) AS RequestTypeId, MIN(r.WorkflowId) AS WorkflowId FROM dbo.RequestTypes r
                 WHERE r.DepartmentId = d.DepartmentId AND r.IsActive = 1 AND LTRIM(RTRIM(r.Name)) COLLATE Latin1_General_CI_AS = N'General') rt
    OUTER APPLY (SELECT COUNT(*) AS n, MIN(w.WorkflowTemplateId) AS WorkflowTemplateId FROM dbo.WorkflowTemplates w
                 WHERE w.WorkflowId = rt.WorkflowId AND w.Status = 2) tpl
    WHERE d.IsActive = 1 AND d.Code <> N'COL';

    SET @CsGap = CASE WHEN @CsCount = 0 THEN 'DepartmentMissing' WHEN @CsCount > 1 THEN 'DepartmentAmbiguous'
                      ELSE (SELECT Gap FROM @Gen WHERE DepartmentId = @CsId) END;

    -- State of every ticket's stored classification (AssessAsync) and whether it already is a General pair (IsDefaultAsync).
    INSERT @Cls (TicketId, TicketNumber, TicketStatus, CurrentDepartmentId, OriginatingDepartmentId, OldCategoryId, OldRequestTypeId, OldWorkflowTemplateId, RowVer,
                 State, IsDefault, IsCollections, Decision)
    SELECT t.TicketId, t.TicketNumber, t.TicketStatus, t.CurrentDepartmentId, t.OriginatingDepartmentId, t.CategoryId, t.RequestTypeId, t.WorkflowTemplateId, t.RowVersion,
           s.State,
           CASE WHEN c.CategoryId IS NOT NULL AND r.RequestTypeId IS NOT NULL AND c.DepartmentId = r.DepartmentId
                 AND LTRIM(RTRIM(c.Name)) COLLATE Latin1_General_CI_AS = N'General' AND LTRIM(RTRIM(r.Name)) COLLATE Latin1_General_CI_AS = N'General' THEN 1 ELSE 0 END,
           CASE WHEN t.CurrentDepartmentId = @ColId OR t.OriginatingDepartmentId = @ColId THEN 1 ELSE 0 END,
           'PENDING'
    FROM dbo.Tickets t
    LEFT JOIN dbo.Categories c   ON c.CategoryId = t.CategoryId
    LEFT JOIN dbo.RequestTypes r ON r.RequestTypeId = t.RequestTypeId
    CROSS APPLY (SELECT CASE
        WHEN t.CategoryId IS NULL AND t.RequestTypeId IS NULL THEN 'NO_CLASSIFICATION'
        WHEN t.CategoryId IS NOT NULL AND c.CategoryId IS NULL THEN 'INVALID_REFERENCE'
        WHEN t.RequestTypeId IS NOT NULL AND r.RequestTypeId IS NULL THEN 'INVALID_REFERENCE'
        WHEN t.CategoryId IS NULL THEN 'REQUEST_TYPE_ONLY'
        WHEN t.RequestTypeId IS NULL THEN 'CATEGORY_ONLY'
        WHEN r.DepartmentId <> c.DepartmentId THEN 'INCONSISTENT'
        ELSE 'VALID' END AS State) s;

    -- DefaultClassificationBackfillClassifier.Decide, then the target (ResolveForDepartmentAsync on the ORIGINATING department).
    UPDATE @Cls SET Decision =
        CASE WHEN @RunClassification = 0 THEN 'PHASE_OFF'
             WHEN IsCollections = 1 THEN 'EXCLUDED_COLLECTIONS'
             WHEN IsDefault = 1 THEN 'ALREADY_DEFAULT'
             WHEN State = 'VALID' THEN 'ALREADY_VALID'
             WHEN State = 'CATEGORY_ONLY' AND @IncludeCategoryOnly = 0 THEN 'CATEGORY_ONLY_KEPT'
             ELSE 'CANDIDATE' END;

    -- No usable Customer Services / General anywhere: the rule is not enforced and the run applies nothing (DefaultClassificationBackfillRunStatus.ConfigurationMissing).
    UPDATE @Cls SET Decision = 'CONFIG_MISSING_CS' WHERE Decision = 'CANDIDATE' AND @CsGap IS NOT NULL;

    -- Originating department known (active, not Collections): ITS OWN General, or a configuration gap (never Customer Services' as a stand-in).
    UPDATE k SET Decision = CASE WHEN g.Gap IS NULL THEN 'APPLY' ELSE 'DEPT_GENERAL_MISSING' END,
                 TargetDeptCode = g.Code, NewCategoryId = CASE WHEN g.Gap IS NULL THEN g.CategoryId END,
                 NewRequestTypeId = CASE WHEN g.Gap IS NULL THEN g.RequestTypeId END, NewWorkflowTemplateId = CASE WHEN g.Gap IS NULL THEN g.WorkflowTemplateId END
    FROM @Cls k JOIN @Gen g ON g.DepartmentId = k.OriginatingDepartmentId
    WHERE k.Decision = 'CANDIDATE';

    -- Department unknown / inactive: Customer Services / General - the existing human classification queue.
    UPDATE k SET Decision = 'APPLY', TargetDeptCode = g.Code, NewCategoryId = g.CategoryId, NewRequestTypeId = g.RequestTypeId, NewWorkflowTemplateId = g.WorkflowTemplateId
    FROM @Cls k CROSS JOIN @Gen g
    WHERE k.Decision = 'CANDIDATE' AND g.DepartmentId = @CsId;

    SELECT @ClsApply = COUNT(*) FROM @Cls WHERE Decision = 'APPLY';
    SELECT @ClsGap   = COUNT(*) FROM @Cls WHERE Decision IN ('DEPT_GENERAL_MISSING', 'CONFIG_MISSING_CS');

    IF @DryRun = 0 AND @RunClassification = 1 AND @ClsApply <> @ApprovedClassificationCount
    BEGIN
        SET @msg = N'ABORTED: classification would change ' + CONVERT(nvarchar(10), @ClsApply) + N' ticket(s) but @ApprovedClassificationCount = '
                 + CONVERT(nvarchar(10), @ApprovedClassificationCount) + N'. The data changed since the approved dry run - re-run it and re-approve.';
        THROW 50811, @msg, 1;
    END;

    -- Writes: category, request type and pinned workflow ONLY. Guarded by RowVersion AND the unchanged classification, so a concurrent edit wins.
    IF @ClsApply > 0
    BEGIN
        UPDATE t SET CategoryId = k.NewCategoryId, RequestTypeId = k.NewRequestTypeId, WorkflowTemplateId = k.NewWorkflowTemplateId
        FROM dbo.Tickets t JOIN @Cls k ON k.TicketId = t.TicketId AND t.RowVersion = k.RowVer
        WHERE k.Decision = 'APPLY'
          AND ((t.CategoryId IS NULL AND k.OldCategoryId IS NULL) OR t.CategoryId = k.OldCategoryId)
          AND ((t.RequestTypeId IS NULL AND k.OldRequestTypeId IS NULL) OR t.RequestTypeId = k.OldRequestTypeId);
        SET @Updated = @@ROWCOUNT;
        IF @Updated <> @ClsApply
        BEGIN
            SET @msg = N'ABORTED: ' + CONVERT(nvarchar(10), @ClsApply - @Updated) + N' ticket(s) changed while the run was in progress (classification / rowversion). Whole run rolled back.';
            THROW 50812, @msg, 1;
        END;

        INSERT dbo.AuditEntries (ActorEmployeeId, Action, EntityType, EntityId, BeforeValue, AfterValue, CorrelationId, OccurredAtUtc)
        SELECT NULL, N'ApplyDefaultClassification', N'Ticket', CONVERT(nvarchar(100), k.TicketId),
               N'CategoryId=' + ISNULL(CONVERT(nvarchar(20), k.OldCategoryId), N'(none)') + N';RequestTypeId=' + ISNULL(CONVERT(nvarchar(20), k.OldRequestTypeId), N'(none)')
                 + N';WorkflowTemplateId=' + ISNULL(CONVERT(nvarchar(20), k.OldWorkflowTemplateId), N'(none)'),
               N'CategoryId=' + CONVERT(nvarchar(20), k.NewCategoryId) + N';RequestTypeId=' + CONVERT(nvarchar(20), k.NewRequestTypeId)
                 + N';WorkflowTemplateId=' + CONVERT(nvarchar(20), k.NewWorkflowTemplateId) + N';Default=' + k.TargetDeptCode + N'/General;State=' + k.State
                 + N';Reason=Historical backfill;BatchId=' + LOWER(CONVERT(nvarchar(36), @BatchId))
                 + N';DepartmentId=' + CONVERT(nvarchar(20), k.CurrentDepartmentId) + N' (unchanged);Status=' + CONVERT(nvarchar(10), k.TicketStatus) + N' (unchanged)',
               @BatchId, @Now
        FROM @Cls k WHERE k.Decision = 'APPLY';

        INSERT @Log (TicketId, Phase, Action, Detail)
        SELECT k.TicketId, 'CLASSIFICATION', N'ApplyDefaultClassification',
               k.State + N': Category ' + ISNULL(CONVERT(nvarchar(20), k.OldCategoryId), N'-') + N' -> ' + CONVERT(nvarchar(20), k.NewCategoryId)
               + N', RequestType ' + ISNULL(CONVERT(nvarchar(20), k.OldRequestTypeId), N'-') + N' -> ' + CONVERT(nvarchar(20), k.NewRequestTypeId)
               + N', Workflow ' + ISNULL(CONVERT(nvarchar(20), k.OldWorkflowTemplateId), N'-') + N' -> ' + CONVERT(nvarchar(20), k.NewWorkflowTemplateId)
               + N' (' + k.TargetDeptCode + N'/General)'
        FROM @Cls k WHERE k.Decision = 'APPLY';
    END;

    ------------------------------------------------------------------------------------------------------------------------
    -- PHASE 2 - CS AGENT ASSIGNMENT (ACTIVE, UNOWNED tickets only)
    ------------------------------------------------------------------------------------------------------------------------
    IF @RunAssignment = 1
    BEGIN
        IF @CcCount <> 1 THROW 50813, 'ABORTED: expected exactly one ACTIVE department with Code = CC (the shared CS Agent pool).', 1;

        -- the rotation cursor is locked exactly like AssignmentRotationRepository.GetForUpdateAsync (live intake waits behind this run)
        IF NOT EXISTS (SELECT 1 FROM dbo.DepartmentAssignmentRotations WITH (UPDLOCK, HOLDLOCK) WHERE DepartmentId = @CcId)
            INSERT dbo.DepartmentAssignmentRotations (DepartmentId, LastAssignedEmployeeId, UpdatedAtUtc) VALUES (@CcId, NULL, @Now);
        SELECT @Cursor = LastAssignedEmployeeId FROM dbo.DepartmentAssignmentRotations WITH (UPDLOCK, ROWLOCK) WHERE DepartmentId = @CcId;

        -- Eligible agents = FilterEligibleAsync for the Call Center: active, member of the Call Center, not locked out, not a service identity, role CS Agent
        -- (TicketRoleSets.CallCenterAutoAssignable). Order = DepartmentAssignmentRotation.PickNext (Guid order == lower-case hex string order).
        INSERT @Agents (Rn, EmployeeId, SortKey)
        SELECT ROW_NUMBER() OVER (ORDER BY CONVERT(char(36), e.EmployeeId) COLLATE Latin1_General_BIN2), e.EmployeeId, CONVERT(char(36), e.EmployeeId) COLLATE Latin1_General_BIN2
        FROM dbo.Employees e WITH (UPDLOCK, ROWLOCK)          -- an agent cannot be deactivated while the run is in flight
        JOIN dbo.AspNetUsers u ON u.Id = e.EmployeeId
        WHERE e.DeactivatedAtUtc IS NULL
          AND EXISTS (SELECT 1 FROM dbo.UserDepartmentAssignments a WHERE a.EmployeeId = e.EmployeeId AND a.DepartmentId = @CcId)
          AND NOT (u.LockoutEnabled = 1 AND u.LockoutEnd IS NOT NULL AND u.LockoutEnd > SYSDATETIMEOFFSET())
          AND NOT EXISTS (SELECT 1 FROM @ServiceIdentity s WHERE s.EmployeeId = e.EmployeeId)
          AND EXISTS (SELECT 1 FROM dbo.AspNetUserRoles ur JOIN dbo.AspNetRoles r ON r.Id = ur.RoleId WHERE ur.UserId = e.EmployeeId AND r.Name = N'CS Agent');
        SET @N = (SELECT COUNT(*) FROM @Agents);

        -- Evidence per active unowned ticket (UnassignedTicketEvidence) -> CsAgentBackfillClassifier.Classify. The stale-AI rule is OFF (the Production default).
        INSERT @Asg (TicketId, TicketNumber, TicketStatus, CurrentDepartmentId, CreatedAtUtc, RowVer, PendingApproval, IsCollections, HumanNeeded, HasGenesys, HasLiveGenesys,
                     HandlerId, Class)
        SELECT t.TicketId, t.TicketNumber, t.TicketStatus, t.CurrentDepartmentId, t.CreatedAtUtc, t.RowVersion,
               CASE WHEN EXISTS (SELECT 1 FROM dbo.TicketApprovals a WHERE a.TicketId = t.TicketId AND a.Status = 1) THEN 1 ELSE 0 END,
               CASE WHEN t.CurrentDepartmentId = @ColId THEN 1 ELSE 0 END,
               CASE WHEN EXISTS (SELECT 1 FROM dbo.TicketInteractions i WHERE i.TicketId = t.TicketId AND i.RequiresHumanFollowUp = 1)
                      OR EXISTS (SELECT 1 FROM dbo.TicketAgentHandoffs h WHERE h.TicketId = t.TicketId AND h.Status IN (2, 3, 4))
                      OR t.FirstHumanResponseAtUtc IS NOT NULL
                      OR EXISTS (SELECT 1 FROM dbo.TicketInteractions i JOIN dbo.TicketInteractionMessages m ON m.TicketInteractionId = i.TicketInteractionId
                                 WHERE i.TicketId = t.TicketId AND m.Sender = 2)
                      OR EXISTS (SELECT 1 FROM dbo.TicketInteractionHandlers h WHERE h.TicketId = t.TicketId)
                      OR EXISTS (SELECT 1 FROM dbo.TicketInteractions i WHERE i.TicketId = t.TicketId AND i.HandledByUserId IS NOT NULL) THEN 1 ELSE 0 END,
               CASE WHEN EXISTS (SELECT 1 FROM dbo.TicketInteractions i WHERE i.TicketId = t.TicketId AND i.Source = 2) THEN 1 ELSE 0 END,
               CASE WHEN EXISTS (SELECT 1 FROM dbo.TicketInteractions i WHERE i.TicketId = t.TicketId AND i.Source = 2 AND i.EndedAtUtc IS NULL AND i.InactivityClosedAtUtc IS NULL) THEN 1 ELSE 0 END,
               -- the agent who actually answered: latest handler row, else the interaction's resolved handler, else the first mapped person who wrote a HumanAgent line
               COALESCE(
                 (SELECT TOP (1) h.HandledByUserId FROM dbo.TicketInteractionHandlers h WHERE h.TicketId = t.TicketId ORDER BY COALESCE(h.ConnectedAtUtc, h.RecordedAtUtc) DESC),
                 (SELECT TOP (1) i.HandledByUserId FROM dbo.TicketInteractions i WHERE i.TicketId = t.TicketId AND i.HandledByUserId IS NOT NULL ORDER BY i.TicketInteractionId),
                 (SELECT TOP (1) u.Id FROM dbo.TicketInteractions i
                    JOIN dbo.TicketInteractionMessages m ON m.TicketInteractionId = i.TicketInteractionId AND m.Sender = 2 AND m.SenderId IS NOT NULL
                    JOIN dbo.AspNetUsers u ON u.GenesysUserId = LTRIM(RTRIM(m.SenderId))
                  WHERE i.TicketId = t.TicketId ORDER BY m.SentAtUtc)),
               'PENDING'
        FROM dbo.Tickets t
        WHERE t.TicketStatus BETWEEN 1 AND 4 AND t.CurrentOwnerEmployeeId IS NULL;

        -- CsAgentBackfillClassifier.Classify, in the same order as the application.
        UPDATE @Asg SET Class =
            CASE WHEN IsCollections = 1 THEN 'COLLECTIONS'
                 WHEN PendingApproval = 1 THEN 'PENDING_APPROVAL'
                 WHEN CreatedAtUtc > DATEADD(minute, -@MinTicketAgeMinutes, @Now) THEN 'TOO_NEW'
                 WHEN HumanNeeded = 1 THEN 'ELIGIBLE'
                 WHEN HasGenesys = 1 AND HasLiveGenesys = 1 THEN 'ACTIVE_AI'
                 WHEN HasGenesys = 1 AND @IncludeAiEndedWithoutHuman = 0 THEN 'AI_ENDED_NO_HUMAN'
                 ELSE 'ELIGIBLE' END;

        -- Call Center workflow settings may disable assignment: then the engine assigns nobody, and neither do we.
        IF EXISTS (SELECT 1 FROM dbo.DepartmentWorkflowSettings WHERE DepartmentId = @CcId AND AllowAssignment = 0)
            UPDATE @Asg SET Class = 'ASSIGNMENT_DISABLED' WHERE Class = 'ELIGIBLE';
        IF @N = 0
            UPDATE @Asg SET Class = 'NO_ELIGIBLE_AGENT' WHERE Class = 'ELIGIBLE';

        -- Who: the confirmed handler when they are an eligible CS Agent (cursor does not move), else the next agent of the shared rotation, oldest first.
        UPDATE a SET Source = 'ConfirmedHandler', AgentId = a.HandlerId
        FROM @Asg a WHERE a.Class = 'ELIGIBLE' AND a.HandlerId IS NOT NULL AND EXISTS (SELECT 1 FROM @Agents g WHERE g.EmployeeId = a.HandlerId);

        UPDATE a SET Source = 'RoundRobin', RotationSeq = q.Seq
        FROM @Asg a JOIN (SELECT TicketId, ROW_NUMBER() OVER (ORDER BY CreatedAtUtc, TicketId) AS Seq FROM @Asg WHERE Class = 'ELIGIBLE' AND Source IS NULL) q ON q.TicketId = a.TicketId;

        IF @Cursor IS NOT NULL AND @N > 0
            SELECT @Start = ISNULL(MIN(Rn), 1) FROM @Agents WHERE SortKey > CONVERT(char(36), @Cursor) COLLATE Latin1_General_BIN2;

        UPDATE a SET AgentId = g.EmployeeId
        FROM @Asg a JOIN @Agents g ON g.Rn = ((@Start - 1 + a.RotationSeq - 1) % NULLIF(@N, 0)) + 1
        WHERE a.Source = 'RoundRobin';

        SELECT @AsgApply    = COUNT(*) FROM @Asg WHERE AgentId IS NOT NULL;
        SELECT @AsgHandler  = COUNT(*) FROM @Asg WHERE Source = 'ConfirmedHandler';
        SELECT @AsgRotation = COUNT(*) FROM @Asg WHERE Source = 'RoundRobin';
        SELECT @AsgNoAgent  = COUNT(*) FROM @Asg WHERE Class IN ('NO_ELIGIBLE_AGENT', 'ASSIGNMENT_DISABLED');
        SELECT TOP (1) @LastAgent = AgentId FROM @Asg WHERE Source = 'RoundRobin' ORDER BY RotationSeq DESC;

        IF @DryRun = 0 AND @AsgApply <> @ApprovedAssignmentCount
        BEGIN
            SET @msg = N'ABORTED: assignment would change ' + CONVERT(nvarchar(10), @AsgApply) + N' ticket(s) but @ApprovedAssignmentCount = '
                     + CONVERT(nvarchar(10), @ApprovedAssignmentCount) + N'. The data changed since the approved dry run - re-run it and re-approve.';
            THROW 50814, @msg, 1;
        END;

        IF @AsgApply > 0
        BEGIN
            -- Guarded exactly like phase 1: still unowned, still active, same department, RowVersion unchanged since selection (@Asg is read AFTER phase 1's
            -- writes in this same transaction, so a ticket classified a moment ago carries its fresh RowVersion).
            UPDATE t SET CurrentOwnerEmployeeId = a.AgentId
            FROM dbo.Tickets t JOIN @Asg a ON a.TicketId = t.TicketId AND t.RowVersion = a.RowVer
            WHERE a.AgentId IS NOT NULL AND t.CurrentOwnerEmployeeId IS NULL AND t.TicketStatus BETWEEN 1 AND 4
              AND t.CurrentDepartmentId = a.CurrentDepartmentId;
            SET @Updated = @@ROWCOUNT;
            IF @Updated <> @AsgApply
            BEGIN
                SET @msg = N'ABORTED: ' + CONVERT(nvarchar(10), @AsgApply - @Updated) + N' ticket(s) changed while the run was in progress (owner / status / department / rowversion). Whole run rolled back.';
                THROW 50815, @msg, 1;
            END;

            -- previous current assignment rows are KEPT as history (IsCurrent -> 0, exactly TicketAssignment.MarkSuperseded)
            UPDATE x SET IsCurrent = 0
            FROM dbo.TicketAssignments x JOIN @Asg a ON a.TicketId = x.TicketId WHERE a.AgentId IS NOT NULL AND x.IsCurrent = 1;

            INSERT dbo.TicketAssignments (TicketId, AssignedEmployeeId, AssignedDepartmentId, AssignedAtUtc, AssigningActorEmployeeId, IsCurrent)
            SELECT a.TicketId, a.AgentId, a.CurrentDepartmentId, @Now, NULL, 1 FROM @Asg a WHERE a.AgentId IS NOT NULL;   -- NULL actor = the system, like AutoAssign
            SET @Inserted = @@ROWCOUNT;
            IF @Inserted <> @AsgApply THROW 50816, 'ABORTED: assignment rows inserted differ from the plan.', 1;

            INSERT dbo.AuditEntries (ActorEmployeeId, Action, EntityType, EntityId, BeforeValue, AfterValue, CorrelationId, OccurredAtUtc)
            SELECT NULL, N'AutoAssign', N'Ticket', CONVERT(nvarchar(100), a.TicketId), N'Unassigned',
                   N'AssignedEmployeeId=' + LOWER(CONVERT(nvarchar(36), a.AgentId)) + N';DepartmentId=' + CONVERT(nvarchar(20), a.CurrentDepartmentId)
                   + CASE WHEN a.Source = 'ConfirmedHandler' THEN N';Mode=ConfirmedHandler'
                          ELSE N';Mode=RoundRobin;Pool=SharedCsAgents;PoolDepartmentId=' + CONVERT(nvarchar(20), @CcId) END
                   + N';Trigger=Backfill;BatchId=' + LOWER(CONVERT(nvarchar(36), @BatchId)),
                   @BatchId, @Now
            FROM @Asg a WHERE a.AgentId IS NOT NULL;

            INSERT dbo.AuditEntries (ActorEmployeeId, Action, EntityType, EntityId, BeforeValue, AfterValue, CorrelationId, OccurredAtUtc)
            SELECT NULL, N'BackfillAssign', N'Ticket', CONVERT(nvarchar(100), a.TicketId), N'Unassigned',
                   N'AssignedEmployeeId=' + LOWER(CONVERT(nvarchar(36), a.AgentId)) + N';DepartmentId=' + CONVERT(nvarchar(20), a.CurrentDepartmentId)
                   + N';Status=' + CONVERT(nvarchar(10), a.TicketStatus) + N';Source=' + a.Source + N';BatchId=' + LOWER(CONVERT(nvarchar(36), @BatchId)),
                   @BatchId, @Now
            FROM @Asg a WHERE a.AgentId IS NOT NULL;

            INSERT @Log (TicketId, Phase, Action, Detail)
            SELECT a.TicketId, 'ASSIGNMENT', N'AutoAssign / BackfillAssign',
                   N'Owner none -> ' + LOWER(CONVERT(nvarchar(36), a.AgentId)) + N' (' + a.Source + N'); department ' + CONVERT(nvarchar(20), a.CurrentDepartmentId) + N' and status '
                   + CONVERT(nvarchar(10), a.TicketStatus) + N' unchanged'
            FROM @Asg a WHERE a.AgentId IS NOT NULL;

            IF @LastAgent IS NOT NULL
                UPDATE dbo.DepartmentAssignmentRotations SET LastAssignedEmployeeId = @LastAgent, UpdatedAtUtc = @Now WHERE DepartmentId = @CcId;
        END;

        -- re-checks after the writes: nothing planned may have become a held-back ticket, and every new owner must STILL be an eligible agent.
        IF EXISTS (SELECT 1 FROM @Asg a WHERE a.AgentId IS NOT NULL AND EXISTS (SELECT 1 FROM dbo.TicketApprovals ap WHERE ap.TicketId = a.TicketId AND ap.Status = 1))
            THROW 50817, 'ABORTED: a planned ticket gained a PENDING APPROVAL while the run was in progress. Whole run rolled back - re-run the dry run.', 1;
        IF EXISTS (SELECT 1 FROM @Asg a WHERE a.AgentId IS NOT NULL AND a.HumanNeeded = 0 AND a.HasGenesys = 1 AND a.HasLiveGenesys = 1)
            THROW 50818, 'ABORTED: internal check failed - a live AI conversation was planned for assignment.', 1;
        IF EXISTS (SELECT 1 FROM @Asg a WHERE a.AgentId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM @Agents g WHERE g.EmployeeId = a.AgentId))
            THROW 50819, 'ABORTED: internal check failed - a planned owner is not an eligible CS Agent.', 1;
    END;

    -- one batch audit row (holds the cursor pre-image a rollback needs)
    IF @ClsApply + @AsgApply > 0
        INSERT dbo.AuditEntries (ActorEmployeeId, Action, EntityType, EntityId, BeforeValue, AfterValue, CorrelationId, OccurredAtUtc)
        VALUES (NULL, N'HistoricalBackfillBatch', N'DepartmentAssignmentRotation', CONVERT(nvarchar(100), ISNULL(@CcId, 0)),
                ISNULL(LOWER(CONVERT(nvarchar(36), @Cursor)), N'none'),
                N'Classified=' + CONVERT(nvarchar(20), @ClsApply) + N';Assigned=' + CONVERT(nvarchar(20), @AsgApply) + N';ConfirmedHandler=' + CONVERT(nvarchar(20), @AsgHandler)
                + N';RoundRobin=' + CONVERT(nvarchar(20), @AsgRotation) + N';LastAssignedEmployeeId=' + ISNULL(LOWER(CONVERT(nvarchar(36), ISNULL(@LastAgent, @Cursor))), N'none')
                + N';BatchId=' + LOWER(CONVERT(nvarchar(36), @BatchId)),
                @BatchId, @Now);

    ------------------------------------------------------------------------------------------------------------------------
    -- INVARIANTS (report 8): nothing outside the intended columns / tables changed
    ------------------------------------------------------------------------------------------------------------------------
    DECLARE @cnt bigint;
    DECLARE chk CURSOR LOCAL FAST_FORWARD FOR SELECT TableName FROM @Counts;
    OPEN chk; FETCH NEXT FROM chk INTO @t;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        SET @sql = N'SELECT @c = COUNT_BIG(*) FROM dbo.' + QUOTENAME(@t) + N';';
        EXEC sp_executesql @sql, N'@c bigint OUTPUT', @c = @cnt OUTPUT;
        UPDATE @Counts SET RowsAfter = @cnt WHERE TableName = @t;
        FETCH NEXT FROM chk INTO @t;
    END;
    CLOSE chk; DEALLOCATE chk;

    SELECT @ProtectedAfter = CHECKSUM_AGG(CHECKSUM(TicketId, TicketNumber, TicketStatus, CurrentDepartmentId, OriginatingDepartmentId, PriorityId, SlaState,
                                                   EscalationLevel, CreatedAtUtc, FirstHumanResponseAtUtc))
    FROM dbo.Tickets;

    INSERT @Check VALUES (N'Status, department, originating department, priority, SLA state, escalation, creation / first-response timestamps unchanged for every ticket',
        CASE WHEN ISNULL(@ProtectedBefore, 0) = ISNULL(@ProtectedAfter, 0) THEN 'PASS' ELSE 'FAIL' END, NULL);
    INSERT @Check SELECT N'Table not written: ' + TableName, CASE WHEN RowsBefore = RowsAfter THEN 'PASS' ELSE 'FAIL' END, CONVERT(nvarchar(40), RowsBefore) + N' -> ' + CONVERT(nvarchar(40), RowsAfter)
                  FROM @Counts WHERE MustBeUnchanged = 1;
    INSERT @Check SELECT N'TicketAssignments grew by exactly the assignments written', CASE WHEN a.RowsAfter - a.RowsBefore = @AsgApply THEN 'PASS' ELSE 'FAIL' END,
                  CONVERT(nvarchar(40), a.RowsAfter - a.RowsBefore) + N' new / ' + CONVERT(nvarchar(20), @AsgApply) + N' planned' FROM @Counts a WHERE a.TableName = N'TicketAssignments';
    INSERT @Check SELECT N'Exactly one current assignment per assigned ticket', CASE WHEN NOT EXISTS (
                      SELECT 1 FROM @Asg a WHERE a.AgentId IS NOT NULL AND (SELECT COUNT(*) FROM dbo.TicketAssignments x WHERE x.TicketId = a.TicketId AND x.IsCurrent = 1) <> 1) THEN 'PASS' ELSE 'FAIL' END, NULL;
    INSERT @Check SELECT N'Every classified ticket now carries its General pair (Category / RequestType / Workflow)', CASE WHEN NOT EXISTS (
                      SELECT 1 FROM @Cls k JOIN dbo.Tickets t ON t.TicketId = k.TicketId WHERE k.Decision = 'APPLY'
                        AND (t.CategoryId <> k.NewCategoryId OR t.RequestTypeId <> k.NewRequestTypeId OR t.WorkflowTemplateId <> k.NewWorkflowTemplateId)) THEN 'PASS' ELSE 'FAIL' END, NULL;
    INSERT @Check SELECT N'No Closed / Resolved ticket received an owner', CASE WHEN NOT EXISTS (
                      SELECT 1 FROM @Asg a JOIN dbo.Tickets t ON t.TicketId = a.TicketId WHERE a.AgentId IS NOT NULL AND t.TicketStatus IN (5, 6)) THEN 'PASS' ELSE 'FAIL' END, NULL;

    IF EXISTS (SELECT 1 FROM @Check WHERE Result = 'FAIL')
        THROW 50820, 'ABORTED: an invariant check failed (report 8 of a dry run lists it). Whole run rolled back.', 1;

    IF @DryRun = 0 COMMIT TRANSACTION; ELSE ROLLBACK TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    DECLARE @err nvarchar(2000) = N'ROLLED BACK - nothing was changed. ' + ERROR_MESSAGE();
    THROW 50899, @err, 1;
END CATCH;

PRINT CASE WHEN @DryRun = 1 THEN N'DRY RUN - every write and check was performed, then ROLLED BACK. Review the reports below.' ELSE N'COMMITTED.' END;

-- ======================================================================================================================
-- REPORTS (read from table variables, so a dry run shows exactly what it would have written)
-- ======================================================================================================================
-- 1. Run summary and the commit gates (1b below: the same counts by ticket status)
SELECT CASE WHEN @DryRun = 1 THEN 'DRY RUN (rolled back)' ELSE 'COMMITTED' END AS Mode, DB_NAME() AS [Database], @BatchId AS BatchId,
       @ClsApply AS ClassificationToApply, @ClsGap AS ClassificationBlockedByConfiguration,
       @AsgApply AS AssignmentToApply, @AsgHandler AS AssignmentToConfirmedHandler, @AsgRotation AS AssignmentToSharedRotation, @AsgNoAgent AS AssignmentBlockedNoAgent,
       @N AS EligibleCsAgents, @Cursor AS RotationCursorBefore, ISNULL(@LastAgent, @Cursor) AS RotationCursorAfter,
       (SELECT COUNT(*) FROM @Check WHERE Result = 'FAIL') AS FailedChecks;

-- 1b. Counts by status (1 Open, 2 InProgress, 3 PendingCustomer, 4 PendingThirdParty, 5 Resolved, 6 Closed)
SELECT x.Category, CASE x.TicketStatus WHEN 1 THEN 'Open' WHEN 2 THEN 'InProgress' WHEN 3 THEN 'PendingCustomer' WHEN 4 THEN 'PendingThirdParty'
                                       WHEN 5 THEN 'Resolved' WHEN 6 THEN 'Closed' END AS Status, COUNT(*) AS Tickets
FROM (
    SELECT 'A. Classification correction (will change)' AS Category, TicketStatus FROM @Cls WHERE Decision = 'APPLY'
    UNION ALL SELECT 'B. Assignment (will change)', TicketStatus FROM @Asg WHERE AgentId IS NOT NULL
    UNION ALL SELECT 'C. Manual review: classification blocked (department has no usable General / CS General)', TicketStatus FROM @Cls WHERE Decision IN ('DEPT_GENERAL_MISSING', 'CONFIG_MISSING_CS')
    UNION ALL SELECT 'C. Manual review: assignment blocked (no eligible CS Agent / assignment disabled)', TicketStatus FROM @Asg WHERE Class IN ('NO_ELIGIBLE_AGENT', 'ASSIGNMENT_DISABLED')
    UNION ALL SELECT 'D. Left alone: category-only classification', TicketStatus FROM @Cls WHERE Decision = 'CATEGORY_ONLY_KEPT'
    UNION ALL SELECT 'D. Left alone: unowned but held back (' + Class + ')', TicketStatus FROM @Asg WHERE AgentId IS NULL AND Class IN ('PENDING_APPROVAL', 'ACTIVE_AI', 'TOO_NEW', 'AI_ENDED_NO_HUMAN', 'COLLECTIONS')
) x GROUP BY x.Category, x.TicketStatus ORDER BY x.Category, x.TicketStatus;

-- 2. Configuration: Customer Services default and every department's own General
SELECT Code AS Department, CASE WHEN Gap IS NULL THEN 'OK' ELSE Gap END AS GeneralConfiguration, CategoryId, RequestTypeId, WorkflowTemplateId,
       CASE WHEN DepartmentId = @CsId THEN 'Customer Services (last-resort default)' ELSE '' END AS Note
FROM @Gen ORDER BY Code;

-- 3. Classification: what each state / status turns into
SELECT State, TicketStatus, Decision, COUNT(*) AS Tickets FROM @Cls WHERE Decision NOT IN ('ALREADY_VALID', 'ALREADY_DEFAULT', 'PHASE_OFF')
GROUP BY State, TicketStatus, Decision ORDER BY Decision, State, TicketStatus;

-- 4. Classification: proposed changes (APPLY) and blocked tickets, oldest first
SELECT TOP (5000) k.TicketId, k.TicketNumber, k.TicketStatus, d.Code AS CurrentDepartment, o.Code AS OriginatingDepartment, k.State,
       k.OldCategoryId, k.OldRequestTypeId, k.NewCategoryId, k.NewRequestTypeId, k.NewWorkflowTemplateId, k.TargetDeptCode AS NewClassificationDepartment, k.Decision
FROM @Cls k JOIN dbo.Departments d ON d.DepartmentId = k.CurrentDepartmentId JOIN dbo.Departments o ON o.DepartmentId = k.OriginatingDepartmentId
WHERE k.Decision IN ('APPLY', 'DEPT_GENERAL_MISSING', 'CONFIG_MISSING_CS') ORDER BY k.Decision, k.TicketId;

-- 5. Assignment: classes (active, unowned tickets only)
SELECT Class, ISNULL(Source, '-') AS Source, TicketStatus, COUNT(*) AS Tickets FROM @Asg GROUP BY Class, Source, TicketStatus ORDER BY Class, Source, TicketStatus;

-- 6. Assignment: proposed changes, in the order the rotation deals them
SELECT TOP (5000) a.TicketId, a.TicketNumber, a.TicketStatus, d.Code AS Department, a.CreatedAtUtc, a.Source, a.AgentId AS ProposedOwner, u.UserName AS ProposedOwnerUserName
FROM @Asg a JOIN dbo.Departments d ON d.DepartmentId = a.CurrentDepartmentId LEFT JOIN dbo.AspNetUsers u ON u.Id = a.AgentId
WHERE a.AgentId IS NOT NULL ORDER BY a.Source, a.RotationSeq, a.TicketId;

-- 7. Per agent: how many each eligible CS Agent would receive
SELECT g.EmployeeId, u.UserName, (SELECT COUNT(*) FROM @Asg a WHERE a.AgentId = g.EmployeeId) AS TicketsProposed
FROM @Agents g JOIN dbo.AspNetUsers u ON u.Id = g.EmployeeId ORDER BY g.Rn;

-- 8. Invariants proven inside the transaction
SELECT CheckName, Result, Detail FROM @Check ORDER BY Result DESC, CheckName;

-- 9. REVIEW ONLY - never changed by this script.
--    9a. Active tickets already owned by someone who is deactivated or not a CS Agent / Call Center member: an existing assignment is never redistributed.
SELECT d.Code AS Department, t.TicketStatus, COUNT(*) AS TicketsOwnedByDeactivatedOrNonCsAgent
FROM dbo.Tickets t JOIN dbo.Departments d ON d.DepartmentId = t.CurrentDepartmentId
LEFT JOIN dbo.Employees e ON e.EmployeeId = t.CurrentOwnerEmployeeId
WHERE t.TicketStatus BETWEEN 1 AND 4 AND t.CurrentOwnerEmployeeId IS NOT NULL AND t.CurrentDepartmentId <> ISNULL(@ColId, -1)
  AND (e.EmployeeId IS NULL OR e.DeactivatedAtUtc IS NOT NULL)
GROUP BY d.Code, t.TicketStatus ORDER BY d.Code, t.TicketStatus;
--    9b. Active tickets whose request type belongs to a different department than the one holding them (department is never changed here).
SELECT TOP (500) t.TicketId, t.TicketNumber, t.TicketStatus, cd.Code AS CurrentDepartment, rd.Code AS RequestTypeDepartment, t.RequestTypeId
FROM dbo.Tickets t JOIN dbo.RequestTypes r ON r.RequestTypeId = t.RequestTypeId
JOIN dbo.Departments cd ON cd.DepartmentId = t.CurrentDepartmentId JOIN dbo.Departments rd ON rd.DepartmentId = r.DepartmentId
WHERE t.TicketStatus BETWEEN 1 AND 4 AND r.DepartmentId <> t.CurrentDepartmentId AND r.DepartmentId <> ISNULL(@ColId, -1) AND t.CurrentDepartmentId <> ISNULL(@ColId, -1)
  AND LTRIM(RTRIM(r.Name)) COLLATE Latin1_General_CI_AS <> N'General'
ORDER BY t.TicketId;

-- 10. Modification log: every change this run makes / made (mirrors the AuditEntries rows with CorrelationId = BatchId)
SELECT Seq, TicketId, Phase, Action, Detail FROM @Log ORDER BY Seq;
