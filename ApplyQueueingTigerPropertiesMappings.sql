/*
================================================================================
 ApplyQueueingTigerPropertiesMappings.sql        (idempotent DATA script - NOT a migration)

 Queueing System -> TigerCS routing for Tiger Properties (CompanyId = N'4').

 ROUTING RULE (see docs/QueueingIntegration/Queueing-Integration.md section 1.1)
   * An active row in QueueingTicketTypeMappings for (CompanyId, TicketTypeId) decides the request type AND, through
     RequestTypes.DepartmentId, the department. One company can therefore serve several departments.
   * QueueingCompanyMappings is only the DEFAULT department, used when no active explicit mapping exists (General request type fallback).

 NO SCHEMA CHANGE. QueueingTicketTypeMappings (CompanyId, TicketTypeId, RequestTypeId) already carries everything: the department is derived from the request
 type, so a stored DepartmentId would only be a second copy that could drift. The ExpectedDepartmentId column below is a guard evaluated by THIS script only.
 AddQueueingIntegration.sql (already applied to production) is not touched.

 SAFE BY CONSTRUCTION
   * Single transaction, all-or-nothing. THROWs - changing nothing - if any mapped request type does not exist, is inactive, does not belong to the expected
     department, whose department is inactive, or has no Published workflow version (the API would hold such an event as REQUEST_TYPE_UNUSABLE).
   * Re-runnable: an existing row is re-pointed / re-activated only when it differs; nothing is deleted; no ticket is touched.
   * Does NOT create or change a company -> default department row (see the optional block at the end).

 Review the PREVIEW result set, then change @Apply to 1.
================================================================================
*/
SET XACT_ABORT ON;
SET NOCOUNT ON;

DECLARE @Apply bit = 0;                       -- 0 = preview only (rolls back), 1 = apply
DECLARE @CompanyId nvarchar(100) = N'4';      -- Tiger Properties

DECLARE @Map TABLE (TicketTypeId nvarchar(100) NOT NULL PRIMARY KEY, RequestTypeId int NOT NULL, ExpectedDepartmentId int NOT NULL);
INSERT INTO @Map (TicketTypeId, RequestTypeId, ExpectedDepartmentId) VALUES
    (N'53', 1, 1), (N'54', 3, 1), (N'55', 2, 1), (N'56', 21, 2),
    (N'57', 17, 1), (N'58', 14, 1), (N'59', 20, 3), (N'60', 40, 1);

BEGIN TRANSACTION;

-- ---- validation ---------------------------------------------------------------------------------------------------------------------------------------
SELECT m.TicketTypeId, m.RequestTypeId, m.ExpectedDepartmentId,
       rt.Name AS RequestType, rt.DepartmentId AS ActualDepartmentId, d.Code AS DepartmentCode, rt.IsActive AS RequestTypeActive, d.IsActive AS DepartmentActive,
       (SELECT COUNT(*) FROM WorkflowTemplates wt WHERE wt.WorkflowId = rt.WorkflowId AND wt.Status = 2) AS PublishedWorkflowVersions,
       CASE WHEN rt.RequestTypeId IS NULL THEN 'MISSING REQUEST TYPE'
            WHEN rt.IsActive = 0 THEN 'REQUEST TYPE INACTIVE'
            WHEN rt.DepartmentId <> m.ExpectedDepartmentId THEN 'DEPARTMENT MISMATCH'
            WHEN d.DepartmentId IS NULL OR d.IsActive = 0 THEN 'DEPARTMENT MISSING/INACTIVE'
            WHEN NOT EXISTS (SELECT 1 FROM WorkflowTemplates wt WHERE wt.WorkflowId = rt.WorkflowId AND wt.Status = 2) THEN 'NO PUBLISHED WORKFLOW'
            ELSE 'OK' END AS Check_
FROM @Map m
LEFT JOIN RequestTypes rt ON rt.RequestTypeId = m.RequestTypeId
LEFT JOIN Departments d ON d.DepartmentId = rt.DepartmentId
ORDER BY CAST(m.TicketTypeId AS int);

IF EXISTS (
    SELECT 1 FROM @Map m
    LEFT JOIN RequestTypes rt ON rt.RequestTypeId = m.RequestTypeId
    LEFT JOIN Departments d ON d.DepartmentId = rt.DepartmentId
    WHERE rt.RequestTypeId IS NULL OR rt.IsActive = 0 OR rt.DepartmentId <> m.ExpectedDepartmentId OR d.DepartmentId IS NULL OR d.IsActive = 0
       OR NOT EXISTS (SELECT 1 FROM WorkflowTemplates wt WHERE wt.WorkflowId = rt.WorkflowId AND wt.Status = 2))
BEGIN
    ROLLBACK TRANSACTION;
    THROW 51000, 'ApplyQueueingTigerPropertiesMappings: a mapped request type is missing, inactive, in another department, or has no Published workflow. Nothing was changed - see the check column above.', 1;
END;

-- ---- apply (idempotent upsert) ------------------------------------------------------------------------------------------------------------------------
UPDATE t SET t.RequestTypeId = m.RequestTypeId, t.IsActive = 1
FROM QueueingTicketTypeMappings t
JOIN @Map m ON m.TicketTypeId = t.TicketTypeId
WHERE t.CompanyId = @CompanyId AND (t.RequestTypeId <> m.RequestTypeId OR t.IsActive = 0);

INSERT INTO QueueingTicketTypeMappings (CompanyId, TicketTypeId, RequestTypeId, IsActive, CreatedAtUtc)
SELECT @CompanyId, m.TicketTypeId, m.RequestTypeId, 1, SYSUTCDATETIME()
FROM @Map m
WHERE NOT EXISTS (SELECT 1 FROM QueueingTicketTypeMappings t WHERE t.CompanyId = @CompanyId AND t.TicketTypeId = m.TicketTypeId);

-- ---- result -------------------------------------------------------------------------------------------------------------------------------------------
SELECT t.CompanyId, t.TicketTypeId, t.RequestTypeId, rt.Name AS RequestType, rt.DepartmentId, d.Code AS DepartmentCode, t.IsActive
FROM QueueingTicketTypeMappings t
JOIN RequestTypes rt ON rt.RequestTypeId = t.RequestTypeId
JOIN Departments d ON d.DepartmentId = rt.DepartmentId
WHERE t.CompanyId = @CompanyId
ORDER BY CAST(t.TicketTypeId AS int);

IF @Apply = 1
BEGIN
    COMMIT TRANSACTION;
    PRINT 'Applied.';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION;
    PRINT 'PREVIEW ONLY - rolled back. Set @Apply = 1 to apply.';
END;

/* ---- OPTIONAL: default department for ticket types that have NO explicit mapping (decision pending - not applied) -------------------------------------
   Without it, a TicketCreated for company 4 with an unlisted ticket type is HELD as COMPANY_NOT_MAPPED (retried automatically) rather than guessed.
   Fill in the department the business wants as the default, then run:

   INSERT INTO QueueingCompanyMappings (CompanyId, DepartmentId, IsActive, CreatedAtUtc)
   SELECT N'4', <DepartmentId>, 1, SYSUTCDATETIME()
   WHERE NOT EXISTS (SELECT 1 FROM QueueingCompanyMappings WHERE CompanyId = N'4');
   -------------------------------------------------------------------------------------------------------------------------------------------------- */
