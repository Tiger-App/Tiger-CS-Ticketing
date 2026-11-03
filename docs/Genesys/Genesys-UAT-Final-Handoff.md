# Genesys UAT — final integration handoff

**Audience:** the Genesys Cloud / Architect team, the TigerGroupWeb operator, and the Ticketing UAT lead.
**Scope:** the Ticketing API and Web as implemented on branch `claude/zealous-albattani-m8qt71`. No feature is added by this document.
Background and rationale: [Integration-Audit-TG-LCS-20261021-0002.md](Integration-Audit-TG-LCS-20261021-0002.md) and
[Live-Transcript-And-Unclassified-Tickets.md](Live-Transcript-And-Unclassified-Tickets.md). Where this document and an older Genesys document disagree, **this one wins**.

## 0. What is and is not verified

| Item | Status |
|---|---|
| Ticketing API behaviour for every payload in section 4 | **Verified** — each of the 9 JSON blocks below was replayed verbatim (only `<EXPORTED_ID>`, the department and a second conversation id substituted) in sequence against an in-process test host of the Ticketing API, plus retries of create, transcript, request type and handoff; every call returned the status shown and the resulting state matched (3 messages + 1 human line stored once each, first response at the first human line's time, one handoff, AI-only chat left with no handoff). Test host only (in-memory database, fake CRM); not UAT. |
| Ticketing Web changes (Interactions, Unclassified filter, Classify) | Verified by automated tests. Not yet seen on a UAT site by a person. |
| TigerGroupWeb forwarding (OAuth token endpoint + pass-through) | **Not verified** — the patch is specified in [TigerGroupWeb-Forwarding-Implementation.md](TigerGroupWeb-Forwarding-Implementation.md) but its own header says it was never applied to TigerGroupWeb. |
| Genesys Architect flows / Data Actions in section 5 | **Not built, not tested.** Everything there is a specification. |
| Intent → `requestTypeId` ids | **No UAT export exists in the repository, so no id is listed.** Section 6 is a template to fill from the UAT database. |
| Any real Genesys event | **None seen.** Message-id stability, trigger values and human-leg message delivery are assumptions until UAT step 11 of the checklist. |

Example values (`8f4c2e1a-…`, ticket `123`, `TG-LCS-20261021-0002`, names, times) are illustrative samples, not identifiers from any system.

## 1. The flow at a glance

```
Customer opens chat ─► Architect inbound message flow
   1. POST /api/genesys/tickets            create (one ticket per conversationId, idempotent)   → ticketId
   2. PATCH …/tickets/{ticketId}           transcript line per message (+ customerActive)        (repeat)
   3. PATCH …/tickets/{ticketId}           requestType once the AI knows the intent              (and again if it changes)
   4. PATCH …/tickets/{ticketId}           handoff {required:true, trigger}   ONLY on an explicit exit
   5. PATCH …/tickets/{ticketId}           routing {agent…} + HumanAgent transcript lines        (human leg)
   6. PATCH …/tickets/{ticketId}           ended {endedAtUtc, endReason[, transcript]}           (always, any end)
```
Rules that never change: a missing request type, a conversation starting, a bot message, or a conversation ending is **never** a human handoff. Only step 4 asks for a person.
Ending a conversation never closes, resolves or re-statuses the ticket.

## 2. Connectivity and authentication

| | |
|---|---|
| Public base URL (what Genesys calls) | `https://tigergroup.ae` (TigerGroupWeb) |
| Token | `POST https://tigergroup.ae/api/genesys/oauth/token`, `Content-Type: application/x-www-form-urlencoded`, body `grant_type=client_credentials&scope=ticketing.genesys&client_id=…&client_secret=…` — done by the integration's **Custom Auth** action ([`data-actions/00-custom-auth-request-config.json`](data-actions/00-custom-auth-request-config.json)); Genesys stores only the OAuth client id/secret |
| Every call | `Authorization: Bearer <access_token>`, `Content-Type: application/json`, `Accept: application/json` (set by the data actions as `${authResponse.token_type} ${authResponse.access_token}`) |
| TigerGroupWeb → Ticketing | forwards the path, query and body unchanged, replacing the Authorization header with a Ticketing **service-account JWT**. Ticketing accepts the roles in policy `CustomerVerification`: CS Agent, CS Supervisor, System Administrator. A Department Employee or no token is refused (403 / 401) — verified. |
| Ticketing internal base (TigerGroupWeb → API) | `{TCS}` = the TigerCS.Api URL of the environment (operator-supplied; not in this repository) |
| Body limit | the forwarder refuses bodies over **256 KiB** with 413 (`MaxBodyBytes`). One transcript message per call is far below this; see 7.4 for the final transcript. |
| Failure statuses added by the forwarder | 401 (bad Genesys token), 413, 502 (Ticketing unreachable / rejected the integration account), 504 (time budget spent) |

Property names are camelCase. Unknown properties are ignored. Genesys data actions cannot omit a field, so a blank string `""` is accepted for optional text fields and means "not supplied" (booleans accept `true`/`false`/`"true"`/`"false"`/`""`). Exception: **`requestTypeId` must be an integer**; a blank/zero there is invalid, so call the action only when a mapping exists.

## 3. Endpoint summary

| # | Purpose | Method + path | Auth | Success | Idempotency key |
|---|---|---|---|---|---|
| 1 | Create / reuse the conversation's ticket | `POST /api/genesys/tickets` | Bearer | `201` first time, `200` repeat | `conversationId` |
| 2 | Everything that happens afterwards | `PATCH /api/genesys/tickets/{ticketId}` | Bearer | `200` | `conversationId` + content (each part independently idempotent) |
| 3 | Customer lookup by phone (optional, existing) | `GET /api/genesys/customers/lookup?phoneNumber=` | Bearer | `200` | read-only |

`{ticketId}` is the numeric id returned by step 1 (store it as participant data `TigerCsTicketId`). The `conversationId` in the body **must belong to that ticket** (otherwise 409).
No other Ticketing endpoint is needed for the scenarios below. Read-only support endpoints and the verification/OTP/documents/Collections routes are out of scope here (see [TigerGroupWeb-Forwarding-Implementation.md](TigerGroupWeb-Forwarding-Implementation.md)).

PATCH is one endpoint: send only the parts that changed; a body can combine parts (e.g. `transcript` + `customerActive` + `requestType`).

### PATCH field reference

| Field | Type | Meaning |
|---|---|---|
| `conversationId` | string, **required** | the Genesys conversation |
| `transcript[]` | array | live messages: `sender` (`Customer` \| `VirtualAgent` \| `HumanAgent` \| `System`), `sentAtUtc` (ISO-8601 UTC), `body` (non-blank), `senderName`, `senderId` (≤ 64), `externalMessageId` (≤ 64, **send it**) |
| `requestType` | `{requestTypeId}` or `{name}` | the AI's classification. **Send the id.** |
| `handoff` | `{required, trigger, reason, mode, agentAvailable, workItemId, assignedAgentId}` | `required:true` asks for a person; `required:false` + `reason` stands it down. `trigger`: `CustomerRequestedHuman` \| `AiEscalated` \| `RoutingDecision` \| `AiConnectionLost` \| `AgentTransfer`. `mode` (optional): `Callback` \| `ContinueChat` \| `ReplyInChannel` \| `HumanTakeover`. |
| `routing` | `{queueId, queueName, agentId, agentName}` | queue change / agent connected (Genesys **user id** in `agentId`) |
| `agentId`, `agentName` | string | the agent handling the conversation (apply-if-absent) |
| `ended` | `{endedAtUtc, endReason, transcript[]}` | the conversation finished, for any reason; end time/reason are write-once |
| `customerActive` | bool | the customer just did something — restarts the website/chatbot inactivity clock. Send `true` with **every** inbound customer message |
| `awaitingCustomerReply` | bool | bot asked a question / customer replied |
| `closedByAi` | bool | the AI finished and may close the ticket (honoured only while no human is involved) |
| `customerConfirmation` | `{confirmedResolved:true, confirmedAtUtc, note}` | record-only |

### PATCH success response (all scenarios)

```json
{
  "outcome": "Applied",
  "conversationId": "8f4c2e1a-6b7d-4c3a-9e52-0d1f5a7b3c90",
  "ticketId": 123,
  "ticketNumber": "TG-LCS-20261021-0002",
  "ticketStatus": "Open",
  "conversationEnded": false,
  "transcriptMessageCount": 2,
  "handoffStatus": null,
  "ticketAgentHandoffId": null,
  "awaitingCustomerReply": false,
  "inactivityDeadlineUtc": null,
  "awaitingCustomerReplyNote": null,
  "classificationStatus": null,
  "requestTypeId": null,
  "departmentId": null,
  "assignedEmployeeId": null,
  "closedByAi": false,
  "closedByAiNote": null
}
```
`ticketStatus` is echoed on every call as proof that nothing here closes the ticket. `transcriptMessageCount` is the **total** stored for the conversation. `classification*`/`requestTypeId`/`departmentId`/`assignedEmployeeId` are filled only when the request carried `requestType`. `handoffStatus` is reported on every update while human work is outstanding (`WaitingForAgent`, `Assigned`, `InProgress`, …). Null fields may be omitted by the serializer — treat absent as null.

## 4. Payloads

Every request below carries `Authorization: Bearer <token>`. Request bodies are exactly what Genesys sends; `<EXPORTED_ID>` is the one placeholder — replace it with the id from the UAT export (section 6), never with a guess.

### 4.1 Create an AI conversation ticket

`POST https://tigergroup.ae/api/genesys/tickets` — sent once, when the conversation starts and before the bot speaks. The department comes from `departmentCode`/`departmentId` if sent, otherwise from the Genesys queue → department mapping (Administration → Genesys routing). No request type yet: the AI has not classified.

<!-- payload: create -->
```json
{
  "conversationId": "8f4c2e1a-6b7d-4c3a-9e52-0d1f5a7b3c90",
  "channel": "LiveChat",
  "customerPhone": "+971500000001",
  "customerName": "Ahmed Ali",
  "customerEmail": "",
  "departmentCode": "LCS",
  "queueId": "",
  "queueName": "",
  "subject": "Website chat started",
  "startedAtUtc": "2026-10-21T09:00:00Z"
}
```
`201 Created` (first call):
```json
{
  "outcome": "TicketCreated",
  "conversationId": "8f4c2e1a-6b7d-4c3a-9e52-0d1f5a7b3c90",
  "ticketId": 123,
  "ticketNumber": "TG-LCS-20261021-0002",
  "classificationStatus": "AwaitingClassification",
  "requestTypeId": null,
  "departmentId": 4,
  "assignedEmployeeId": null,
  "classificationDetail": "no request type was supplied."
}
```
`200 OK` on any repeat of the same `conversationId` (same ticket, no second ticket): `"outcome": "AlreadyIngested"` with `conversationId`, `ticketId`, `ticketNumber`.
`channel`: `Phone`, `LiveChat` (aliases `WebsiteChat`, `WebChat`, `WebMessaging`), `WhatsApp`, `Instagram`, `Facebook` (alias `FacebookMessenger`), `SocialMedia` (generic: X/Telegram/other). **Architect must send `Instagram` and `Facebook` as their own values** — `SocialMedia` files the ticket under the generic Social Media channel and it will not appear under Instagram/Facebook. The ticket is created **Unclassified**, on the default priority (Normal), with **no human handoff** — `classificationStatus: "AwaitingClassification"` means "still unclassified", not "waiting for an agent".

### 4.2 Update the Request Type after AI intent classification

`PATCH https://tigergroup.ae/api/genesys/tickets/123` — when the intent is known **and has a mapped id** (section 6). Send again if the AI later changes its mind.

<!-- payload: request-type -->
```json
{
  "conversationId": "8f4c2e1a-6b7d-4c3a-9e52-0d1f5a7b3c90",
  "requestType": { "requestTypeId": <EXPORTED_ID> }
}
```
`200` — ticket moved to the Request Type's department (`OriginatingDepartmentId` unchanged), that department's automatic assignment run, the type's SLA applied to the running period without restarting the clock:
```json
{ "outcome": "Applied", "conversationId": "8f4c2e1a-6b7d-4c3a-9e52-0d1f5a7b3c90", "ticketId": 123, "ticketNumber": "TG-LCS-20261021-0002",
  "ticketStatus": "Open", "conversationEnded": false, "transcriptMessageCount": 0, "handoffStatus": null, "ticketAgentHandoffId": null,
  "classificationStatus": "Classified", "requestTypeId": <EXPORTED_ID>, "departmentId": 4, "assignedEmployeeId": null }
```
(`assignedEmployeeId` null = the department queue.) Same id again → `200` no-op. A **different** id replaces the first only while the ticket is Open and no person has worked it (no category set by a person, no human reply, no accepted handoff, no approval); afterwards `409` `genesys-request-type-conflict` and nothing changes. Unknown/inactive/ambiguous/unpublished id → `422` `genesys-invalid-request-type`, nothing changes. Never send a placeholder/default id to avoid leaving a ticket Unclassified.

### 4.3 Send live transcript messages

`PATCH https://tigergroup.ae/api/genesys/tickets/123` — after each message (or small batch), **in the order the messages happened, one call at a time** (see 7.5). Customer messages also carry `customerActive: true`.

<!-- payload: transcript-customer -->
```json
{
  "conversationId": "8f4c2e1a-6b7d-4c3a-9e52-0d1f5a7b3c90",
  "customerActive": true,
  "transcript": [
    { "sender": "Customer", "senderName": "Ahmed Ali", "senderId": "", "sentAtUtc": "2026-10-21T09:00:03Z",
      "body": "How does the handover process work?", "externalMessageId": "msg-0001" }
  ]
}
```
<!-- payload: transcript-bot -->
```json
{
  "conversationId": "8f4c2e1a-6b7d-4c3a-9e52-0d1f5a7b3c90",
  "transcript": [
    { "sender": "VirtualAgent", "senderName": "Tiger Bot", "senderId": "tiger-bot", "sentAtUtc": "2026-10-21T09:00:05Z",
      "body": "Handover happens after the final payment. Shall I send the checklist?", "externalMessageId": "msg-0002" }
  ]
}
```
`200` with `"transcriptMessageCount": 1`, then `2`, … , `"conversationEnded": false`, `"handoffStatus": null`. After a call carrying `customerActive:true`, `inactivityDeadlineUtc` is filled (the moment the website-chat inactivity rule would close an idle ticket) — informational.
Rules: stored in arrival order with the real `sentAtUtc`, sender and names; a message already stored (same `externalMessageId`, or — when blank — same sender + time + body) is ignored, so a retry returns `200` with an unchanged count; the bot is **always** `VirtualAgent`; bot lines never count as First Response; sending a transcript never raises a handoff. A bad `sender` or a blank `body` → `400 genesys-invalid-transcript` and **nothing** in that request is applied.

### 4.4 Customer-requested human handoff

`PATCH https://tigergroup.ae/api/genesys/tickets/123` — only when the customer explicitly asks for a person.

<!-- payload: handoff-customer -->
```json
{
  "conversationId": "8f4c2e1a-6b7d-4c3a-9e52-0d1f5a7b3c90",
  "handoff": {
    "required": true,
    "trigger": "CustomerRequestedHuman",
    "reason": "Customer asked to speak to an agent",
    "mode": "HumanTakeover",
    "agentAvailable": false
  }
}
```
`200` with `"handoffStatus": "WaitingForAgent"` and a `ticketAgentHandoffId`. Ticket Details then shows "Initiated by: Customer asked for a human" and the activity entry "Genesys — customer asked for a human agent". Repeating the call — or sending a different trigger while the first request is still outstanding — returns the same `ticketAgentHandoffId` (no second work item; the first request's trigger is kept). `mode` is optional; omit it if Genesys does not know — it is never guessed.

### 4.5 AI-initiated human handoff

Same endpoint, only the trigger and reason differ — the AI cannot resolve the request, or a business rule requires a person.

<!-- payload: handoff-ai -->
```json
{
  "conversationId": "8f4c2e1a-6b7d-4c3a-9e52-0d1f5a7b3c90",
  "handoff": {
    "required": true,
    "trigger": "AiEscalated",
    "reason": "Bot could not resolve the handover scheduling question",
    "mode": "HumanTakeover",
    "agentAvailable": false
  }
}
```
Use `"trigger": "RoutingDecision"` for a business rule and `"AiConnectionLost"` only when the bot genuinely failed — and only as an explicit call; Ticketing no longer infers any of these from a conversation ending. Always send the true trigger: if it is omitted Ticketing shows "Not stated by Genesys" and never assumes the customer asked.
If the AI recovers and no person is needed: `{"conversationId":"…","handoff":{"required":false,"reason":"AI resumed"}}` (a reason is mandatory; `400 genesys-handoff-reason-required` otherwise).

### 4.6 Human agent connects (human leg)

<!-- payload: agent-connects -->
```json
{
  "conversationId": "8f4c2e1a-6b7d-4c3a-9e52-0d1f5a7b3c90",
  "agentId": "5b0c1d7e-3f2a-4a8b-9c61-7e4d2f9a1b05",
  "agentName": "Layla Hassan",
  "routing": { "queueId": "", "queueName": "", "agentId": "5b0c1d7e-3f2a-4a8b-9c61-7e4d2f9a1b05", "agentName": "Layla Hassan" },
  "transcript": [
    { "sender": "HumanAgent", "senderName": "Layla Hassan", "senderId": "5b0c1d7e-3f2a-4a8b-9c61-7e4d2f9a1b05",
      "sentAtUtc": "2026-10-21T09:03:10Z", "body": "Hello Ahmed, this is Layla. I can help with the handover.", "externalMessageId": "msg-0005" }
  ]
}
```
`agentId` is the **Genesys user id**; it must be mapped to a Ticketing user (Administration → Genesys agent mapping) for the agent to appear as the handler/owner; an unmapped id is still stored verbatim but no owner is set. An agent connecting to work that is `WaitingForAgent` marks it `Assigned`. **First Response is recorded at the first `HumanAgent` line's `sentAtUtc`** (write-once). The bot id must never appear in `agentId`.

### 4.7 End an AI-only conversation without handoff

<!-- payload: end-ai-only -->
```json
{
  "conversationId": "8f4c2e1a-6b7d-4c3a-9e52-0d1f5a7b3c90",
  "ended": { "endedAtUtc": "2026-10-21T09:12:00Z", "endReason": "CustomerDisconnect" }
}
```
`200` with `"conversationEnded": true`, `"ticketStatus"` unchanged (e.g. `"Open"`), `"handoffStatus": null`. No handoff is created, nothing is resolved or closed, no First Response is recorded. To also close the ticket because the AI really finished, add `"closedByAi": true` to the same body (honoured only while no human is requested/connected and no person owns the ticket; otherwise the response says why in `closedByAiNote`).
`ended.transcript` is optional (see 7.4): if every message was already sent live, **omit it**. If included, it may repeat live messages — duplicates are ignored — and only new ones are added.

### 4.8 End a human-handled conversation

<!-- payload: end-human -->
```json
{
  "conversationId": "8f4c2e1a-6b7d-4c3a-9e52-0d1f5a7b3c90",
  "agentId": "5b0c1d7e-3f2a-4a8b-9c61-7e4d2f9a1b05",
  "agentName": "Layla Hassan",
  "ended": {
    "endedAtUtc": "2026-10-21T09:25:00Z",
    "endReason": "AgentDisconnect",
    "transcript": [
      { "sender": "HumanAgent", "senderName": "Layla Hassan", "senderId": "5b0c1d7e-3f2a-4a8b-9c61-7e4d2f9a1b05",
        "sentAtUtc": "2026-10-21T09:24:40Z", "body": "Your handover appointment is booked for Sunday.", "externalMessageId": "msg-0009" }
    ]
  }
}
```
`200`, `"conversationEnded": true`. The outstanding handoff is **not** completed by this call: the employee completes (or cancels) the human work in Ticketing → Pending Interactions, and the ticket continues under its normal lifecycle (Open → InProgress → … ). Ending never resolves or closes the ticket.

## 5. Required Genesys Architect / Data Action changes

### 5.1 Existing data actions — what to do with each

| Data action file | Today | Change for UAT |
|---|---|---|
| `00-custom-auth-request-config.json` | OAuth token via TigerGroupWeb | none (needs TigerGroupWeb patch applied — blocker B1) |
| `02-create-reuse-ticket.json` | POST create; template also sends `requestType.name` | Keep. **Do not populate `requestTypeName` at creation unless the AI already knows it; prefer 4.2 with the id.** Outputs `ticketId` → set participant data `TigerCsTicketId` |
| `03-update-routing.json` | PATCH routing, **always sends `requestType.name`** | Keep for queue/agent routing (4.6). Stop passing `requestTypeName` from this action — classification moves to the new action below (a blank name is ignored, so leaving it blank is safe) |
| `04-end-conversation.json` | PATCH `ended` (no transcript) | Keep for 4.7. Add optional `agentId`/`agentName` inputs for 4.8 (template change: add `"agentId":"$!esc.jsonString($input.agentId)","agentName":"…"` at the top level) |
| `05-request-human-agent.json` | PATCH `handoff{required:true,…}` | Keep. **Make `trigger` a required input** and set it from the exit that fired (`CustomerRequestedHuman` / `AiEscalated` / `RoutingDecision`); never default it |
| `06-cancel-human-request.json` | PATCH `handoff{required:false,reason}` | Keep (4.5 tail) |
| `10-awaiting-customer-reply.json` | PATCH `awaitingCustomerReply` / `closedByAi` | Keep for the bot-question timer and `closedByAi` |

### 5.2 Two new data actions (do not exist yet)

**`TigerCS - Update Request Type`** — `PATCH https://tigergroup.ae/api/genesys/tickets/$esc.url(${input.ticketId})`
Inputs: `ticketId` (string, required), `conversationId` (string, required), `requestTypeId` (**integer**, required).
```
{"conversationId": "$esc.jsonString(${input.conversationId})", "requestType": {"requestTypeId": ${input.requestTypeId}}}
```
Output map: `outcome`, `classificationStatus`, `requestTypeId`, `departmentId`, `ticketStatus` (all `$.…` of the success body).

**`TigerCS - Send Transcript Message`** — `PATCH https://tigergroup.ae/api/genesys/tickets/$esc.url(${input.ticketId})`
Inputs: `ticketId`, `conversationId`, `sender` (enum string `Customer|VirtualAgent|HumanAgent`), `body`, `sentAtUtc`, `externalMessageId` (required in practice), `senderName`, `senderId`, `customerActive` (string `"true"`/`""`).
```
{"conversationId": "$esc.jsonString(${input.conversationId})", "customerActive": "$!esc.jsonString($input.customerActive)", "transcript": [{"sender": "$esc.jsonString(${input.sender})", "senderName": "$!esc.jsonString($input.senderName)", "senderId": "$!esc.jsonString($input.senderId)", "sentAtUtc": "$esc.jsonString(${input.sentAtUtc})", "body": "$esc.jsonString(${input.body})", "externalMessageId": "$!esc.jsonString($input.externalMessageId)"}]}
```
Output map: `outcome`, `transcriptMessageCount`, `conversationEnded`, `handoffStatus`.
One message per call is the tested shape. Batching several messages needs an array-building template that has not been exercised — do not rely on it in UAT.
These two templates follow the style of the existing action files; **they have not been imported into Genesys or run there.** (The request bodies they produce were replayed from sections 4.2/4.3 only.)

### 5.3 Flow changes (inbound message flow, bot flow, handoff)

1. **Start:** call *Create or Reuse Ticket* first; store `ticketId`/`ticketNumber` as participant data (`TigerCsTicketId`, `TigerCsTicketNumber`). If it fails, continue the conversation — retry per section 7; never block the customer.
2. **Each message:** after each customer message and each bot reply, call *Send Transcript Message* **sequentially** (wait for the response before the next call). Customer lines carry `customerActive:"true"`. `sender` is `VirtualAgent` for the bot — never `HumanAgent`.
3. **Intent:** when the AI classifies, look up the intent in the mapping table (section 6). Found → call *Update Request Type* with the id. Not found → **call nothing** (the ticket stays Unclassified; an employee classifies it from Tickets → Classification: Unclassified). Call again only if the intent changes.
4. **Handoff exits only:** wire *Request Human Agent* only to (a) the customer's explicit request for a person, (b) the bot's deliberate escalation, (c) a business-rule transfer. **Remove any call that requests a human** on conversation start, on a missing intent, per bot message, or at conversation end.
5. **Human leg:** when an agent is connected call *Update Routing* with the agent's Genesys user id (4.6). After the transfer to a queue the bot flow no longer sees the conversation — **how `HumanAgent` messages reach Ticketing is an open design item (blocker B3).**
6. **End:** *always* call *End Conversation* on any disconnect (customer left, agent ended, bot completed). Include `closedByAi` only when the AI truly finished (separate call via *10*).
7. Handle responses per section 7. Do not treat `classificationStatus: "AwaitingClassification"` or any non-null `handoffStatus` on a call you did not make as an error.

## 6. Intent → `requestTypeId` mapping template

**Status: ids not exported.** The repository holds only the approved UAT *baseline catalogue* (names, departments, default priorities). Request Type ids are database identity values and differ per environment, and no UAT export was supplied, so the id column below is intentionally empty. **Do not copy ids from another environment and do not guess.**

How to fill it (read-only):

* SQL: run [`uat/export-request-type-mapping.sql`](uat/export-request-type-mapping.sql) on the UAT database — returns `RequestTypeId`, name, department and `UsableByGenesys` (active + department active + workflow Published); or
* API: `GET {TCS or TigerGroupWeb}/api/request-types` with any staff token (active types only; check `hasPublishedWorkflow: true`).

Only rows with `UsableByGenesys = 1` may be mapped. Fill **Genesys intent(s)** with the exact intent/topic names the bot already produces; one intent → one id; leave the id blank (and the intent unmapped) when unsure — an unmapped intent safely leaves the ticket Unclassified.

Genesys-side storage: a data table with columns `intent` (key), `requestTypeId` (integer), `requestTypeName` (reference only, never sent), `environment`.

| Request code | Department | Request Type (baseline name — verify in the export) | Default priority | UAT `requestTypeId` | Genesys intent(s) | Verified usable |
|---|---|---|---|---|---|---|
| `CS-GEN-001` | Customer Service | General Inquiry | Normal | **NOT EXPORTED** | _(Genesys / business to fill)_ | ☐ |
| `CS-GEN-002` | Customer Service | Office Hours / Contact Information | Low | **NOT EXPORTED** | _(Genesys / business to fill)_ | ☐ |
| `CS-GEN-003` | Customer Service | Construction Update | Normal | **NOT EXPORTED** | _(Genesys / business to fill)_ | ☐ |
| `CS-CMP-001` | Customer Service | Complaint | High | **NOT EXPORTED** | _(Genesys / business to fill)_ | ☐ |
| `CS-CMP-002` | Customer Service | Feedback / Suggestion | Low | **NOT EXPORTED** | _(Genesys / business to fill)_ | ☐ |
| `REG-NOC-001` | Customer Service | NOC for Resale | Normal | **NOT EXPORTED** | _(Genesys / business to fill)_ | ☐ |
| `REG-NOC-002` | Customer Service | NOC for Golden Visa | Normal | **NOT EXPORTED** | _(Genesys / business to fill)_ | ☐ |
| `REG-NOC-003` | Customer Service | NOC for Mortgage | Normal | **NOT EXPORTED** | _(Genesys / business to fill)_ | ☐ |
| `REG-CON-001` | Registration | SPA / Contract Inquiry | Normal | **NOT EXPORTED** | _(Genesys / business to fill)_ | ☐ |
| `REG-DLD-001` | Registration | Ownership Transfer / Title Deed Inquiry | Normal | **NOT EXPORTED** | _(Genesys / business to fill)_ | ☐ |
| `COL-PAY-001` | Collections | Payment / Outstanding Balance Inquiry | Normal | **NOT EXPORTED** | _(Genesys / business to fill)_ | ☐ |
| `COL-PAY-002` | Collections | Returned Cheque | High | **NOT EXPORTED** | _(Genesys / business to fill)_ | ☐ |
| `COL-PAY-003` | Collections | Cheque Collection | Normal | **NOT EXPORTED** | _(Genesys / business to fill)_ | ☐ |
| `COL-PAY-004` | Collections | Payment Cheque Inquiry | Normal | **NOT EXPORTED** | _(Genesys / business to fill)_ | ☐ |
| `HO-NOC-001` | Customer Service | NOC for Handover | Normal | **NOT EXPORTED** | _(Genesys / business to fill)_ | ☐ |
| `HO-HND-001` | Handover | Coordinate Handover | Normal | **NOT EXPORTED** | _(Genesys / business to fill)_ | ☐ |
| `HO-HND-002` | Handover | Schedule Handover Appointment | Normal | **NOT EXPORTED** | _(Genesys / business to fill)_ | ☐ |
| `HO-HND-003` | Handover | Move-In / Move-Out | Normal | **NOT EXPORTED** | _(Genesys / business to fill)_ | ☐ |
| `HO-HND-004` | Handover | Handover Maintenance Follow-up | Normal | **NOT EXPORTED** | _(Genesys / business to fill)_ | ☐ |
| `FM-MNT-001` | Facilities Management | Repair / Maintenance Request | Normal | **NOT EXPORTED** | _(Genesys / business to fill)_ | ☐ |
| `FM-UTL-001` | Facilities Management | Utilities Inquiry | Normal | **NOT EXPORTED** | _(Genesys / business to fill)_ | ☐ |
| `FM-COM-001` | Facilities Management | Common Area Maintenance | Normal | **NOT EXPORTED** | _(Genesys / business to fill)_ | ☐ |
| `FM-SVC-001` | Facilities Management | Service Charge Inquiry | Normal | **NOT EXPORTED** | _(Genesys / business to fill)_ | ☐ |
| `LCS-TEN-001` | Leasing Customer Services | Tenancy Contract | Normal | **NOT EXPORTED** | _(Genesys / business to fill)_ | ☐ |
| `LCS-EJR-001` | Leasing Customer Services | Ejari | Normal | **NOT EXPORTED** | _(Genesys / business to fill)_ | ☐ |
| `LCS-BKG-001` | Leasing Customer Services | Booking | Normal | **NOT EXPORTED** | _(Genesys / business to fill)_ | ☐ |
| `LCS-MOV-001` | Leasing Customer Services | Move-In / Move-Out | Normal | **NOT EXPORTED** | _(Genesys / business to fill)_ | ☐ |
| `LCS-CHK-001` | Leasing Customer Services | Rent / DEWA / AC Cheque Inquiry | Normal | **NOT EXPORTED** | _(Genesys / business to fill)_ | ☐ |
| `BRK-COM-001` | Customer Service | Broker Commission Inquiry | Normal | **NOT EXPORTED** | _(Genesys / business to fill)_ | ☐ |
| `BRK-CHK-001` | Customer Service | Broker Cheque Collection | Normal | **NOT EXPORTED** | _(Genesys / business to fill)_ | ☐ |
| `SAL-INQ-001` | Customer Service | Property Sales Inquiry | Normal | **NOT EXPORTED** | _(Genesys / business to fill)_ | ☐ |
| `LEG-INQ-001` | Customer Service | Legal Notice / Permit / Approval Inquiry | High | **NOT EXPORTED** | _(Genesys / business to fill)_ | ☐ |
| `REC-HR-001` | Customer Service | HR Inquiry | Low | **NOT EXPORTED** | _(Genesys / business to fill)_ | ☐ |
| `REC-MKT-001` | Customer Service | Marketing Inquiry | Low | **NOT EXPORTED** | _(Genesys / business to fill)_ | ☐ |
| `REC-OTH-001` | Customer Service | Other Reception Inquiry | Normal | **NOT EXPORTED** | _(Genesys / business to fill)_ | ☐ |

Maintenance: re-export after any Request Type is added, renamed, deactivated or has its workflow unpublished. A mapped id that becomes unusable makes step 4.2 answer `422` (the conversation continues; the ticket stays as it was).

## 7. Handling HTTP errors, retries and idempotency

### 7.1 Why retries are safe

* `POST /tickets` is keyed by `conversationId`: repeating it returns the **same** ticket (`200`), never a second one, even under concurrent delivery.
* Every PATCH part is idempotent: same `requestType` → no-op; `handoff.required:true` while work is outstanding → same work item; `ended` is write-once (a repeat never moves the end time); transcript messages are de-duplicated (7.3); `customerActive` repeats within the same instant change nothing.
* Request validation happens first: a malformed transcript, an unrecognised handoff mode/trigger, an invalid request type, a missing ticket/conversation or a ticket/conversation mismatch is refused **before anything in that request is written**. The only refusals that can follow parts already applied in the same body are `409 concurrency-conflict` and `422 genesys-no-open-handoff`; resending the identical body is still safe because the applied parts are idempotent no-ops.

### 7.2 Status handling

Errors are `application/problem+json`; branch on the `type` URL, not the title.

| HTTP | `type` (suffix of `https://tigercs.internal/problems/…`) | Meaning | What Genesys does | Retry? |
|---|---|---|---|---|
| **400** | `genesys-conversation-id-required` | `conversationId` missing/blank | Flow bug — fix the mapping. | No |
| 400 | *(validation problem)* on POST | unsupported `channel` / malformed body | Flow/config bug. | No |
| 400 | `genesys-invalid-transcript` | unknown `sender` or blank `body`; **nothing in the request applied** | Fix and resend a corrected request; log. A bad message must not be retried unchanged. | No (unchanged) |
| 400 | `genesys-invalid-handoff-trigger` / `-mode` / `genesys-handoff-reason-required` | unrecognised vocabulary or missing reason | Fix the flow constants. | No |
| 400 | `genesys-integration-disabled` | `Genesys:Enabled=false` (note: on **PATCH** this is 400; on POST it is 503) | Operational incident; alert. | Later, after it is enabled |
| 400 | `genesys-invalid-customer-confirmation` | `confirmedResolved` not true | Fix flow. | No |
| **404** | `ticket-not-found`, `genesys-conversation-not-found` | wrong `ticketId`, or the create never succeeded | Re-run *Create or Reuse Ticket* (idempotent) to obtain the id, then resend. | Once, after re-create |
| **409** | `concurrency-conflict` | a concurrent write to the same ticket/conversation won; the part that collided was **not stored** (a part applied earlier in the same body, e.g. a request type, stays applied and is a no-op on resend) | Wait ~1 s, resend the **same body**; up to 3–5 attempts with growing delay (1 s, 2 s, 4 s). | **Yes** |
| 409 | `genesys-request-type-conflict` | ticket already has a different type and a person has worked it | Business outcome, not an error: keep going, do not resend. | No |
| 409 | `ticket-closed` | ticket is Closed | Stop sending classification; other parts may still be sent. | No |
| 409 | `genesys-conversation-ticket-mismatch` | `conversationId` does not belong to `ticketId` | Flow bug (wrong participant data). Alert. | No |
| **422** | `genesys-invalid-request-type` (PATCH) / `genesys-request-type-invalid` (POST) | unknown, inactive, ambiguous, department inactive or workflow unpublished; PATCH: ticket unchanged; **POST: no ticket created** | Mapping/config bug — alert. On POST, resend **without** `requestType` (ticket created Unclassified). On PATCH continue the conversation. | Only after fixing the cause (POST: resend without type) |
| 422 | `genesys-department-not-resolved` | no department from `departmentId`/`departmentCode`/queue mapping | Fix queue mapping/flow constant (Administration → Genesys routing). | After fix |
| 422 | `genesys-channel-not-configured`, `genesys-ticket-creation-failed` | channel inactive / creation refused | Alert. | After fix |
| 422 | `genesys-no-open-handoff` | assignment reported with no outstanding human work | Ignore if you were assigning; otherwise flow ordering bug. | No |
| 401 | — | bad/expired Genesys token | The integration refreshes its OAuth token and retries once. | Once |
| 413 | — | body > 256 KiB at the forwarder | Send fewer messages per call / omit `ended.transcript` (7.4). | Smaller body |
| 502 / 503 / 504 | — | Ticketing/forwarder unavailable or timed out | The outcome is unknown, but retrying is safe (idempotent). Backoff 2 s → 4 s → 8 s → 16 s, max 5 tries, then log. For the live transcript keep an ordered local queue and drain it in order. | **Yes** |

A retry must send the **identical body** (same `externalMessageId`s, same `sentAtUtc`), so Ticketing can recognise it.

### 7.3 Duplicates and ordering

* De-duplication key: `externalMessageId` when present (**keep it ≤ 64 characters**; surrounding spaces are ignored and a longer id is cut to 64 when stored, and retries are matched against that stored form); otherwise sender + `sentAtUtc` + `body`. A blank `""` id is treated as absent. **Use the Genesys message id** — two different customers' "yes" at the same second would otherwise collapse.
* The transcript order is the **arrival order** of the first delivery (Ticketing assigns the next sequence number). It is not re-sorted by timestamp.

### 7.4 Final transcript

If every message was sent live, call *End Conversation* **without** `transcript`. If a complete final transcript is also sent it is reconciled, not duplicated (live rows are kept untouched; a line first sent without an id and repeated with one is recognised as the same line), but a long conversation can exceed the 256 KiB forwarder limit (413) — keep it out unless a message was missed.

### 7.5 Ordering and concurrency

Data actions called concurrently can arrive out of order and can collide (a collision answers `409 concurrency-conflict`; the colliding messages were not stored — resend). Call the transcript action **one at a time per conversation**, in message order.

## 8. Deployment checklist — Ticketing API and Web

No database migration, no new table/column/index. Rollback = redeploy the previous build (rows written by the new build are readable by the old one).

**Before**
- [ ] Branch `claude/zealous-albattani-m8qt71` reviewed and merged per your process; CI green (full suite: 5198 passed, 0 failed, 51 skipped at hand-off, run with DOTNET_USE_POLLING_FILE_WATCHER=1).
- [ ] Back up / export `TicketAgentHandoffs` (used by the optional cleanup in step 7).
- [ ] Configuration review in the target environment: `Genesys:Enabled=true`; `Genesys:DefaultTicketPriority` as intended (Normal); remove `Genesys:HumanQueueForUnclassified` (ignored if left); `Genesys:AutoRaiseHandoffOnConversationEnd` **unset/false** (setting it true restores the old "ended = request a human" behaviour); `BackgroundJobs:Enabled=true` (SLA sweeps, inactivity closure); channels `LIVE_CHAT` etc. active; Genesys queue → department mappings present.
- [ ] Service account for TigerGroupWeb exists with role CS Agent/CS Supervisor/System Administrator.

**Deploy**
1. [ ] Deploy **Ticketing API** first (or both together). The Web's Unclassified filter sends `unclassified=true`; an older API would ignore it and list every ticket.
2. [ ] Deploy **Ticketing Web**.
3. [ ] API smoke: `GET /api/tickets?unclassified=true` → 200; `POST /api/genesys/tickets` with a test `conversationId` twice → `201` then `200` same ticket; `PATCH` it with a one-message transcript → `200`, count 1; repeat → count 1.
4. [ ] Web smoke: Tickets → *Classification* filter present; a test unclassified ticket shows **Classify** on Ticket Details for a department member and not for a Reporting User; Interactions tab shows *Handled by*, *Handoff requested*, start/end.
5. [ ] Run [`uat/export-request-type-mapping.sql`](uat/export-request-type-mapping.sql); hand the export to the Genesys team (section 6).
6. [ ] Confirm TigerGroupWeb patch applied (blocker B1) and `https://tigergroup.ae/api/genesys/oauth/token` issues tokens.
7. [ ] Optional: run [`CancelLegacyAwaitingClassificationHandoffs.sql`](../../CancelLegacyAwaitingClassificationHandoffs.sql) (preview `SELECT` first) to stand down old "Awaiting classification" rows on still-Unclassified tickets (e.g. TG-LCS-20261021-0002).
8. [ ] Only then change the Genesys flows (section 5) in the UAT organisation.

## 9. End-to-end UAT checklist

Tester needs: a CS Agent and a Department Employee of the Leasing/LCS department, a Reporting User, an agent mapped to a Genesys user id, the exported ids, a test chat entry point. Record ticket numbers.

| # | Action | Expected result | Pass |
|---|---|---|---|
| 1 | Start a chat; do not ask for a person. | One ticket (`TG-LCS-…`), Open, **Unclassified**, Normal priority. Ticket Details → Overview: Request Type *Unclassified*, *Handled by AI*. Interactions: Handoff requested **No**; no "Waiting for agent"; Activity has no "asked for a human agent". | ☐ |
| 2 | Re-send the create call (simulate retry). | `200 AlreadyIngested`, same ticket number; still one ticket. | ☐ |
| 3 | Send 3–4 chat messages (customer + bot). | Interactions shows each in order with correct sender (*Customer* / *Virtual agent*) and real times while the chat is still **Active**; count matches. | ☐ |
| 4 | Replay one message payload exactly. | `200`, count unchanged, no duplicate line. | ☐ |
| 5 | Ask "How does the handover process work?" and let the AI classify to a mapped intent. | `PATCH requestType` `200 Classified`; Request Type shown; ticket in the type's department; owner per that department's assignment (or its queue); SLA due times updated, **clock start unchanged**; `OriginatingDepartmentId` unchanged; still no handoff. | ☐ |
| 6 | Unmapped intent. | No request-type call; ticket stays Unclassified. | ☐ |
| 7 | As a department member: Tickets → *Classification: Unclassified*. | Lists unclassified tickets of the member's own departments only; the classified ticket from 5 is absent; another department's ticket is absent. | ☐ |
| 8 | Open an unclassified ticket → **Classify** (category, request type, priority). | Classified in place, leaves the filter, SLA clock not restarted, **no handoff row**. Reporting User sees no Classify control. | ☐ |
| 9 | Change the AI intent before any human reply. | Request type replaced, history in Activity ("changed the request type"); after a human has replied a further change → `409 genesys-request-type-conflict` and no change. | ☐ |
| 10 | Customer asks for a person (4.4). | Handoff **Waiting for agent**, *Initiated by: Customer asked for a human*; Activity "Genesys — customer asked for a human agent"; appears in Pending Interactions; the earlier AI conversation is already visible. Repeat call → same handoff. | ☐ |
| 11 | New chat; bot escalates (`AiEscalated`). | *Initiated by: AI escalated…*. | ☐ |
| 12 | Agent accepts / connects (4.6) and sends the first reply. | *Handled by: Human agent — Layla*; handoff Assigned/InProgress; **First Response recorded at that reply's time**; bot lines never recorded one. | ☐ |
| 13 | End the human conversation (4.8). | Ended with start/end times; ticket status unchanged by the end; handoff still open until the employee completes it in Pending Interactions. | ☐ |
| 14 | New AI-only chat, customer leaves (4.7, no handoff). | Interaction Ended; **no handoff**; ticket status unchanged; no First Response. | ☐ |
| 15 | End a chat with `ended.transcript` repeating live messages. | No duplicates; live rows unchanged. | ☐ |
| 16 | Send an invalid `sender` (e.g. `Robot`) alone, and again together with a `requestType`. | `400 genesys-invalid-transcript`; nothing stored; the ticket's type unchanged. | ☐ |
| 17 | Use an inactive/unknown request type id on PATCH. | `422 genesys-invalid-request-type`; ticket unchanged. | ☐ |
| 18 | Call PATCH with a conversationId of another ticket. | `409 genesys-conversation-ticket-mismatch`. | ☐ |
| 19 | Call with no/invalid token; with a Department Employee token. | `401` / `403`. | ☐ |
| 20 | Stop the Ticketing API briefly mid-chat; resume. | Flow retries (502/504); after recovery all queued messages appear once, in order. | ☐ |
| 21 | Let a website chat sit idle past the inactivity timeout with `customerActive` sent on the last customer message. | Auto-close behaves per the existing inactivity rule (timer counted from the last `customerActive`). | ☐ |
| 22 | Customer sent no `customerActive` (negative test of the flow). | Note result — confirms why every customer line must carry it. | ☐ |

## 10. Remaining blockers before production

| ID | Blocker | Owner | Why it blocks |
|---|---|---|---|
| **B1** | **TigerGroupWeb forwarding exists** (confirmed from its `GenesysController`/`TicketingGenesysService`; routes and pass-through of `requestType`/`transcript` need no change), but it has not been tested against UAT Ticketing. A hardening patch (time budget/cancellation, Bearer-challenge-only refresh, token expiry and single-flight login, `Retry-After`/ProblemDetails relay, log safety) is in [`TigerGroupWeb-Patch/`](TigerGroupWeb-Patch/README.md) — **compiled and tested only in an isolated harness, not yet applied to TigerGroupWeb**. | TigerGroupWeb operator | Apply, build and test it in the real project, and confirm the request-size limit in Program.cs. |
| **B2** | **Architect flows and the two new data actions are unbuilt and untested** (section 5). Existing action 03 still always sends `requestType.name`; 05 has an optional `trigger`. | Genesys team | No live transcript, no id-based classification, handoff trigger may be blank. |
| **B3** | **Human-leg messages have no defined source.** Architect flows cannot see messages after the conversation is transferred to a queue; the `HumanAgent` lines that record First Response and complete the transcript need another mechanism (a Genesys Cloud trigger/process-automation or middleware reading the conversation) — or accept First Response being recorded manually / from `ended.transcript` at the end. Decision and design required. | Genesys + Ticketing owners | Without it First Response SLA for AI→human chats is late or manual. |
| **B4** | **No UAT request-type ids** and no intent list (section 6). | Ticketing UAT lead + business | Without the mapping every AI ticket stays Unclassified. |
| **B5** | No real Genesys event has been observed: stable message ids, `conversationId` consistency, trigger values, `disconnectType` → `endReason`, user-id format for `agentId` and its mapping to Ticketing users. | UAT | The assumptions in this document are untested. |
| **B6** | Agent mapping (Genesys user id → Ticketing user) must exist for every UAT agent. | Ticketing admin | Otherwise no owner / "handled by" for that agent (the id is stored verbatim). |
| **B7** | Operational monitoring: nobody currently watches 4xx/5xx from these calls or the size of the Unclassified list. | Operations | Silent mapping/config failures. |

Defect found and fixed while preparing this package: an id longer than 64 characters was stored shortened but compared in full, so a retry of such a message was stored twice. Ids are now normalised (trimmed, ≤ 64) before comparison; covered by a regression test. Genesys message ids are expected to be well under 64 characters — confirm in UAT (B5).

Known minor items (not blockers): the 422 text for an invalid request type on POST still says "Omit requestType to create the ticket for human classification" (the ticket is created Unclassified, not queued for a human); the Postman collection (`TigerCS-Genesys.postman_collection.json`) and the data-action inventory (`system/09-data-action-inventory.md`) do not yet show the `transcript` field; Ticketing does not push messages to Genesys (one-way integration); a long final transcript can exceed the forwarder's 256 KiB body limit.
