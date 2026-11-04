# Backend audit fixes — escalation, SLA Met, first-response integrity, CRM lookup result

Branch `claude/backend-audit-fixes`, independent of the Ticket Details UX PR. No schema change, no new endpoint, no DTO field removed.
Each finding was re-read in code before changing it; "red" = the new test fails on the old code, "green" = passes on the new code.

## 1. Escalation authorization — confirmed, fixed

| | |
|---|---|
| **Before** | `TicketEscalationAppService.IsAuthorizedAsync` treated every CS-layer role as "inherently cross-department" and never checked the ticket. A restricted CS Agent (visibility = only tickets assigned to them, or whose department work they follow) could escalate any ticket id, including ones assigned to other agents that they cannot open. |
| **After** | Before the role gate, `TicketVisibilityRule.GetTicketAccessAsync` must return **Full** for this ticket (the same rule used by Classify, Close, Reopen and priority-downgrade). Otherwise `Forbidden` (HTTP 403) and nothing is written. The role gate (Agent/Supervisor/Manager/GM/Department roles, Level-4 = CS Manager/GM, read-only roles refused, System Administrator override) is untouched. |
| **Behaviour change** | A CS Agent can no longer escalate a ticket that is not theirs. Supervisor, Manager, GM, System Administrator and a Department Head in the ticket's department are unchanged. |
| **Evidence** | `TicketEscalationVisibilityTests` (14): red on old code for the three "agent not owner / unassigned / responsible for a different ticket" cases; green now. Existing 21 escalation tests still pass. |

## 2. SLA state accuracy — confirmed, fixed

| | |
|---|---|
| **Before** | `ResolveAsync` staged the resolution row and then evaluated the SLA in the same unit of work. `SlaBreachProcessor` read the resolution back through a database query, which cannot see a staged row, so "achieved" was null, nothing was breached, and `MarkSlaMet` never ran: an on-time resolved ticket kept `SlaState = Running`. The unit-test fake returned staged rows, which hid it. The system closures that Resolve and Close in one step (document delivery, closed-by-AI) evaluated with the ticket already Closed, so the processor returned `TicketClosed` and the SLA was never settled either. |
| **After** | `ProcessDeadlineAsync` takes an optional `resolutionAchievedAtUtc` (the caller passes the moment it just staged). `ResolveAsync` passes it. The system closure (Resolve + Close in one step) settles only the Resolution clock, with a `settlingClosure` flag: on time it records **Met**; after the deadline it records the Resolution **breach** (flag, ticket state, status history, audit) but raises **no Level 2 escalation** and does not reopen or otherwise change the closed ticket. First Response and Resolution stay independent: a system closure never writes a first human response and never touches the First Response flag or timestamp (an AI completion cannot satisfy that clock - ISSUE-019). Sticky `Breached` and `Met → Running` on reopen are unchanged; no duration or rule changed. |
| **Lists / reports** | Tickets list filters, dashboard and Team Performance all read the same `Tickets.SlaState` column, so they now agree with Ticket Details. |
| **Evidence** | `SlaMetPersistenceTests` (8, using a fake that hides unsaved rows like the database does): 4 red on old code (on-time resolve, resolve exactly on the deadline, system closure, reopen-after-Met), all green now; late resolve, missed-FR-then-on-time, cancelled closure, reopen kept. |
| **Existing data** | Tickets resolved before this fix keep `Running`. `Diagnose_TigerCS_ResolvedTicketsSlaStateNotMet_ReadOnly.sql` (SELECT only) lists them. **No repair script is provided or run**; correcting them is a business decision (a reopened ticket's deadline differs from the original). |

## 3. First-response timestamp integrity — confirmed, fixed

| | |
|---|---|
| **Before** | `POST /sla/first-response` accepted any `Source`. The "not before ticket creation" bound applied only when `Source == Manual`, so an authorised caller could send `GenesysCallAnswer`, `HumanAgentMessage` or an undefined number (`Enum.TryParse` accepts `"99"`) with an old `occurredAtUtc` and erase a first-response breach. The Genesys transcript path picked the "first" human line by delivery sequence rather than time. The audit row did not say when the entry was made. |
| **After** | Undefined/numeric sources and `HumanAgentMessage` (observed by the system from the transcript, never claimed by a client) → 400. `GenesysCallAnswer` (SLA-Architecture §16 Example E) **stays available to authorized recorders** (owner, supervisory roles, Department Head) but may precede the ticket's creation by at most **one hour** (`SlaFirstResponseAppService.GenesysCallAnswerLeadAllowance`); beyond that → 400, never clamped. It is deliberately **not** service-identity-only: the record-first-response route is refused to service identities by the platform policy, so that rule would have made the approved value unreachable for everyone. `Manual` is bounded from ticket creation up to now (unchanged). The Genesys transcript path uses the earliest human line **by sent time** (sequence breaks ties). Audit and status-history rows carry the actor, the source, the stated moment, `recordedAtUtc`, `backdatedMinutes` and `actorIsSystem`. The rule that only a human response or an authorized Record First Response stops the clock is untouched; write-once is untouched. |
| **Behaviour change** | A person can no longer submit `HumanAgentMessage` or an undefined/numeric source, and can no longer place a `GenesysCallAnswer` more than one hour before the ticket existed. The Web UI only ever sends `Manual`. |
| **Evidence** | `FirstResponseIntegrityTests` (one case per source Manual / GenesysCallAnswer / HumanAgentMessage / undefined, the one-hour bound at and beyond the limit, authorization, write-once, full audit content), a transcript out-of-order test, and real-host tests in `SlaAndEscalationEndpointsTests`. |

## 4. CRM lookup result accuracy — confirmed, fixed

| | |
|---|---|
| **Before** | `CrmBuyerHttpGateway` folded timeout, network failure, missing configuration and unexpected status into one `Unavailable`. `CustomerSearchAppService` reduced every failure to `"Failed"`, and Genesys ingestion recorded it as `NotFound(Crm=Failed)`; ambiguous matches were also recorded as `NotFound(Crm=AmbiguousMatch)`. If CRM found nobody but PACT/Tasleeh was down, the audit still said not found. |
| **After** | Compatible extension: `CrmBuyerLookupResult.FailureKind` (TimedOut, Unreachable, NotConfigured, Unauthorized, RejectedRequest, UnexpectedStatus, MalformedResponse), set by the gateway; `CrmBuyerLookupOutcome` and `CrmStatus` values unchanged; `CustomerSearchResultDto.CrmFailureKind` optional. The ingestion audit now says exactly one of: `Found`, `Found(Ambiguous…)`, `Ambiguous(Crm=AmbiguousMatch)`, `NotFound(Crm=NotFound)` (only when every source answered), or `LookupFailed(Crm=TimedOut\|Unreachable\|Misconfigured\|Rejected\|UnexpectedStatus\|InvalidResponse[;Pact=Failed][;Tasleeh=Failed])`. A timeout surfacing as a cancellation reads `LookupFailed(TimedOut)`. Exceptions still record the type name only. Gateway logs keep masking the phone and never contain the key; a test asserts both. |
| **Evidence** | `GenesysCrmLookupStatusTests` (11) and `CrmLookupFailureKindTests` (11). Pre-fix: `GenesysInquiryIngestionAppService` built `NotFound(Crm={result.CrmStatus})` for any non-found result (git history). Red/green not run for these either, for the same compile reason. |
| **Fault isolation** | An unexpected fault in the CRM leg is classified (`CrmLookupFailureKind.UnexpectedError`, customer-search kind `Error`) instead of being thrown: the gateway catches it (caller cancellation still propagates) and `CustomerSearchAppService` isolates the CRM leg so PACT/Tasleeh answers survive. Only the exception type is logged. |
| **Not changed** | The lookup result is still recorded only in the audit entry (persisting it would need a schema change); PACT/Tasleeh failures are still just "Failed" (no per-kind detail from those gateways). |

## Verification

See the PR description for the Release build and full-suite results.

## Deployment

* No migration, configuration or feature flag. Deploy the API only (the Web project is unchanged).
* After deploy: run the read-only SQL above on UAT, then Production, to size the historical `Running` rows. Nothing runs automatically.
* Consumers parsing the Genesys ingestion audit text (`CustomerLookup=`) will see the new labels.

## Verification

* Local: Release build 0 warnings / 0 errors; full suite on this branch merged with the Ticket Details redesign (#166): see the PR conversation for the final numbers.
* The SQL Server-gated tests (`*SqlServerTests`) are skipped locally unless `TIGERCS_TEST_SQL_SERVER` is set. They run in CI (`.github/workflows/ci.yml` starts SQL Server 2022 and **fails the job if any is skipped**), so the CI result on the PR head is the one that executes them. The skip reason in the test output is "Not executed: TIGERCS_TEST_SQL_SERVER".

## Deployment

* No migration, configuration or feature flag. Deploy the API only (the Web project is unchanged).
* Historical data is **not** modified. `Diagnose_TigerCS_ResolvedTicketsSlaStateNotMet_ReadOnly.sql` (SELECT only) sizes the tickets resolved before this fix that are still `Running`; no repair is provided or run.
* Consumers parsing the Genesys ingestion audit text (`CustomerLookup=`) will see the new labels. Reported breach counts will rise slightly: late system closures now record their Resolution breach.

## Remaining risks / open items

1. Historical tickets with a wrong `SlaState` stay wrong until the business decides how to correct them.
2. A ticket closed by AI before its First Response deadline, with no human reply, can be `Met` (Resolution on time, First Response not yet breached); the First Response flag is never fabricated or re-evaluated after Closed.
3. `TicketReconciliationAppService.ReconcileAsync` checks session ownership only; restricted CS Agents are stopped by the API ticket-access filter in front of it (defense-in-depth gap, not changed here).
4. PACT/Tasleeh failures are still just "Failed" (no per-kind detail from those gateways).
