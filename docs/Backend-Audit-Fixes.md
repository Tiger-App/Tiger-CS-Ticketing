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
| **After** | `ProcessDeadlineAsync` takes an optional `resolutionAchievedAtUtc` (the caller passes the moment it just staged). `ResolveAsync` passes it. The system closure settles only the Resolution clock, with a new `settlingClosure` flag, and only ever records **Met** (a breach would mutate a Closed ticket, which the domain forbids, so a late system closure behaves exactly as before). First Response and Resolution stay independent; sticky `Breached` and `Met → Running` on reopen are unchanged; no duration or rule changed. |
| **Lists / reports** | Tickets list filters, dashboard and Team Performance all read the same `Tickets.SlaState` column, so they now agree with Ticket Details. |
| **Evidence** | `SlaMetPersistenceTests` (8, using a fake that hides unsaved rows like the database does): 4 red on old code (on-time resolve, resolve exactly on the deadline, system closure, reopen-after-Met), all green now; late resolve, missed-FR-then-on-time, cancelled closure, reopen kept. |
| **Existing data** | Tickets resolved before this fix keep `Running`. `Diagnose_TigerCS_ResolvedTicketsSlaStateNotMet_ReadOnly.sql` (SELECT only) lists them. **No repair script is provided or run**; correcting them is a business decision (a reopened ticket's deadline differs from the original). |

## 3. First-response timestamp integrity — confirmed, fixed

| | |
|---|---|
| **Before** | `POST /sla/first-response` accepted any `Source`. The "not before ticket creation" bound applied only when `Source == Manual`, so an authorised caller could send `GenesysCallAnswer`, `HumanAgentMessage` or an undefined number (`Enum.TryParse` accepts `"99"`) with an old `occurredAtUtc` and erase a first-response breach. The Genesys transcript path picked the "first" human line by delivery sequence rather than time. The audit row did not say when the entry was made. |
| **After** | Undefined sources and `HumanAgentMessage` (observed by the system from the transcript, never claimed by a client) → 422. `GenesysCallAnswer` (SLA-Architecture §16 Example E) is allowed only for a configured service identity; a person gets 403. People record responses that happened outside the system as `Manual`, from the ticket's creation up to now (unchanged). The transcript path now uses the earliest human line **by sent time** (sequence breaks ties). Audit and status-history rows now also carry `recordedAtUtc`/"entered at" and `actorIsSystem`, so a backdated entry is visible. The rule that only a human response or an authorised Record First Response stops the clock is untouched; write-once is untouched. |
| **Behaviour change** | A person can no longer submit `GenesysCallAnswer`/`HumanAgentMessage`. The Web UI only ever sends `Manual`. An existing test that had a person record `GenesysCallAnswer` was changed to use a service identity. **This narrows an approved contract value — please confirm.** |
| **Evidence** | `FirstResponseIntegrityTests` (one case per source Manual / GenesysCallAnswer / HumanAgentMessage / undefined, bounds, authorisation, write-once, audit content) and a transcript out-of-order test. Red/green on the new tests was **not** shown: stashing the fix broke compilation (new constructor parameter). The pre-fix behaviour is shown by the previously passing test in which a person recorded `GenesysCallAnswer` before ticket creation successfully. |

## 4. CRM lookup result accuracy — confirmed, fixed

| | |
|---|---|
| **Before** | `CrmBuyerHttpGateway` folded timeout, network failure, missing configuration and unexpected status into one `Unavailable`. `CustomerSearchAppService` reduced every failure to `"Failed"`, and Genesys ingestion recorded it as `NotFound(Crm=Failed)`; ambiguous matches were also recorded as `NotFound(Crm=AmbiguousMatch)`. If CRM found nobody but PACT/Tasleeh was down, the audit still said not found. |
| **After** | Compatible extension: `CrmBuyerLookupResult.FailureKind` (TimedOut, Unreachable, NotConfigured, Unauthorized, RejectedRequest, UnexpectedStatus, MalformedResponse), set by the gateway; `CrmBuyerLookupOutcome` and `CrmStatus` values unchanged; `CustomerSearchResultDto.CrmFailureKind` optional. The ingestion audit now says exactly one of: `Found`, `Found(Ambiguous…)`, `Ambiguous(Crm=AmbiguousMatch)`, `NotFound(Crm=NotFound)` (only when every source answered), or `LookupFailed(Crm=TimedOut\|Unreachable\|Misconfigured\|Rejected\|UnexpectedStatus\|InvalidResponse[;Pact=Failed][;Tasleeh=Failed])`. A timeout surfacing as a cancellation reads `LookupFailed(TimedOut)`. Exceptions still record the type name only. Gateway logs keep masking the phone and never contain the key; a test asserts both. |
| **Evidence** | `GenesysCrmLookupStatusTests` (11) and `CrmLookupFailureKindTests` (11). Pre-fix: `GenesysInquiryIngestionAppService` built `NotFound(Crm={result.CrmStatus})` for any non-found result (git history). Red/green not run for these either, for the same compile reason. |
| **Not changed** | The lookup result is still recorded only in the audit entry (persisting it would need a schema change); PACT/Tasleeh failures are still just "Failed" (no per-kind detail from those gateways). |

## Verification

See the PR description for the Release build and full-suite results.

## Deployment

* No migration, configuration or feature flag. Deploy the API only (the Web project is unchanged).
* After deploy: run the read-only SQL above on UAT, then Production, to size the historical `Running` rows. Nothing runs automatically.
* Consumers parsing the Genesys ingestion audit text (`CustomerLookup=`) will see the new labels.

## Remaining risks / open items

1. Contract decision: restricting `GenesysCallAnswer`/`HumanAgentMessage` for people (item 3).
2. Historical tickets with a wrong `SlaState` stay wrong until the business decides how to correct them (item 2).
3. A late system closure still records no breach (closed-ticket immutability); a ticket resolved before its first-response due time with no human reply can be `Met` and is never re-evaluated after Closed (pre-existing, not changed).
4. The escalate endpoint is now scoped, but `Retry assignment`, notes and other endpoints were not re-audited here.
5. Checked only against in-memory fakes and SQLite-free unit tests; the EF/SQL Server behaviour (staged rows invisible to queries) is inferred from EF semantics and the repository code, not run against SQL Server.
