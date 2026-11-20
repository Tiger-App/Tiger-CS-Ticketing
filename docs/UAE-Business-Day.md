# UAE business day — before / after (for Operations)

**What changed.** Every timestamp TigerCS stores is still UTC; nothing in the database was touched. What changed is
what counts as "a day": every business-day filter, the dashboard's "today", daily/monthly period reports and the
dates in exports now use the **UAE calendar day** (Asia/Dubai, UTC+4, no daylight saving). A UAE day
`[00:00, 24:00)` is the UTC range `[previous day 20:00Z, day 20:00Z)`.

**Why.** Until now the day boundaries were UTC midnight, which is 04:00 in the UAE. Anything created
**between 00:00 and 04:00 UAE** was filed under the *previous* date, so "today" on the dashboard stayed on yesterday until 04:00
and a ticket opened at 00:30 on the 10th showed up in the 9th's figures.

One helper owns the rule: `TigerCS.Domain.Common.UaeCalendar` (`DayStartUtc`, `DayRangeUtc`, `MonthRangeUtc`, `ToUaeDate`,
`ToUae`). It always uses Asia/Dubai (Windows id `Arabian Standard Time`, else a fixed +04:00) — never the server's own zone.
SQL still compares the **UTC column** against UTC instants, so existing indexes stay usable.

## The timezone standard (system-wide)

| Layer | Zone |
|---|---|
| External integration timestamps (Genesys, CRM, Queueing, PACT instants) | UTC |
| Database persistence | UTC |
| Backend timestamp calculations (SLA durations, ageing, due times) | UTC |
| Every user-facing timestamp (screens, emails, exports) | UAE time (UTC+4) |
| Business-day filters, daily and monthly reports | UAE calendar boundaries |

There is **one** implementation of the UAE rules, `TigerCS.Domain.Common.UaeCalendar`. `TicketDisplay.ToUae` / `AsUtc` (the UI helper from the Genesys hotfix)
and the customer-email date formatting are thin delegates to it; nothing else adds four hours by hand. Configured Collections / SLA / job-schedule zones
(`Collections:TimeZoneId`, the SLA business calendar's `TimeZone`, Hangfire cron zones) are separate, explicitly configured business calendars and are unchanged.

**SLA durations are unchanged.** Deadlines, breach times and elapsed/overdue durations are computed from UTC instants exactly as before; only how a
timestamp is *displayed* (UAE) and where a *day* starts for a filter/report changed.

## No migration, no configuration change

No schema change, no data rewrite, no new or changed setting, no new environment variable. The release is code-only and can be rolled back by redeploying the previous build.

## What changed, screen by screen

| Area | Before | After |
|---|---|---|
| Dashboard — **Due Today** card and the Tickets list `dueToday` filter | UTC day | UAE day |
| Dashboard — date range (Volume by channel / request type / department / status / priority, ageing, Customers by Location) | `dateFrom..dateTo` = UTC days | UAE days |
| Dashboard — default period ("last 30 days, today included") | ends on the UTC date | ends on the UAE date |
| Dashboard — **Resolved Today** (summary) | since UTC midnight | since UAE midnight |
| Dashboard — Closure KPIs and the queue they link to (`closedFrom`/`closedTo`) | UTC days | UAE days |
| Tickets list — `createdFrom`/`createdTo` | UTC days | UAE days |
| Customers directory — dashboard date filter | UTC days | UAE days |
| Team Performance report, records drill-down and SLA details — period and default period | UTC days | UAE days |
| Team Performance SLA details — timestamps and explanations | shown in **UTC** | shown in **UAE time**, columns labelled "(UAE)" |
| SLA calendar — upcoming holidays ("today" cut-off) | UTC date | UAE date |
| Web pages that used `.ToLocalTime()` (Dashboard, Tickets rows, Customers, Customer profile, New ticket, Pending interactions, Team Performance records, Admin users/workflows, payment tab) | the **web server's** zone (UTC on the servers, so UTC) | UAE time on every machine |
| Collections admin email — "Approved at" | `… UTC` | `… UAE (Asia/Dubai)` |
| Collections exports — file names (`…-yyyyMMdd-HHmm.csv`) and the "PreviousDispatch" date | UTC | UAE |
| Collections pages — "today" default / `DubaiToday()` and the receivables-status date | already UTC+4 (unchanged result), now via the shared helper | same result |

Not changed on purpose: Collections business date, campaign cycle and history ranges (already used the Dubai zone), SLA
business-hours calculation (already an explicit Asia/Dubai `TimeZoneInfo`), instalment/voucher **due dates** (calendar dates
from the source systems, not instants), and all Genesys ownership / timestamp-ingest code.

## Numbers that will visibly shift

For any day **D**, tickets/closures/resolutions/work/due times that fall between **00:00 and 04:00 UAE** move from **D−1** to **D**.
In practice:

1. **Every daily figure on the dashboard** (volume for a given date, Closed-by-AI/Agent, Reviewed / Not reviewed, Customers by
   Location) loses its 00:00–04:00 UAE items from the previous date and gains them on the right date. Typical effect: the
   *previous* day's count drops slightly and the *current* day's rises. Ranges spanning several whole days change only at their two ends.
2. **"Due Today" and "Resolved Today"** now reset at 00:00 UAE instead of 04:00 UAE. Right after midnight they read lower
   (nothing yet) where they previously still showed yesterday's numbers until 04:00.
3. **Team Performance** (tickets worked, follow-ups, SLA breaches/compliance) per day or short period shifts the same way. A
   30-day default period moves by four hours at each end, so totals change by a handful of items, not by a day.
4. **Exports** compared with last week's: file-name times and the previous-dispatch date are UAE, not UTC.
5. Ticket timestamps in the SLA details view are now 4 hours *later* on screen than before (same instant, shown in UAE time).

Nothing is re-stated retroactively in the database; the same stored data simply reads under the correct UAE date.
Reports saved or screenshotted before the release will not match a re-run for the 00:00–04:00 UAE slice. Worth telling people before go-live.

## Tests

* `UaeCalendarTests` — the helper at 19:59:59Z vs 20:00:00Z, month ends, leap day, and no dependence on the host zone.
* `UaeBusinessDayFilterTests` (SQLite) — tickets at 23:30 / 00:30 / 03:59 UAE (and the two seconds around the day) through the dashboard
  volume, default "today", Due Today, ticket list created range, `dueToday`, closure ranges.
* `TeamPerformanceReportTests` — worked-ticket period and default period at 00:30 UAE.
* `UaeBusinessDaySqlServerTests` — the created range, volume and Due Today on a real SQL Server (`TIGERCS_TEST_SQL_SERVER`).
* CI runs the same set again with `TZ=Asia/Dubai`.
