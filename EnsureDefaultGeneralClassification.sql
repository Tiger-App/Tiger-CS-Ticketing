/*
================================================================================
 EnsureDefaultGeneralClassification.sql        (idempotent configuration change)

 Makes sure the approved default classification exists:
     Department  : Customer Services   (Departments.Code = 'CS')   - never created here
     Category    : General
     Request type: General             - with workflow 'CS-GEN-001', version 1 Published

 SAFE BY CONSTRUCTION
   * Reuses an existing General category / request type of the CS department. A second run creates nothing.
   * Additive: no UPDATE, no DELETE, no ticket is touched.
   * Duplicates (more than one General category or request type in CS), a missing CS department, an existing General request type whose workflow
     has no Published version, or a workflow code already in use are REPORTED and NOTHING is written (the script stops with an error).
   * No assignment rule and no SLA policy row is created: tickets get a CS Agent through the existing confirmed-agent / sequential Round-Robin rules.
   * Dry run: set @CommitChanges = 0 - everything is applied inside the transaction, the result is printed, then rolled back.

 It is the SQL equivalent of GeneralFallbackImporter (TigerCS.Infrastructure). It is NOT executed by the application, a scheduler or CI.
 Status 2 = Published. Step kinds (WorkflowStepKind): 1 Created, 2 Assigned, 5 InProgress, 8 Resolved, 9 Closed.

 RUNNING IT
     sqlcmd -S <server> -d <database> -U <user> -P <password> -C -b -I -i EnsureDefaultGeneralClassification.sql
================================================================================
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @CommitChanges bit = 0;     -- 0 = dry run (rolled back). Set to 1 only after approval.

DECLARE @Now datetime2 = SYSUTCDATETIME();
DECLARE @DepartmentId int, @CategoryId int, @RequestTypeId int, @WorkflowId int, @TemplateId int;
DECLARE @Code nvarchar(50) = N'CS-GEN-001';
DECLARE @WorkflowName nvarchar(200) = N'CS-GEN-001 General';
DECLARE @Description nvarchar(1000) = N'Default classification: Customer Services / General. CS Queue -> Agent -> Review -> Resolve -> Close. Used when a ticket has no valid classification.';

SELECT @DepartmentId = [DepartmentId] FROM [Departments] WHERE [Code] = N'CS';
IF @DepartmentId IS NULL THROW 50700, N'ABORTED: the Customer Services department (Code = CS) does not exist. It is never created by this script.', 1;

IF (SELECT COUNT(*) FROM [Categories] WHERE [DepartmentId] = @DepartmentId AND [Name] = N'General') > 1
   OR (SELECT COUNT(*) FROM [RequestTypes] WHERE [DepartmentId] = @DepartmentId AND [Name] = N'General') > 1
    THROW 50701, N'ABORTED: more than one General category or request type exists in Customer Services. A person must choose the canonical one.', 1;

SELECT @CategoryId = [CategoryId] FROM [Categories] WHERE [DepartmentId] = @DepartmentId AND [Name] = N'General';
SELECT @RequestTypeId = [RequestTypeId], @WorkflowId = [WorkflowId] FROM [RequestTypes] WHERE [DepartmentId] = @DepartmentId AND [Name] = N'General';

IF @RequestTypeId IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM [WorkflowTemplates] WHERE [WorkflowId] = @WorkflowId AND [Status] = 2)
    THROW 50702, N'ABORTED: the General request type exists but its workflow has no Published version. Publish it from Administration; nothing was changed.', 1;

IF @RequestTypeId IS NULL
   AND (EXISTS (SELECT 1 FROM [Workflows] WHERE [Code] = @Code) OR EXISTS (SELECT 1 FROM [WorkflowTemplates] WHERE [Code] = @Code))
    THROW 50703, N'ABORTED: workflow code CS-GEN-001 is already in use by something that is not the General request type. Nothing was changed.', 1;

BEGIN TRANSACTION;

IF @RequestTypeId IS NULL
BEGIN
    INSERT INTO [Workflows] ([Code], [Name], [Description], [IsActive], [CreatedAtUtc]) VALUES (@Code, @WorkflowName, @Description, 1, @Now);
    SET @WorkflowId = CAST(SCOPE_IDENTITY() AS int);

    INSERT INTO [WorkflowTemplates]
        ([Code], [Name], [Description], [AllowsPendingCustomer], [AllowsPendingInternal], [RequiresApproval], [IsActive],
         [WorkflowId], [VersionNumber], [Status], [CreatedAtUtc], [CreatedByEmployeeId], [PublishedAtUtc], [PublishedByEmployeeId])
    VALUES (@Code, @WorkflowName, @Description, 0, 0, 0, 1, @WorkflowId, 1, 2, @Now, NULL, @Now, NULL);
    SET @TemplateId = CAST(SCOPE_IDENTITY() AS int);

    -- Same step kinds as GeneralFallbackImporter (Created, Assigned, Assigned, InProgress, Resolved, Closed); see WorkflowStepKind.
    INSERT INTO [WorkflowTemplateSteps] ([WorkflowTemplateId], [Sequence], [Name], [Kind], [IsOptional], [ApprovalType])
    SELECT @TemplateId, v.[Sequence], v.[Name], v.[Kind], 0, NULL
    FROM (VALUES (1, N'Ticket Created', 1), (2, N'CS Queue', 2), (3, N'Agent', 2), (4, N'Review', 5), (5, N'Resolve', 8), (6, N'Close', 9)) AS v([Sequence], [Name], [Kind]);

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

SELECT d.[Code] AS DepartmentCode, c.[CategoryId], c.[Name] AS Category, r.[RequestTypeId], r.[Name] AS RequestType, w.[Code] AS WorkflowCode,
       (SELECT COUNT(*) FROM [WorkflowTemplates] t WHERE t.[WorkflowId] = r.[WorkflowId] AND t.[Status] = 2) AS PublishedVersions
FROM [Departments] d
JOIN [Categories] c ON c.[CategoryId] = @CategoryId
JOIN [RequestTypes] r ON r.[RequestTypeId] = @RequestTypeId
JOIN [Workflows] w ON w.[WorkflowId] = r.[WorkflowId]
WHERE d.[DepartmentId] = @DepartmentId;

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
