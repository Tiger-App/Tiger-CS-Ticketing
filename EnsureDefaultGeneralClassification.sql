/*
================================================================================
 EnsureDefaultGeneralClassification.sql        (idempotent configuration change)

 Makes sure the approved default classification exists:
   * for EVERY active department except Collections (COL): a General category and a General request type with workflow '<CODE>-GEN-001'
     (version 1 Published), so a ticket whose department is known is classified General / General WITHIN that department;
   * Customer Services (Code = 'CS') is REQUIRED: it is the last-resort default when no valid department is known.

 SAFE BY CONSTRUCTION
   * Reuses an existing General category / request type of a department. A second run creates nothing.
   * Additive: no UPDATE, no DELETE, no ticket is touched. Never creates a department. Collections is never given a General.
   * A department with duplicate General rows, a General request type whose workflow has no Published version, or a workflow code already in use by
     something else is REPORTED and SKIPPED (nothing written for it). If that department is CS the script stops with an error.
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

CREATE TABLE #Report (DepartmentCode nvarchar(50), Result nvarchar(200), CategoryId int NULL, RequestTypeId int NULL);

DECLARE @Now datetime2 = SYSUTCDATETIME();
DECLARE @DepartmentId int, @DepartmentCode nvarchar(50), @DepartmentName nvarchar(200);
DECLARE @CategoryId int, @RequestTypeId int, @WorkflowId int, @TemplateId int, @Code nvarchar(50), @WorkflowName nvarchar(200), @Description nvarchar(1000);

BEGIN TRANSACTION;

DECLARE dept_cursor CURSOR LOCAL FAST_FORWARD FOR
    SELECT [DepartmentId], [Code], [Name] FROM [Departments] WHERE [IsActive] = 1 AND [Code] <> N'COL' ORDER BY [Code];
OPEN dept_cursor;
FETCH NEXT FROM dept_cursor INTO @DepartmentId, @DepartmentCode, @DepartmentName;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @CategoryId = NULL; SET @RequestTypeId = NULL; SET @WorkflowId = NULL;
    SET @Code = @DepartmentCode + N'-GEN-001';
    SET @WorkflowName = @Code + N' General';
    SET @Description = N'Default classification: ' + @DepartmentName + N' / General. ' + @DepartmentCode + N' Queue -> Agent -> Review -> Resolve -> Close. Used when a ticket has no valid classification.';

    IF (SELECT COUNT(*) FROM [Categories] WHERE [DepartmentId] = @DepartmentId AND [Name] = N'General') > 1
       OR (SELECT COUNT(*) FROM [RequestTypes] WHERE [DepartmentId] = @DepartmentId AND [Name] = N'General') > 1
    BEGIN
        INSERT #Report VALUES (@DepartmentCode, N'SKIPPED: duplicate General records - a person must choose the canonical one', NULL, NULL);
        IF @DepartmentCode = N'CS' THROW 50701, N'ABORTED: more than one General category or request type exists in Customer Services.', 1;
        FETCH NEXT FROM dept_cursor INTO @DepartmentId, @DepartmentCode, @DepartmentName; CONTINUE;
    END;

    SELECT @CategoryId = [CategoryId] FROM [Categories] WHERE [DepartmentId] = @DepartmentId AND [Name] = N'General';
    SELECT @RequestTypeId = [RequestTypeId], @WorkflowId = [WorkflowId] FROM [RequestTypes] WHERE [DepartmentId] = @DepartmentId AND [Name] = N'General';

    IF @RequestTypeId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM [WorkflowTemplates] WHERE [WorkflowId] = @WorkflowId AND [Status] = 2)
    BEGIN
        INSERT #Report VALUES (@DepartmentCode, N'SKIPPED: General request type has no Published workflow - publish it from Administration', @CategoryId, @RequestTypeId);
        IF @DepartmentCode = N'CS' THROW 50702, N'ABORTED: the Customer Services General request type has no Published workflow.', 1;
        FETCH NEXT FROM dept_cursor INTO @DepartmentId, @DepartmentCode, @DepartmentName; CONTINUE;
    END;

    IF @RequestTypeId IS NULL
       AND (EXISTS (SELECT 1 FROM [Workflows] WHERE [Code] = @Code) OR EXISTS (SELECT 1 FROM [WorkflowTemplates] WHERE [Code] = @Code))
    BEGIN
        INSERT #Report VALUES (@DepartmentCode, N'SKIPPED: workflow code ' + @Code + N' is already in use by something else', @CategoryId, NULL);
        IF @DepartmentCode = N'CS' THROW 50703, N'ABORTED: workflow code CS-GEN-001 is already in use.', 1;
        FETCH NEXT FROM dept_cursor INTO @DepartmentId, @DepartmentCode, @DepartmentName; CONTINUE;
    END;

    IF @CategoryId IS NOT NULL AND @RequestTypeId IS NOT NULL
    BEGIN
        INSERT #Report VALUES (@DepartmentCode, N'ALREADY CONFIGURED', @CategoryId, @RequestTypeId);
        FETCH NEXT FROM dept_cursor INTO @DepartmentId, @DepartmentCode, @DepartmentName; CONTINUE;
    END;

    IF @RequestTypeId IS NULL
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

    INSERT #Report VALUES (@DepartmentCode, N'CREATED (missing parts only)', @CategoryId, @RequestTypeId);
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
