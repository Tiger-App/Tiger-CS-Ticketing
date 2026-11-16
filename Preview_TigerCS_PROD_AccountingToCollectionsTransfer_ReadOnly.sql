/*
================================================================================
 Preview_TigerCS_PROD_AccountingToCollectionsTransfer_ReadOnly.sql   (DRY RUN - NO WRITES)

 Dry-run report for retiring the Accounting (ACC) department. SELECT-only, READ UNCOMMITTED. Run from a read-only login.
 Nothing here transfers, assigns, approves, cancels or deactivates anything.

 Status codes (TicketStatus): 1 Open, 2 InProgress, 3 PendingCustomer, 4 PendingThirdParty(legacy), 5 Resolved, 6 Closed.
 "Final" = Closed (6) - the only status TicketAssignmentAppService.TransferAsync refuses (TicketClosed). Resolved (5) is NOT refused by the
 service, so it is reported in its own bucket for a decision rather than silently included or dropped.
 Approval status: 1 Pending, 2 Approved, 3 Rejected, 4 Cancelled.

 Result sets:
   1  Departments        - ACC and Collections resolved by Code (verify both exist, exactly one row each, Collections active)
   2  Summary            - counts by bucket
   3  Tickets to move    - every non-final ticket currently in ACC (the transfer population), with owner, status, SLA state, unit/tower evidence
   4  Pending approvals  - Pending TicketApprovals that touch ACC (ticket currently in ACC, OR approval targets ACC) - NOT to be touched
   5  Request types      - request types / approval requirements still pointing at ACC (must be re-pointed before deactivation)
   6  ACC members        - employees whose membership would be left behind
   7  Closed tickets     - count only, for the record (never modified)
================================================================================
*/
SET NOCOUNT ON;
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

DECLARE @AccCode  nvarchar(10) = N'ACC';
DECLARE @ColCode  nvarchar(10) = N'COL';   -- CONFIRM: must equal the configured Collections department code (CollectionsOptions.CollectionsDepartmentCode)

DECLARE @Acc int = (SELECT DepartmentId FROM dbo.Departments WHERE Code = @AccCode);
DECLARE @Col int = (SELECT DepartmentId FROM dbo.Departments WHERE Code = @ColCode);

-- 1 ---------------------------------------------------------------------------
SELECT 'Departments' AS ResultSet, DepartmentId, Code, Name, IsActive
FROM dbo.Departments WHERE Code IN (@AccCode, @ColCode);

-- 2 ---------------------------------------------------------------------------
SELECT 'Summary' AS ResultSet,
       SUM(CASE WHEN TicketStatus <> 6 THEN 1 ELSE 0 END)                                           AS NonFinalInAcc,
       SUM(CASE WHEN TicketStatus IN (1,2,3,4) THEN 1 ELSE 0 END)                                    AS OpenLikeInAcc,
       SUM(CASE WHEN TicketStatus = 5 THEN 1 ELSE 0 END)                                             AS ResolvedInAcc_DecisionNeeded,
       SUM(CASE WHEN TicketStatus = 6 THEN 1 ELSE 0 END)                                             AS ClosedInAcc_Untouched,
       SUM(CASE WHEN TicketStatus <> 6 AND CurrentOwnerEmployeeId IS NOT NULL THEN 1 ELSE 0 END)     AS NonFinalWithOwner,
       SUM(CASE WHEN TicketStatus <> 6 AND EXISTS (SELECT 1 FROM dbo.TicketApprovals a
                 WHERE a.TicketId = t.TicketId AND a.Status = 1) THEN 1 ELSE 0 END)                  AS NonFinalWithPendingApproval
FROM dbo.Tickets t WHERE t.CurrentDepartmentId = @Acc;

-- 3 ---------------------------------------------------------------------------
SELECT 'TicketsToMove' AS ResultSet,
       t.TicketId, t.TicketNumber, t.TicketStatus, t.SlaState, t.PriorityId, t.CreatedAtUtc,
       t.OriginatingDepartmentId, t.CurrentDepartmentId, t.CurrentOwnerEmployeeId, t.RequestTypeId,
       t.UnitReferenceId, t.CrmBuyerUnitNumber, t.ManualUnitNumber,
       CASE WHEN t.CrmBuyerUnitNumber IS NULL AND t.ManualUnitNumber IS NULL THEN 'NO UNIT ON TICKET - Collections guard will likely refuse (Tower+Unit required)' ELSE 'unit present' END AS CollectionsUnitEvidence,
       (SELECT COUNT(*) FROM dbo.TicketApprovals a WHERE a.TicketId = t.TicketId AND a.Status = 1) AS PendingApprovals,
       (SELECT COUNT(*) FROM dbo.TicketAssignments x WHERE x.TicketId = t.TicketId)               AS AssignmentHistoryRows
FROM dbo.Tickets t
WHERE t.CurrentDepartmentId = @Acc AND t.TicketStatus <> 6
ORDER BY t.TicketStatus, t.TicketId;

-- 4 ---------------------------------------------------------------------------
SELECT 'PendingApprovals' AS ResultSet,
       a.TicketApprovalId, a.TicketId, t.TicketNumber, t.CurrentDepartmentId, a.ApprovalType, a.TargetKind,
       a.TargetDepartmentId, a.TargetRoleName, a.TargetEmployeeId, a.RequestedByEmployeeId, a.RequestedAtUtc, a.IsCurrent,
       CASE WHEN a.TargetDepartmentId = @Acc THEN 'TARGETS ACC (approvers are ACC members)' ELSE '' END AS TargetsAcc,
       CASE WHEN t.CurrentDepartmentId = @Acc THEN 'ticket in ACC' ELSE '' END                           AS TicketInAcc
FROM dbo.TicketApprovals a JOIN dbo.Tickets t ON t.TicketId = a.TicketId
WHERE a.Status = 1 AND (a.TargetDepartmentId = @Acc OR t.CurrentDepartmentId = @Acc)
ORDER BY a.RequestedAtUtc;

-- 5 ---------------------------------------------------------------------------
SELECT 'RequestTypesPointingAtAcc' AS ResultSet, rt.RequestTypeId, rt.Name, rt.IsActive,
       (SELECT COUNT(*) FROM dbo.Tickets t WHERE t.RequestTypeId = rt.RequestTypeId AND t.TicketStatus <> 6) AS NonFinalTickets
FROM dbo.RequestTypes rt WHERE rt.DepartmentId = @Acc;

SELECT 'ApprovalRequirementsTargetingAcc' AS ResultSet, r.RequestTypeApprovalRequirementId, r.RequestTypeId, rt.Name AS RequestType,
       r.ApprovalType, r.BlocksWorkUntilApproved, r.IsActive
FROM dbo.RequestTypeApprovalRequirements r JOIN dbo.RequestTypes rt ON rt.RequestTypeId = r.RequestTypeId
WHERE r.TargetDepartmentId = @Acc;

-- 6 ---------------------------------------------------------------------------
SELECT 'AccMembers' AS ResultSet, u.EmployeeId, e.DisplayName, e.DeactivatedAtUtc, u.IsPrimary
FROM dbo.UserDepartmentAssignments u JOIN dbo.Employees e ON e.EmployeeId = u.EmployeeId
WHERE u.DepartmentId = @Acc;

-- 7 ---------------------------------------------------------------------------
SELECT 'ClosedInAcc_NeverModified' AS ResultSet, COUNT(*) AS ClosedTickets
FROM dbo.Tickets WHERE CurrentDepartmentId = @Acc AND TicketStatus = 6;
