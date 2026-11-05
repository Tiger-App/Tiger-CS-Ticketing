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
| **No trace of failures** | `GenesysController` / `GenesysTicketUpdateAppService` log nothing and write no audit row for a rejected or no-op PATCH. A failed end therefore leaves **no row in TigerCS** — only the IIS / proxy logs and Genesys' own execution history show it. |

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
