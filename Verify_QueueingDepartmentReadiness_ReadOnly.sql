/*
================================================================================
 Verify_QueueingDepartmentReadiness_ReadOnly.sql              (READ ONLY - writes nothing)

 Is every department a Queueing company is mapped to ready to receive queue tickets classified with ITS OWN General request type?

 BACKGROUND (docs/QueueingIntegration/Queueing-Integration.md section 1.1)
   * A company mapping (QueueingCompanyMappings) is MANDATORY. A company without an active mapping to an active department is HELD (COMPANY_NOT_MAPPED).
   * An explicit (company, ticket type) mapping (QueueingTicketTypeMappings) is an optional OVERRIDE. A ticket type without one is classified with the
     mapped department's General category / General request type / its Published workflow. The Queueing System's own service name is kept separately.
   * A department with no usable General, or Collections (excluded from the default-classification rule), HOLDS every such event (TICKET_TYPE_NOT_MAPPED).

 WHAT THIS SCRIPT CHECKS, per mapped company
   1. Department exists and is active; it is not Collections.
   2. Exactly ONE active 'General' category and exactly ONE active 'General' request type in that department.
   3. That request type's workflow has a Published version (WorkflowTemplates.Status = 2), and the version is active.
   4. APPROVALS: no active RequestTypeApprovalRequirements row on General, the Published version does not require approval, no workflow step carries an approval.
   5. SLA OVERRIDES: no active RequestTypeSlaPolicies row on General for the queue's starting priority (that row would override the per-priority SlaPolicy).
      Rows for other priorities are listed as a WARNING - they cannot affect a queue ticket but would apply if the priority were changed.
   6. The queue's starting priority (@QueuePriorityName = QueueingIntegration:DefaultTicketPriority) exists and has exactly one SlaPolicy row.
   Also listed (informational): every explicit ticket-type override and whether its request type is still usable.

 HOW TO READ THE RESULT
   Section A: one row per company mapping with Verdict READY / NOT READY (+ every reason). Section B: explicit overrides. Section C: the queue priority.
   Nothing is changed. Set @CompanyId to look at one company; leave NULL for all.
================================================================================
*/
SET NOCOUNT ON;

DECLARE @CompanyId nvarchar(100) = NULL;            -- NULL = every company mapping
DECLARE @QueuePriorityName nvarchar(50) = N'Normal'; -- QueueingIntegration:DefaultTicketPriority (resolved by name, as the API does)

-- ---- Section A: per company ---------------------------------------------------------------------------------------------------------------------------
;WITH Gen AS (
    SELECT cm.QueueingCompanyMappingId, cm.CompanyId, cm.IsActive AS MappingActive, cm.DepartmentId,
           d.Code AS DepartmentCode, d.Name AS DepartmentName, d.IsActive AS DepartmentActive,
           (SELECT COUNT(*) FROM Categories c WHERE c.DepartmentId = cm.DepartmentId AND c.IsActive = 1 AND LTRIM(RTRIM(c.Name)) = N'General') AS GeneralCategories,
           (SELECT COUNT(*) FROM RequestTypes r WHERE r.DepartmentId = cm.DepartmentId AND r.IsActive = 1 AND LTRIM(RTRIM(r.Name)) = N'General') AS GeneralRequestTypes,
           (SELECT TOP (1) r.RequestTypeId FROM RequestTypes r WHERE r.DepartmentId = cm.DepartmentId AND r.IsActive = 1 AND LTRIM(RTRIM(r.Name)) = N'General' ORDER BY r.RequestTypeId) AS GeneralRequestTypeId
    FROM QueueingCompanyMappings cm
    LEFT JOIN Departments d ON d.DepartmentId = cm.DepartmentId
    WHERE (@CompanyId IS NULL OR cm.CompanyId = @CompanyId)
), Chk AS (
    SELECT g.*,
           rt.WorkflowId,
           (SELECT COUNT(*) FROM WorkflowTemplates wt WHERE wt.WorkflowId = rt.WorkflowId AND wt.Status = 2 AND wt.IsActive = 1) AS PublishedVersions,
           (SELECT COUNT(*) FROM WorkflowTemplates wt WHERE wt.WorkflowId = rt.WorkflowId AND wt.Status = 2 AND wt.RequiresApproval = 1) AS PublishedRequiringApproval,
           (SELECT COUNT(*) FROM WorkflowTemplateSteps s JOIN WorkflowTemplates wt ON wt.WorkflowTemplateId = s.WorkflowTemplateId
             WHERE wt.WorkflowId = rt.WorkflowId AND wt.Status = 2 AND s.ApprovalType IS NOT NULL) AS PublishedApprovalSteps,
           (SELECT COUNT(*) FROM RequestTypeApprovalRequirements a WHERE a.RequestTypeId = g.GeneralRequestTypeId AND a.IsActive = 1) AS ActiveApprovalRequirements,
           (SELECT COUNT(*) FROM RequestTypeSlaPolicies sp JOIN Priorities p ON p.PriorityId = sp.PriorityId
             WHERE sp.RequestTypeId = g.GeneralRequestTypeId AND sp.IsActive = 1 AND p.Name = @QueuePriorityName) AS SlaOverridesForQueuePriority,
           (SELECT COUNT(*) FROM RequestTypeSlaPolicies sp
             WHERE sp.RequestTypeId = g.GeneralRequestTypeId AND sp.IsActive = 1) AS SlaOverridesAnyPriority
    FROM Gen g
    LEFT JOIN RequestTypes rt ON rt.RequestTypeId = g.GeneralRequestTypeId AND g.GeneralRequestTypes = 1
)
SELECT c.CompanyId, c.MappingActive, c.DepartmentId, c.DepartmentCode, c.DepartmentName, c.DepartmentActive,
       c.GeneralCategories, c.GeneralRequestTypes, c.GeneralRequestTypeId, c.PublishedVersions,
       c.PublishedRequiringApproval, c.PublishedApprovalSteps, c.ActiveApprovalRequirements,
       c.SlaOverridesForQueuePriority, c.SlaOverridesAnyPriority,
       CASE WHEN r.Reasons IS NULL THEN 'READY' ELSE 'NOT READY' END AS Verdict,
       r.Reasons AS NotReadyBecause,
       CASE WHEN c.SlaOverridesAnyPriority > c.SlaOverridesForQueuePriority THEN 'General has an active request-type SLA row for another priority' END AS Warning
FROM Chk c
CROSS APPLY (
    SELECT NULLIF(CONCAT_WS(N'; ',
        CASE WHEN c.MappingActive = 0 THEN N'company mapping is inactive (events are held)' END,
        CASE WHEN c.DepartmentCode IS NULL THEN N'department does not exist' END,
        CASE WHEN c.DepartmentActive = 0 THEN N'department is inactive' END,
        CASE WHEN UPPER(LTRIM(RTRIM(c.DepartmentCode))) = N'COLLECTIONS' THEN N'Collections is excluded from the default classification' END,
        CASE WHEN c.GeneralCategories <> 1 THEN CONCAT(N'needs exactly one active General category (found ', c.GeneralCategories, N')') END,
        CASE WHEN c.GeneralRequestTypes <> 1 THEN CONCAT(N'needs exactly one active General request type (found ', c.GeneralRequestTypes, N')') END,
        CASE WHEN c.GeneralRequestTypes = 1 AND c.PublishedVersions = 0 THEN N'General has no active Published workflow version' END,
        CASE WHEN c.GeneralRequestTypes = 1 AND c.PublishedVersions > 1 THEN N'General has more than one Published workflow version' END,
        CASE WHEN c.PublishedRequiringApproval > 0 THEN N'General''s Published workflow requires approval' END,
        CASE WHEN c.PublishedApprovalSteps > 0 THEN N'General''s Published workflow has approval steps' END,
        CASE WHEN c.ActiveApprovalRequirements > 0 THEN N'General has an active approval requirement' END,
        CASE WHEN c.SlaOverridesForQueuePriority > 0 THEN N'General has an active request-type SLA row for the queue priority (overrides the priority SLA)' END
    ), N'') AS Reasons
) r
ORDER BY CASE WHEN r.Reasons IS NULL THEN 1 ELSE 0 END, c.CompanyId;

-- ---- Section B: explicit ticket-type overrides (optional; informational) ------------------------------------------------------------------------------
SELECT t.CompanyId, t.TicketTypeId, t.IsActive AS OverrideActive, t.RequestTypeId, rt.Name AS RequestType, d.Code AS DepartmentCode,
       CASE WHEN rt.RequestTypeId IS NULL THEN 'REQUEST TYPE MISSING (event would be held: REQUEST_TYPE_UNUSABLE)'
            WHEN rt.IsActive = 0 THEN 'REQUEST TYPE INACTIVE (event would be held)'
            WHEN d.DepartmentId IS NULL OR d.IsActive = 0 THEN 'DEPARTMENT MISSING/INACTIVE (event would be held)'
            WHEN NOT EXISTS (SELECT 1 FROM WorkflowTemplates wt WHERE wt.WorkflowId = rt.WorkflowId AND wt.Status = 2) THEN 'NO PUBLISHED WORKFLOW (event would be held)'
            WHEN t.IsActive = 0 THEN 'inactive: treated as absent, the department''s General applies'
            ELSE 'OK' END AS OverrideCheck,
       CASE WHEN NOT EXISTS (SELECT 1 FROM QueueingCompanyMappings cm WHERE cm.CompanyId = t.CompanyId AND cm.IsActive = 1)
            THEN 'company has NO active company mapping: every event of this company is held, override or not' END AS CompanyWarning
FROM QueueingTicketTypeMappings t
LEFT JOIN RequestTypes rt ON rt.RequestTypeId = t.RequestTypeId
LEFT JOIN Departments d ON d.DepartmentId = rt.DepartmentId
WHERE (@CompanyId IS NULL OR t.CompanyId = @CompanyId)
ORDER BY t.CompanyId, t.TicketTypeId;

-- ---- Section C: the queue's starting priority and its per-priority SLA --------------------------------------------------------------------------------
SELECT @QueuePriorityName AS QueuePriorityName,
       (SELECT COUNT(*) FROM Priorities p WHERE p.Name = @QueuePriorityName) AS PrioritiesFound,
       (SELECT COUNT(*) FROM SlaPolicies s JOIN Priorities p ON p.PriorityId = s.PriorityId WHERE p.Name = @QueuePriorityName) AS SlaPoliciesFound,
       CASE WHEN (SELECT COUNT(*) FROM Priorities p WHERE p.Name = @QueuePriorityName) = 1
             AND (SELECT COUNT(*) FROM SlaPolicies s JOIN Priorities p ON p.PriorityId = s.PriorityId WHERE p.Name = @QueuePriorityName) = 1
            THEN 'OK' ELSE 'NOT READY: PRIORITY_NOT_CONFIGURED or no/duplicate SLA policy' END AS Verdict;

-- ---- Section D: events currently held for a mapping reason (so you can see what a fix will release) -------------------------------------------------------
SELECT e.OutcomeCode, COUNT(*) AS HeldEvents, MIN(e.ReceivedAtUtc) AS OldestReceivedUtc,
       SUM(CASE WHEN e.Status = 6 THEN 1 ELSE 0 END) AS AlreadyFailed_NeedsRedelivery
FROM QueueingEvents e
WHERE e.Status IN (3, 6) AND e.OutcomeCode IN (N'COMPANY_NOT_MAPPED', N'TICKET_TYPE_NOT_MAPPED', N'REQUEST_TYPE_UNUSABLE')
GROUP BY e.OutcomeCode
ORDER BY e.OutcomeCode;
