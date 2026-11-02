# Backlog-distribution validation suite

Validates `Preview_…`, `Execute_…` and `Rollback_TigerCS_PROD_CallCenterBacklogDistribution*.sql` on a **real, disposable SQL Server 2022** container.
It never touches Production: the scripts' own guard requires a database named `TigerCS_Ticketing_Prod`, which this suite creates **inside its own container**.

```bash
tests/backlog-distribution/setup_test_db.sh        # container + real schema (repo migrations) + fixture + backup
python3 tests/backlog-distribution/run_tests.py     # all tests (T01-T14); optional filter: run_tests.py t04 t07
SCALE_N=50000 python3 tests/backlog-distribution/run_tests.py t10
```

* Schema = the repo's own migration scripts. The expected Round Robin deal is recomputed by an **independent Python oracle** (canonical GUID text order == .NET `Guid` order); nothing is graded against its own T-SQL.
* The fixture (`01_seed.sql`) has 72 eligible and 12 held-back tickets in 5 departments (one inactive): pending approvals, live AI conversations, a ticket with both, and eligible look-alikes (decided approvals; AI conversations that ended, were closed for inactivity, need a human, have an open handoff or were taken by an agent). It also has tickets whose originating department differs from the current one, unclassified tickets, owned / legacy / data-drift / Resolved / Closed tickets that must never be touched, and 3 eligible CS Agents plus a Department Employee (in the pool only with `@CsAgentRoleOnly = 0`), deactivated, locked, service, read-only and supervisory accounts. Agent GUIDs are chosen so .NET order differs from SQL Server's native `uniqueidentifier` order.
* Mutation checks (remove one safeguard, confirm a test fails) were run for: SQL-native GUID ordering, ignoring pending approvals in Execute / Preview, ignoring active AI conversations, the post-write approval re-check, locking of agent rows, the held-back gate counts, and the CS-Agent-only filter — all eight caught.

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
