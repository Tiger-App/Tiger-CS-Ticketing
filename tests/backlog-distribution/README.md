# Backlog-distribution validation suite

Validates `Preview_…`, `Execute_…` and `Rollback_TigerCS_PROD_CallCenterBacklogDistribution*.sql` on a **real, disposable SQL Server 2022** container.
Rule under test: **every Open ticket (assigned or not, any department) → Call Center → one eligible CS Agent** (the agent who answered the call if eligible, otherwise Round Robin);
InProgress / Pending / Resolved / Closed tickets, pending approvals and live AI conversations are never touched; the previous assignment row is retired (history kept); rollback restores the previous owner, department and assignment.
It never touches Production: the scripts' own guard requires a database named `TigerCS_Ticketing_Prod`, which this suite creates **inside its own container**.

```bash
tests/backlog-distribution/setup_test_db.sh        # container + real schema (repo migrations) + fixture + backup
python3 tests/backlog-distribution/run_tests.py     # all tests; optional filter: run_tests.py t04 t07
SCALE_N=50000 python3 tests/backlog-distribution/run_tests.py t10
```
(`TIGERSQL_CONTAINER` / `TIGERSQL_SA_PASSWORD` select another container.) Last full run: **193 checks, 193 passed** (incl. 20,000 extra tickets).

* Schema = the repo's own migration scripts. The expected plan is recomputed by an **independent Python oracle** (canonical GUID text order == .NET `Guid` order); nothing is graded against its own T-SQL. The seven approval values are read from the Preview (the plan fingerprint is the Preview's own checksum; the other six are also asserted against the oracle).
* The fixture (`01_seed.sql`): 40 Open tickets in the plan (15 `E-`, 4 `F-` carrying approval/AI history, 6 `X-OWN-FIN` + 2 `X-LEGACY` + 3 `X-DRIFT` already owned or with legacy assignment data, 10 `K-` ownership/answering-agent cases), 7 of them already in CC; 33 move. Held back: 3 pending approvals, 2 live AI conversations. Not touched: InProgress / Pending (assigned and not), Resolved, Closed. Answering-agent cases: kept-as-is, moved unowned, moved from another owner, handler ineligible (→ Round Robin), newest ticket is an answering-agent ticket. Agent GUIDs are chosen so .NET order differs from SQL Server's native `uniqueidentifier` order.
* **Mutation checks** (sabotage one rule, confirm a test fails): 16 mutants of Execute/Rollback — all caught (see the runbook §11).
* A second, independent layer — `src/TigerCS.Tests/Ticketing/TakeOwnership/BulkDistributionEndToEndSqlServerTests.cs` — runs these same scripts on the application's own EF schema (needs `TIGERCS_TEST_SQL_SERVER`) and drives the real API.

| Test | Proves |
|---|---|
| T01 / T01b | Preview is read-only (also as a plain `db_datareader`), plan/action/basis and per-agent distribution = oracle, blockers on unconfirmed / empty / mistyped service-account list |
| T02 | `@DryRun = 1` performs and validates everything, then leaves the database byte-identical |
| T03 | Every commit gate refuses (each of the seven values missing or wrong, unconfirmed / mistyped service ids, wrong database name) and changes nothing |
| T04 | Commit: owners = oracle, Open only, preserved fields, one current assignment each, assignment history only grows, audit with previous state, events, KEPT / outside-plan tickets byte-identical, cursor = last Round Robin agent; second run refused |
| T05 | Rollback of an untouched batch restores the **exact** pre-batch `Tickets` table and the set of current assignments; history intact; cursor restored |
| T06 / T06b | Rollback skips (byte-identical) tickets that were reassigned, worked, transferred, or had only their owner column changed; `@IncludeWorked` relaxes only the "worked" test |
| T07 | Concurrency and **ownership protection**: second run fails on the lock; competing assign and the previous owner's stale-version save wait, then change 0 rows; unique index |
| T08 | A person taking a ticket between Preview and run refuses the run (reassigned count) |
| T09 | Cursor mid-list / at end / on an ineligible agent / single agent equal the oracle; CC assignment disabled / no agent / CC inactive abort |
| T11 / T11b / T12 | Pending approvals and live AI conversations are held back, listed, untouched; decided / ended / human-flagged / taken ones are in the plan; changes between Preview and run refuse the run |
| T13 | Concurrent safeguards: approval / AI conversation created mid-run aborts the whole run; agent locked out aborts; deactivation blocked until commit; live-intake cursor lock waits |
| T14 | `@CsAgentRoleOnly` and `@IncludeLiveAiConversations` each equal the oracle; Preview and Execute must agree |
| T15 | The final rules: Open only, assigned included, KEEP, answering agent / ineligible answering agent, legacy repair, previous owner holds a superseded row, My Tickets |
| T16 | A second run cannot double-assign |
| T10 | Scale: 20,000 extra tickets (5,040 Open in plan): preview 0.5 s, commit 1.7 s, rollback 1.1 s, oracle match, closed tickets untouched |
