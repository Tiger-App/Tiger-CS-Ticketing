# Phone conversations with no end event — investigation and proposed fix

Trigger: the Production ACTIVE_AI diagnostic (`Diagnose_TigerCS_PROD_BackfillActiveAi_ReadOnly.sql`) found **181** active, unowned tickets held back by the
backfill, **all with a live Phone Genesys interaction and no recorded end event** (179 `NO_CLOCK_NO_END_EVENT`, 2 `EXCLUDED_CALL_CENTER`).

> **What is and is not proven.** Everything about TigerCS below is read from the code. **No Genesys configuration, trigger log, IIS log or
> TigerGroupWeb log was available**, so the actual cause is **not yet identified**. This document lists the candidate causes, the read-only checks
> that tell them apart, and a fix proposal. Nothing was changed in Production; BackgroundJobs stays off; no ticket is closed.

## 1. How a phone call is meant to end in TigerCS (verified in code and in `docs/Genesys`)

```
Genesys trigger  v2.detail.events.conversation.{id}.customer.end   (mediaType voice, message)
   -> Architect workflow "TigerCS - Conversation Event" (Flow.conversationId, mediaType, queueId, disconnectType)
        1. data action 02 "Create or Reuse Ticket"   -> Task.ticketId   (idempotent on conversationId + serviceRequestId)
        2. Decision: IsSet(Flow.disconnectType) and disconnectType != ""
              yes -> data action 04 "TigerCS - End Conversation"
                       PATCH https://tigergroup.ae/api/genesys/tickets/{ticketId}
                       { "conversationId": "...", "ended": { "endedAtUtc": "<ToString(GetCurrentDateTimeUtc())>", "endReason": "<disconnectType>" } }
              no  -> data action 03 "Update Routing"          (this branch NEVER ends the conversation)
   -> tigergroup.ae (TigerGroupWeb) validates the Genesys bearer and forwards to {TigerCS}/api/genesys/tickets/{ticketId} with a service-account token
   -> TigerCS  GenesysController.UpdateTicket -> GenesysTicketUpdateAppService.UpdateAsync -> GenesysConversationEndAppService.EndAsync -> TicketInteraction.End()
```

### TigerCS endpoint — verified
| Question | Finding |
|---|---|
| Route / auth | `PATCH /api/genesys/tickets/{ticketId:long}`, policy `CustomerVerification` (service-account JWT). No employee id in the token = 401. |
| Which interaction | Addressed by **(ticketId, conversationId)** (`GetByConversationAndTicketAsync`). A conversation that belongs to another ticket answers 409; an unknown one 404. A sibling ticket's interaction is never touched. |
| Where `EndedAtUtc` is written | Only when the body contains the **`ended`** object (`GenesysConversationEndPart`). `EndAsync` calls `interaction.End(endedAtUtc, endReason)`; `EndedAtUtc` defaults to the server clock when absent; write-once; ticket status is never touched; the response echoes `conversationEnded`. |
| Channel | The end path does not look at the channel: Phone and chat behave identically. A voice call needs no transcript. |
| `Genesys:Enabled = false` | Every PATCH answers **503** and stores nothing. |
| **Silent no-ops** | Properties the model does not know are **ignored**: a body with a top-level `endedAtUtc` / `endReason` (no `ended` object) answers **200 Applied with `conversationEnded: false`** and ends nothing. A date that does not parse (the template inserts `ToString(GetCurrentDateTimeUtc())` verbatim) is a **400** and the workflow's data action fails. |
| **No trace of failures** | `GenesysController` / `GenesysTicketUpdateAppService` log nothing and write no audit row for a rejected or no-op PATCH. A failed end therefore leaves **no row in TigerCS** — only the IIS / proxy logs and Genesys' own execution history show it. (Superseded going forward: §10 adds one application log line per PATCH.) |

The TigerCS code itself looks correct for this path. The 181 are therefore either (a) PATCH-with-`ended` never arriving, (b) arriving but rejected/no-op, or
(c) arriving for a different ticket id of the same conversation.

## 2. Candidate causes and how to tell them apart

| # | Cause | Where | Evidence that points to it |
|---|---|---|---|
| H1 | The `customer.end` trigger / workflow is not active, or the topic does not fire for this call shape (blind transfer, queue abandon, IVR hang-up) | Genesys | Section A: phone end rate is low for **all** weeks, or drops to 0 from a date (= when the trigger changed). Section B: `GenesysConversationEnded` stops while `GenesysInquiryIngested` continues. |
| H2 | `Flow.disconnectType` is empty/unset in the event, so the Decision takes the **"no"** branch (Update Routing) and never ends | Genesys workflow | Section C: ended phone calls exist but with blank/odd `EndReason`; un-ended calls have routing audit rows (section D `WithRoutingPatchAudit`) = the workflow ran but took the other branch. |
| H3 | `endedAtUtc` is sent in a format the API cannot parse (400), or in a different shape (top-level fields → 200 no-op) | Genesys data action / template | IIS log: PATCH status mix (section 3). Many 400s on `PATCH /api/genesys/tickets/*` = format. 200s with no end = shape. |
| H4 | The proxy (TigerGroupWeb) does not forward **PATCH**, or answers 502/413, or its service token fails (401 -> 502) | tigergroup.ae | Proxy log has the PATCH but TigerCS IIS log does not. `docs/Genesys/TigerGroupWeb-Forwarding-Implementation.md` states its forwarding patch was **specified, not applied**. |
| H5 | The workflow's "Create or Reuse Ticket" call used a different `serviceRequestId` than the flow's first call, so it **created a second ticket** and the end landed on that one | Genesys workflow | Section E: `SiblingInteractions > 0` and `SiblingsEnded > 0` (the conversation WAS ended — on another ticket). |
| H6 | The caller hung up before the ticket existed / the flow's Create call failed (the workflow repairs it, see above) | Genesys | Un-ended calls with no queue, no agent, no routing audit (section D). |
| H7 | The calls are genuinely still connected | Genesys | Section F: `GENESYS_STILL_CONNECTED`. Implausible for 181 calls of the ages in section E, but only Genesys can say. |
| H8 | Auth (expired/rotated service token or Genesys OAuth client) | proxy / Genesys | 401 in IIS / proxy log; `AuditEntries` for every Genesys action stop on a date (section B). |

## 3. Read-only checks (nothing here writes)

1. **TigerCS database:** `Diagnose_TigerCS_PROD_PhoneEndEvents_ReadOnly.sql` — sections A–E. Send back A, B, C, D and the summary rows of E.
2. **TigerCS IIS log (read-only; needs the file system, not the database).** Status mix of the end call over the last 14 days:
   ```powershell
   Get-ChildItem C:\inetpub\logs\LogFiles -Recurse -Filter *.log | Where-Object LastWriteTime -gt (Get-Date).AddDays(-14) | ForEach-Object {
       $fields = $null
       foreach ($line in Get-Content $_.FullName) {
           if ($line.StartsWith('#Fields:')) { $fields = $line.Substring(9).Split(' '); continue }
           if ($line.StartsWith('#') -or -not $fields) { continue }
           $v = $line.Split(' ')
           if ($v[[array]::IndexOf($fields,'cs-method')] -eq 'PATCH' -and $v[[array]::IndexOf($fields,'cs-uri-stem')] -like '/api/genesys/tickets/*') {
               [pscustomobject]@{ Day = $v[0]; Status = $v[[array]::IndexOf($fields,'sc-status')] } } } } |
       Group-Object Day, Status | Sort-Object Name | Select-Object Name, Count
   ```
   Do the same on the **TigerGroupWeb** server for the public route, and compare the counts per day: PATCH in the proxy but not in TigerCS = H4.
3. **Genesys:** the execution history of data action `TigerCS - End Conversation` and of the `TigerCS - Conversation Event` workflow for a few of the conversation ids listed by section E (failures, HTTP status, the exact `endedAtUtc` string sent).
4. **Matching with Genesys (section F of the script):** export the conversations listed by section E from Genesys (conversation id, conversation end time UTC, disconnect type; end time empty if still connected), fill `@GenesysExport`, and run the script again. The verdict per conversation is `GENESYS_ENDED_TIGERCS_MISSING_END` (the end event never reached TigerCS: replay candidate), `GENESYS_STILL_CONNECTED` (stays protected), `BOTH_ENDED`, or `NOT_IN_TIGERCS`; ticket numbers in the set that the export does not cover are reported as unverified and stay protected.

## 4. Proposed fix (not implemented — needs the evidence above and approval)

**Future phone calls**
1. **Genesys side (most likely place of the defect):** after step 3 above, correct the trigger / Decision / date template. Specifically make the End action run **whenever the trigger fires** (do not gate it on `disconnectType` — send `endReason` blank when absent) and send `endedAtUtc` as ISO-8601 UTC (`yyyy-MM-ddTHH:mm:ssZ`) or leave it empty (the server clock is used).
2. **TigerGroupWeb:** confirm the PATCH route and the service-token renewal are live (it must forward `PATCH /api/genesys/tickets/{ticketId}` byte-for-byte).
3. **TigerCS hardening (small, additive, behaviour-preserving):**
   - log one structured line per `PATCH /api/genesys/tickets/{id}` with the outcome, HTTP status and **which parts were present** (`ended`, `routing`, `transcript`, …) — no customer data — so a rejected or no-op end is visible;
   - a body that carries `endedAtUtc`/`endReason` at the top level (the known wrong shape) is answered **400** with a message pointing at `ended`, instead of a silent 200 no-op;
   - accept the Architect `ToString(DateTime)` variants for `endedAtUtc` (same approach as the existing `LenientNullableBoolConverter`), falling back to the server clock only when the value is blank.
4. **Detection, not automation:** schedule this diagnostic (read-only) as a weekly report: phone calls older than N hours with no end event, per day. No job closes anything.

**The historical 181**
- They **stay protected** (ACTIVE_AI, live-AI restriction unchanged, `StaleAiAfterHours` stays null, BackgroundJobs stays off) until section F proves each conversation's real state.
- For conversations Genesys reports as **ended**, replay the **real** end time through the existing, idempotent, audited operation — `PATCH /api/genesys/tickets/{ticketId}` with `{ "conversationId": "...", "ended": { "endedAtUtc": "<Genesys end time>", "endReason": "<Genesys disconnectType>" } }` (write-once; `GenesysConversationEnded` audit row each; ticket status/owner/SLA untouched; `Genesys:AutoRaiseHandoffOnConversationEnd` is off by default, **confirm it is off in Production first**). A small dry-run-by-default replay script reading the verified export can be provided on request; it is not part of this change.
- For conversations Genesys reports as **still connected**, or that are not in the export: nothing is written; they stay excluded.
- Consequence to expect: once an interaction is ended, the ticket is "AI ended without human" and the **existing approved rules** apply (`@IncludeAiEndedWithoutHuman = 1`): the next backfill dry run assigns it to a CS Agent, and the classification rule labels it General / General if unclassified. Nothing is closed.

---

# Addendum — after the first Production results

## 5. What the Production results establish

| Result (Production) | Conclusion |
|---|---|
| 181 ACTIVE_AI tickets = 181 **distinct** live conversation ids, **0** ended sibling interactions | The end was **not** applied to a sibling ticket of the same conversation (H5 ruled out), and no conversation is shared by two held-back tickets. |
| Week of 2026-11-02: **192** Phone interactions, **1** ended (0.5%) | Phone end events essentially **never** arrive. This is systematic, not a few lost calls. |
| `GenesysConversationEnded` audit rows exist | Some PATCH-with-`ended` reaches TigerCS. Whether those are Phone or Chat is answered by `Diagnose_TigerCS_PROD_PhoneEndAuditChannel_ReadOnly.sql` (result 1). If they are all Chat, the end integration works for chat and is **missing or gated for Phone only**. |

## 6. Hop-by-hop trace (status of each hop)

| # | Hop | Evidence | Status |
|---|---|---|---|
| 1 | Genesys trigger `v2.detail.events.conversation.{id}.customer.end`, mediaType in `voice`,`message` | `Genesys-Cloud-Configuration.md` §8.2 | **Genesys config, not visible from TigerCS.** Check it exists, is active, and its media-type criteria include `voice`. |
| 2 | Workflow `TigerCS - Conversation Event` | §8.1 | **Genesys config.** |
| 2a | step 1: data action 02 "Create or Reuse Ticket" (no `customerPhone`, no `serviceRequestId`) | `GenesysInquiryIngestionAppService.IngestAsync`: idempotency is checked **first** (`FindExistingAsync`; no `serviceRequestId` = "the conversation IS the ticket"), before channel/phone validation | **Verified in code: cannot create a duplicate ticket and is not rejected for a missing phone** when the ticket already exists. Returns the existing `ticketId` (200 `AlreadyIngested`). |
| 2b | step 2: Decision `IsSet(Flow.disconnectType) and Flow.disconnectType != ""` | §8.1 | **Prime suspect.** *Yes* → End Conversation; *No* → **Update Routing, which never ends the conversation**. If the `customer.end` event for voice carries an empty/absent `disconnectType` under that input variable, **every phone call takes the routing branch and is never ended**. Confirm in the Genesys execution history of the workflow for one phone conversation id (which branch ran, and the value of `Flow.disconnectType`). Chat events may carry the field and voice not, which would explain "chat ends, phone does not". |
| 3 | Data action 04 "TigerCS - End Conversation" | `data-actions/04-end-conversation.json` | Body template verified (below). `endedAtUtc` is inserted verbatim as `ToString(GetCurrentDateTimeUtc())`. |
| 4 | TigerGroupWeb proxy `PATCH /api/genesys/tickets/{ticketId}` | `TigerGroupWeb-Patch/GenesysController.cs`, `TicketingGenesysService.cs` | See §7. Forwards the body **verbatim**; cannot drop `ended`. Whether the **patched** version is deployed is **unverified**. |
| 5 | TigerCS `GenesysController.UpdateTicket` → `GenesysTicketUpdateAppService` → `GenesysConversationEndAppService.EndAsync` → `TicketInteraction.End()` | `src/TigerCS.Api/Controllers/GenesysController.cs` and services | Verified: records `EndedAtUtc` when `ended` is present; matched by (conversationId, ticketId). |

## 7. TigerGroupWeb proxy review (repo copy of the patch)

* `UpdateTicket(long ticketId, [FromBody] JsonElement body, ...)` → `requestJson = body.GetRawText()` → `UpdateTicketAsync` → `PATCH {BaseUrl}/api/genesys/tickets/{ticketId}` with the **service-account** token. The body is forwarded **byte for byte**: `ended`, `routing`, `transcript`, … reach TigerCS untouched. **The proxy is not a place where `ended` can be lost or reshaped.**
* A non-JSON or empty body is refused by the proxy itself with **400** before TigerCS sees it.
* TigerCS answers (200/400/404/409/422/503) are relayed as received; a timeout is **504**, a network failure **502** (never retried automatically; the caller may resend — the call is idempotent).
* The proxy logs **one line per call**: operation, ticket id, status code, elapsed ms — **no body**. So in the proxy log, `ticket update (TicketId N) answered by TigerCS Ticketing: <status>` shows every PATCH and its status, but **not which parts it carried**. The original (unpatched) proxy retried on any 401 and logged the full exception; whether the patched build is live is not known.
* Status to look for in the proxy log for phone calls: 400 (bad date / bad JSON), 401/502 (service token), 404/409 (ticket/conversation mismatch), 413, 504.

## 8. The exact PATCH body

What data action 04 sends (rendered):
```json
PATCH https://tigergroup.ae/api/genesys/tickets/12345
Authorization: Bearer <genesys token>      (replaced by the proxy with the TigerCS service-account token)
{ "conversationId": "7a1c…", "ended": { "endedAtUtc": "2026-11-05T09:41:07.123Z", "endReason": "peer" } }
```

| Body | TigerCS answer | Ends the conversation? |
|---|---|---|
| `{"conversationId":"…","ended":{"endedAtUtc":"<ISO-8601 UTC>","endReason":"peer"}}` | 200 `Applied`, `conversationEnded: true` | **Yes** |
| `{"conversationId":"…","ended":{"endedAtUtc":null,"endReason":""}}` | 200 `Applied`, `conversationEnded: true` (server clock) | **Yes** |
| `{"conversationId":"…","endedAtUtc":"…","endReason":"peer"}` (fields at the top level) | **200 `Applied`, `conversationEnded: false`** | **No — silently ignored** |
| `ended.endedAtUtc` not parseable (e.g. `"11/5/2026 9:41:07 AM"`) | **400** (model binding), nothing stored | No |
| `{"conversationId":"…","routing":{"queueId":"…"}}` (the "Update Routing" branch) | 200 `Applied`, `conversationEnded: false` | **No** (by design) |
| wrong `ticketId` for the conversation | 409 `ConversationTicketMismatch` | No |
| `Genesys:Enabled = false` | 503 | No |

The response field **`conversationEnded`** is the proof: a data action that does not map it (data action 04 maps it) hides a no-op from the workflow.

## 9. Do the `ConversationEnded_PATCH` audits belong to Phone or Chat?

Run `Diagnose_TigerCS_PROD_PhoneEndAuditChannel_ReadOnly.sql` (SELECT only, CTEs). Result 1 splits the end audits by day and channel; result 2 shows, per day, phone calls with **a routing PATCH but no end** (workflow ran, End branch not taken or failed) versus **no PATCH evidence at all** (nothing reached TigerCS); result 3 shows what the rare working phone end looks like; result 4 checks audit-versus-row consistency.

| Pattern | Meaning |
|---|---|
| End audits: Chat yes, Phone ~0; phone calls mostly `NotEnded_ButRoutingPatchArrived` | The workflow runs for voice and takes the **"Update Routing" branch** → `disconnectType` empty for voice (§6 step 2b). |

> **Correction (after the Production result: Chat 69 / Phone 2 ended; 0 phone calls with a routing audit and no end).** `GenesysRoutingChanged` is written only when the routing *changed* (`RecordRoutingAsync`: "an unchanged routing event writes nothing"). The "Update Routing" branch re-sends the queue the ticket was created with, so it leaves **no audit**. "No routing audit" therefore does **not** prove that no PATCH arrived, and it does **not** exclude the `disconnectType` Decision. Only the Genesys flow-instance history, the proxy/IIS status counts or the PR 193 log line can tell the cases apart. See [Phone-End-Architect-Verification-Checklist.md](Phone-End-Architect-Verification-Checklist.md).
| End audits: Chat yes, Phone ~0; phone `NotEnded_NoPatchEvidenceAtAll` | No PATCH reaches TigerCS for voice: the trigger criteria exclude `voice`, or the workflow fails before its PATCH (e.g. Create step), or the proxy/auth drops it. Compare the proxy log with the TigerCS IIS log. |
| End audits all phone and chat, but phone ends 0.5% | An intermittent failure (400 date, 401 token) — read the status mix in both logs. |

## 10. Smallest safe fix for future Phone calls

1. **Genesys (the fix itself, configuration only, no TigerCS release):** make the workflow call **End Conversation whenever the `customer.end` trigger fires**, not only when `Flow.disconnectType` is set: remove the Decision, send `endReason` = `Flow.disconnectType` or a fixed `"Unknown"` when empty, and send `endedAtUtc` either as ISO-8601 UTC or **empty** (the server clock is used). Keep "Update Routing" on the `acd.start` trigger only. Confirm the trigger criteria include `voice`.
2. **TigerCS logging (this PR, additive, no behaviour change):** `PATCH /api/genesys/tickets/{id}` now writes **one log line per call** — `REJECTED` (warning, with the outcome), `IGNORED` (warning: no recognised part at all, e.g. `endedAtUtc` at the top level), or `applied` (information, with which parts were present and `conversationEnded`). No body, transcript, name, phone or token is logged. A model-binding failure (unparseable date) is rejected by ASP.NET **before** the action runs and is therefore visible only as a 400 in the IIS/proxy log; the proxy line shows the status.
3. **Not changed:** historical interactions and their end times, the backfill, `BackgroundJobs`, the live-AI restriction, any ticket status.
4. **Later, only if the Genesys change is impossible:** treat an absent `disconnectType` server-side — not recommended, because TigerCS would be inventing an end for a conversation it cannot see.

## 11. Verification with ONE completed Phone conversation

Use a test number and a call you place yourself (UAT first if available). Nothing below touches historical data.

1. **Before the call:** note the time. In Genesys, enable/confirm the corrected workflow and trigger.
2. **Place the call**, let the flow create the ticket (note `TigerCsTicketNumber` from the screen pop or the ticket list), then **hang up**.
3. **Genesys:** in the workflow execution history for that conversation, confirm the End Conversation action ran and returned `conversationEnded = true` (note the `conversationId`).
4. **Proxy log:** one line `Genesys ticket update (TicketId N) answered by TigerCS Ticketing: 200`.
5. **TigerCS application log (new):** `Genesys ticket update applied: ticket N, conversation <id>; parts: ended=True, …; conversationEnded=True.` A `REJECTED` or `IGNORED` line instead names the cause.
6. **Database (read-only):**
   ```sql
   SELECT i.TicketInteractionId, i.GenesysConversationId, i.ChannelId, i.EndedAtUtc, i.EndReason
   FROM dbo.TicketInteractions i WHERE i.GenesysConversationId = N'<conversationId>';
   SELECT a.OccurredAtUtc, a.Action FROM dbo.AuditEntries a WHERE a.EntityId = N'<conversationId>' AND a.Action = N'GenesysConversationEnded';
   ```
   Expect `EndedAtUtc` within seconds of the hang-up and one `GenesysConversationEnded` audit row; ticket status unchanged.
7. **Negative control (optional, same test conversation, after step 6):** resend the same End call: response `conversationEnded: true`, `EndedAtUtc` **unchanged** (write-once), and the log line says applied again.
8. **Pass criteria:** steps 3–6 all agree. If step 3 shows the action did not run, the cause is the trigger/Decision (§6); if it ran but step 4 shows a non-200, read the status (§7); if step 5 says `IGNORED`, the body shape is wrong (§8).
