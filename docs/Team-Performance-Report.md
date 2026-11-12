# Team Performance Report (CS Manager)

The Team Performance report (`/Reports/TeamPerformance`) gives a CS Manager
one row per **active employee in every department** with four counts, a
totals row, and — behind every count — the list of tickets it was computed
from. The **Tickets with SLA Breach** count (and the other SLA figures) opens a dedicated SLA details page
(`/Reports/TeamPerformanceSla`) that shows how the number was calculated
(see [SLA details](#sla-details-behind-the-sla-breaches-count)). Every number comes from one
read, `GET /api/reports/team-performance`, computed in the database by
`TeamPerformanceQueryRepository`; every list from
`GET /api/reports/team-performance/records`, produced by the very same
predicate the count used, so a number and its list always agree.

## Who may open it

- Api: `ReportsController` carries `PolicyNames.CsManagerOrGeneralManager`
  — CS Manager, General Manager, Chairman/CEO — and System Administrator
  passes through the ADR-0024 override. Every other role receives 403.
- Web: the `/Reports` folder is gated by `ReportsPolicy` (the same roles),
  and the "Team Performance" nav item is shown to those roles only. That is
  presentation; the Api is the enforcement point. A 403 from the Api is
  rendered as a "No permission" state, never as an empty table.
- Nothing is scoped by the caller's departments: the roles that may open
  the report already see every department.

## Who is listed

- Every **active** employee (`Employees.DeactivatedAtUtc IS NULL`) in
  **every department** — Customer Service, Call Center, Collections and any
  other department configured in the system. Nothing is hardcoded: employees,
  roles (`AspNetUserRoles` / `AspNetRoles`) and department memberships
  (`UserDepartmentAssignments` / `Departments`) are all read from the
  database. A configured service identity (the Genesys integration account)
  is not a person and is not listed.
- **One row per employee**, however many roles or departments they hold.
  Roles are shown comma-joined (alphabetical); departments comma-joined,
  primary first.
- Employees with **no activity** in the period are included with zeros.
- Rows are sorted by display name.
- There is no "agent type" any more: a Call Center agent is simply an
  employee whose departments include Call Center — filter by Department.

## Filters

All optional, kept in the page URL (a GET form):

| Parameter | Meaning |
| --- | --- |
| `dateFrom`, `dateTo` | UTC calendar days, inclusive. Default: the last 30 days, today included (the Dashboard's `ResolvePeriod`). A reversed range is swapped. |
| `employeeId` | One active employee. An employee who is not on the report yields no rows. |
| `role` | An Identity role name (case-insensitive); employees holding it. An unknown value is ignored. |
| `departmentId` | Employees who are members (primary or not) of that department. |

The pickers always offer **everyone**, **every configured (active) department**
— including one with no members yet — and **every role held by an active
employee**, not only what the current filters left. Filters combine (AND).

## Authorization and visibility

Unchanged by the department/employee extension: the report is for the CS
Manager tier only (see *Who may open it*). Nothing is scoped by the
caller's departments, because the roles that may open it already see every
department; a CS Agent, Supervisor, Department Head/Employee or Reporting
User receives **403** on every report endpoint, including the SLA details.

## Metric definitions

| Metric | Definition |
| --- | --- |
| **Currently Assigned** | Current state, **not period-bound**: tickets whose `CurrentOwnerEmployeeId` is the employee and whose `TicketStatus` is not `Closed`. |
| **Tickets Worked** | **Distinct ticket ids** on which the employee appears as the actor of at least one recorded action within the period: a `TicketAssignment` assigned to them (`AssignedAtUtc` in period); a `TicketStatusHistory` row with `ActorEmployeeId` = them; a `TicketNote` they authored; a `TicketResolution` with `ResolvingEmployeeId` = them; a `TicketWorkflowEvent` with `ActorEmployeeId` = them; or a `TicketAgentHandoff` assigned to them whose `AssignedAtUtc` or `CompletedAtUtc` is in the period. Measured from history — **not** from current ownership: a ticket the employee holds now but did nothing on in the period does not count. |
| **Completed Follow-ups** | `TicketAgentHandoff` rows with `Status = Completed`, `AssignedEmployeeId` = the employee and `CompletedAtUtc` in the period. Counts **work items, not tickets**: a ticket with two completed follow-ups counts twice. |
| **Tickets with SLA Breach** | **Distinct tickets** with at least one *genuine SLA breach event* in the period, attributed to the employee. See [What counts as a breach](#what-counts-as-a-breach) and [attribution](#who-a-breach-belongs-to). |
| **Breach Events** | The genuine breach events themselves: one per (ticket, clock, deadline), so a ticket whose First Response *and* Resolution both breached is two events (and a reopened ticket's new Resolution cycle breaching is another). |
| **SLA Evaluated** | Finalized SLA clock cycles in the period: **SLA Met + Breach Events**. Pending and not-applicable clocks are excluded. |
| **SLA Met** | Clock cycles achieved on time in the period (a first human response, or a resolution, on or before its due time with no breach flagged), credited to the holder at that moment. |
| **SLA Compliance %** | **SLA Met ÷ SLA Evaluated × 100** (one decimal); blank when nothing was evaluated. |

"In the period" means `OccurredAtUtc >= dateFrom 00:00 UTC` and
`< dateTo + 1 day 00:00 UTC`.

### Totals row

Every figure is the sum of the rows shown (after the Date, Employee, Role
and Department filters), with two exceptions that are not sums:
**Tickets with SLA Breach** is the number of *distinct tickets across the
employees shown* (a ticket whose First Response breached under one employee and
Resolution under another is one ticket), and **SLA Compliance %** is the combined
Met ÷ combined Evaluated — never an average of the rows' percentages. Tickets Worked
is summed per employee, so a ticket two agents both
worked counts once for each of them — the total is the team's effort, not a
ticket count.

## Records behind a count

`GET /api/reports/team-performance/records?employeeId=&metric=&dateFrom=&dateTo=`
with `metric` one of `CurrentlyAssigned`, `TicketsWorked`,
`CompletedFollowUps`, `SlaBreaches`. The period is resolved exactly as on the
report. Each record carries the ticket id, number, summary, status,
priority, current department name and creation time, plus:

- `breachedAtUtc` for SLA Breaches = Tickets with SLA Breach (the earliest genuine breach event in the period for
  that ticket);
- `completedAtUtc` for Completed Follow-ups (one row per follow-up).

On the page, the Tickets with SLA Breach number does **not** open this panel; it opens
the SLA details page below, which lists the same tickets with the full
calculation. (`metric=SlaBreaches` still works on the API and by URL.)

Outcomes: `400` for an unknown metric, `404` for an employee who is not on
the report (expressed as `TeamPerformanceRecordsOutcome`, never an
exception). On the page the records render as a panel under the report
(`?employeeId=..&metric=..&dateFrom=..&dateTo=..`), with every row linking to
`/Tickets/{id}`; the report above it is narrowed to that employee, with a
"Show all agents" link back.

## SLA details behind the SLA numbers

`GET /api/reports/team-performance/sla-details?employeeId=&dateFrom=&dateTo=&role=&departmentId=&status=&sortBy=&sortDir=&page=&pageSize=`
— page: `/Reports/TeamPerformanceSla` (same query string; the report's link
carries the period, role and department filters and opens on `status=Breached`).

**It changes no SLA rule.** It restates what the SLA engine recorded and
decides nothing; targets, due dates, breach detection and attribution are
exactly those documented in `docs/architecture/SLA-Architecture.md` and
implemented by `SlaDueDateService`, `SlaBreachProcessor`,
`FirstHumanResponseRecorder` and `SlaPauseService`.

### What counts as a breach

Only a **genuine SLA breach event**: a `TicketStatusHistory` row on the SlaState
dimension (new value Breached) that `SlaBreachProcessor` wrote for a clock, recognised by
its note — `FirstResponse SLA breached (due …)` or `Resolution SLA breached (due …)`.
The two prefixes are constants shared by the processor (which writes the note) and the
report (which reads it): `SlaBreachNotes`. `SlaBreachNoteContractTests` pins both exact
prefixes and runs the real processor and recorder to prove what each writes.

- **Not a breach:** the `First human response recorded …` row that `FirstHumanResponseRecorder`
  writes on *every* first response. On a ticket that is already Breached it is a
  `Breached → Breached` row; the old report counted it, which re-counted a breached ticket in
  every later period in which somebody answered it. It is bookkeeping, not a missed deadline.
- **Is a breach:** a `Breached → Breached` row that carries a breach note — the *second* clock
  breaching on an already-Breached ticket (or a reopened ticket's new Resolution cycle).
- *Why the note and not a structured source.* The deadline-missed fact has no dedicated column.
  Alternatives considered: the `DetectSlaBreach` audit entry (structured JSON, written in the same
  transaction) would need JSON parsing in SQL on a large generic table and gives the clock only
  through a flag name; the `TicketSlaInstances` breach flags say *whether* but not *when*, so
  they cannot date a breach to a period; the idempotency key is a pruned string. The history note is
  one indexed row per event, covers all history since the engine existed, is written and read from the
  same constants, and is contract-tested.

### Who a breach belongs to

The employee who held the ticket at the **breach deadline** — the due time of the clock's SLA period —
*not* at the moment the scheduler happened to record it. A delayed write therefore cannot move a breach
to a later holder. Holder = the latest `TicketAssignment` at or before the deadline, unless that
assignment had already **ended** (reassigned-away is handled by "latest at or before"; **released** to a
queue or closed) by then, in which case nobody held it and the breach belongs to nobody. If a breach has no
SLA period to read a deadline from, the recorded moment is used. Met cycles use the moment of the action.

Assignment end times come from `TicketAssignments.SupersededAtUtc` (migration
`AddTicketAssignmentSupersededAt`), stamped by `TigerCsDbContext` on save for *every* code path that
supersedes an assignment. **Historical data:** rows ended before the column existed have no end time. For
those that were replaced, "latest at or before" is still exact; for those that were *released with no
successor*, the release moment was never recorded and is not invented — they are treated as still held, i.e.
as before. The column fills forward from deployment.

The period still means "recorded in the period" for breaches and "action happened in the period" for Met.

### Evaluations

| Status | Population (all attributed to one employee, within the period) |
| --- | --- |
| **Breached** | One record per **breach event** (above). Employee = holder at the deadline. |
| **Met** | A first human response (`Ticket.FirstHumanResponseAtUtc`) or a resolution (`TicketResolution.ResolvedAtUtc`) recorded in the period, **on or before** the SLA period's due time, with that clock's breach flag not set. One Resolution cycle per SLA period (a reopen starts a new one); First Response is carried across periods and counted once. Employee = holder at the moment of the action. |
| **Pending** | A clock still open on a non-Closed ticket the employee holds **now**: no first human response / resolution in the current period and not breached. Current state, like Currently Assigned. **Not evaluated** — shown for context only. |

`SLA Evaluated = Met + Breach Events`; `SLA Compliance % = Met ÷ Evaluated × 100`. The main table's rows,
its totals row and the employee-details summary all call the same aggregation (`SlaCounts.Of`), and the
details list is the same set of events enriched with the facts they were decided from (`GetSlaEventsAsync`).
The employee details also split Breach Events into First Response and Resolution.

**Impact on past numbers.** Wherever a breached ticket was answered later (or a first response was recorded
on a ticket already Breached) in a later period, the old count included that ticket again; the corrected count
does not. Past periods will read lower by that amount. The size is not known from the code alone — run the
read-only query in the PR description against production to quantify it.

### Fields

Ticket number (link to Ticket Details), department responsible (the department
of the holder's assignment), SLA type, ticket created, SLA started, SLA target
(configured duration + clock basis), SLA due, actual response / resolution,
status, breach duration, assigned employee, SLA policy applied and a
plain-language reason. All timestamps are **UTC**, formatted
`yyyy-MM-dd HH:mm`.

- *SLA started*: First Response = the first SLA period's clock start (it is never restarted); Resolution = the start of the period in force.
- *SLA due*: the due time the system holds, pauses already added (`TicketSlaInstances`).
- *Actual*: first human response / the first resolution recorded in that period.
- *Breach duration*: actual − due; while the action has not happened yet, now − due, marked "(ongoing)".
- *SLA policy*: the priority, whether a request-type SLA row or the priority policy governed it, the target and clock basis, any paused time, and why a configured request-type SLA was not applied (all from the snapshot on the SLA period).

### The rules block

The page shows the rules the system actually applies — when the timer starts
and stops, what counts as a first response, business hours / weekends /
holidays, pausing, reassignment, reopened tickets, how a breach is recorded
and how it is attributed — together with the live configuration read from the
database: the per-priority targets and clock basis (`SlaPolicies`) and the
active business calendar (`BusinessCalendars`, working days, holiday count).
In short: reassignment never changes a clock, it only changes who is
accountable; a reopen starts a new Resolution cycle and carries First Response
unchanged; a breach belongs to whoever held the ticket when it was recorded.

### Paging, sorting, filtering

`status` (`Met`/`Breached`/`Pending`, omitted = all) narrows the list only — the
summary always covers every status. `sortBy` ∈ `event` (default: breach /
achievement time), `ticket`, `department`, `type`, `created`, `started`,
`target`, `due`, `actual`, `status`, `breach`, `policy`; `sortDir` `asc`/`desc`
(default `desc`; missing values sort last; ties break on ticket, then type, so
paging is stable). `pageSize` 1–100 (default 25), `page` is clamped to the last
page. The employee's evaluations are computed in set-based queries limited to
that employee and period and then sorted and paged on the server; only one page
travels to the browser.

The employee must satisfy the same `role` / `departmentId` filters as a row on
the report, otherwise **404** (a deactivated employee or service identity also
404). Rows are highlighted red (Breached) and green (Met). The main table's SLA numbers link here: Tickets with
SLA Breach and Breach Events open the breach events, SLA Evaluated opens all cycles, SLA Met opens the Met ones.

## Where it lives

| Layer | Files |
| --- | --- |
| Application | `Modules/Reporting/Dto/TeamPerformanceDtos.cs`, `Modules/Reporting/Abstractions/ITeamPerformanceQueryRepository.cs`, `Modules/Reporting/Services/TeamPerformanceAppService.cs`, `Modules/Reporting/Services/TeamPerformanceSlaPresentation.cs` (wording, durations, rules, sorting) |
| Infrastructure | `Modules/Reporting/Repositories/TeamPerformanceQueryRepository.cs` |
| Api | `Controllers/ReportsController.cs` (`api/reports/team-performance`, `…/records`, `…/sla-details`) |
| Web | `Pages/Reports/TeamPerformance.cshtml(.cs)`, `Pages/Reports/TeamPerformanceSla.cshtml(.cs)`, `Services/Api/ReportsApiClient.cs`, `Services/Auth/ReportsPolicy.cs`, nav item in `Pages/Shared/_Nav.cshtml`, styles in `wwwroot/css/site.css` |
| Tests | `Tests/Reporting/TeamPerformanceReportTests.cs` and `TeamPerformanceSlaDetailsTests.cs` (SQLite, real SQL), `Tests/Reporting/Integration/TeamPerformanceEndpointsTests.cs` (authorization), `Tests/Web/TeamPerformanceRenderTests.cs` (Razor render) |

The report is read-only: no state changes, so no audit entry is written.
