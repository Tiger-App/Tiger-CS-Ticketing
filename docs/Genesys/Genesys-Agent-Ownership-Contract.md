# Genesys agent ownership — what TigerCS can and cannot know

**Rule.** The CS Agent who actually handles a Genesys interaction is the assigned CS Agent of its ticket. Round robin is only the fallback when nobody has confirmed handling it.

## 1. Which event identifies the handling agent (today)

TigerCS has exactly four sources. Only the first three are *confirmation*:

| Source | API | Counts as handling agent? |
|---|---|---|
| `agentId` on the ticket-creating inquiry of a **phone call** (or an inquiry already flagged `humanFollowUpRequired`) | `POST /api/genesys/tickets` | **Yes** — answered before the ticket existed (handler Source `Inquiry`) |
| `routing.agentId` (+ `connectedAtUtc`) | `POST /api/genesys/tickets/{id}/updates` | **Yes** — "agent now **connected**" (handler Source `Routing`) |
| A `HumanAgent` transcript line whose `senderId` maps to a user (GUID, or strict e-mail fallback §9) | updates / conversation end | **Yes** — the first mapped person who wrote to the customer; every such person is also recorded as a handler (Source `Transcript`) |
| Top-level `agentId` on an update / end; `handoff.agentAvailable`+`agentId`; `handoff.assignedAgentId` | | **No** — context only / "assigned or ringing". Recorded as display data or a pending work item, never as ownership |

Identity is `AspNetUsers.GenesysUserId` only (filtered unique index `UX_AspNetUsers_GenesysUserId`); never a name. The one exception is the **HumanAgent transcript `senderId`** — see §9.

## 2. Precedence

1. **Authorized explicit manual assignment / supervisor override** — an assignment whose actor is a person other than the Genesys integration. Authoritative: no Genesys event replaces it (tested). It changes only through a new authorized reassignment.
2. **Confirmed Genesys handling agent** — replaces an *automatic* pre-assignment only (ticket still Open, no human reply/note, assignment made by the system). Never replaces a person's ownership.
3. **Existing eligible CS Agent** — preserved across department transfer, category/request-type routing and reopen.
4. **Shared sequential round robin** — only when the ticket needs a person and has no owner.

A fallback owner is **never** recorded as a Genesys handler; when the real agent connects, the fallback assignment row is superseded (history kept).

## 3. Mapping validation (this change)

| Case | Result |
|---|---|
| Blank / over-long id | `Invalid`, nothing happens |
| No user carries the id | `NotMapped` → ticket waits in the Call Center queue, flagged `ManualAssignmentRequired` (never given to someone else); logged `Genesys User ID … is not mapped` |
| Mapped user deactivated / locked / not a Call Center member / not a CS Agent | flagged, never assigned |
| **Id mapped to a service identity (e.g. the Genesys integration account)** | **new:** `ServiceAccount` — never a handling agent, never a handler row; flagged; logged |
| **More than one user carries the id** (only possible if the unique index is missing) | **new:** `Ambiguous` — resolved to *neither* user (the old query silently took the first); flagged; logged as an error |
| Name-only event | nothing resolves (no id) |

Logs carry the opaque Genesys User ID only — no names, e-mails, tokens or secrets.

## 4. Transfers — an integration gap, deliberately not guessed

Genesys delivers **no transfer state**. `routing.agentId` says "agent X is connected"; it does not say that agent A *left* (blind/warm transfer) or *stayed* (consultation, conference), nor whether a transfer was offered, accepted, failed or cancelled. With only that, "A→B" and "A consults B" are identical. TigerCS therefore:

* offered / ringing / assigned agent (handoff assignee, queue change) → **no ownership change** (tested; Agent A preserved);
* a second agent **connected** → recorded as a handler and as a visible `GenesysCallTransferred` workflow event; **the owner is not changed** — a person (Assign / Transfer) moves it, or the Genesys contract below is delivered;
* never guesses the owner when several agents participate.

**Required Genesys-side contract change** (not built; to be agreed, then implemented additively in `GenesysRoutingUpdateDto`):

```jsonc
"routing": {
  "agentId": "<connected agent>", "connectedAtUtc": "...",
  "transfer": {
    "transferId": "<stable id, for de-duplication>",
    "type": "Blind | Warm | Consult | Conference",
    "state": "Offered | Accepted | Completed | Failed | Cancelled",
    "fromAgentId": "...", "toAgentId": "...", "occurredAtUtc": "..."
  }
}
```

Proposed TigerCS rule once delivered: ownership moves A→B only for `Blind`/`Warm` with `state=Completed`, `toAgentId` connected and `fromAgentId` disconnected, **and** A is still the current owner **and** no authorized reassignment is newer than `occurredAtUtc`; `Consult`/`Conference`/`Offered`/`Failed`/`Cancelled` never move it; events are de-duplicated by `transferId` and ordered by `occurredAtUtc`.

## 5. AI → human and conversation end (this change)

* AI closed / resolved (`closedByAi`) → no CS Agent needed; nothing is assigned or flagged.
* AI hands off → provisional round-robin owner until a mapped agent connects; that agent then owns it.
* **New:** the AI conversation **ends without the AI resolving or closing the ticket** (no `closedByAi`, no customer confirmation, not waiting for the customer's reply, nobody owns or handled it) → routed for human handling (Call Center first + shared round robin). Previously it sat unassigned in its department. Redelivery is idempotent.
* A late AI event never overwrites a confirmed human assignment, and an interaction ending never reassigns an owned ticket.

## 6. Concurrency and ordering

Same-event redelivery writes nothing (unique handler row per interaction+agent, row-versioned ticket, unique current assignment). **New:** the first-handler save used to let a racing identical delivery surface as an unhandled error; it now answers `ConcurrencyConflict` (safe to redeliver). Out-of-order events cannot take a confirmed owner (tested); handler order follows `connectedAtUtc`, not delivery order. The shared round-robin cursor and its row lock are untouched.

## 7. Existing data

`Diagnose_TigerCS_GenesysAgentOwnership_ReadOnly.sql` (read-only) reports active tickets whose CS Agent differs from the latest confirmed handler, unmapped Genesys ids, duplicate mappings, tickets with no eligible CS Agent, conflicting ownership evidence and supervisor overrides. **Nothing is corrected automatically.** A correction would be a separately approved, dry-run-first operation that excludes supervisor overrides and multi-handler conflicts.

## 8. UAT verification checklist

- [ ] Every Call Center CS Agent has a `GenesysUserId`; diagnostic section 2 shows no duplicates and no unmapped CS Agents.
- [ ] Agent A answers a **phone** call → ticket created already owned by A (Call Center); round-robin cursor unchanged.
- [ ] Chat with `humanFollowUpRequired`, nobody accepted → a round-robin owner; Agent B accepts (`routing.agentId`) → owner becomes B, the earlier assignment is superseded, B is the only handler row.
- [ ] Agent only **ringing/offered/screen-popped** → ownership unchanged.
- [ ] AI-only chat the AI closes → closed, no CS Agent, no "assignment failed" flag.
- [ ] AI chat ends unresolved → ticket in the Call Center with a provisional agent; ends while "awaiting customer reply" → untouched.
- [ ] A→B transfer: B appears as a handler + "call transferred" event; **owner stays A** until a manager reassigns (or the transfer contract is delivered).
- [ ] Supervisor reassigns to C, then B answers → owner stays C; B visible as handler.
- [ ] Redeliver the same connect event twice, and a stale earlier event after a later one → one owner, one current assignment.
- [ ] Unmapped agent connects → ticket flagged *needs manual assignment*, not given to anyone else; the log names the Genesys id.
- [ ] Classify to Facilities Management / Finance / Maintenance → department changes, CS Agent unchanged.
- [ ] Reopen a closed ticket → previous agent restored if eligible, else shared round robin.
- [ ] Run the diagnostic: sections 4–6 contain only the intentional exceptions.

## 9. HumanAgent transcript `senderId`: GUID contract, strict e-mail fallback (Ticket 225)

**Incident.** Ticket 225's three `HumanAgent` lines carried `senderName = "Esraa Alfadil Alnour Ahmed"` and `senderId = callcenter5@tigergroup.ae`, while her
`AspNetUsers.GenesysUserId` is a GUID. The lookup is by `GenesysUserId`, so every line resolved to nobody (`SENDERID_MAPS_TO_NOBODY`), no `TicketInteractionHandlers`
row was written, and the ticket kept its explicit owner (Shahd Mirza, Take Ownership) — correctly.

**Contract.** `transcript[].senderId` for a `HumanAgent` is the agent's **Genesys User ID (GUID)** — the same value as `routing.agentId`
(`docs/Genesys/Live-Transcript-And-Unclassified-Tickets.md`, `Genesys-UAT-Final-Handoff.md`). Nothing in this repository documents or sends an e-mail there.

**What TigerCS now does** (`GenesysAgentResolutionAppService.ResolveSenderIdAsync`, HumanAgent transcript lines only — `routing.agentId`, inquiry `agentId` and every other id stay GUID-only):

1. GUID first, exactly as before. If the id maps to *anybody* (even an inactive/ambiguous/service user) that answer is final.
2. Only if it maps to nobody **and** is a bare, well-formed e-mail **and** `Genesys:AllowEmailSenderIdFallback` is `true` (default; `false` = GUID only): match the *whole address*, case-insensitively, to
   `AspNetUsers.NormalizedEmail` or `GenesysEmail`. Names, display names, partial addresses, lists and `Name <addr>` forms never match.
3. Accepted only if exactly one distinct account matches **and** it is an active employee, not locked out, not a service identity, and holds the **CS Agent** role. Two or more matches
   (even if one is inactive) = `Ambiguous`, nobody is chosen. Inactive / locked / service / non-CS-Agent are rejected with their own outcome and flagged exactly like the GUID path.
4. A recognised sender is recorded as a handler (`InteractionHandlerSource.Transcript = 5`, tinyint — no schema change) with the account's GUID when it has one, else the e-mail. This records evidence only.
5. Ownership rules are untouched: an explicit/manual/supervisor owner is never overwritten (the recognised agent becomes a handler and can see the ticket); an unowned or provisional-Round-Robin ticket goes to the recognised, eligible agent.

**Required Genesys-side contract update (to be confirmed).** The flow/data action that builds the transcript `senderId` must send the agent's Genesys **User ID (GUID)** (`participant.userId` / the user's `id`),
not the user's e-mail or username. This repository contains no captured Genesys connected-agent event, so whether `routing.agentId` is also an e-mail in production is **unverified**:
check the live `routing.agentId` values (`TicketInteractions.GenesysAgentId`, `TicketInteractionHandlers.GenesysAgentUserId`) with a read-only query. `routing.agentId` is **not** given the e-mail fallback; if it turns out to be an e-mail, fix it in the flow rather than widening this fallback.
The fallback is a safety net and can be switched off once Genesys sends GUIDs.

**Existing data.** Nothing is backfilled. Ticket 225 (and any other ticket whose lines carried an e-mail `senderId`) gets no retroactive handler row; a redelivered transcript line for it would record one, and still would not change Shahd's ownership.
