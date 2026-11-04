# Default classification: Customer Services / General

**Rule.** Every ticket - AI-created, AI-handled, AI-resolved, AI-closed and manual alike - has a valid classification. A ticket with no
classification that can be trusted is classified:

1. **General / General within the department the AI (or caller) named**, when that is a valid, active, non-Collections department that has its own
   General category and General request type (published workflow);
2. otherwise - no valid department, or a department without its own General - **Customer Services (CS) / General / General**.

It is never left `Unclassified` / `Not yet classified`. A valid, compatible classification supplied by the AI is preserved.

Nothing in this change runs against Production by itself: the configuration script, the backfill and the diagnostic are all manual,
and the live backfill is switched off (`DefaultClassificationBackfill:Enabled=false`) in every shipped configuration.

## What counts as "no trusted classification"

| Situation | New ticket | Existing ticket (backfill) |
|---|---|---|
| No category and no request type | Fallback | Fallback |
| Request type without a category (partial) | Fallback | Fallback |
| Category/request type id that no longer resolves | Fallback | Fallback |
| Category and request type from different departments (ambiguous) | Fallback | Fallback |
| Genesys request type that is unknown, inactive, ambiguous by name, has no published workflow, or has no unique matching category | Fallback (was HTTP 422 / Unclassified) | n/a |
| Valid category + valid request type | **unchanged** | **unchanged** |
| Valid category, no request type | unchanged (the agent chose the category) | reported, **left alone** unless the approved run sets `IncludeCategoryOnly` |
| Collections department (`COL`) | **out of scope, never touched** | **never touched** |

If Customer Services / General is not configured (or is duplicated / has no published workflow) every path keeps its previous behaviour and
nothing is created at runtime. `DefaultClassificationService.ResolveAsync` reports the exact gap.

## Where it is applied (one service, `DefaultClassificationService`)

* `TicketCreationAppService` - the single creation path for manual / API / email-style creation and for every Genesys channel.
* `GenesysInquiryIngestionAppService` - Phone and AI, WhatsApp, chat, website, Instagram, Facebook, social DM. A valid request type still
  routes and classifies as before; only a missing / invalid / ambiguous / incomplete one takes the fallback. If a validated request type
  cannot be applied after creation, the safety net applies the fallback to that new ticket.
* The fallback keeps the supplied priority (the Genesys default "Normal", or the agent's choice); otherwise the General request type's default.

## Classification and ownership are independent

* The fallback never runs the request-type assignment rule, so no ticket is assigned to a department employee automatically.
* Human-needed tickets still go Call Center first and get the Genesys-confirmed agent or the next CS Agent of the one sequential Round-Robin.
* AI-handled tickets get no agent; AI-closed tickets stay exempt. Closing keeps the classification it had.
* Applying the fallback to an existing ticket changes **category, request type and pinned workflow only**: owner, department, priority,
  SLA, status and history are untouched (one `ApplyDefaultClassification` audit entry is added).
* Later Genesys updates never replace a valid classification with General. A valid request type from Genesys replaces General only under the
  existing "AI refines its own work" rule (no person has responded / accepted / started an approval).

## Reclassification

`POST /api/tickets/{id}/classification` now also accepts a ticket that is on the fallback (and only that): it replaces category, request type
and pinned workflow, keeps the owner, priority and running SLA clock, transfers to the category's department when it differs (owner preserved),
and audits `ReclassifyTicket`. Choosing General again is `AlreadyClassified`. Ticket Details shows **Customer Services / General** and the action
reads **Reclassify**; read-only roles do not get it. The queue layout is unchanged.

## Configuration (migration requirements)

No EF migration and no schema change. Each environment needs a General category + General request type (published workflow `<CODE>-GEN-001`) in
**Customer Services (required)** and in every other active department except Collections, once:

```bash
sqlcmd -S <server> -d <database> -U <user> -P <password> -C -b -I -i EnsureDefaultGeneralClassification.sql
```

`EnsureDefaultGeneralClassification.sql` is idempotent and dry-run by default (`@CommitChanges = 0`). It reuses existing `General` rows,
never creates a department, never duplicates and never gives Collections a General. A department with duplicates, an unpublished General workflow or a
workflow-code clash is reported and skipped (for CS the script stops with an error). The same logic is `GeneralFallbackImporter.ImportAllAsync` (C#, used by tests).

## Existing tickets (read-only first, separately approved backfill)

1. Diagnostic (SELECT only): `Diagnose_TigerCS_PROD_DefaultClassification_ReadOnly.sql` - configuration check, candidates by state / status /
   department, Collections exclusions, sample.
2. Preview / dry run (API, System Administrator): `GET /api/admin/default-classification-backfill/preview` and
   `POST /api/admin/default-classification-backfill/run` with `{"dryRun": true}` (the default). Writes nothing.
3. Live run, **only after separate approval**: set `DefaultClassificationBackfill:Enabled=true`, then `POST .../run` with `{"dryRun": false}` in
   batches (resume with `resumeAfterTicketId`). Not scheduled, not executed by this change, and not part of any deployment.

## Tests

`DefaultClassificationTests`, `DefaultClassificationBackfillTests`, `GeneralFallbackImporterTests` and the Ticket Details classify-control tests
cover: missing / invalid / ambiguous / partial classification, valid classifications unchanged, all six Genesys channels plus manual creation,
Genesys updates and reclassification, department transfers, CS Agent ownership and the sequential rotation, duplicate prevention, AI-closed
tickets, Collections exclusion, and the backfill preview / dry run / live run.

## AI scenarios

* AI-handled tickets (no human requested) are classified at creation and get **no** CS Agent; the rotation does not advance.
* An AI-**closed** ticket keeps its classification when closed; one that arrives at closure with none (created before the default existed or while it
  was unconfigured) gets the default at that moment - classification only, no assignment, no department move.
* Human-handled tickets are still Call Center first, with the Genesys-confirmed agent or the next CS Agent of the sequential Round-Robin - never a
  department employee through a rule on General.
* Duplicates are never created or chosen between: a department with more than one General falls back to Customer Services.

## Human-handled tickets (final rule)

Every human-handled ticket has an eligible CS Agent (the existing Call Center-first / confirmed-agent / sequential Round-Robin rules, flagged for a
manual assignment when nobody is eligible) **and** a valid Department, Category and Request Type:

* A known department and request type are preserved. A missing category or request type becomes **General within the same department**; a category
  chosen without a request type keeps the category and gets that department's General request type.
* An unknown department becomes Customer Services / General / General.
* **A valid department is never changed because its own General is missing.** The ticket stays in its department, takes Customer Services' General
  classification as a safe stand-in, and the gap is recorded as `ConfigurationIssue=<DEPT>: ...` on the `ApplyDefaultClassification` and Genesys
  ingestion audit entries (also listed by the read-only diagnostic). Run `EnsureDefaultGeneralClassification.sql` to remove the gap.
* A ticket that becomes human-handled later (human follow-up, handoff, a connected agent) and still has no valid classification is classified at that
  moment - classification only; owner, department, priority, SLA and status are untouched.
* **The assigned CS Agent can review and correct Department, Category and Request Type** through the classification action (Ticket Details shows
  *Reclassify* with every department's choices). Choosing a category of another department transfers the ticket there. A correction that names no
  request type keeps the current one when it still fits, else uses the target department's General request type.
* A reclassification keeps the assigned CS Agent, status, priority and the **running SLA clock/period** (its due dates are not re-timed), and only
  appends to the audit trail (`ReclassifyTicket`, plus a transfer entry when the department changes). Only the assigned agent (or a ticket on the
  default classification, for any authorized employee) can replace a classification; closed tickets and read-only roles cannot.
* AI-closed tickets require a valid classification but never a CS Agent.
