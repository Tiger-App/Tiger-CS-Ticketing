/*
================================================================================
 Diagnose_TigerCS_SlaState_ResolvedWithinSla_ReadOnly.sql          (SELECT ONLY - NO WRITES)

 WHY: until the fix in this change, a ticket resolved WITHIN its SLA was never stored as SlaState = Met (3): the breach processor judged the
      resolution before it was saved, found none, and left the ticket-level state at Running (1). Tickets resolved late were still stored as
      Breached (4) correctly. This script counts and lists the tickets resolved before the fix whose stored state is still Running/Paused although
      both clocks were actually satisfied, so the owner can decide whether to correct them. It changes nothing.

 CRITERIA (all must hold):
   - ticket is Resolved (5) or Closed (6)  [TicketStatus values per the TicketStatus enum]
   - ticket SlaState is Running (1) or Paused (2)
   - it has a CURRENT resolution (IsCurrent = 1) whose ResolvedAtUtc <= the current SLA period's ResolutionDueAtUtc
   - neither FirstResponseBreached nor ResolutionBreached is set on that period
 NOT decided here: a ticket that was resolved with no first human response recorded and whose First Response deadline had not yet fallen due.
 It matches the criteria above but is a business-rule question (is "no first response yet" Met?), so it is flagged in the FirstResponse column.

 READ-ONLY: only SELECTs; READ UNCOMMITTED; safe from a db_datareader login. Times are stored UTC (Asia/Dubai = UTC+4, no daylight saving).
================================================================================
*/
SET NOCOUNT ON;
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

-- 1. How many, by stored state and ticket status
SELECT t.SlaState AS StoredSlaState, t.TicketStatus, COUNT(*) AS Tickets
FROM dbo.Tickets t
JOIN dbo.TicketSlaInstances i ON i.TicketId = t.TicketId AND i.PeriodEndAtUtc IS NULL
JOIN dbo.TicketResolutions r  ON r.TicketId = t.TicketId AND r.IsCurrent = 1
WHERE t.TicketStatus IN (5, 6)
  AND t.SlaState IN (1, 2)
  AND i.FirstResponseBreached = 0 AND i.ResolutionBreached = 0
  AND r.ResolvedAtUtc <= i.ResolutionDueAtUtc
GROUP BY t.SlaState, t.TicketStatus
ORDER BY t.SlaState, t.TicketStatus;

-- 2. The tickets themselves (newest resolution first)
SELECT t.TicketId, t.TicketNumber, t.TicketStatus, t.SlaState AS StoredSlaState,
       t.CreatedAtUtc, i.FirstResponseDueAtUtc, t.FirstHumanResponseAtUtc,
       CASE WHEN t.FirstHumanResponseAtUtc IS NULL THEN 'NOT RECORDED' ELSE 'recorded' END AS FirstResponse,
       i.ResolutionDueAtUtc, r.ResolvedAtUtc,
       DATEDIFF(MINUTE, r.ResolvedAtUtc, i.ResolutionDueAtUtc) AS MinutesToSpare
FROM dbo.Tickets t
JOIN dbo.TicketSlaInstances i ON i.TicketId = t.TicketId AND i.PeriodEndAtUtc IS NULL
JOIN dbo.TicketResolutions r  ON r.TicketId = t.TicketId AND r.IsCurrent = 1
WHERE t.TicketStatus IN (5, 6)
  AND t.SlaState IN (1, 2)
  AND i.FirstResponseBreached = 0 AND i.ResolutionBreached = 0
  AND r.ResolvedAtUtc <= i.ResolutionDueAtUtc
ORDER BY r.ResolvedAtUtc DESC;

-- 3. Sanity: the stored-state distribution of ALL resolved/closed tickets, for context
SELECT t.SlaState AS StoredSlaState, t.TicketStatus, COUNT(*) AS Tickets
FROM dbo.Tickets t
WHERE t.TicketStatus IN (5, 6)
GROUP BY t.SlaState, t.TicketStatus
ORDER BY t.SlaState, t.TicketStatus;
