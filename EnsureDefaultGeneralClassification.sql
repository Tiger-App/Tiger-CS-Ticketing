/*
================================================================================
 EnsureDefaultGeneralClassification.sql        (idempotent configuration change)

 Makes sure the approved default classification exists:
   * for EVERY active department except Collections (COL): a General category and a General request type with workflow '<CODE>-GENERAL-001'
     (version 1 Published), so a ticket whose department is known is classified General / General WITHIN that department;
   * Customer Services (Code = 'CS') is REQUIRED: it is the last-resort default when no valid department is known.

 SAFE BY CONSTRUCTION
   * Reuses an existing General category / request type of a department. A second run creates nothing.
   * Additive: no UPDATE, no DELETE, no ticket is touched. Never creates a department. Collections is never given a General.
   * DEDICATED WORKFLOW PER DEPARTMENT: every General request type gets its OWN workflow with the code '<CODE>-GENERAL-001'
     (CS -> CS-GENERAL-001). The business-review workflows (e.g. Production WorkflowId 4 = CS-GEN-001 'General Inquiry') and their request types are
     NEVER reused, shared or modified by this script.
   * If '<CODE>-GENERAL-001' already exists it is INSPECTED and reused only when compatible with a General flow:
       active workflow; exactly one Published template that belongs to it and is active; no approval required / approval steps; first step Created (1),
       an Assigned step (2), a Resolved step (8), last step Closed (9); and every request type already using it belongs to the SAME department.
     Reuse creates only the missing General request type / category. An incompatible workflow is never modified: the department is reported and skipped.
   * A department with duplicate General rows, a General request type whose workflow has no Published version, or when its dedicated
     workflow code is in use by an incompatible workflow is REPORTED and SKIPPED (nothing written for it). If that department is CS the script stops with an error.
   * Section 1 below is a READ-ONLY inspection of CS-GEN-001 (left untouched) and CS-GENERAL-001 (workflow, templates, steps, request types) so the result can be reviewed first.
   * No assignment rule and no SLA policy row is created: tickets get a CS Agent through the existing confirmed-agent / sequential Round-Robin rules.
   * Dry run: set @CommitChanges = 0 - everything is applied inside the transaction, the result is printed, then rolled back.

 It is the SQL equivalent of GeneralFallbackImporter.ImportAllAsync. It is NOT executed by the application, a scheduler or CI.
 Status 2 = Published. Step kinds (WorkflowStepKind): 1 Created, 2 Assigned, 5 InProgress, 8 Resolved, 9 Closed.

 RUNNING IT
     sqlcmd -S <server> -d <database> -U <user> -P <password> -C -b -I -i EnsureDefaultGeneralClassification.sql
================================================================================
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @CommitChanges bit = 0;     -- 0 = dry run (rolled back). Set to 1 only after approval.

IF NOT EXISTS (SELECT 1 FROM [Departments] WHERE [Code] = N'CS')
    THROW 50700, N'ABORTED: the Customer Services department (Code = CS) does not exist. It is never created by this script.', 1;

-- 1. READ-ONLY INSPECTION of the workflow code in question (no writes).
SELECT N'WORKFLOW' AS [Section], w.[WorkflowId], w.[Code], w.[Name], w.[IsActive] FROM [Workflows] w WHERE w.[Code] IN (N'CS-GEN-001', N'CS-GENERAL-001');
SELECT N'TEMPLATE' AS [Section], t.[WorkflowTemplateId], t.[WorkflowId], t.[Code], t.[VersionNumber], t.[Status], t.[IsActive], t.[RequiresApproval]
FROM [WorkflowTemplates] t WHERE t.[Code] IN (N'CS-GEN-001', N'CS-GENERAL-001') OR t.[WorkflowId] IN (SELECT [WorkflowId] FROM [Workflows] WHERE [Code] IN (N'CS-GEN-001', N'CS-GENERAL-001'));
SELECT N'STEP' AS [Section], s.[WorkflowTemplateId], s.[Sequence], s.[Name], s.[Kind], s.[IsOptional], s.[ApprovalType]
FROM [WorkflowTemplateSteps] s WHERE s.[WorkflowTemplateId] IN (SELECT [WorkflowTemplateId] FROM [WorkflowTemplates] WHERE [Code] IN (N'CS-GEN-001', N'CS-GENERAL-001')) ORDER BY s.[WorkflowTemplateId], s.[Sequence];
SELECT N'REQUESTTYPE' AS [Section], r.[RequestTypeId], r.[DepartmentId], d.[Code] AS DepartmentCode, r.[Name], r.[WorkflowId], r.[IsActive]
FROM [RequestTypes] r JOIN [Departments] d ON d.[DepartmentId] = r.[DepartmentId]
WHERE r.[WorkflowId] IN (SELECT [WorkflowId] FROM [Workflows] WHERE [Code] IN (N'CS-GEN-001', N'CS-GENERAL-001'));

CREATE TABLE #Report (DepartmentCode nvarchar(50), Result nvarchar(400), CategoryId int NULL, RequestTypeId int NULL, WorkflowId int NULL);

DECLARE @Now datetime2 = SYSUTCDATETIME();
DECLARE @DepartmentId int, @DepartmentCode nvarchar(50), @DepartmentName nvarchar(200);
DECLARE @CategoryId int, @RequestTypeId int, @WorkflowId int, @TemplateId int, @Code nvarchar(50), @WorkflowName nvarchar(200), @Description nvarchar(1000);
DECLARE @Attempt int, @ExistingWorkflowId int, @ExistingTemplateId int, @Compatible bit, @WhyNot nvarchar(200), @Reused bit;

BEGIN TRANSACTION;

DECLARE dept_cursor CURSOR LOCAL FAST_FORWARD FOR
    SELECT [DepartmentId], [Code], [Name] FROM [Departments] WHERE [IsActive] = 1 AND [Code] <> N'COL' ORDER BY [Code];
OPEN dept_cursor;
FETCH NEXT FROM dept_cursor INTO @DepartmentId, @DepartmentCode, @DepartmentName;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @CategoryId = NULL; SET @RequestTypeId = NULL; SET @WorkflowId = NULL;
    SET @Code = @DepartmentCode + N'-GENERAL-001';
    SET @WorkflowName = @Code + N' General';
    SET @Description = N'Default classification: ' + @DepartmentName + N' / General. ' + @DepartmentCode + N' Queue -> Agent -> Review -> Resolve -> Close. Used when a ticket has no valid classification.';

    IF (SELECT COUNT(*) FROM [Categories] WHERE [DepartmentId] = @DepartmentId AND [Name] = N'General') > 1
       OR (SELECT COUNT(*) FROM [RequestTypes] WHERE [DepartmentId] = @DepartmentId AND [Name] = N'General') > 1
    BEGIN
        INSERT #Report VALUES (@DepartmentCode, N'SKIPPED: duplicate General records - a person must choose the canonical one', NULL, NULL, NULL);
        IF @DepartmentCode = N'CS' THROW 50701, N'ABORTED: more than one General category or request type exists in Customer Services.', 1;
        FETCH NEXT FROM dept_cursor INTO @DepartmentId, @DepartmentCode, @DepartmentName; CONTINUE;
    END;

    SELECT @CategoryId = [CategoryId] FROM [Categories] WHERE [DepartmentId] = @DepartmentId AND [Name] = N'General';
    SELECT @RequestTypeId = [RequestTypeId], @WorkflowId = [WorkflowId] FROM [RequestTypes] WHERE [DepartmentId] = @DepartmentId AND [Name] = N'General';

    IF @RequestTypeId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM [WorkflowTemplates] WHERE [WorkflowId] = @WorkflowId AND [Status] = 2)
    BEGIN
        INSERT #Report VALUES (@DepartmentCode, N'SKIPPED: General request type has no Published workflow - publish it from Administration', @CategoryId, @RequestTypeId, @WorkflowId);
        IF @DepartmentCode = N'CS' THROW 50702, N'ABORTED: the Customer Services General request type has no Published workflow.', 1;
        FETCH NEXT FROM dept_cursor INTO @DepartmentId, @DepartmentCode, @DepartmentName; CONTINUE;
    END;

    IF @CategoryId IS NOT NULL AND @RequestTypeId IS NOT NULL
    BEGIN
        INSERT #Report VALUES (@DepartmentCode, N'ALREADY CONFIGURED', @CategoryId, @RequestTypeId, @WorkflowId);
        FETCH NEXT FROM dept_cursor INTO @DepartmentId, @DepartmentCode, @DepartmentName; CONTINUE;
    END;

    SET @Reused = 0;
    IF @RequestTypeId IS NULL
    BEGIN
        -- Resolve the dedicated workflow <CODE>-GENERAL-001: reuse if compatible, create if free, otherwise skip.
        SET @Attempt = 1; SET @WhyNot = NULL;
        WHILE @Attempt <= 1 AND @WorkflowId IS NULL
        BEGIN
            SET @Code = @DepartmentCode + N'-GENERAL-001';
            SET @ExistingWorkflowId = (SELECT [WorkflowId] FROM [Workflows] WHERE [Code] = @Code);
            SET @ExistingTemplateId = NULL; SET @Compatible = 0;

            IF @ExistingWorkflowId IS NULL AND NOT EXISTS (SELECT 1 FROM [WorkflowTemplates] WHERE [Code] = @Code)
                BREAK;   -- code is free: create below with @Code

            IF @ExistingWorkflowId IS NULL
                SET @WhyNot = N'template code ' + @Code + N' is used without a matching workflow';
            ELSE
            BEGIN
                SELECT @ExistingTemplateId = MIN([WorkflowTemplateId]) FROM [WorkflowTemplates] WHERE [WorkflowId] = @ExistingWorkflowId AND [Status] = 2;
                IF NOT EXISTS (SELECT 1 FROM [Workflows] WHERE [WorkflowId] = @ExistingWorkflowId AND [IsActive] = 1) SET @WhyNot = @Code + N' workflow is inactive';
                ELSE IF (SELECT COUNT(*) FROM [WorkflowTemplates] WHERE [WorkflowId] = @ExistingWorkflowId AND [Status] = 2) <> 1 SET @WhyNot = @Code + N' does not have exactly one Published template';
                ELSE IF NOT EXISTS (SELECT 1 FROM [WorkflowTemplates] WHERE [WorkflowTemplateId] = @ExistingTemplateId AND [IsActive] = 1 AND [RequiresApproval] = 0) SET @WhyNot = @Code + N' published template is inactive or requires approval';
                ELSE IF EXISTS (SELECT 1 FROM [WorkflowTemplateSteps] WHERE [WorkflowTemplateId] = @ExistingTemplateId AND [ApprovalType] IS NOT NULL) SET @WhyNot = @Code + N' has approval steps';
                ELSE IF NOT EXISTS (SELECT 1 FROM [WorkflowTemplateSteps] s WHERE s.[WorkflowTemplateId] = @ExistingTemplateId AND s.[Kind] = 1
                                    AND s.[Sequence] = (SELECT MIN([Sequence]) FROM [WorkflowTemplateSteps] WHERE [WorkflowTemplateId] = @ExistingTemplateId)) SET @WhyNot = @Code + N' does not start with a Created step';
                ELSE IF NOT EXISTS (SELECT 1 FROM [WorkflowTemplateSteps] s WHERE s.[WorkflowTemplateId] = @ExistingTemplateId AND s.[Kind] = 9
                                    AND s.[Sequence] = (SELECT MAX([Sequence]) FROM [WorkflowTemplateSteps] WHERE [WorkflowTemplateId] = @ExistingTemplateId)) SET @WhyNot = @Code + N' does not end with a Closed step';
                ELSE IF NOT EXISTS (SELECT 1 FROM [WorkflowTemplateSteps] WHERE [WorkflowTemplateId] = @ExistingTemplateId AND [Kind] = 2) SET @WhyNot = @Code + N' has no Assigned step';
                ELSE IF NOT EXISTS (SELECT 1 FROM [WorkflowTemplateSteps] WHERE [WorkflowTemplateId] = @ExistingTemplateId AND [Kind] = 8) SET @WhyNot = @Code + N' has no Resolved step';
                ELSE IF EXISTS (SELECT 1 FROM [RequestTypes] WHERE [WorkflowId] = @ExistingWorkflowId AND [DepartmentId] <> @DepartmentId) SET @WhyNot = @Code + N' is used by another department''s request type';
                ELSE SET @Compatible = 1;
            END;

            IF @Compatible = 1
            BEGIN
                SET @WorkflowId = @ExistingWorkflowId; SET @Reused = 1;
            END
            ELSE SET @Attempt += 1;
        END;

        IF @WorkflowId IS NULL AND @Attempt > 1
        BEGIN
            INSERT #Report VALUES (@DepartmentCode, N'SKIPPED: dedicated workflow code is not usable - ' + ISNULL(@WhyNot, @Code + N' is in use'), @CategoryId, NULL, NULL);
            IF @DepartmentCode = N'CS' THROW 50703, N'ABORTED: workflow code CS-GENERAL-001 is in use and not compatible with a General flow (see report).', 1;
            FETCH NEXT FROM dept_cursor INTO @DepartmentId, @DepartmentCode, @DepartmentName; CONTINUE;
        END;
        SET @WorkflowName = @Code + N' General';
        SET @Description = N'Default classification: ' + @DepartmentName + N' / General. ' + @DepartmentCode + N' Queue -> Agent -> Review -> Resolve -> Close. Used when a ticket has no valid classification.';
    END;

    IF @RequestTypeId IS NULL AND @WorkflowId IS NULL
    BEGIN
        INSERT INTO [Workflows] ([Code], [Name], [Description], [IsActive], [CreatedAtUtc]) VALUES (@Code, @WorkflowName, @Description, 1, @Now);
        SET @WorkflowId = CAST(SCOPE_IDENTITY() AS int);

        INSERT INTO [WorkflowTemplates]
            ([Code], [Name], [Description], [AllowsPendingCustomer], [AllowsPendingInternal], [RequiresApproval], [IsActive],
             [WorkflowId], [VersionNumber], [Status], [CreatedAtUtc], [CreatedByEmployeeId], [PublishedAtUtc], [PublishedByEmployeeId])
        VALUES (@Code, @WorkflowName, @Description, 0, 0, 0, 1, @WorkflowId, 1, 2, @Now, NULL, @Now, NULL);
        SET @TemplateId = CAST(SCOPE_IDENTITY() AS int);

        INSERT INTO [WorkflowTemplateSteps] ([WorkflowTemplateId], [Sequence], [Name], [Kind], [IsOptional], [ApprovalType])
        SELECT @TemplateId, v.[Sequence], v.[Name], v.[Kind], 0, NULL
        FROM (VALUES (1, N'Ticket Created', 1), (2, @DepartmentCode + N' Queue', 2), (3, N'Agent', 2), (4, N'Review', 5), (5, N'Resolve', 8), (6, N'Close', 9))
             AS v([Sequence], [Name], [Kind]);

    END;

    IF @RequestTypeId IS NULL
    BEGIN
        -- DefaultPriorityId 3 = Medium ("Normal").
        INSERT INTO [RequestTypes]
            ([DepartmentId], [Name], [WorkflowId], [DefaultPriorityId], [AllowAgentPriorityChange], [AllowPendingCustomer],
             [AllowPendingInternal], [AllowReopen], [RequiredFieldsJson], [IsActive])
        VALUES (@DepartmentId, N'General', @WorkflowId, 3, 0, 1, 1, 1, NULL, 1);
        SET @RequestTypeId = CAST(SCOPE_IDENTITY() AS int);
    END;

    IF @CategoryId IS NULL
    BEGIN
        INSERT INTO [Categories] ([Name], [DepartmentId], [ParentCategoryId], [IsActive]) VALUES (N'General', @DepartmentId, NULL, 1);
        SET @CategoryId = CAST(SCOPE_IDENTITY() AS int);
    END;

    INSERT #Report VALUES (@DepartmentCode, CASE WHEN @Reused = 1 THEN N'CREATED (missing parts only; EXISTING dedicated workflow ' + @Code + N' reused)' ELSE N'CREATED (missing parts only; new dedicated workflow ' + @Code + N')' END, @CategoryId, @RequestTypeId, @WorkflowId);
    FETCH NEXT FROM dept_cursor INTO @DepartmentId, @DepartmentCode, @DepartmentName;
END;
CLOSE dept_cursor; DEALLOCATE dept_cursor;

SELECT * FROM #Report ORDER BY DepartmentCode;

IF @CommitChanges = 1
BEGIN
    COMMIT TRANSACTION;
    PRINT N'COMMITTED.';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION;
    PRINT N'DRY RUN - rolled back. Set @CommitChanges = 1 to apply.';
END;
DROP TABLE #Report;
