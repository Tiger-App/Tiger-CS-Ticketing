# Backlog-distribution validation suite

Validates `Preview_…`, `Execute_…` and `Rollback_TigerCS_PROD_CallCenterBacklogDistribution*.sql` on a **real, disposable SQL Server 2022** container.
It never touches Production: the scripts' own guard requires a database named `TigerCS_Ticketing_Prod`, which this suite creates **inside its own container**.

```bash
tests/backlog-distribution/setup_test_db.sh        # container + real schema (repo migrations) + fixture + backup
python3 tests/backlog-distribution/run_tests.py     # all tests (T01-T11); optional filter: run_tests.py t04 t07
SCALE_N=50000 python3 tests/backlog-distribution/run_tests.py t10
```

* Schema = the repo's own migration scripts. The expected Round Robin deal is recomputed by an **independent Python oracle** (canonical GUID text order == .NET `Guid` order); nothing is graded against its own T-SQL.
* The fixture (`01_seed.sql`) has 62 in-scope tickets in 5 departments (plus 6 live Genesys AI conversations that must be held back, and AI conversations that asked for a human or ended, which stay in scope) (one inactive), tickets whose originating department differs from the current one, unclassified tickets, owned / legacy / data-drift / Resolved / Closed tickets that must never be touched, and 3 eligible CS Agents (and a Department Employee who joins only with `@CsAgentRoleOnly = 0`) plus deactivated, locked, service, read-only and supervisory accounts. Agent GUIDs are chosen so .NET order differs from SQL Server's native `uniqueidentifier` order.
* A deliberately broken script (SQL-native GUID order) is caught by T04's oracle comparison (47 of 62 tickets differ).

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
| T10 | 20,062 tickets: preview 0.4 s, commit ≈2 s, rollback ≈2 s, oracle match, spread ≤ 1, 100,000 closed tickets untouched |
| T11 | `Diagnose_TigerCS_PROD_CallCenterFirst_ReadOnly.sql`: no SQL error and no writes on the full schema and on a pre-release schema (no rotation table); DB-name guard works |

Not covered by this fixture: tickets with a `RequestTypeId` (compared NULL-safe, none seeded), live Genesys traffic, Production data volumes/indexes beyond the T10 shape.
