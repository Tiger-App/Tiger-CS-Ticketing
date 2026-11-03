# Genesys ↔ TigerCS — the three integration contracts

Genesys asked for three capabilities. This is the whole externally consumable
surface: **there is nothing else on `api/genesys`.**

| # | Capability | Route |
|---|---|---|
| 1 | Create Ticket, any channel | `POST /api/genesys/tickets` |
| 2 | Customer lookup on call pickup | `GET /api/genesys/customers/lookup?phoneNumber=` |
| 3 | Update Ticket | `PATCH /api/genesys/tickets/{ticketId}` |
| 4 | Agent context (agent identity mapping) | `POST /api/genesys/agent-context` |

All four are behind the `Genesys:Enabled` feature flag and answer `503` while
it is off.

**Collections** adds six routes under `api/genesys/collections/*` (balances,
payments, reminder candidates, recording a reminder, reminder outcomes,
reminder history). They have their own `Collections:Enabled` flag and explicit
financial permissions, and they change nothing about contracts 1–4: a customer
response creates or reuses its ticket through contract 1's ingestion. See
`docs/Collections/Collections-Integration.md`. Contract 4 is the agent-identity addition described under
*Agent identity mapping* below; it changes nothing about contracts 1–3.

---

## Authentication

Genesys Cloud never calls TigerCS directly and never holds a TigerCS
credential.

```text
Genesys Cloud ──OAuth 2.0 client_credentials──▶ TigerGroupWeb  https://tigergroup.ae/api/genesys/…
                                                  │  (TicketingGenesysService, internal)
                                                  ▼
                                               TigerCS API     /api/genesys/…
```

1. Genesys obtains a token:

```http
POST https://tigergroup.ae/api/genesys/oauth/token
Content-Type: application/x-www-form-urlencoded

grant_type=client_credentials&scope=ticketing.genesys&client_id=…&client_secret=…
```

```json
{ "access_token": "eyJ…", "token_type": "Bearer", "expires_in": 3600, "scope": "ticketing.genesys" }
```

2. Every call carries `Authorization: Bearer {access_token}`. The old
   `X-API-Key` alone is refused with `401`.
3. TigerGroupWeb forwards the call to the same route on TigerCS, signed in as
   a TigerCS **CS Agent** service account whose credentials exist only in
   TigerGroupWeb's configuration. Request and response bodies pass through
   unchanged, so every contract below is exactly what Genesys sends and
   receives.

Collections (payment summary, read-only payment history, reminders and outcomes)
has its own contract: [`docs/Collections/Genesys-Collections-API.md`](../Collections/Genesys-Collections-API.md).

The Genesys-side setup (integration, Custom Auth action, data actions,
Architect flows) is in `docs/Genesys/Genesys-Cloud-Configuration.md`.

---

## 1. Create Ticket

`POST /api/genesys/tickets`

One endpoint for **every** channel — `Phone`, `LiveChat`, `WhatsApp`,
`Instagram`, `Facebook`, `SocialMedia`. `WebsiteChat`, `WebChat` and
`WebMessaging` are accepted as the same Live Chat channel; `FacebookMessenger`
and `Messenger` as Facebook. Send Instagram and Facebook as their own values:
`SocialMedia` files the ticket under the generic Social Media channel (`SOCIAL_DM`)
and it will not appear under Instagram/Facebook. There is no per-channel
endpoint and no per-channel ticket-creation code.

**Who owns a human-handled ticket (every channel).** A ticket that needs a person
goes to the Call Center. If Genesys reports an agent as *connected*
(`routing.agentId` on an update; on **Phone** only, also the `agentId` of the
pickup inquiry) the ticket is given directly to that mapped Call Center agent.
On digital channels a top-level `agentId` is only an offered/assigned/queued
agent — it is never treated as acceptance. With no accepted agent, Round Robin
picks one eligible Call Center agent; a later *connected* report replaces that
automatic pick, but never a person's ownership or a completed transfer.

**Idempotent on `conversationId`.** The first accepted event creates the
ticket; every retry returns *that same ticket* with `outcome:
"AlreadyIngested"` and creates nothing. A unique database index makes this hold
under concurrent delivery, not only under sequential retries.

**This endpoint means exactly one thing:** create or reuse the ticket for this
conversation. **There is no `event` field.** It is not an event receiver for
call progress:

```
Inbound call flow starts → GET  /api/genesys/customers/lookup?phoneNumber={Call.Ani}
                         → POST /api/genesys/tickets   (conversationId = Call.ConversationId)
                         → Transfer to ACD
Everything after that    → PATCH /api/genesys/tickets/{ticketId}   (same ticket, always)
```

Digital channels (Live Chat / Web Messaging, chatbot, WhatsApp, social) post
here when the conversation starts.

**`customerPhone` accepts `Call.Ani` as-is.** `tel:+971…`, `sip:+971…@host`,
`+971…`, `971…` and `00971…` are all normalized to `+971…` before the
number is searched or stored. A withheld caller id (`tel:anonymous`) is no
number. **An optional field sent as `""` is treated as absent**, because a
Genesys data action cannot leave a field out of its template.

**Only `conversationId` and `channel` are required.** Everything else is
optional context, because Genesys' per-channel guarantees are not confirmed —
absent values are stored as null, never guessed.

**A customer-lookup failure never blocks creation.** Nor does a missing phone
number (withheld caller id, a social DM).

### Request

```json
{
  "conversationId": "8f2c1e40-3d2a-4b1c-9e77-1a2b3c4d5e6f",
  "channel": "LiveChat",
  "customerPhone": "tel:+971501234567",
  "customerName": "Ahmed Ali",
  "customerEmail": "ahmed@example.com",
  "departmentId": 3,
  "departmentCode": "CS",
  "towerName": "Tiger Tower A",
  "unitNumber": "1204",
  "queueId": "genesys-queue-cs",
  "queueName": "Customer Service",
  "agentId": "ga-7",
  "agentName": "Layla",
  "calledNumber": "+97140000000",
  "direction": "Inbound",
  "startedAtUtc": "2026-09-10T09:31:00Z",
  "subject": "Asking about handover"
}
```

Department resolution: `departmentId`/`departmentCode` is the customer's own
website-chat choice (Leasing / Customer Service / Maintenance) where there is
one; otherwise the configured `queueId` → department mapping. An unmapped
queue is refused with `422` naming the queue — never routed to a guess.

### Response — `201 Created`

```json
{
  "outcome": "TicketCreated",
  "conversationId": "8f2c1e40-3d2a-4b1c-9e77-1a2b3c4d5e6f",
  "ticketId": 10432,
  "ticketNumber": "TG-CS-20260910-0007"
}
```

### Response — `200 OK` (the retry answer)

```json
{
  "outcome": "AlreadyIngested",
  "conversationId": "8f2c1e40-3d2a-4b1c-9e77-1a2b3c4d5e6f",
  "ticketId": 10432,
  "ticketNumber": "TG-CS-20260910-0007"
}
```

Same body, same ticket, nothing created. `outcome` is how the caller tells the
two apart.

### The ticket that is created

It is **Unclassified** (`categoryId` null) and starts on the configured default
priority ("Normal" = Medium), with its SLA period opened at ticket creation.
When the bot supplies a valid `requestType` (`{requestTypeId}` or `{name}`) on
this call, or later on `PATCH /api/genesys/tickets/{ticketId}`, TigerCS validates
it, routes the ticket to the request type's department (the originating
department never changes), runs the configured automatic assignment (no
eligible employee = department queue) and applies the request type's SLA policy
without restarting the clock. A missing request type creates the ticket on Normal priority, awaiting
classification in the human follow-up queue. An explicitly invalid one
(unknown, inactive, ambiguous name, no published workflow) returns `422` and
writes nothing. A TigerCS agent can
still classify through `POST /api/tickets/{ticketId}/classification`. See
[Request-Type-Routing-And-Default-Priority.md](../Genesys/Request-Type-Routing-And-Default-Priority.md).

### Other responses

| Code | When |
|---|---|
| `400` | Unrecognized `channel`, or blank `conversationId` |
| `422` | No department could be resolved, or ticket creation was refused |
| `503` | `Genesys:Enabled` is false |

---

## 2. Customer lookup on call pickup

`GET /api/genesys/customers/lookup?phoneNumber=%2B971501234567`

Called when an agent **answers** — never while ringing.

Reuses the same CRM Buyer Lookup and PACT/Tasleeh services the New Ticket
wizard uses. **No second CRM integration exists**, and Genesys never reaches
those systems directly. Read-only: nothing is created or persisted.

**Any Genesys ANI format is accepted:** `tel:+971…`, `+971…` and `971…`
are searched as `+971…` (CRM receives it that way, and PACT without the
`+`). Recent tickets match the number however an agent typed it on earlier
tickets (`+971 50 123 4567` is the same caller).

### Response — `200 OK`, customer found

```json
{
  "phoneNumber": "+971501234567",
  "found": true,
  "crmStatus": "Found",
  "crmBuyers": [
    {
      "customer": { "customerId": 4001, "fullNameEnglish": "Ahmed Al-Farsi", "fullNameArabic": null,
                    "mobileNumber": "+971501234567", "email": "ahmed@example.com" },
      "units": [
        { "leadId": 9100, "leadStatus": 8, "leadStatusName": "Sold", "unitId": 9001, "unitNumber": "1204",
          "unitStatus": 3, "unitType": 2, "floorNumber": 12, "projectId": 79, "projectName": "Tiger Tower A",
          "projectArabicName": null, "customerType": 1, "customerTypeName": "Buyer" }
      ]
    }
  ],
  "externalSources": [
    { "source": "Pact", "status": "NotFound", "customers": [] },
    { "source": "Tasleeh", "status": "NotFound", "customers": [] }
  ],
  "tickets": [
    { "ticketId": 10310, "ticketNumber": "TG-CS-20260901-0044", "ticketStatus": "InProgress", "isOpen": true,
      "requestSummary": "NOC for resale", "currentDepartmentId": 3, "createdAtUtc": "2026-09-01T08:12:00Z" }
  ],
  "openTicketCount": 1,
  "screenPop": {
    "customerName": "Ahmed Al-Farsi",
    "customerEmail": "ahmed@example.com",
    "verificationSource": "Crm",
    "externalCustomerId": "4001",
    "matchedCustomerCount": 1,
    "units": ["Tiger Tower A - 1204"],
    "unitsText": "Tiger Tower A - 1204",
    "selectableUnits": [
      { "label": "Tiger Tower A - 1204", "unitCode": "TP125-1204", "paymentLookupReady": true, "sources": ["Crm"],
        "customers": [ { "source": "Crm", "externalCustomerId": "4001" } ],
        "sourcesText": "Crm", "customersText": "Crm:4001" }
    ],
    "recentTicketNumbers": ["TG-CS-20260901-0044"],
    "openTicketNumbers": ["TG-CS-20260901-0044"],
    "recentTicketsText": "TG-CS-20260901-0044 (InProgress)"
  }
}
```

**Units.** Expired PACT contracts (end date before today in Dubai; ending today or no end date stay) are omitted from `externalSources[].customers[].units`, `screenPop.units`, `unitsText` and `selectableUnits`; CRM units are never dropped for a PACT expiry. CRM units (`crmBuyers[].units[]`) gain an additive `unitCode` (`projectCode` + `-` + `unitNumber`, e.g. `TP130-902-B`); it is null when `projectCode` is missing or not a tower code, and is never derived from `projectId`. `selectableUnits` is the de-duplicated list: a unit is merged only when records resolve to the same unit code (CRM label kept) — never by unit number, similar tower names or an identical display label. Records without a resolved code are never merged: each stays its own entry with its own `sources` and `customers`, so `units` may repeat a label when two sources list an unresolved unit. `customers[]` on each selectable unit keeps every matched customer record (source + id) the unit was found under, so two people sharing a phone number are never merged. `sourcesText` / `customersText` are the same two lists flattened to strings (`Crm,Pact` / `Crm:4001;Pact:T1`); the Genesys Data Action maps those as parallel primitive arrays (`selectableUnitLabels`, `selectableUnitCodes`, `selectableUnitPaymentReady`, `selectableUnitSources`, `selectableUnitCustomers`) because Architect outputs cannot hold nested arrays. `paymentLookupReady` only means the unit code is resolved (false, with an empty `unitCode`, when it is not); it does not mean ownership or the caller's identity is verified. `matchedCustomerCount` and the customer records are unaffected, and a shared phone number is not verified identity.

`tickets` are the tickets that already arrived from this number, **open ones
first**, so the agent knows the caller has a live case before they speak.

`screenPop` is the same answer flattened for a Genesys data action and agent
script: plain strings (never null, empty when there is nothing) and string
lists. It is a projection of the arrays above, never a second lookup. Its
order is CRM, then PACT, then Tasleeh. When `matchedCustomerCount` is more
than 1, the agent must confirm who is calling; nothing is auto-selected.

### Response — `200 OK`, nobody found

```json
{
  "phoneNumber": "+971509999999",
  "found": false,
  "crmStatus": "NotFound",
  "crmBuyers": [],
  "externalSources": [
    { "source": "Pact", "status": "NotFound", "customers": [] },
    { "source": "Tasleeh", "status": "NotFound", "customers": [] }
  ],
  "tickets": [],
  "openTicketCount": 0,
  "screenPop": { "customerName": "", "customerEmail": "", "verificationSource": "", "externalCustomerId": "",
                 "matchedCustomerCount": 0, "units": [], "unitsText": "", "recentTicketNumbers": [],
                 "openTicketNumbers": [], "recentTicketsText": "" }
}
```

A withheld caller id (`tel:anonymous`) answers the same shape with
`"phoneNumber": ""` and `"crmStatus": "NotSearched"`. No source is called.

**`found: false` is a `200`, not a `404`** — and it never stops the Create
Ticket call that follows. A source being down reports `"crmStatus": "Failed"`
(or the source's own `"Failed"`) alongside whatever the others returned, and is
equally not a blocker.

| Code | When |
|---|---|
| `400` | `phoneNumber` missing or blank |
| `503` | `Genesys:Enabled` is false |

---

## 3. Update Ticket

`PATCH /api/genesys/tickets/{ticketId}`

Everything Genesys owns about a ticket after creating it, in one call. Every
part of the body is optional and independently idempotent — send only what
changed, resend freely.

`conversationId` is **required** and must resolve to an interaction on the
ticket in the route. That cross-check is what stops a transcript being applied
to the wrong ticket.

### What Genesys can update

| Field | Meaning |
|---|---|
| `agentId` / `agentName` | Who is handling the conversation. Apply-if-absent — a later, different agent never overwrites the one who took it |
| `ended.endedAtUtc` / `ended.endReason` | The conversation finished, for any reason |
| `ended.transcript[]` | The conversation, in order |
| `handoff.*` | Human-agent state, on any channel |
| `routing.queueId` / `routing.queueName` / `routing.agentId` / `routing.agentName` | Where the conversation is **now**: a queue change, an agent connecting, a transfer. Overwrites the interaction's current queue/agent; every change is audited (`GenesysRoutingChanged`) |
| `startedAtUtc` | Interaction start, if the Create call did not carry it. Never moves once known |
| `customerConfirmation.*` | The customer explicitly confirmed the issue is resolved. **Record-only** — see below |

### What Genesys **cannot** update

There is no field for `categoryId`, `requestTypeId`, `priorityId`,
`ticketStatus`, owner, department, resolution or closure.
`customerConfirmation` records the customer's word and never resolves or
closes the ticket. Those move only
through their own TigerCS operations, with their own authorization, workflow
and SLA consequences. **A field absent from this contract is a field Genesys
cannot reach.**

### Request — conversation ended, with transcript

```json
{
  "conversationId": "8f2c1e40-3d2a-4b1c-9e77-1a2b3c4d5e6f",
  "agentId": "ga-7",
  "agentName": "Layla",
  "ended": {
    "endedAtUtc": "2026-09-10T09:47:00Z",
    "endReason": "CustomerDisconnect",
    "transcript": [
      { "sender": "Customer",     "sentAtUtc": "2026-09-10T09:33:00Z", "body": "I want to sell my apartment." },
      { "sender": "VirtualAgent", "sentAtUtc": "2026-09-10T09:33:20Z", "body": "Are you asking about an NOC for resale?" },
      { "sender": "Customer",     "sentAtUtc": "2026-09-10T09:33:45Z", "body": "Yes." }
    ]
  }
}
```

`sender` is one of **`Customer`**, **`VirtualAgent`**, **`HumanAgent`**,
**`System`**. Anything else fails the whole update — the sender is never
silently attributed, and nothing is stored, so the caller simply resends.

Each message preserves, where supplied: `messageId`, sender type, `sentAtUtc`,
`body` (verbatim), plus `senderName` / `senderId`. Ticket Details renders the
whole conversation in delivered order.

**Supplying `messageId` is strongly preferred.** It is the deduplication key,
and the only one that survives a retry whose timestamps were regenerated.
Without it the fallback is sender + timestamp + body.

### Request — queue changed / agent connected / transferred

```json
{
  "conversationId": "8f2c1e40-3d2a-4b1c-9e77-1a2b3c4d5e6f",
  "routing": { "queueId": "b7c0…", "queueName": "Customer Service", "agentId": "6f1d2c3b-…", "agentName": "Layla" }
}
```

Send only what changed; an empty or missing value leaves that part as it
was. The same ticket, always. The ticket's department, owner and status are
not moved by routing. The **first** mapped agent stays recorded as the
interaction's handler (`HandledByUserId`).

**Agent connected to waiting human work.** When `routing.agentId` arrives
while the conversation's human work is `WaitingForAgent` (after an AI
handoff), that work is marked **Assigned** to this Genesys agent, exactly as
`handoff.assignedAgentId` would. Work a TigerCS agent has already accepted
is never reassigned by a routing event. Accepting in TigerCS still assigns
the ticket owner and moves Open → InProgress. It still does **not** record a
First Human Response, which only the first actual HumanAgent message does.

### Request — a human agent is needed

```json
{
  "conversationId": "8f2c1e40-3d2a-4b1c-9e77-1a2b3c4d5e6f",
  "handoff": {
    "required": true,
    "agentAvailable": false,
    "mode": "ContinueChat",
    "reason": "Customer asked about an NOC for resale",
    "workItemId": "genesys-workitem-8871"
  }
}
```

`mode` is one of `Callback`, `ContinueChat`, `ReplyInChannel`,
`HumanTakeover`. **Omit it unless Genesys actually states it** — TigerCS never
derives it from the channel, and a callback is one mode among several rather
than the concept. `workItemId` is optional and used as a stronger idempotency
key when supplied; omit it if Genesys has none.

### Request — an agent took the pending work

```json
{
  "conversationId": "8f2c1e40-3d2a-4b1c-9e77-1a2b3c4d5e6f",
  "agentName": "Noor",
  "handoff": { "assignedAgentId": "ga-9" }
}
```

### Request — the customer confirmed the issue is resolved

```json
{
  "conversationId": "8f2c1e40-3d2a-4b1c-9e77-1a2b3c4d5e6f",
  "customerConfirmation": {
    "confirmedResolved": true,
    "confirmedAtUtc": "2026-09-30T09:30:00Z",
    "note": "Customer said the NOC arrived"
  }
}
```

**Record-only.** Send it when the customer **explicitly** says, during the
interaction, that the issue is resolved. TigerCS records it on the same
ticket as a `CustomerConfirmedResolved` workflow event (shown in the Ticket
Details activity feed) and a `GenesysCustomerConfirmedResolved` audit entry.
It does **not** resolve or close the ticket. Resolve stays with the ticket
owner / Department Head, and Close with Customer Service, under their existing
authorization. The response's unchanged `ticketStatus` shows this.

- `confirmedResolved` must be `true`. Anything else is a `400` and nothing in
  the update is written.
- `confirmedAtUtc` defaults to now. Send it: a resend with the same value is
  recognized and stores nothing twice.
- `note` is optional, display only.
- No schema change: the event uses the existing `TicketWorkflowEvents` table.

### Response — `200 OK`

```json
{
  "outcome": "Applied",
  "conversationId": "8f2c1e40-3d2a-4b1c-9e77-1a2b3c4d5e6f",
  "ticketId": 10432,
  "ticketNumber": "TG-CS-20260910-0007",
  "ticketStatus": "Open",
  "conversationEnded": true,
  "transcriptMessageCount": 3,
  "handoffStatus": "WaitingForAgent",
  "ticketAgentHandoffId": 55
}
```

`ticketStatus` is echoed on **every** response deliberately: it is the standing
proof that **ending a conversation did not close the ticket**. A customer who
asked for an NOC has an open ticket for days after the chat window closed.

`handoffStatus` is reported on every update too, not only on one that touched
it — so an end event shows that the pending human work is *still outstanding*.
That is exactly the customer-disconnected case: the live session ended, the
business case did not.

### Idempotency — no duplicates, and no truncation

- Resending an already-stored transcript stores nothing twice —
  `transcriptMessageCount` stays put.
- A redelivered end does **not** move the recorded `endedAtUtc`/`endReason`;
  those are write-once.
- But its transcript **is** still read, and any message not already stored is
  appended. A conversation stored from a truncated first delivery is completed
  by the retry rather than staying permanently short — "the complete
  transcript is persisted" is the requirement, so a redelivery is not simply
  discarded.
- A redelivered handoff returns the same `ticketAgentHandoffId`; there is never
  a second work item for a conversation whose work is still outstanding.
- Re-reporting the same assigned agent changes nothing.

### Other responses

| Code | When |
|---|---|
| `400` | Blank `conversationId`, a transcript message with an unrecognized sender or empty body, an unrecognized `handoff.mode` or `handoff.trigger`, `handoff.required: false` with no `handoff.reason`, or `customerConfirmation` without `confirmedResolved: true` |
| `404` | No such ticket, or no interaction exists for this conversation |
| `409` | The conversation belongs to a different ticket than the one in the route |
| `422` | `handoff.assignedAgentId` supplied but no outstanding human work exists |
| `503` | `Genesys:Enabled` is false |

A malformed transcript is refused **before anything is written**, so a bad
update never leaves a half-stored conversation.

---

## 4. Agent context — Genesys agent → Ticketing user

`POST /api/genesys/agent-context`

When an agent opens or works with Ticketing from Genesys, Ticketing must know
**exactly which authenticated Ticketing user that agent is**. The mapping is:

```text
Genesys User ID  →  AspNetUsers.GenesysUserId  →  Ticketing user  →  user id / roles / departments
```

The immutable **Genesys User ID** is the only key. The agent's display name
and email are never used to identify an agent: `agentEmail` is informational
and is recorded on the audit trail only.

### Request

```json
{
  "genesysUserId": "6f1d2c3b-0a9e-4b8d-9c7f-1e2d3c4b5a69",
  "agentEmail": "agent@tigerproperties.ae",
  "conversationId": "e2c4a1b0-7d3f-4c9e-9a1b-2f3e4d5c6b7a"
}
```

| Field | Required | Meaning |
|---|---|---|
| `genesysUserId` | **yes** | The agent's Genesys User ID. |
| `agentEmail` | no | Informational only. Never trusted as identity. |
| `conversationId` | no | The conversation the agent is working. When present, its interaction records the resolved user as the handler. |

There is deliberately **no Ticketing user id** in this body. Which user
handled the interaction is resolved on the server from `genesysUserId` alone;
a `handledByUserId` (or any similar property) sent by a client is ignored.

### Response — `200 OK`

```json
{
  "outcome": "Resolved",
  "genesysUserId": "6f1d2c3b-0a9e-4b8d-9c7f-1e2d3c4b5a69",
  "userId": "0a1b2c3d-…",
  "userName": "layla.agent",
  "displayName": "Layla",
  "roles": ["CS Agent"],
  "departmentIds": [3],
  "conversationId": "e2c4a1b0-…",
  "ticketId": 1042,
  "ticketNumber": "TG-CS-260911-0007",
  "ticketInteractionId": 2210,
  "handledByUserId": "0a1b2c3d-…"
}
```

`ticketId`, `ticketNumber`, `ticketInteractionId` and `handledByUserId` are
null when no `conversationId` was supplied. `handledByUserId` is the user
recorded on the interaction — the **first** agent resolved on it; a later,
different agent (a transfer) does not overwrite it.

### What it records — and what it does not

The resolved user is stored on the conversation's `TicketInteractions` row:

```text
TicketInteractions.GenesysAgentUserId = the incoming Genesys User ID
TicketInteractions.HandledByUserId    = the resolved Ticketing user (FK → AspNetUsers.Id)
```

This is *who handled the interaction*. It is **not** the ticket's assignee:
the ticket's owner, department, queue, status and workflow are untouched, and
move only through their own TigerCS operations exactly as before.

### Other responses

| Code | Body | When |
|---|---|---|
| `400` | standard validation problem, `errors.GenesysUserId` | `genesysUserId` missing or blank |
| `403` | ProblemDetails with `"code": "GENESYS_AGENT_NOT_MAPPED"` | No Ticketing user carries this Genesys User ID. **No user is created.** |
| `403` | ProblemDetails with `"code": "GENESYS_AGENT_INACTIVE"` | The mapped Ticketing user is deactivated |
| `404` | ProblemDetails | `conversationId` supplied but never ingested |
| `503` | ProblemDetails | `Genesys:Enabled` is false |

The `403` body is the API's standard ProblemDetails shape plus the stable
`code` member:

```json
{
  "type": "https://tigercs.internal/problems/genesys-agent-not-mapped",
  "title": "Genesys agent is not mapped to a Ticketing user",
  "status": 403,
  "detail": "Genesys agent is not mapped to a Ticketing user.",
  "code": "GENESYS_AGENT_NOT_MAPPED"
}
```

### The same mapping on contracts 1 and 3

`agentId` on Create Ticket and Update Ticket **is the Genesys User ID** (it
always was the handling agent's identifier; `agentName` is display-only).
When it is supplied and maps to an active Ticketing user, that user is
recorded as the interaction's handler exactly as above, apply-if-absent.
Those two contracts are integration events — a queue or system may send them
before any agent exists — so an absent or unmapped `agentId` there is **not**
refused: the ticket is still created/updated, the verbatim `agentId` is kept,
ownership stays null, and the ingestion audit entry names the outcome
(`AgentMapping=NotMapped(...)`). Only contract 4 — an agent acting — refuses
an unmapped agent.

### Setting up the mapping (UAT)

There is no administration screen for this yet. For UAT the mapping is set
directly on the Ticketing user. Each Genesys User ID may be mapped to at most
one Ticketing user (`UX_AspNetUsers_GenesysUserId`, filtered unique); users
that are not Genesys agents simply keep `NULL`.

```sql
-- @TicketingUserId : AspNetUsers.Id of the existing Ticketing user (uniqueidentifier)
-- @GenesysUserId   : the agent's immutable Genesys User ID
-- @GenesysEmail    : informational; may be NULL
UPDATE AspNetUsers
SET
    GenesysUserId = @GenesysUserId,
    GenesysEmail  = @GenesysEmail
WHERE Id = @TicketingUserId;
```

To remove a mapping, set both columns back to `NULL`. Never create a user
this way — the Ticketing account must already exist (Administration →
Users), with its role and department assignments, before it is mapped.

---

## The digital / chatbot conversation flow

Mandatory for chatbot, website chat, WhatsApp and social conversations.

```
Chat starts                → POST  /api/genesys/tickets          → ticket created
Conversation runs          → Customer / VirtualAgent / HumanAgent / System messages
Chat closes or disconnects → PATCH /api/genesys/tickets/{id}
                             ├─ every transcript message stored
                             ├─ endedAtUtc + endReason recorded
                             ├─ the interaction marked Ended
                             └─ the TICKET stays open
```

The whole conversation is then visible in Ticket Details → Conversation
History, in order, with each line attributed to the customer, the virtual
agent, the human agent or the platform. A human agent taking over later reads
exactly what the bot and the customer said.

---

## Remaining Genesys-side work (configuration only)

No further TigerCS development is required for the approved flows. What
remains is configuration, described step by step in
`docs/Genesys/Genesys-Cloud-Configuration.md`:

1. **Credentials.** Issue the OAuth client secret and signing key on
   TigerGroupWeb; configure the Web Services Data Actions integration
   (User Defined (OAuth)).
2. **Data actions.** Import and publish the six provided definitions.
3. **Architect.** Voice inbound call flow, inbound message flow, agent
   script, and the `acd.start` / `customer.end` triggers with their workflow.
4. **Queue ids.** Map every Genesys queue id to a TigerCS department.
5. **Agent identity.** Map each agent's Genesys User ID to their Ticketing user.
6. **Transcripts (optional).** Send `ended.transcript[]` when chat
   transcripts are wanted in Ticket Details. It is supported, but not
   needed for ticket lifecycle.
