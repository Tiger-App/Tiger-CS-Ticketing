# Human-accepted Genesys calls → Call Center (runbook)

Trigger: Production ticket `TG-ACC-20261102-0021` (Genesys PHONE) stayed **Open, unassigned, in Accounting**; many PHONE tickets carry no
human-follow-up flag. Nothing in this document deploys or writes to Production — every SQL block below is a read-only `SELECT`.

## 1. Root cause (code, on `main` before this change)

| # | Finding | Where |
|---|---|---|
| 1 | Ticket creation entered the Call Center **only** when Genesys sent `humanFollowUpRequired: true` (`inquiry.HumanFollowUpRequired == true`). A voice call is created *at pickup* by a person, but the `Create/Reuse Ticket` data action sends `humanFollowUpRequired` as `""` unless the Architect flow sets it, so the flag was absent → `needsHuman = false` → category/queue department (Accounting), no assignment rule → department queue. The ingestion `agentId` was recorded but never counted as evidence. | `GenesysInquiryIngestionAppService` |
| 2 | The *late* path (PR #145) also keyed **only** on `humanFollowUpRequired: true`. An agent connecting (`routing.agentId`), an agent taking pending work (`handoff.assignedAgentId`) or a `HumanAgent` transcript line never routed the ticket. | `GenesysTicketUpdateAppService.UpdateAsync` |
| 3 | `/health` returned a constant `{"status":"healthy"}` — the deployed commit could not be read from the API, and `MANIFEST.txt` is not inside the package. | `Program.cs`, `tools/build-release.sh` |
| 4 | The tracked `publish/` folder and `publish/TigerCS.zip` are **stale** (DLLs dated 2026-10-22; verified to contain none of the Call Center-first strings). Deploying from them yields a build with the schema but no Call Center-first behaviour. | `publish/` |

A note on the diagnostic's `RequiresHumanFollowUp = NULL`: the column is `bit NOT NULL DEFAULT 0` (migration `20261022090000`), so a genuine
value is only ever `0`/`1`. `NULL` means the diagnostic's join found **no `TicketInteractions` row** for that ticket. Check this first (§5, query A):
if the ticket has no Genesys interaction row, later Genesys updates return `ConversationNotFound` and no code change can route it.

## 2. What counts as "human accepted" (and what does not)

Implemented once in `GenesysHumanHandlingEvidence`, used by ticket creation and by every update; the signal is written to the audit entry
(`HumanSignal=…` on `GenesysInquiryIngested` / `GenesysHumanFollowUpRequired`).

| Signal | Event | Human? |
|---|---|---|
| `ExplicitFlag` | `humanFollowUpRequired: true` | yes (unchanged) |
| `PhonePickupByMappedAgent` | **Phone** inquiry whose `agentId` maps to an active Ticketing user (a voice ticket is created at pickup) | yes |
| `MappedAgentConnected` | update `routing.agentId` (documented as *connected*, never ringing/offered) maps to an active user | yes |
| `HandoffAgentAssigned` | update `handoff.assignedAgentId` while the handoff is not being cancelled | yes |
| `HumanTranscriptLine` | transcript sender `HumanAgent` | yes |
| — | no agent; unmapped agent id (may be a bot); queue-only change; ringing agent; `VirtualAgent`/`Customer`/`System` lines; `handoff.required:false`; digital-channel `agentId` at creation (may be only the offered agent) | **no — never moved** |

A handoff request (`handoff.required:true`) already routed to Call Center + Round Robin before this change and still does.

## 3. Behaviour after the fix

* Human evidence at creation → ticket created, flagged `RequiresHumanFollowUp`, transferred to `CC` (`OriginatingDepartmentId` untouched), one eligible agent by Round Robin.
* Human evidence on a later update → `RouteExistingTicketToCallCenterAsync` (same engine); an unowned/automatically pre-assigned ticket moves, genuine human ownership is never overwritten, Resolved/Closed tickets are skipped.
* **No re-routing of AI conversations:** no evidence ⇒ nothing runs. New guard: a ticket that originated in, or has already been transferred into, the Call Center is never pulled back by a later update after a Call Center agent transferred it onward.
* Idempotent: a redelivered update changes nothing and does not advance the rotation.
* No eligible agent → ticket stays in the `CC` queue, audited `Status=Unassigned` (never a guess).
* Existing tickets are **not** swept by this change. `TG-ACC-20261102-0021` moves when Genesys next reports an agent connected (replay the routing update), or via the gated backlog scripts (live-AI conversations are held back by default there).

## 4. Deployment (manual — nothing here auto-deploys)

1. Merge this PR. Build **from the merge commit, on a clean checkout, outside the repo**: `tools/build-release.sh artifacts/release` (or the *Release package* workflow). The script now stamps `-p:SourceRevisionId=<commit>`; `MANIFEST.txt` records the commit and sha256. **Do not deploy from `publish/` or `publish/TigerCS.zip`.**
2. No schema change in this release (the five migrations are already applied). Deploy **Api and Web** per `docs/Production-Deployment.md` §5 (`tools/deploy/Deploy-TigerCS.ps1`, which backs up and recycles only the TigerCS pools).
3. Genesys side: the data actions already send `agentId` on create and `routing.agentId` on *Update Routing* (`docs/Genesys/data-actions/02, 03`). Ensure the Phone flow's *Create Ticket* runs **after the agent answers** and passes the answering agent's Genesys User ID, and that *Update Routing* fires on connect/transfer. No Architect change is needed if it already does.
4. Every human agent must be mapped (`AspNetUsers.GenesysUserId`) — an unmapped agent is deliberately not treated as human (§5, query C lists them).

## 5. Verification

**Is the running API the new build? (run on `TGAESHJAPP05`, read-only)**

```powershell
# After this release: the commit is exposed.
(Invoke-RestMethod http://localhost:8091/health)      # -> status: healthy, version: 1.0.0+<full commit sha>  (must equal the merge commit)

# Works on ANY build, including the one currently running (no version endpoint there): look for the code's own strings in the DLL.
$dll = 'C:\inetpub\TigerCS\Api\TigerCS.Application.dll'
$b = [IO.File]::ReadAllBytes($dll)
$latin = [Text.Encoding]::GetEncoding(28591)            # byte-for-byte, so UTF-16 strings are found at any offset
$hay = $latin.GetString($b)
function Has($s){ $hay.Contains($latin.GetString([Text.Encoding]::Unicode.GetBytes($s))) }
'CC-first (create):   ' + (Has 'New ticket requires a person')
'Late follow-up #145: ' + (Has 'Human follow-up required — routed to Call Center first')
'Human evidence (new): ' + (Has 'HumanSignal=')
Get-Item $dll | Select LastWriteTime, Length
```
Expected after deploy: all three `True` for **both** `C:\inetpub\TigerCS\Api` and `...\Web`. Before: the third is `False`. Compare `sha256` of `TigerCS.Api.dll`/`TigerCS.Application.dll` with the release package.

**Database (read-only), after deploy + one real human-answered call**

```sql
-- A. The ticket and whether it has a Genesys interaction row (NULL in the diagnostic = no row)
SELECT t.TicketNumber, cd.Code AS CurrentDept, od.Code AS OriginDept, t.CurrentOwnerEmployeeId,
       i.TicketInteractionId, i.RequiresHumanFollowUp, i.GenesysConversationId, i.GenesysAgentId, i.HandledByUserId
FROM dbo.Tickets t
JOIN dbo.Departments cd ON cd.DepartmentId = t.CurrentDepartmentId
JOIN dbo.Departments od ON od.DepartmentId = t.OriginatingDepartmentId
LEFT JOIN dbo.TicketInteractions i ON i.TicketId = t.TicketId AND i.IsOriginatingInteraction = 1
WHERE t.TicketNumber = N'TG-ACC-20261102-0021';

-- B. New activity proves the new code ran: signal recorded, Transfer + Round Robin assignment
SELECT TOP (50) a.OccurredAtUtc, a.Action, a.EntityId, a.AfterValue
FROM dbo.AuditEntries a
WHERE a.OccurredAtUtc >= '2026-11-02T00:00:00'
  AND ( a.AfterValue LIKE N'%HumanSignal=%' OR a.AfterValue LIKE N'%routed to Call Center first%' OR a.AfterValue LIKE N'%Trigger=CallCenterIntake%')
ORDER BY a.AuditEntryId DESC;

-- C. Human-handled calls still sitting outside CC (should drain to 0 for NEW calls) and unmapped agents (config gaps)
SELECT t.TicketNumber, cd.Code AS CurrentDept, i.GenesysAgentId, i.HandledByUserId, i.RequiresHumanFollowUp,
       CASE WHEN i.GenesysAgentId IS NOT NULL AND i.HandledByUserId IS NULL THEN 'AGENT NOT MAPPED' ELSE 'mapped/other' END AS Mapping
FROM dbo.TicketInteractions i
JOIN dbo.Tickets t ON t.TicketId = i.TicketId
JOIN dbo.Departments cd ON cd.DepartmentId = t.CurrentDepartmentId
WHERE i.IsOriginatingInteraction = 1 AND i.GenesysConversationId IS NOT NULL
  AND t.CreatedAtUtc >= '2026-11-02T00:00:00' AND cd.Code <> N'CC' AND t.TicketStatus IN (1, 2)
  AND (i.HandledByUserId IS NOT NULL OR i.GenesysAgentId IS NOT NULL
       OR EXISTS (SELECT 1 FROM dbo.TicketInteractionMessages m WHERE m.TicketInteractionId = i.TicketInteractionId AND m.Sender = 2))
ORDER BY t.CreatedAtUtc DESC;

-- D. AI conversations were NOT moved: live AI tickets (no human evidence) remain in their department
SELECT cd.Code, COUNT(*) AS LiveAiTickets
FROM dbo.TicketInteractions i JOIN dbo.Tickets t ON t.TicketId = i.TicketId JOIN dbo.Departments cd ON cd.DepartmentId = t.CurrentDepartmentId
WHERE i.IsOriginatingInteraction = 1 AND i.GenesysConversationId IS NOT NULL AND i.RequiresHumanFollowUp = 0
  AND i.EndedAtUtc IS NULL AND i.InactivityClosedAtUtc IS NULL AND t.TicketStatus IN (1, 2)
GROUP BY cd.Code;
```

**Acceptance (one test call each, after deploy)**
1. Phone, agent answers (mapped) → ticket in `CC`, one `OwnerEmployeeId` from the CC pool, audit `HumanSignal=PhonePickupByMappedAgent`.
2. Phone/chat answered by the bot only → stays in its department, unassigned, `RequiresHumanFollowUp=0`.
3. Chat starts with AI, agent later connects (`routing.agentId`) → moves to `CC` on that update with `HumanSignal=MappedAgentConnected`.
4. Replay the same update → no new `Transfer`/`AutoAssign` audit rows, same owner.
5. CC agent transfers the ticket onward, then the call ends → ticket stays in the destination department.

**Rollback:** redeploy the previous package with `tools/deploy/Rollback-TigerCS.ps1`. No data migration is involved; tickets already routed stay in `CC` (use the backlog rollback script only for the backlog run).
