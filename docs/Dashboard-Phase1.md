# Operational Dashboard — Phase 1

The Dashboard (`/Dashboard`) is the operational landing page: the existing
customer phone search as the quick action, a filter bar, six KPI cards plus
four closure KPI cards, six breakdown cards and the Recent / Critical ticket list. Every number comes
from one read — `GET /api/dashboard/overview` — computed in the database by
`DashboardQueryRepository` over the caller's own visible-department scope.

## Scope and authorization

- Visible departments are resolved exactly as the ticket queue resolves them
  (`TicketQueryAppService.ResolveVisibleDepartmentIdsAsync`): CS-layer roles,
  General Manager, Chairman/CEO and System Administrator see every
  department; Department Employee / Department Head / Reporting User see
  their own memberships only.
- The `departmentId` filter can only narrow that scope. A department outside
  it yields empty numbers, never another department's.
- Every filter picker (departments, agents, channels, request types,
  statuses, priorities) is built from the existing master tables, restricted
  to the same scope. Selecting a department restricts the agent and
  request-type pickers to that department.
- Pending Approval counts tickets carrying a `Pending` approval the caller is
  authorized to action, using the same target-kind rules as
  `TicketApprovalAppService.IsAuthorizedApproverAsync` (expressed set-based in
  `ApprovalApproverScope` / `TicketQueryFilters.ActionableBy`).

## Two time contexts

| Widgets | Context |
| --- | --- |
| Six KPI cards, Open Backlog Ageing, Recent / Critical | **Current state** — every active ticket in scope, whatever its age. The date range does not apply, so an old open ticket is never hidden. |
| Volume by Channel / Request Type / Department, Status, Priority | **Date range** — tickets created within `dateFrom`..`dateTo` (UTC calendar days, inclusive). Default: the last 30 days, today included. |

The dimension filters (department, agent, channel, request type, status,
priority) apply to both contexts. The four closure KPI cards are a third
case, described below: they use the date range as a *closure* date.

## KPI definitions

| KPI | Definition |
| --- | --- |
| Open Tickets | Active tickets: `Open`, `InProgress`, `PendingCustomer` — plus any historical ticket still in the legacy `PendingThirdParty`, which is non-terminal and therefore still work in hand. |
| My Tickets | Active tickets whose `CurrentOwnerEmployeeId` is the signed-in user. |
| In Department Queue | Active tickets with no `CurrentOwnerEmployeeId` (queued to their responsible department). |
| SLA Breached | Active tickets whose `SlaState` is `Breached`. |
| Due Today | Active tickets whose current SLA period (`PeriodEndAtUtc IS NULL`) has an unbreached `ResolutionDueAtUtc` within today's UAE calendar day. |
| Pending Approval | Tickets with a `Pending` approval the caller may action. |

"Today" and every date filter are UAE (Asia/Dubai, UTC+4) calendar days. Timestamps stay UTC in the database; a UAE day
[00:00, 24:00) is converted to the UTC instants [previous day 20:00Z, day 20:00Z) by `TigerCS.Domain.Common.UaeCalendar`
before it reaches SQL. See `docs/UAE-Business-Day.md`.

## Closure KPIs

A second row of four cards counts **currently Closed** tickets. They always
represent Closed tickets — the Dashboard's status filter does not apply to
them — while the department scope, the agent / channel / request type /
priority filters and the date range do.

- **Date range = the ticket's latest closure date**: the most recent time the
  ticket entered Closed in the append-only status history
  (`TicketStatusHistoryEntries`, dimension `TicketStatus`, new value `Closed`),
  within `dateFrom`..`dateTo` (UTC days, inclusive). Creation date is not used.
  A reopened ticket closed again is dated by its last closure.
- Each card links to `/Tickets?view=closed&ticketStatus=Closed&closureCategory=<category>&closedFrom=…&closedTo=…`
  plus the dashboard's dimension filters. `closedFrom` / `closedTo` are queue
  filters (latest closure date, UTC days inclusive) evaluated by
  `TicketQueryFilters.LatestClosureBetween`; `closureCategory` by
  `TicketQueryFilters.InClosureCategory` — the very predicates the cards are
  counted with, so a card and its list always agree. Each ticket is counted once
  (the predicates are `EXISTS` / `MAX` subqueries, not joins).

| KPI | `closureCategory` | Definition |
| --- | --- | --- |
| Reviewed Tickets | `Reviewed` | Closed, with a submitted `TicketReviews` row for the ticket's current cycle (`ReopenCycle = Ticket.ReopenCount`) of the kind below. |
| Not Reviewed | `NotReviewed` | Closed, with no such review. An invitation without a submission is not a review. |
| Closed by AI | `ClosedByAi` | Closed, and the ticket's latest closing audit entry is `AutoCloseByAi` (Genesys `closedByAi`). |
| Closed by Agent | `ClosedByAgent` | Closed, and the ticket's latest closing audit entry is `Close` (a person, via the Close action). |

- **Which reviews count follows `CustomerNotifications:IsTestMode`** — the same
  switch that decides whether a new review is flagged `IsTest`. In production
  (`IsTestMode=false`) only real reviews (`IsTest = false`) count; in UAT
  (`IsTestMode=true`) only Test-mode reviews count, so UAT can validate
  Reviewed / Not Reviewed end to end (the cards then say "test-mode reviews" in
  their tooltip). Production satisfaction statistics
  (`GetProductionSummaryAsync`, `IsTest = false`) and the review flow are
  unchanged and never include Test-mode reviews.
- No new status or column: feedback is the existing `TicketReviews` table, the
  closer is the existing `AuditEntries` row written by `TicketLifecycleAppService`.
- "Latest closing audit entry" = the highest `AuditEntryId` among `Close`,
  `AutoCloseByAi`, `AutoCloseCustomerInactivity`, `AutoCloseDocumentDelivery`
  for the ticket. Inactivity and document-delivery closures are system closures
  that are **neither** AI nor agent — AI is never inferred from them — so
  Closed by AI + Closed by Agent can be less than the closed total.

## Open Backlog Ageing

Age = evaluation instant − `CreatedAtUtc`, active tickets only:

| Bucket | Age |
| --- | --- |
| `< 24h` | age < 24h |
| `1–3 days` | 24h ≤ age < 72h |
| `3–7 days` | 72h ≤ age < 168h |
| `> 7 days` | age ≥ 168h |

Exactly 24h → `1–3 days`; exactly 72h → `3–7 days`; exactly 168h → `> 7 days`.

## Drill-down

Every KPI and bar row links into the existing ticket queue (`/Tickets`) with
query-string filters. The queue's filters added for this were: `channelId`,
`requestTypeId`, `activeOnly`, `inDepartmentQueue`, `slaBreached`,
`dueToday`, `backlogAge`, `pendingApproval`, `createdFrom`, `createdTo`, `closureCategory`, `closedFrom`, `closedTo`.
The queue evaluates them with the same shared predicates
(`TicketQueryFilters`) the dashboard aggregates use, so a number and its list
always agree.
