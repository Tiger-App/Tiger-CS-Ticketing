# Backlog-distribution validation suite

Validates `Preview_…`, `Execute_…` and `Rollback_TigerCS_PROD_CallCenterBacklogDistribution*.sql` on a **real, disposable SQL Server 2022** container.
It never touches Production: the scripts' own guard requires a database named `TigerCS_Ticketing_Prod`, which this suite creates **inside its own container**.

```bash
tests/backlog-distribution/setup_test_db.sh        # container + real schema (repo migrations) + fixture + backup
python3 tests/backlog-distribution/run_tests.py     # all tests (T01-T18); optional filter: run_tests.py t04 t07
SCALE_N=50000 python3 tests/backlog-distribution/run_tests.py t10
```

* Schema = the repo's own migration scripts. The expected Round Robin deal is recomputed by an **independent Python oracle** (canonical GUID text order == .NET `Guid` order); nothing is graded against its own T-SQL.
* The fixture (`01_seed.sql`, counts hand-stated in its header): 99 eligible Open tickets (62 unowned, 10 with approval/AI history, 3 with a stale current assignment, **24 owned** by an active employee of another department, a deactivated / locked / service-account owner, the supervisor, an eligible CC agent, or with no assignment row), 14 held back (8 pending approval, 6 live AI, owned and unowned), and decoys that must never be touched: InProgress / Pending / Resolved / Closed tickets, owned and unowned. Agents: A1-A3 eligible CS Agents, A4 has an explicit DENY of Tickets.Resolve, plus a Department Employee, deactivated, locked, service, read-only and supervisory accounts. Agent GUIDs are chosen so .NET order differs from SQL Server's native `uniqueidentifier` order.
* Scope under test: every Open ticket, owned or not; previous owners replaced with history kept; rollback restores previous owner, department and previous current assignment row.
* Mutation checks (remove a safeguard, confirm a test fails): Open-only filter, denial filter, rollback re-activation of the previous row, rollback owner restore, superseded-rows check - all caught.

| Test | Proves |
|---|---|
| T01 | Preview is read-only (table digests unchanged), matches the oracle for all 62 tickets, blocks on unconfirmed / empty / mistyped service-account list |
| T02 | `@DryRun = 1` performs and validates everything, then leaves the database byte-identical |
| T03 | Every commit gate refuses (missing counts, wrong ticket / moved / agent count, unconfirmed or mistyped service ids, wrong database name) and changes nothing |
| T04 | Commit: owners = oracle, all in CC, originating dept / request type / category / priority / status / SLA / created preserved, exactly one current assignment each, Transfer + AutoAssign audit, `TicketTransferred/SystemRouting` event per moved ticket, 23 non-eligible tickets byte-identical, history untouched, spread ≤ 1, no ineligible account used, cursor = last agent; second run refused |
| T05 | Rollback of an untouched batch restores the **exact** pre-batch `Tickets` table; assignment rows retired not deleted; audit/events only grow; cursor restored; second rollback refused |
| T06 / T06b | Rollback skips (byte-identical) tickets that were reassigned, worked (status / note / handler / first response / later event) or transferred out; `@IncludeWorked` relaxes only the "worked" test |
| T07 | A second concurrent run fails on the application lock; a concurrent "assign this ticket" blocks and then updates 0 rows; the unique index rejects a duplicate current assignment |
| T08 | A ticket taken by a human between preview and run aborts the run (counts no longer match) and the human's ownership survives |
| T09 | Cursor mid-list / at end / on an ineligible agent / single agent all equal the oracle; CC assignment disabled, no agent, CC inactive abort |
| T11 | **Pending approvals**: held back and listed separately (with approval type / date; a ticket that is also an AI conversation listed once); decided approvals do NOT block; held-back rows, their approvals, assignments, audit and events byte-identical after commit; rollback cannot reach them |
| T11b | An approval decided / newly requested between preview and run changes the counts and refuses the run |
| T12 | **Active AI conversations**: held back; ended / inactivity-closed / flagged-for-human / open-handoff / taken-by-agent are eligible; interaction, handoff and approval tables byte-identical; a new interaction or approval after the batch makes rollback skip the ticket |
| T13 | **Concurrent safeguards**: approval / AI conversation created mid-run (before the update → whole run aborts, Msg 50340/50341; after the update → the competing writer waits / loses the deadlock); agent locked out mid-run aborts (50342); agent deactivation blocked until commit; live-intake cursor lock waits and reads the advanced cursor; no excluded account owns anything; one owner and one current assignment per batch ticket |
| T14 | `@CsAgentRoleOnly` 1 / 0 and `@IncludeLiveAiConversations` 0 / 1 each equal the oracle; Preview and Execute must agree (a mismatch changes the counts and refuses the run); pending approvals stay held back either way |
| T10 | 20,072 tickets: preview 0.5 s, commit ≈13 s, rollback ≈2 s, oracle match, spread ≤ 1, held-back tickets still unowned, 100,000 closed tickets untouched |
| T01b | The Preview runs and reports READY under a plain `db_datareader` login, which cannot write |

Not covered by this fixture: tickets with a `RequestTypeId` (compared NULL-safe, none seeded), live Genesys traffic, Production data volumes/indexes beyond the T10 shape.

| T15 | **Owned tickets**: 24 replaced across 7 kinds of previous owner; previous rows retained and retired; legacy / drift handled; CC tickets re-dealt; My Tickets per agent; Resolve/Close prerequisites; rollback restores owners and rows |
| T16 | Explicit denial excludes an agent (default commit proves it), switch off includes it, no `UserPermissionOverrides` table works |
| T17 | `@ReassignTicketsAlreadyOwnedByEligibleCcAgent = 0` leaves CC tickets owned by a pool agent byte-identical; gates must match |
| T18 | **Stale / human-touched "active AI" conversations**: the read-only diagnostic equals an independent oracle (states, headline, 24 h projection) and writes nothing; Preview / Execute switches `@StaleAiAfterHours`, `@TreatHumanTouchedAiAsHistorical` equal the oracle (off = unchanged, old approved counts refused); genuinely active, recently silent, recent-message and pending-approval tickets stay byte-identical; no conversation record modified; a new conversation created mid-run aborts (Msg 50341) |
| T19 | **Recorded human handlers** (PR #154): a `TicketInteractionHandlers` row, `TicketInteractions.HandledByUserId` or a `HumanAgent` transcript line holds the ticket back (customer / system / virtual-agent lines do not); the Preview lists them as `RECORDED HUMAN HANDLER` with the handler ids; the **seventh gate** is mandatory and exact; held tickets, their assignments, audit, events and the conversation tables are byte-identical after commit; Rollback never takes a ticket from an owner who is a recorded handler (even with `@IncludeWorked = 1`); a handler recorded mid-run aborts the whole run (Msg 50343) |
| T20 | **Production ownership audit** (`Verify_TigerCS_PROD_BacklogBatch_OwnershipAudit_ReadOnly.sql`): per-ticket classification (MISMATCH / MATCH / WEAK_MISMATCH / MISSING_EVIDENCE_*) equals an independent expectation for the whole batch; mismatches and missing evidence are separate result sets; `HumanWorkSinceBatch`; read-only (digests unchanged, SELECT-only, runs under `db_datareader`); refuses when no batch exists |

Running without Docker (this repository's CI sandbox): the suite only needs `docker exec ... sqlcmd`, `docker cp`, `docker run/rm`. A tiny `docker` shim that maps those onto a local SQL Server 2022 + `sqlcmd` (and `TIGERSQL_SA_PASSWORD`) runs it unchanged.

