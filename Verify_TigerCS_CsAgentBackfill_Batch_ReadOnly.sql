/*
================================================================================
 Verify_TigerCS_CsAgentBackfill_Batch_ReadOnly.sql              (READ-ONLY - SELECT only)

 Audits what the CS Agent backfill did. Set @BatchId to the id the run returned (or leave NULL for every batch). Nothing is written.
   1. Per batch: tickets assigned and the first/last time.
   2. Per agent: how the backfill spread the work (should be an even, sequential split, offset by wherever the shared cursor stood).
   3. Preservation check: a backfilled ticket must show NO department change (no 'Transfer' audit row at the same moment) and must still have exactly one
      current assignment row.
   4. Rotation cursor now.
================================================================================
*/
SET NOCOUNT ON;
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

DECLARE @BatchId nvarchar(64) = NULL;          -- e.g. N'a1b2c3...'; NULL = all batches
DECLARE @Like nvarchar(100) = N'%BatchId=' + ISNULL(@BatchId, N'') + N'%';

SELECT a.EntityId AS TicketId, a.OccurredAtUtc, a.AfterValue
INTO #B
FROM dbo.AuditEntries a
WHERE a.Action = N'BackfillAssign' AND a.EntityType = N'Ticket' AND (@BatchId IS NULL OR a.AfterValue LIKE @Like);

-- 1.
SELECT SUBSTRING(AfterValue, CHARINDEX(N'BatchId=', AfterValue) + 8, 64) AS BatchId, COUNT(*) AS TicketsAssigned,
       MIN(OccurredAtUtc) AS FirstUtc, MAX(OccurredAtUtc) AS LastUtc
FROM #B GROUP BY SUBSTRING(AfterValue, CHARINDEX(N'BatchId=', AfterValue) + 8, 64);

-- 2.
SELECT t.CurrentOwnerEmployeeId AS Agent, COUNT(*) AS Tickets
FROM #B b JOIN dbo.Tickets t ON CONVERT(nvarchar(40), t.TicketId) = b.TicketId
GROUP BY t.CurrentOwnerEmployeeId ORDER BY Tickets DESC;

-- 3a. Backfilled tickets with a department move at the same moment (must be 0 rows).
SELECT b.TicketId FROM #B b
JOIN dbo.AuditEntries x ON x.EntityId = b.TicketId AND x.Action = N'Transfer' AND ABS(DATEDIFF(SECOND, x.OccurredAtUtc, b.OccurredAtUtc)) < 5;

-- 3b. Backfilled tickets whose current-assignment count is not exactly 1 (must be 0 rows).
SELECT t.TicketId, COUNT(ta.TicketAssignmentId) AS CurrentRows
FROM #B b JOIN dbo.Tickets t ON CONVERT(nvarchar(40), t.TicketId) = b.TicketId
LEFT JOIN dbo.TicketAssignments ta ON ta.TicketId = t.TicketId AND ta.IsCurrent = 1
GROUP BY t.TicketId HAVING COUNT(ta.TicketAssignmentId) <> 1;

-- 4.
SELECT d.Name AS Department, r.LastAssignedEmployeeId AS SharedCursor, r.UpdatedAtUtc
FROM dbo.DepartmentAssignmentRotations r JOIN dbo.Departments d ON d.DepartmentId = r.DepartmentId WHERE d.Code = N'CC';

DROP TABLE #B;
