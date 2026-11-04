/*
================================================================================
 Diagnose_TigerCS_ResolvedTicketsSlaStateNotMet_ReadOnly.sql      (SELECT ONLY - NO WRITES)

 WHY: before the "Resolve stores SLA Met" fix, a ticket resolved INSIDE its SLA kept Tickets.SlaState = Running (1) because the
      evaluation could not read the resolution row staged in the same transaction. This lists the tickets that fix leaves behind in the
      database, so the business can decide whether (and how) to correct them. It changes NOTHING. No repair script is provided or run.

 SlaState: 1 Running, 2 Paused, 3 Met, 4 Breached, 5 NotApplicable.   TicketStatus: 5 Resolved, 6 Closed.
 Run on any environment from a read-only login. READ UNCOMMITTED. Times are UTC (add 4h for Asia/Dubai).
================================================================================
*/
SET NOCOUNT ON;
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

-- 1. How many finished tickets still carry each SLA state
SELECT t.TicketStatus, t.SlaState, COUNT(*) AS Tickets
FROM dbo.Tickets t
WHERE t.TicketStatus IN (5, 6)
GROUP BY t.TicketStatus, t.SlaState
ORDER BY t.TicketStatus, t.SlaState;

-- 2. The suspect rows: finished, current resolution is on/before the (current) period's resolution deadline, no breach flag, yet SlaState is
--    still Running (1) or Paused (2). A repair candidate is only a CANDIDATE: a reopened ticket's period/deadline differs from the one that
--    applied at the original resolution, so review each before any correction.
SELECT t.TicketId, t.TicketNumber, t.TicketStatus, t.SlaState, t.ReopenCount,
       r.ResolvedAtUtc, s.ResolutionDueAtUtc, s.ResolutionBreached, s.FirstResponseBreached, t.FirstHumanResponseAtUtc
FROM dbo.Tickets t
JOIN dbo.TicketResolutions r ON r.TicketId = t.TicketId AND r.IsCurrent = 1
JOIN dbo.TicketSlaInstances s ON s.TicketId = t.TicketId AND s.PeriodEndAtUtc IS NULL
WHERE t.TicketStatus IN (5, 6)
  AND t.SlaState IN (1, 2)
  AND s.ResolutionBreached = 0 AND s.FirstResponseBreached = 0
  AND r.ResolvedAtUtc <= s.ResolutionDueAtUtc
ORDER BY r.ResolvedAtUtc DESC;
