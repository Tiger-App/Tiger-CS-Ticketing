/*
================================================================================
 Diagnose_TigerCS_PROD_DefaultClassification_ReadOnly.sql     (READ-ONLY - SELECT only)
 Database : TigerCS_Ticketing_Prod

 Which tickets have NO valid classification, and what would the approved default (Customer Services / General / General) change?
 Nothing is written. Temp tables live in tempdb only. Run from a read-only login.

 Result sets
   1. Configuration check  - the CS department, its General category and General request type, and whether the request type has a Published workflow.
                             The backfill and the application fall back to the old behaviour until all four are present exactly once.
   2. Summary              - candidate tickets by classification state and ticket status.
   3. By current department- where the candidates sit (department is NOT changed by the backfill).
   4. Excluded             - tickets that are never candidates (Collections), so they can be seen to be left alone.
   5. Sample               - the oldest 200 candidates, for a dry-run preview.

 Classification state (first that applies)
   NO_CLASSIFICATION   no category and no request type.
   REQUEST_TYPE_ONLY   a request type but no category (partial).
   CATEGORY_ONLY       a category but no request type (partial). Reported separately: many valid tickets were classified by category alone, so the
                       backfill leaves them UNLESS the approved run explicitly includes partial classifications.
   INVALID_REFERENCE   the category or request type id does not resolve to a row.
   INCONSISTENT        the category and request type belong to different departments.
   VALID               everything else - never a candidate.

 The backfill (when separately approved) sets Category + Request type + pinned workflow ONLY. Owner, department, priority, SLA, status and history are untouched.
================================================================================
*/
SET NOCOUNT ON;
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

DECLARE @ExpectedDb sysname = N'TigerCS_Ticketing_Prod';
IF DB_NAME() <> @ExpectedDb
BEGIN
    DECLARE @m nvarchar(400) = N'ABORTED: connected to [' + ISNULL(DB_NAME(), N'?') + N'], this script is for [' + @ExpectedDb + N'] only.';
    THROW 50520, @m, 1;
END;

-- 1. Configuration ------------------------------------------------------------------------------------------------------------------------
SELECT d.[DepartmentId], d.[Code], d.[Name], d.[IsActive],
       (SELECT COUNT(*) FROM [Categories] c WHERE c.[DepartmentId] = d.[DepartmentId] AND c.[Name] = N'General' AND c.[IsActive] = 1) AS ActiveGeneralCategories,
       (SELECT COUNT(*) FROM [RequestTypes] r WHERE r.[DepartmentId] = d.[DepartmentId] AND r.[Name] = N'General' AND r.[IsActive] = 1) AS ActiveGeneralRequestTypes,
       (SELECT COUNT(*) FROM [RequestTypes] r JOIN [WorkflowTemplates] t ON t.[WorkflowId] = r.[WorkflowId] AND t.[Status] = 2
         WHERE r.[DepartmentId] = d.[DepartmentId] AND r.[Name] = N'General' AND r.[IsActive] = 1) AS PublishedGeneralWorkflowVersions
FROM [Departments] d WHERE d.[Code] = N'CS';

-- 1b. Per-department General (an AI-named department is classified General / General WITHIN itself when it has its own; otherwise Customer Services).
SELECT d.[Code], d.[Name],
       (SELECT COUNT(*) FROM [Categories] c WHERE c.[DepartmentId] = d.[DepartmentId] AND c.[Name] = N'General' AND c.[IsActive] = 1) AS ActiveGeneralCategories,
       (SELECT COUNT(*) FROM [RequestTypes] r JOIN [WorkflowTemplates] t ON t.[WorkflowId] = r.[WorkflowId] AND t.[Status] = 2
         WHERE r.[DepartmentId] = d.[DepartmentId] AND r.[Name] = N'General' AND r.[IsActive] = 1) AS GeneralRequestTypesWithPublishedWorkflow
FROM [Departments] d WHERE d.[IsActive] = 1 AND d.[Code] <> N'COL' ORDER BY d.[Code];

DECLARE @Col int = (SELECT [DepartmentId] FROM [Departments] WHERE [Code] = N'COL');

-- State of every ticket's stored classification -------------------------------------------------------------------------------------------
SELECT t.[TicketId], t.[TicketNumber], t.[TicketStatus], t.[CurrentDepartmentId], t.[OriginatingDepartmentId], t.[CurrentOwnerEmployeeId],
       t.[CategoryId], t.[RequestTypeId], t.[CreatedAtUtc],
       CASE
           WHEN t.[CategoryId] IS NULL AND t.[RequestTypeId] IS NULL THEN N'NO_CLASSIFICATION'
           WHEN t.[CategoryId] IS NULL THEN N'REQUEST_TYPE_ONLY'
           WHEN c.[CategoryId] IS NULL THEN N'INVALID_REFERENCE'
           WHEN t.[RequestTypeId] IS NULL THEN N'CATEGORY_ONLY'
           WHEN r.[RequestTypeId] IS NULL THEN N'INVALID_REFERENCE'
           WHEN r.[DepartmentId] <> c.[DepartmentId] THEN N'INCONSISTENT'
           ELSE N'VALID'
       END AS ClassificationState,
       CASE WHEN t.[CurrentDepartmentId] = @Col OR t.[OriginatingDepartmentId] = @Col THEN 1 ELSE 0 END AS IsCollections
INTO #State
FROM [Tickets] t
LEFT JOIN [Categories] c ON c.[CategoryId] = t.[CategoryId]
LEFT JOIN [RequestTypes] r ON r.[RequestTypeId] = t.[RequestTypeId];

-- 2. Summary --------------------------------------------------------------------------------------------------------------------------------
SELECT s.[ClassificationState], s.[TicketStatus], COUNT(*) AS Tickets
FROM #State s WHERE s.[ClassificationState] <> N'VALID' AND s.[IsCollections] = 0
GROUP BY s.[ClassificationState], s.[TicketStatus] ORDER BY s.[ClassificationState], s.[TicketStatus];

SELECT N'ALL_TICKETS' AS Measure, COUNT(*) AS Tickets FROM #State
UNION ALL SELECT N'VALID', COUNT(*) FROM #State WHERE [ClassificationState] = N'VALID'
UNION ALL SELECT N'CANDIDATES (excl. Collections)', COUNT(*) FROM #State WHERE [ClassificationState] <> N'VALID' AND [IsCollections] = 0
UNION ALL SELECT N'CANDIDATES that are CATEGORY_ONLY (left alone unless partial is included)', COUNT(*) FROM #State WHERE [ClassificationState] = N'CATEGORY_ONLY' AND [IsCollections] = 0;

-- 3. By current department ---------------------------------------------------------------------------------------------------------------
SELECT d.[Code] AS CurrentDepartment, s.[ClassificationState], COUNT(*) AS Tickets,
       SUM(CASE WHEN s.[CurrentOwnerEmployeeId] IS NULL THEN 1 ELSE 0 END) AS WithoutOwner
FROM #State s JOIN [Departments] d ON d.[DepartmentId] = s.[CurrentDepartmentId]
WHERE s.[ClassificationState] <> N'VALID' AND s.[IsCollections] = 0
GROUP BY d.[Code], s.[ClassificationState] ORDER BY d.[Code], s.[ClassificationState];

-- 4. Excluded (Collections) ------------------------------------------------------------------------------------------------------------
SELECT s.[ClassificationState], COUNT(*) AS CollectionsTicketsLeftAlone FROM #State s WHERE s.[IsCollections] = 1 AND s.[ClassificationState] <> N'VALID' GROUP BY s.[ClassificationState];

-- 5. Sample -------------------------------------------------------------------------------------------------------------------------------
SELECT TOP (200) s.[TicketId], s.[TicketNumber], s.[TicketStatus], d.[Code] AS CurrentDepartment, s.[ClassificationState], s.[CategoryId], s.[RequestTypeId],
       s.[CurrentOwnerEmployeeId], s.[CreatedAtUtc]
FROM #State s JOIN [Departments] d ON d.[DepartmentId] = s.[CurrentDepartmentId]
WHERE s.[ClassificationState] <> N'VALID' AND s.[IsCollections] = 0
ORDER BY s.[TicketId];

DROP TABLE #State;
