# Auto-assignment audit — final report, fixes, tests and Production deployment plan

**Status: code fixed and tested in the repository. NOTHING deployed, NO Production data read or changed.** The 84 already-distributed tickets are not touched by any change here.

## 1. Business rules → where enforced

| # | Rule | Enforcement | Verdict |
|---|---|---|---|
| 1 | Call Center Round Robin = active, eligible **CS Agents** only | `TicketAutoAssignmentService.AssignRoundRobinAsync` → `FilterEligibleAsync(csAgentsOnly)` | **Was defective — fixed (D1)** |
| 2 | Mapped **CS Agent** who accepts/connects (any Genesys channel) gets the ticket directly; no Round Robin turn is spent | `ApplyAnsweringAgentAsync` via creation, handoff and `routing.agentId` update; `CheckAnsweringAgentAsync` | **Was too permissive — fixed (D4)** |
| 4 | Ineligible answering agent → Call Center queue, flagged for manual assignment | `ManualAssignmentRequired` event + `AnswerAssignmentSkipped` audit (issue `NotCsAgent`, `NotMapped`, `Inactive`, `LockedOut`, …) | OK |
| 3 | Human needed, nobody answered → Round Robin | `AssignCallCenterIntakeAsync`; `RouteExistingTicketToCallCenterAsync` for tickets that already exist | OK |
| 4 | Manual tickets by Call Center agents keep Round Robin | `TicketCreationAppService` Call Center branch (creator self-assignment deliberately skipped) | Unchanged, as instructed |
| 5 | AI-only protected until a human is needed | `needsHuman` (creation) + `GenesysHumanHandlingEvidence`; a connected unmapped id never moves an AI ticket | OK |
| 8 | My Tickets = current owner only; paging | `OwnerEmployeeId == viewer` filter; paging | Owner filter OK; **paging defective — fixed (D2, D3, D5)** |
| 9 | Do not redistribute the 84 | no data script changed; Execute script still `@DryRun = 1` | OK |

## 2. Exact defects and fixes

**D1 — Call Center rotation included non-CS-Agent roles.** `FilterEligibleAsync` admitted any member holding `CS Agent`, `Department Employee` or `Department Head`. A Call Center member with only Department Employee/Head was dealt tickets, contradicting rule 1 and the (already CS-Agent-only) backlog script `@CsAgentRoleOnly = 1`.
*Fix:* when the ticket's current department is the Call Center (`Code = 'CC'`) the pool additionally requires the `CS Agent` role. Other departments keep the wider pool. With no CS Agent the ticket stays visibly Unassigned in the Call Center (audited) — never a guess.
*File:* `src/TigerCS.Application/Modules/Ticketing/Services/TicketAutoAssignmentService.cs`.

**D2 — Ticket list paging was non-deterministic.** `TicketRepository.SearchAsync` ordered by `CreatedAtUtc` (or priority then `CreatedAtUtc`) with no unique tiebreaker. Tickets created in the same instant (bulk loads, the backlog distribution) have an undefined relative order in SQL Server, so adjacent pages can repeat or skip tickets — this also shifts My Tickets/Queue pages.
*Fix:* `ThenBy(TicketId)` in the same direction, for both sort modes.

**D3 — Ticket notes paging had the same flaw** (`TicketNoteRepository.ListByTicketIdAsync`, ordered by `CreatedAtUtc` only). *Fix:* `ThenByDescending(TicketNoteId)`.

**D4 — The answering agent did not have to be a CS Agent.** `CheckAnsweringAgentAsync` accepted any Call Center member with an operational role, so a call or chat accepted by a Department Employee/Head was assigned to them, while Round Robin already required CS Agent. *Fix:* the answering agent must also hold `CS Agent` (new issue `NotCsAgent`); otherwise the ticket stays in the Call Center queue, flagged for manual assignment. The same check serves Phone, Website Chat, WhatsApp and Social DM (Genesys normalizes Instagram and Facebook messaging to `SocialMedia`).

**D5 — Next / Prev never changed the page (confirmed by rendering the page).** Two stacked causes: (a) the pager built its links with `Url.Page("/Tickets", new { page = p })`, but `page` is the reserved Razor Pages route key that names the page itself, so the number was silently dropped (`/Tickets?view=queue&pageSize=20`); (b) the handler parameter `int page` binds from *route data first*, where `page` is `"/Tickets"`, so even a hand-written `?page=2` never bound and the list stayed on page 1. *Fix:* the link appends `page=N` to the query string by hand; the parameter is `[FromQuery(Name = "page")]`; the Pending Interactions action forms (which also carried `page` as route data and lost it after Start/Complete/Cancel) now return it as a `returnPage` form field; and a page number past the last page now shows the last page instead of an empty "No tickets match" list. *Files:* `Pages/Tickets.cshtml.cs`, `Pages/Shared/_TicketListView.cshtml`, `Pages/Shared/_PendingInteractionsView.cshtml`.
**Same pattern elsewhere, NOT changed in this PR (unrelated lists, not verified individually):** `Pages/Customers.cshtml.cs` (`OnGetAsync … int page`), `Pages/Admin/Users.cshtml.cs` (`int page`) and `Pages/Collections/Receivables.cshtml.cs` (`int page = 1`) take the same plain `int page` parameter and, on the same reasoning, may not page either. Verify them with a click-through test like `TicketsPagerClickTests` and apply `[FromQuery(Name = "page")]`.

**Not defects (checked):** Kawther seeing Moaz's and Shahd's tickets on Queue is the Supervisor/Manager cross-department view; My Tickets applies `CurrentOwnerEmployeeId == viewer` on top of that (`TicketRepository.cs`, `Tickets.cshtml.cs`), and the count and list use the same predicate. One current owner per ticket is enforced by the unique filtered index `TicketAssignments(TicketId) WHERE IsCurrent = 1`. Round Robin is serialized by `UPDLOCK` on the per-department cursor row, which is held for the caller's transaction (every caller runs inside one).

**Digital-channel creation note (by design):** a Website Chat / WhatsApp / Social DM inquiry that merely *names* `agentId` without `humanFollowUpRequired` is not an acceptance (the agent may be assigned or ringing, or a bot): the AI keeps the conversation and nothing is assigned. Acceptance is the Genesys `routing.agentId` update (or `humanFollowUpRequired = true` together with `agentId` at creation, or a handoff carrying the connected agent) — Genesys must send one of these.

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
4. **Data:** exactly one active department with `Code = 'CC'`; every intended agent holds the **CS Agent** role (both for Round Robin and to be assigned a call or chat they accept), is active, and has `AspNetUsers.GenesysUserId` filled (only that id maps a Genesys agent — names/emails never match). Result set 3 of the new diagnostic lists this.
5. **Genesys payloads** (capture 3 real events per channel from the Genesys flow/webhook log or API request log):
   - phone creation: `agentId` present = the answering agent's Genesys **user id**, `channel = Phone`;
   - update: the answering agent must be in **`routing.agentId`** (connected). `agentId` / `handoff.agentAvailable` alone mean "assigned", **not answered**, and intentionally do not assign;
   - needs-a-human chat/WhatsApp: `humanFollowUpRequired = true` or `handoff.required = true`;
   - AI-only: none of the above (and no `HumanAgent` transcript lines).
   Then confirm in `AuditEntries` for those tickets: `AssignOnAnswer` (answered), `AutoAssign … Trigger=CallCenterIntake` (Round Robin), or `AnswerAssignmentSkipped` (flagged).

## 5. Tests and results

New test classes (all fail on the old behaviour where applicable and pass now):
- `CallCenterCsAgentOnlyRotationAndPagingTests` (5) — Call Center rotation CS Agents only; no CS Agent → Unassigned; other departments keep the wider pool; ticket paging with identical `CreatedAtUtc` neither repeats nor skips; My Tickets owner filter across pages.
- `DigitalChannelAgentAcceptanceTests` (23 cases across Website Chat, WhatsApp, Social DM) — mapped CS Agent accepting an AI chat is assigned directly with the Round Robin cursor untouched; accepted at creation (`humanFollowUpRequired`); agent merely named stays AI-protected; accepting non-CS-Agent (Department Employee) flagged `NotCsAgent`, no Round Robin; phone answered by a Department Head flagged; human required with nobody accepted → Round Robin among CS Agents only; an existing human owner is never overwritten; redelivery changes nothing; unmapped id on a chat is a bot and moves nothing.
- `TicketsPagerClickTests` (4) — the rendered Next/Prev links are followed like a browser against a fake API that really pages 45 tickets: Next/Prev walk every ticket exactly once; Prev disabled on page 1, Next disabled on the last page; My Tickets keeps its owner filter, search, priority, sort and page size on page 2; a page number past the end shows the last page; Pending Interactions carries `page` to the API. All 4 fail on the old Razor code.

Results (this branch, .NET SDK 10.0.111):
- `dotnet build src/TigerCS.slnx -c Release`: **succeeded, 0 warnings, 0 errors**.
- `dotnet test -c Release` with `TIGERCS_TEST_SQL_SERVER` pointing at a real **SQL Server 2022** (Docker `mcr.microsoft.com/mssql/server:2022-latest`): **6,528 passed, 0 failed, 51 skipped**. The 22 SQL Server integration tests (CS Agent visibility, take-ownership, escalated filter, answering-agent assignment) executed.
- The 51 skipped tests are Collections-receivables performance tests that need a separately provisioned database (`TIGERCS_PERF_SQL`); unrelated to ticket routing and **not run**.

## 6. Production deployment plan (manual, nothing automatic)

1. CI green including the SQL Server-backed tests; review the PR.
2. Run the §4 checks 1–4 read-only on Production and fix data/config gaps first (missing CS Agent roles, GenesysUserId mapping, `CC` department).
3. Publish **both API and Web** with `tools/deploy/Publish-TigerCS.ps1` from the merged commit (not the tracked `publish/` folder). The pager fix is in the Web app, the routing fixes in the API; deploying only one leaves the other defect live. **No migration, no configuration change and no data change is needed for this PR.**
4. Smoke test on UAT, then Production: a mapped CS Agent accepting a WhatsApp / Website Chat / Social DM conversation (`routing.agentId`) → that agent owns it, rotation unchanged; a mapped Department Employee accepting → ticket in Call Center, unowned, flagged; create a manual Call Center ticket → one CS Agent, one current assignment; call with a mapped agent in `routing.agentId` → that agent, cursor unchanged; chat with `humanFollowUpRequired=true` and no answerer → next CS Agent; AI-only chat → untouched.
5. Page through Queue, My Tickets, Closed and Pending Interactions with **Next and Prev** (page size 20; also open `/Tickets?page=999`) and confirm page 2 differs from page 1, no repeated/missing ticket numbers, filters survive each click, and My Tickets count = list total.
6. **Rollback:** redeploy the previous build (no schema change). `Genesys:AssignOnAnswer=false` is the existing lever for the answering-agent rule.
7. Existing unowned Open tickets: after step 2, run the Preview script; execute the backlog Execute script only after you approve its output. The 84 already-assigned tickets must be excluded: run it with `@ReassignTicketsAlreadyOwnedByEligibleCcAgent = 0` (keeps tickets an eligible Call Center agent already owns). Do not run it with the default `1`, which deals every Open ticket again. Review the Preview "who loses tickets" list first: an owner who is not an eligible CS Agent (e.g. a Supervisor) would still lose their tickets.
