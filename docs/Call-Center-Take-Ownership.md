# Call Center — Take Ownership of existing unassigned tickets

**Status: implemented and tested in a build/test environment. NOT deployed. Nothing was run against Production.**
Needs: `TigerCS.Api` + `TigerCS.Web` deploy. **No database migration, no schema change, no data change, no new permission.**

## 1. Why Close is unavailable on the old tickets (exact reason)

An old Accounting / Leasing ticket that nobody owns fails for a Call Center agent at **four** independent points. Any one is enough; all four apply:

| # | Where | Rule in code | What the agent sees |
|---|---|---|---|
| 1 | **Visibility** — `TicketVisibilityRule.GetTicketAccessAsync`, `TicketQueryFilters.VisibleTo` | A CS Agent (without Supervisor/Manager/Admin) sees **only** tickets assigned to them, tickets on which they handled a Genesys interaction, or tickets they created. *Department or Call Center membership alone grants nothing*, and an unassigned ticket nobody handled is visible to **no** CS Agent. | The ticket is not in the list; opening it by URL → **403 "Ticket not accessible"** (`RequireTicketAccess` filter, before any action runs). |
| 2 | **Close permission** — `TicketLifecycleAppService.CloseAsync` | Close is allowed for the **current owner**, or a Close-role holder who additionally passes `PassesAgentScopeAsync` (a CS Agent must be able to *modify* the ticket = owner / handler / department grant). An unowned ticket has no owner; the agent has none of the grants. | Close button hidden (`TicketActions.CanCloseTicket`); API → 403. |
| 3 | **Lifecycle** — `CloseAsync` / `Ticket.Close` | Close needs a **current resolution** (`Resolved`). Otherwise → `NotYetResolved` (409). An old ticket is Open / InProgress / PendingCustomer. | Close is not offered until the ticket is Resolved. |
| 4 | **Resolve is owner-only for the CS layer** — `IsResolveAuthorizedAsync`, `TicketRoleSets.Resolve` | CS Agent / Supervisor / Manager hold **no** role-based Resolve; only the current owner, or Department Employee/Head. So no Call Center agent can resolve the ticket **in order to** close it unless they own it. | Resolve hidden / 403. |

**Root cause:** the old tickets have *no owner*, and a CS Agent's rights (view, Resolve, Close, Transfer) all hang on *owning or having handled* the ticket. The Call Center-first routing (#145–#148) gives owners to **new** tickets only; the backlog of old ones was never given owners (the bulk distribution that would is planned, not run).

## 2. What changed

* **See** — an eligible Call Center agent now sees (read-only) every *available* ticket of every department: in the default Queue list, in the **Call Center** department filter, and in a new **Ownership → Available to take** filter. They can open the ticket, its history, notes, interactions and approvals. Nothing else is writable until the ticket is theirs (add note, classify, assign, status, resolve, close, transfer, escalate are all still refused).
* **Take Ownership** — `POST /api/tickets/{id}/ownership` and a **Take Ownership** button on Ticket Details. In **one transaction**: the ticket moves to the Call Center (if not already there), is assigned **exclusively** to the caller, and the move is recorded.
* **Then** — the agent is an ordinary owner. Resolve / Close / Transfer follow the *existing* rules and validations, unchanged (e.g. an `Open` ticket must go `InProgress` before Resolve; Close needs Resolved; a classified ticket leaving the Call Center must go to its request type's department).

### Who may take (`TicketClaimPolicy`)
CS Agent role, not read-only, not a service identity, and an **active, unlocked member of the Call Center with an operational role** (the same test the Genesys answering-agent assignment uses). The System Administrator override does **not** make anyone a Call Center agent — an administrator gets 403 and keeps Assign / Transfer (with which they can place a ticket with any agent). A **Grant** never makes a non-agent eligible. An explicit **Deny** of `Tickets.Edit` or `Tickets.Assign` always blocks (list scope, button and API together). No new permission was added, so there is nothing new to administer.

### Which tickets (`TicketQueryFilters.AvailableToTake` — one definition for the list, the filter, the button and the API)
Active (Open / InProgress / PendingCustomer / legacy PendingThirdParty) **and** no owner **and** no current assignment **and** no pending approval **and** not an active AI conversation. Closed, Resolved, owned-by-anyone-else, pending-approval and active-AI tickets are excluded.
*Pending approval* is excluded although the request did not list it: it is the same hold-back the bulk distribution applies (moving the ticket would pull it from its approvers). *Active AI conversation* = a Genesys interaction not ended, not closed for inactivity, needing no human, not taken by a person, with no human-flagged interaction and no open human handoff — **identical** to the bulk Preview's definition.

### What a claim writes / preserves
| Written | Preserved untouched |
|---|---|
| `CurrentDepartmentId → CC` (only when not already CC); `CurrentOwnerEmployeeId → agent`; one new **current** `TicketAssignment` (department CC, *assigning actor = the agent*, so it is a human act that a later Genesys event will not treat as an automatic pre-assignment and overwrite); `Transfer` audit; `TakeOwnership` audit (same correlation id); one `TicketTransferred` workflow event (reason *SystemRouting* — so it is **not** counted as a person's hand-over or an escalation, like the Call Center-first routing it continues) | `OriginatingDepartmentId`, request type, category, priority, **status**, verification, **SLA state, clocks and instances (no SLA code runs)**, resolution, interactions, notes, approvals, and every earlier audit / workflow / status-history row |

### Concurrency (two agents, one ticket)
The write is guarded by the ticket's **row version** and the filtered unique index `TicketAssignments(TicketId) WHERE IsCurrent = 1`. Exactly one agent commits; the other gets **409** (`ticket-concurrently-modified`), and `ticket-already-owned` / 403 on retry. A SQL Server **deadlock victim** (error 1205) is answered as the same 409 instead of an unhandled 500 (read side retried, write side translated in `TicketingUnitOfWork`). Nothing of a losing attempt is written.

### Operational off-switch
`TicketClaim__Enabled=false` (config / environment variable; recycle the API; **no redeploy**) turns the whole feature off together — list scope, details flag, endpoint. Default `true`. Nothing already claimed is undone.

## 3. PR #148 and the backlog-distribution scripts — review

* **PR #148 is already merged** (13:59 UTC today) and is the base of this branch. Part A (*the agent who answers owns the ticket*) and this change do not overlap: that sets the owner at Genesys *answer* time; this is a manual take for tickets that already exist. They interlock safely: a claimed ticket has a human owner whose assignment was made by the agent, so a later Genesys connect event **keeps** it (`HumanOwnerKept`), and the claim's `SystemRouting` event is not a "person's transfer" that would stop later Genesys routing logic.
* **No duplication.** The bulk distribution (Part B) and Take Ownership are two ways to give the same tickets owners. They use the **same eligibility** (verified: on the backlog suite's seeded database the Preview reports 72 eligible = 24 SUP / 19 FIN / 15 LEG / 8 OLD / 6 CC, 7 held back for pending approval, 5 for active AI — identical to this change's pool and held-back counts, `Verify_TigerCS_TakeOwnership_ReadOnly.sql`).
* **No conflict in effect.** A claimed ticket is owned, so it drops out of the bulk plan (`owner IS NULL AND no current assignment` is its first test). The bulk batch is identified by its `AutoAssign` audit rows + `CorrelationId`, so **Rollback never touches a claim** (`TakeOwnership` audit). Claims do not use Round Robin and never advance its cursor.
* **One operational interaction (real, not a bug):** the bulk Execute re-checks every planned row and **aborts the whole run** if any ticket changed after the Preview. A claim landing between Preview and Execute therefore aborts that run (safe — fully rolled back; re-preview and re-approve). **Set `TicketClaim__Enabled=false` for the Preview → Execute window**, or accept a re-run. Also decide the sequencing: if you run the bulk distribution first, the pool shrinks to just the held-back tickets' successors; if you take manually first, the bulk plan is simply smaller. They never double-assign.
* **Open items inherited from #148 (unchanged by this work):** its backlog scripts are still **not executed on Production**; the restored-copy rehearsal is still mandatory; `main`'s earlier (#146) scripts without the pending-approval hold-back were superseded by #148's and must not be run.

## 4. Deployment requirements

1. **Code:** deploy `TigerCS.Api` **and** `TigerCS.Web` from this branch (Web carries the button, filter and wording). Call Center-first (#145–#148) must already be live.
2. **Database:** none. No migration, no `ALTER`, no seed. (`EfModelAndMigrationConsistencyTests` pass: no model change.)
3. **Data prerequisites** (run `Verify_TigerCS_TakeOwnership_ReadOnly.sql`, SELECT-only, before and after):
   * exactly **one active** department with `Code = 'CC'`;
   * Call Center agents who should use it are **active, not locked, members of CC, holding the CS Agent role** (the script lists them). Members without the CS Agent role (e.g. Department Employee) cannot take.
4. **Config:** none required. `TicketClaim__Enabled` (default `true`) is the off-switch; `Genesys__AssignOnAnswer` is unaffected.
5. **Permissions:** no new permission key. Existing `Tickets.Edit` / `Tickets.Assign` Deny overrides are honoured. Users with those denied will not see the button or the other departments' tickets.
6. **Order with the bulk distribution:** see §3 — either order is safe; disable Take Ownership during the bulk Preview → Execute window.
7. **Smoke test after deploy (as one real Call Center agent, on a low-risk old ticket):** open Tickets → *Ownership: Available to take* → open one (read-only banner + **Take Ownership**) → take it → confirm it is now in the Call Center under *My Tickets*, status/SLA unchanged → move it `InProgress`, Resolve, Close (or Transfer) → run the verify script's §3 and confirm 0 violations.
8. **Rollback:** set `TicketClaim__Enabled=false` (instant, keeps data) or redeploy the previous build. There is no data migration to reverse; claimed tickets are ordinary owned Call Center tickets and can be reassigned by a CS Manager / Supervisor as any other.

## 5. Tests (all executed — see the PR for the run)

* `TakeOwnershipEndpointsTests` (15, real HTTP pipeline / DI / filters / permissions): the pool is visible before claim in the default list, the *Available to take* filter and the Call Center filter; owned-by-another / Closed / Resolved / active-AI / pending-approval are excluded and untakeable; AI that needs a person / ended / taken by an agent **is** takeable; a claim moves + assigns + preserves status, priority, SLA, origin, numbers, and writes exactly one current assignment, the `Transfer` + `TakeOwnership` audit and one workflow event; take-in-place in the Call Center; idempotent for the owner; Resolve / Close / Transfer afterwards; role / membership matrix and **UI flag == API answer**; Deny of `Tickets.Edit` / `Tickets.Assign`; a Grant does not make a non-agent eligible; read-only access to the pool allows no other write.
* `TakeOwnershipSqlServerTests` (5, **real SQL Server 2022**): the predicate translates to SQL and the list agrees with the single-ticket decision for 14 ticket shapes; **8 agents racing one ticket truly in parallel × 12 rounds → exactly one winner, one owner, one current assignment, one audit, one event**; a deterministic interleaving (second agent decides before the first commits) is rejected by the row version with nothing written; a stale client row version is rejected; the database itself refuses a second current assignment.
* `TakeOwnershipOffSwitchTests`, `TakeOwnershipUiContractTests`, two page-render scenarios, the System Administrator authorization test, OpenAPI inventory entries.
* **Mutation checks:** 10 deliberate sabotages of the rules (owner / AI / pending / terminal exclusion, Deny, role gate, Call Center membership, status reset, missing audit, read-only visibility) — all 10 are caught by a failing test.

## 6. Not proven / residual risks

* **Production data and volume.** Pool size on Production is unknown; the list adds three `NOT EXISTS` sub-queries per row for the agent's own list only — measure the Queue page on a Production-sized copy before relying on it (the SQL tests prove correctness, not Production performance).
* **Real users and browsers** were not exercised; the page was verified through rendered HTML in tests, not clicked in Production.
* **Window of mixed views:** the pool is read-only to the agent; two agents can both *see* a ticket and both press the button — one gets 409 (by design).
* **The bulk scripts** were re-validated only for eligibility parity with this change (against the repo's own backlog test database); they remain unexecuted on Production.
