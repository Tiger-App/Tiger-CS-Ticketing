# Historical ticket backfill — classification and CS Agent assignment

Script: `Backfill_TigerCS_PUBLISHED_ClassificationAndAssignment.sql` (default `@DryRun = 1`, database guard `@ExpectedDb = TigerCS_Ticketing_Prod`, confirmed).
**Not executed by the application, CI or any scheduler. Production only after written approval.**

## Rules reused (no new business rule)

| Phase | Existing implementation the SQL transcribes |
|---|---|
| Classification state / candidates | `DefaultClassificationService.AssessAsync`, `IsDefaultAsync`, `DefaultClassificationBackfillClassifier.Decide` |
| Target classification | `ResolveForDepartmentAsync`: the *originating* department's own General / General + Published workflow; Customer Services / General (the human classification queue) when the department is unknown or inactive |
| Which tickets get a CS Agent | `CsAgentBackfillClassifier.Classify` (active, unowned; not pending approval, not Collections, not a live AI conversation, not younger than 3 min) |
| Who | `AssignConfirmedHandlerAsync` (handling agent, if an eligible Call Center CS Agent), else `EnsureCsAgentAsync` → shared Call Center rotation continuing the persisted cursor |

## AI conversations that ended without human intervention

Evidence (identical in the classification and assignment phases; a dry-run check proves they agree): a Genesys interaction exists, none is still live (every one has `EndedAtUtc` or `InactivityClosedAtUtc`), and there is no human evidence (no `RequiresHumanFollowUp`, open/accepted handoff, first human response, `HumanAgent` message, handler row or handled-by user).

| Situation | Result |
|---|---|
| No / invalid / inconsistent / request-type-only classification | `<originating department> / General / General` + that department's Published workflow |
| Originating department unknown or inactive | `Customer Services / General / General` |
| Valid classification (any status) | **Preserved**, never replaced (`ALREADY_VALID` / `ALREADY_DEFAULT`) |
| Category-only (partial) | **Converted for AI-ended tickets only**; every other ticket still follows `@IncludeCategoryOnly` (unchanged, default 0). Old values are in the audit row |
| Department | Never written. The target is the originating department's General; Customer Services General (classification only) when it is unknown or inactive |
| Valid department with no usable General | Reported `DEPT_GENERAL_MISSING`, nothing written (run `EnsureDefaultGeneralClassification.sql`) |
| Closed / Resolved | Label only: never reopened, never assigned, owner and status unchanged (proved by checks in report 8) |
| Active, unowned | Existing approved rules only, no AI-specific path (confirmed handler, else shared Call Center rotation; `@IncludeAiEndedWithoutHuman` keeps its Production default). Report 5b shows each AI-ended ticket's assignment class |
| Owned | Owner kept (every owned ticket is compared before / after) |

Dry-run counts: report 1 `ClassificationToApply_AiEndedWithoutHuman` (and `_Other`, `AiEndedWithoutHuman_TicketsTotal`), report 1b row **A1**, report 3b (outcome x stored state x status), 3c (what was converted, by stored state), 5b (assignment outcome), report 4 column `AiEndedWithoutHuman`. `@ApprovedClassificationCount` is still the **total** `ClassificationToApply`.

## Held back as ACTIVE_AI (live Genesys interaction, no end event)

The live-AI protection stays **on**. A ticket is `ACTIVE_AI` only when a Genesys interaction has neither `EndedAtUtc` nor `InactivityClosedAtUtc` **and** there is no human evidence. The human evidence is the application's: follow-up flag, open handoff, first human response, `HumanAgent` line, handler row, handled-by user, human workflow event (11/12/18/19) or a phone pick-up by an agent id that maps to no user (the last two were added to the SQL to match `UnassignedTicketEvidence`; before that, such tickets were wrongly held back).

**Find out whether they are really live:** run `Diagnose_TigerCS_PROD_BackfillActiveAi_ReadOnly.sql` (SELECT only). Result 2 shows whether end events arrive at all per channel and age, result 3 whether the inactivity job ever ran, result 4 classifies each held-back ticket:

| Cause | Meaning | What to do |
|---|---|---|
| `GENUINELY_RECENT` | activity within 60 minutes | stays protected |
| `CLOSE_RULE_OVERDUE` | a 10 min customer-inactivity / 5 min awaiting-reply clock is overdue outside the Call Center: the approved job should have closed the ticket | the inactivity job is not running (`BackgroundJobs:Enabled`); fix the configuration, do not write data |
| `EXCLUDED_CALL_CENTER` | Call Center (voice): the inactivity rule never applies | only a Genesys end event can end it |
| `NO_CLOCK_NO_END_EVENT` | no clock, no end | the Genesys end event never reached TigerCS |

TigerCS cannot query Genesys (Genesys only calls TigerCS), so "still live" can only be proven from Genesys itself. **Reconciliation, only for tickets the diagnostic shows as stale and only after approval:** export the conversation ids (result 6) and their real end times from Genesys, then replay them through the existing, idempotent conversation-end operation (`GenesysConversationEndAppService`): write-once `EndedAtUtc`/`EndReason`, audited as `GenesysConversationEnded`, nothing on the ticket changes, safe to repeat. No end time is ever invented. A conversation Genesys reports as still connected stays excluded. The opt-in `CsAgentBackfill:StaleAiAfterHours` rule (null today, 24 h only proposed) is the alternative that writes nothing, but it needs a separate business approval.

**Production finding (all 181 held-back tickets are live *Phone* interactions with no end event):** see [Genesys/Phone-End-Event-Investigation.md](Genesys/Phone-End-Event-Investigation.md) and run `Diagnose_TigerCS_PROD_PhoneEndEvents_ReadOnly.sql` (SELECT only; section F compares with a Genesys export). They stay protected until each conversation's real state is verified; nothing is closed and BackgroundJobs stays off.

## What is and is not written

| Written | Never written |
|---|---|
| `Tickets.CategoryId / RequestTypeId / WorkflowTemplateId` (classification candidates only) | ticket status, department, priority, SLA, escalation, timestamps |
| `Tickets.CurrentOwnerEmployeeId` + one superseded and one new `TicketAssignments` row (unowned active tickets only) | status history, SLA instances / pauses, interactions, Genesys fields, messages, notes, approvals, workflow events |
| `AuditEntries`: `ApplyDefaultClassification`, `AutoAssign`, `BackfillAssign`, one `HistoricalBackfillBatch` | notifications / outbox (so no Genesys call or customer notification) |
| `DepartmentAssignmentRotations` cursor (only when the rotation dealt a ticket) | any owned ticket's owner, any Closed / Resolved ticket's owner |

Closed and Resolved tickets can be re-classified (a label only) but are never assigned or reopened.

## Decisions (approved)

1. **Department is not changed.** Only an explicit transfer / request-type routing event moves a department in the existing workflow; report 9b lists mismatches for review.
2. **A valid department with no usable General of its own** is reported (`DEPT_GENERAL_MISSING`) and left alone — run `EnsureDefaultGeneralClassification.sql` first. It is never swapped for Customer Services'.
3. **Owned tickets are never reassigned**, even if the owner is deactivated (report 9a lists them). Reassignment would be a new rule.
4. Category-only tickets are kept (`@IncludeCategoryOnly = 0`) except AI-ended-without-human ones; stale-AI rule off, as in Production configuration.

## Run

1. Backup, or restore a copy. 2. Run with defaults: a real transaction that performs every write and check, prints reports 1–10, then **rolls back**.
3. Review: 1 and 1b (counts, 1b by Open / InProgress / Pending / Resolved / Closed), 2 (configuration), 3–4 (classification), 5–7 (assignment), 8 (all `PASS`), 9 (review only), 10 (modification log).
4. After sign-off: copy `ClassificationToApply` / `AssignmentToApply` into `@ApprovedClassificationCount` / `@ApprovedAssignmentCount`, settle the service-account gate, set `@DryRun = 0`. A changed count aborts the run.

**Rerun:** safe. Fixed tickets no longer match a candidate rule; unfixable ones are reported again. **Rollback:** each `ApplyDefaultClassification` audit row holds the previous category / request type / workflow template; each assignment is a superseded + new `TicketAssignments` pair under `CorrelationId = BatchId`; the batch row holds the cursor pre-image. No rollback script is shipped yet.

**Run the dry run on a restored copy of Production, not on the live server:** it takes row locks (the Call Center rotation cursor, agent rows, planned tickets) inside its transaction before rolling back.

## Stale Phone conversations (approved rule, 2026-11)

**Rule.** A live Genesys **Phone** conversation with **no recorded activity for more than 6 hours** no longer blocks assignment. An **Open** or **In Progress**, unowned ticket in that state is treated as `STALE_AI` and dealt to the next CS Agent of the shared rotation.

| Requirement | How it is met |
|---|---|
| Phone only | `UnassignedTicketEvidence.LiveGenesysIsPhoneOnly`: at least one live Genesys interaction and **every** live one is a Phone call. A live chat / website / other-channel conversation (even next to a call) stays `ACTIVE_AI`. |
| Open / In Progress only | `CsAgentBackfillClassifier.IsStalePhoneAi`. Pending Customer / Pending Third Party, Closed and Resolved are never released by this rule. |
| Last activity > 6 h | Strictly more than the threshold; exactly 6 h is still protected. Last activity = latest of ticket creation, interaction stamps, transcript lines and Genesys workflow events (`LastActivityUtc`). |
| No end event needed, none invented | Nothing writes `TicketInteractions.EndedAtUtc` / `InactivityClosedAtUtc`. The SQL script proves the live-conversation count is unchanged (report 8). |
| No auto-close, nothing moved | Only `CurrentOwnerEmployeeId`, one assignment row and audit rows are written. Department, classification, status, SLA and history are untouched (existing invariant checks). |
| Never reassigns | Only unowned tickets are candidates; the owner is re-checked inside the per-ticket transaction. |
| Recent / human work protected | Human evidence (follow-up flag, handoff, handler, human transcript line, phone pick-up) makes a ticket eligible as human work, not as stale. A call that shows new activity between listing and writing is protected again by the in-transaction re-check. |
| Audit | `BackfillAssign` / `AutoAssign` audit rows carry `Reason=StalePhoneAi` (and `LastActivityUtc`). |

**Where it applies**

1. **Historical tickets, SQL.** `Backfill_TigerCS_PUBLISHED_ClassificationAndAssignment.sql`, parameter `@StalePhoneAfterHours` (default 6; NULL or <= 0 switches the rule off). Assignment-only dry run: `@RunClassification = 0`, `@RunAssignment = 1`, `@DryRun = 1`. Reports: **1** (`AssignmentToApply_StalePhone`), **5c** (every STALE_AI Phone ticket with its last activity and proposed owner), **11a** (outcome by Open / other active, with age), **11b** (what stays `ACTIVE_AI`, by department, channel mix and silence), **11c** (proposed distribution per CS Agent), **8** (invariants).
2. **Historical tickets, application.** `CsAgentBackfill:StalePhoneAiAfterHours` (6) drives the admin preview / dry run (`GET api/admin/cs-agent-backfill/preview`, `POST .../run` with `dryRun: true`); rows carry `stalePhoneAi`. A live run still needs `CsAgentBackfill:Enabled = true`.
3. **Future tickets, recurring process.** Its own section, hosted service and run lock (shared with the backfill and reconciler, so runs never overlap):

```json
"StalePhoneAssignment": {
  "Enabled": false,        // off until approved
  "DryRun": true,          // logs "would assign", writes nothing
  "IntervalMinutes": 15,
  "BatchSize": 100,
  "MaxAssignmentsPerRun": 50,
  "StaleAfterHours": 6
}
```

It acts **only** on tickets this rule releases; every other unowned ticket remains the job of normal routing, the reconciler and the backfill. Going live needs `Enabled = true` **and** `DryRun = false` - not done without approval.

## Live run stopped with Msg 50806 (service-account list is empty)

The live backfill refuses to commit until it knows every service / integration account, so it never deals a ticket to one. **The gate must not be removed or bypassed.**

**Where the answer is.** The application treats an employee as a service identity only if its id is in one of two *configuration* lists - they are not stored in the database, so SQL cannot derive them:

- `Authorization:ServiceIdentity:EmployeeIds`
- `Collections:Authorization:IntegrationEmployeeIds`

In Production these are API-pool environment variables (`Authorization__ServiceIdentity__EmployeeIds__0`, `__1` ... and `Collections__Authorization__IntegrationEmployeeIds__0` ...) or the secret store. The ids to exclude are **exactly the union of the two lists**. The Genesys integration account is a service identity only if its id is in one of them.

**Steps**
1. Read both settings on the live API pool.
2. Run `Identify_TigerCS_PROD_ServiceAccounts_ReadOnly.sql` (SELECT only). Paste the ids into `@ConfiguredIds` (one INSERT per id). It validates them (employee exists, active, locked, would otherwise be dealt tickets) and looks for database evidence of integration accounts that are *not* in your list (accounts that create Genesys tickets but never handle one and have no Genesys mapping). Evidence is a prompt to re-check the configuration; it is never added to the list for you.
3. Read the single **Verdict** row, then apply exactly its values to the backfill script:

| Situation | `@ServiceIdentity` | `@ServiceIdsConfirmed` | `@NoServiceAccountsExist` |
|---|---|---|---|
| One or more ids are configured | one `INSERT INTO @ServiceIdentity VALUES ('<id>');` per id (result 1b) | `1` | `0` |
| Both settings verified empty **and** the script shows no TIER_A account | empty | `0` | `1` |
| Anything else (list empty but TIER_A evidence, ids not found, settings not read) | do not run live | - | - |

The backfill itself enforces the same rules (Msg 50804 contradictory gates, 50805 ids listed but not confirmed, 50806 empty list without `@NoServiceAccountsExist`, 50807 an id that is not an employee). Re-run the assignment **dry run** with the final gate values first; `@ApprovedAssignmentCount` must still equal report 1 `AssignmentToApply`, and excluding a service account that was in the rotation can change who receives tickets (not how many).

### Finding the Genesys integration account's EmployeeId

The committed `appsettings.Production.json` lists are empty **by design**; an empty file says nothing about the live API pool, where the values are environment variables. The integration account is an ordinary user (one `AspNetUsers` row; its `Id` is also its `Employees.EmployeeId`) with the role CS Agent, created by hand and used by TigerGroupWeb (`Ticketing:Username`) to forward Genesys calls. Its user name is configuration, so the name is only a look-up key.

Run `Identify_TigerCS_PROD_GenesysIntegrationAccount_ReadOnly.sql` (SELECT only). An account is `CONFIRMED_INTEGRATION_ACCOUNT` only when the name matches **and** it creates Genesys tickets (`Tickets.CreatedByEmployeeId`) **and** no person handled a conversation as it. Accounts that create Genesys tickets under another name are listed as candidates (result 3): confirm the real user name with the TigerGroupWeb owner, add it to `@KnownUserNames` and re-run. Result 4 shows whether the account is currently in the rotation pool, i.e. would be dealt tickets unless excluded. Nothing is added to `@ServiceIdentity` without sign-off from the owner of the API-pool configuration.

If the live pool really has both lists empty, the integration account is also **not** being confined to the integration surface today; setting `Authorization__ServiceIdentity__EmployeeIds__0` is a separate, approved deployment change.

