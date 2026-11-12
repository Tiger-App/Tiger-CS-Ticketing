/*
================================================================================
 Diagnose_TigerCS_AssignmentEndTimes_ReadOnly.sql                   (SELECT ONLY - NO WRITES)

 WHY: SLA reporting attributes a breach to whoever held the ticket at the breach DEADLINE. That needs to know when an assignment ENDED.
      TicketAssignments.SupersededAtUtc (migration AddTicketAssignmentSupersededAt) records it from deployment onward; rows that ended before
      that have no recorded end time. This script says how many such rows exist, which of them are safely reconstructible from facts already
      in the database, and which are not - and how many genuine SLA breach events sit on a holder whose assignment end is unknown.

      NOTHING IS BACKFILLED. A guessed end time would silently change who a breach belongs to; the column stays NULL for history.

 CLASSES (every TicketAssignments row, first match wins):
   CURRENT              IsCurrent = 1: the assignment has not ended
   ENDED_STAMPED        IsCurrent = 0 and SupersededAtUtc is set (ended after the migration was deployed): exact
   REPLACED_EXACT       IsCurrent = 0, no stamp, a LATER assignment row exists: it ended when the next one began. The report already reads it that way
                        ("latest assignment at or before the deadline"), so no stored value is needed - exact
   ENDED_ON_CLOSURE     IsCurrent = 0, no stamp, no later row, ticket Closed: released by the closure. No SLA deadline can pass after closure, so it cannot
                        change an attribution - harmless
   RELEASED_END_UNKNOWN IsCurrent = 0, no stamp, no later row, ticket NOT Closed: released (returned to a queue, transferred out with no new owner ...).
                        The release moment was never recorded on the row; the report treats the holder as still holding - the only class that can matter

 For RELEASED_END_UNKNOWN the script also counts rows with an audited release candidate (AuditEntries 'Unassign', 'ReturnToCallCenterQueue',
 'CollectionsAutoAssignSkipped' with Result=ReleasedToCollectionsQueue). That is a COUNT ONLY, not a source for backfill: the audit time is when the audit
 row was written, several actions can exist for one ticket, and nothing ties an audit row to this assignment row.

 READ-ONLY: only SELECTs; READ UNCOMMITTED; safe from a db_datareader login at any time.
================================================================================
*/
SET NOCOUNT ON;
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

DECLARE @From datetime2 = '2026-01-01T00:00:00';     -- period for the breach-impact section (UTC, inclusive)
DECLARE @To   datetime2 = SYSUTCDATETIME();          -- exclusive

IF OBJECT_ID(N'dbo.TicketAssignments') IS NULL OR COL_LENGTH(N'dbo.TicketAssignments', N'SupersededAtUtc') IS NULL
    THROW 50400, 'ABORTED: TicketAssignments.SupersededAtUtc does not exist - deploy AddTicketAssignmentSupersededAt.sql first.', 1;

-- 1. Every assignment row, classified -------------------------------------------------------------------------------------------------------------
;WITH Classified AS (
    SELECT a.TicketAssignmentId, a.TicketId, a.AssignedAtUtc,
           CASE
               WHEN a.IsCurrent = 1 THEN 'CURRENT'
               WHEN a.SupersededAtUtc IS NOT NULL THEN 'ENDED_STAMPED'
               WHEN s.TicketAssignmentId IS NOT NULL THEN 'REPLACED_EXACT'
               WHEN t.TicketStatus = 6 THEN 'ENDED_ON_CLOSURE'
               ELSE 'RELEASED_END_UNKNOWN'
           END AS Class
    FROM dbo.TicketAssignments a
    JOIN dbo.Tickets t ON t.TicketId = a.TicketId
    OUTER APPLY (SELECT TOP (1) n.TicketAssignmentId
                 FROM dbo.TicketAssignments n
                 WHERE n.TicketId = a.TicketId
                   AND (n.AssignedAtUtc > a.AssignedAtUtc OR (n.AssignedAtUtc = a.AssignedAtUtc AND n.TicketAssignmentId > a.TicketAssignmentId))
                 ORDER BY n.AssignedAtUtc, n.TicketAssignmentId) s
)
SELECT Class, COUNT(*) AS AssignmentRows
FROM Classified
GROUP BY Class
ORDER BY CASE Class WHEN 'CURRENT' THEN 1 WHEN 'ENDED_STAMPED' THEN 2 WHEN 'REPLACED_EXACT' THEN 3 WHEN 'ENDED_ON_CLOSURE' THEN 4 ELSE 5 END;

-- 2. The only class that can matter: released while the ticket was open, end time never recorded --------------------------------------------------
;WITH Unknown AS (
    SELECT a.TicketAssignmentId, a.TicketId, a.AssignedAtUtc
    FROM dbo.TicketAssignments a
    JOIN dbo.Tickets t ON t.TicketId = a.TicketId
    WHERE a.IsCurrent = 0 AND a.SupersededAtUtc IS NULL AND t.TicketStatus <> 6
      AND NOT EXISTS (SELECT 1 FROM dbo.TicketAssignments n
                      WHERE n.TicketId = a.TicketId
                        AND (n.AssignedAtUtc > a.AssignedAtUtc OR (n.AssignedAtUtc = a.AssignedAtUtc AND n.TicketAssignmentId > a.TicketAssignmentId)))
)
SELECT COUNT(*) AS ReleasedEndUnknownRows,
       SUM(CASE WHEN EXISTS (SELECT 1 FROM dbo.AuditEntries e
                             WHERE e.EntityType = N'Ticket' AND e.EntityId = CAST(u.TicketId AS nvarchar(50)) AND e.OccurredAtUtc >= u.AssignedAtUtc
                               AND (e.Action IN (N'Unassign', N'ReturnToCallCenterQueue')
                                    OR (e.Action = N'CollectionsAutoAssignSkipped' AND e.AfterValue LIKE N'%ReleasedToCollectionsQueue%')))
                THEN 1 ELSE 0 END) AS WithAnAuditedReleaseCandidate_CountOnly_NotABackfillSource
FROM Unknown u;

-- 3. Data quality: a row still flagged current while the ticket has no owner (an owner cleared without ending the assignment) -----------------------
SELECT COUNT(*) AS CurrentAssignmentButTicketHasNoOwner
FROM dbo.TicketAssignments a
JOIN dbo.Tickets t ON t.TicketId = a.TicketId
WHERE a.IsCurrent = 1 AND t.CurrentOwnerEmployeeId IS NULL AND t.TicketStatus <> 6;

-- 4. Impact: genuine SLA breach events in the period whose holder row's end time is unknown ---------------------------------------------------------
-- (the recorded moment stands in for the deadline here - the exact deadline is read from the SLA period by the application)
;WITH Events AS (
    SELECT h.TicketStatusHistoryId, h.TicketId, h.OccurredAtUtc
    FROM dbo.TicketStatusHistory h
    WHERE h.Dimension = 4 AND h.NewValue = 4
      AND h.OccurredAtUtc >= @From AND h.OccurredAtUtc < @To
      AND (h.Note LIKE N'FirstResponse SLA breached%' OR h.Note LIKE N'Resolution SLA breached%')
),
Holders AS (
    SELECT ev.TicketStatusHistoryId, ev.TicketId,
           a.IsCurrent, a.SupersededAtUtc, t.TicketStatus,
           CASE WHEN a.TicketAssignmentId IS NULL THEN 1 ELSE 0 END AS NoHolderRow,
           CASE WHEN a.TicketAssignmentId IS NOT NULL AND a.IsCurrent = 0 AND a.SupersededAtUtc IS NULL AND t.TicketStatus <> 6
                 AND NOT EXISTS (SELECT 1 FROM dbo.TicketAssignments n
                                 WHERE n.TicketId = a.TicketId
                                   AND (n.AssignedAtUtc > a.AssignedAtUtc OR (n.AssignedAtUtc = a.AssignedAtUtc AND n.TicketAssignmentId > a.TicketAssignmentId)))
                THEN 1 ELSE 0 END AS HolderEndUnknown
    FROM Events ev
    JOIN dbo.Tickets t ON t.TicketId = ev.TicketId
    OUTER APPLY (SELECT TOP (1) x.TicketAssignmentId, x.AssignedAtUtc, x.IsCurrent, x.SupersededAtUtc
                 FROM dbo.TicketAssignments x
                 WHERE x.TicketId = ev.TicketId AND x.AssignedAtUtc <= ev.OccurredAtUtc
                 ORDER BY x.AssignedAtUtc DESC, x.TicketAssignmentId DESC) a
)
SELECT COUNT(*) AS GenuineBreachEventsInPeriod,
       SUM(NoHolderRow) AS NobodyHeldIt,
       SUM(HolderEndUnknown) AS HolderRowEndUnknown_AttributionMayBeWrong
FROM Holders;
