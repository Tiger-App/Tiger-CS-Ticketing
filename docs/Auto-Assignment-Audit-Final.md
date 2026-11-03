# Auto-assignment audit — final report, fixes, tests and Production deployment plan

**Status: code fixed and tested in the repository. NOTHING deployed, NO Production data read or changed.** The 84 already-distributed tickets are not touched by any change here.

## 1. Business rules → where enforced

| # | Rule | Enforcement | Verdict |
|---|---|---|---|
| 1 | Call Center Round Robin = active, eligible **CS Agents** only | `TicketAutoAssignmentService.AssignRoundRobinAsync` → `FilterEligibleAsync(csAgentsOnly)` | **Was defective — fixed (D1)** |
| 2 | Mapped eligible Genesys agent who answers gets the ticket directly | `ApplyAnsweringAgentAsync` (creation, handoff, routing update); rotation cursor untouched | OK (needs config check, §4) |
| 3 | Human needed, nobody answered → Round Robin | `AssignCallCenterIntakeAsync`; `RouteExistingTicketToCallCenterAsync` for tickets that already exist | OK |
| 4 | Manual tickets by Call Center agents keep Round Robin | `TicketCreationAppService` Call Center branch (creator self-assignment deliberately skipped) | Unchanged, as instructed |
| 5 | AI-only protected until a human is needed | `needsHuman` (creation) + `GenesysHumanHandlingEvidence`; a connected unmapped id never moves an AI ticket | OK |
| 8 | My Tickets = current owner only; paging | `OwnerEmployeeId == viewer` filter; paging | Owner filter OK; **paging defective — fixed (D2, D3)** |
| 9 | Do not redistribute the 84 | no data script changed; Execute script still `@DryRun = 1` | OK |

## 2. Exact defects and fixes

**D1 — Call Center rotation included non-CS-Agent roles.** `FilterEligibleAsync` admitted any member holding `CS Agent`, `Department Employee` or `Department Head`. A Call Center member with only Department Employee/Head was dealt tickets, contradicting rule 1 and the (already CS-Agent-only) backlog script `@CsAgentRoleOnly = 1`.
*Fix:* when the ticket's current department is the Call Center (`Code = 'CC'`) the pool additionally requires the `CS Agent` role. Other departments keep the wider pool. With no CS Agent the ticket stays visibly Unassigned in the Call Center (audited) — never a guess.
*File:* `src/TigerCS.Application/Modules/Ticketing/Services/TicketAutoAssignmentService.cs`.

**D2 — Ticket list paging was non-deterministic.** `TicketRepository.SearchAsync` ordered by `CreatedAtUtc` (or priority then `CreatedAtUtc`) with no unique tiebreaker. Tickets created in the same instant (bulk loads, the backlog distribution) have an undefined relative order in SQL Server, so adjacent pages can repeat or skip tickets — this also shifts My Tickets/Queue pages.
*Fix:* `ThenBy(TicketId)` in the same direction, for both sort modes.

**D3 — Ticket notes paging had the same flaw** (`TicketNoteRepository.ListByTicketIdAsync`, ordered by `CreatedAtUtc` only). *Fix:* `ThenByDescending(TicketNoteId)`.

**Not defects (checked):** Kawther seeing Moaz's and Shahd's tickets on Queue is the Supervisor/Manager cross-department view; My Tickets applies `CurrentOwnerEmployeeId == viewer` on top of that (`TicketRepository.cs`, `Tickets.cshtml.cs`), and the count and list use the same predicate. One current owner per ticket is enforced by the unique filtered index `TicketAssignments(TicketId) WHERE IsCurrent = 1`. Round Robin is serialized by `UPDLOCK` on the per-department cursor row, which is held for the caller's transaction (every caller runs inside one).

**Residual, by design (no change):** the answering-agent rule (rule 2) uses the older eligibility check (operational role + Call Center member), not CS-Agent-only; a call answered by a Call Center Department Employee is therefore still assigned to them. Say so if rule 2 should also be CS-Agent-only.

## 3. Why existing Open tickets sit in department queues unowned (item 6)

Routing runs **only at ticket creation and on a later Genesys update that signals a human**. It never sweeps existing tickets, so an unowned Open ticket outside the Call Center is one of:

1. created before the Call Center-first build was live (never routed; only the one-off backlog script reaches it);
2. an AI conversation still active (protected on purpose until `humanFollowUpRequired`, a handoff, or a connected mapped agent arrives);
3. held by a pending approval;
4. a Genesys ticket where the platform never sent a human signal (`NOT_IN_CC_NO_FLAG`);
5. inside the Call Center but with no eligible CS Agent, or Call Center assignment disabled, or the answering agent unmapped/locked (flagged `ManualAssignmentRequired`).

`Diagnose_TigerCS_PROD_UnownedOpenTickets_ReadOnly.sql` (new, SELECT-only) classifies each ticket by the first cause that applies. Put the three example ticket numbers in `@Numbers` (only `TG-ACC-20261102-0021` is pre-filled; **the other two were not available in this session**). It also lists every unowned Open ticket by cause and department, and the Call Center pool exactly as the application evaluates it. *This script could not be executed here (no SQL Server); read its output before acting on it.*

## 4. Verify Production before concluding routing works (item 7) — to be done on the server

The repo cannot prove what runs on `TGAESHJAPP05`; the tracked `publish/` folder is stale.

1. **Build:** file/product version of `TigerCS.Application.dll` and `TigerCS.Infrastructure.dll` on the server; it must contain this fix and `AssignCallCenterIntakeAsync`. Run `Diagnose_TigerCS_PROD_CallCenterFirst_ReadOnly.sql` (build fingerprint via audit rows).
2. **Schema:** `Deploy_TigerCS_Prod_Migrations.sql` and `Deploy_TigerCS_PROD_AddUserPermissionOverrides.sql` applied (Diagnose result set 2).
3. **Config:** `appsettings.Production.json` ships `Genesys:Enabled=false`; Production must override it by environment variable. `Genesys:AssignOnAnswer` defaults true — confirm it is not set false. Service-identity employee ids configured (the Genesys account must not be dealt tickets).
4. **Data:** exactly one active department with `Code = 'CC'`; every intended agent holds the **CS Agent** role, is active, and has `AspNetUsers.GenesysUserId` filled (only that id maps a Genesys agent — names/emails never match). Result set 3 of the new diagnostic lists this.
5. **Genesys payloads** (capture 3 real events per channel from the Genesys flow/webhook log or API request log):
   - phone creation: `agentId` present = the answering agent's Genesys **user id**, `channel = Phone`;
   - update: the answering agent must be in **`routing.agentId`** (connected). `agentId` / `handoff.agentAvailable` alone mean "assigned", **not answered**, and intentionally do not assign;
   - needs-a-human chat/WhatsApp: `humanFollowUpRequired = true` or `handoff.required = true`;
   - AI-only: none of the above (and no `HumanAgent` transcript lines).
   Then confirm in `AuditEntries` for those tickets: `AssignOnAnswer` (answered), `AutoAssign … Trigger=CallCenterIntake` (Round Robin), or `AnswerAssignmentSkipped` (flagged).

## 5. Tests

New: `src/TigerCS.Tests/Ticketing/Services/CallCenterCsAgentOnlyRotationAndPagingTests.cs` (5 tests): Call Center deals only to CS Agents (equal split, excludes Department Employee/Head/Supervisor); no CS Agent → Unassigned; other departments keep the wider pool; paging with 25 identical `CreatedAtUtc` never repeats/skips and is ordered by id within the tie; My Tickets owner filter returns only the viewer's tickets across all pages. **3 of the 5 fail on the old code and pass on the fix.**
Full suite on this branch: 6,479 passed, 0 failed, 73 skipped (the skipped tests need SQL Server; they were not run — run them in CI against SQL Server before release).

## 6. Production deployment plan (manual, nothing automatic)

1. CI green including the SQL Server-backed tests; review the PR.
2. Run the §4 checks 1–4 read-only on Production and fix data/config gaps first (missing CS Agent roles, GenesysUserId mapping, `CC` department).
3. Publish API and Web with `tools/deploy/Publish-TigerCS.ps1` from the merged commit (not the tracked `publish/` folder). **No migration is needed for this change.**
4. Smoke test on UAT, then Production: create a manual Call Center ticket → one CS Agent, one current assignment; call with a mapped agent in `routing.agentId` → that agent, cursor unchanged; chat with `humanFollowUpRequired=true` and no answerer → next CS Agent; AI-only chat → untouched.
5. Page through Queue and My Tickets (page size 20) and confirm no repeated/missing ticket numbers and My Tickets count = list total.
6. **Rollback:** redeploy the previous build (no schema change). `Genesys:AssignOnAnswer=false` is the existing lever for the answering-agent rule.
7. Existing unowned Open tickets: after step 2, run the Preview script; execute the backlog Execute script only after you approve its output. The 84 already-assigned tickets must be excluded: run it with `@ReassignTicketsAlreadyOwnedByEligibleCcAgent = 0` (keeps tickets an eligible Call Center agent already owns). Do not run it with the default `1`, which deals every Open ticket again. Review the Preview "who loses tickets" list first: an owner who is not an eligible CS Agent (e.g. a Supervisor) would still lose their tickets.
