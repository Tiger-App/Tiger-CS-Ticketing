# Queueing System integration - receiving API (phase 1)

TigerCS receives the lifecycle of QR-code queue tickets from the Queueing System and mirrors it onto **one TigerCS ticket per queue ticket**.
**The Queueing System is the source of truth** for the lifecycle of these tickets (who serves them, when they are returned, recalled, closed or
cancelled); TigerCS records it and keeps its own history, SLA and reporting consistent with it.

This phase is the **TigerCS receiving API only**. The Queueing System is not modified here.

- Endpoint: `POST /api/integrations/queueing/events`
- Postman: [`TigerCS-Queueing.postman_collection.json`](TigerCS-Queueing.postman_collection.json)
- Database script: [`/AddQueueingIntegration.sql`](../../AddQueueingIntegration.sql) (idempotent; EF migration `20261117082231_AddQueueingIntegration`)

## 1. How it fits the existing system (nothing parallel was built)

| Need | Existing TigerCS service reused |
|---|---|
| Create the ticket | `TicketCreationAppService` (ticket number, status history, SLA period, audit, originating interaction) |
| Customer lookup by mobile | `CustomerSearchAppService.SearchByPhoneAsync` (the CRM Buyer / PACT / Tasleeh lookup the New Ticket wizard and Genesys use) |
| Channel | The existing **Walk in / Kiosk** channel (`WALK_IN_KIOSK`, id 5) - configurable by code |
| Department / request type / category / workflow | Existing `Departments`, `RequestTypes`, paired `Categories`, published workflow versions - **mapped**, never created |
| Priority / SLA | The existing default priority by name (`Normal`) and the existing SLA policies |
| Assign / reassign | `TicketAssignmentAppService.AssignAsync` (membership checks, `TicketAssignments` history, audit) |
| Start work | `TicketLifecycleAppService.ChangeStatusAsync` (Open -> InProgress), performed as the receiving employee |
| Close / cancel | `TicketLifecycleAppService` system closure (Resolved -> Closed, `TicketResolutions`, SLA settlement), outcome `Resolved` / `Cancelled` |
| Comment | `TicketNotes` |
| Duplicate-notification control | The existing Outbox + customer-email handlers (they skip, with a recorded reason) |

New code: the receiving endpoint, an event inbox, a small per-ticket state row, three mapping tables, the integration's own API-key authentication,
and a few narrow additions to existing services (listed in section 10).

## 2. Event -> TigerCS mapping (no duplicate statuses)

| Event | Required action | What TigerCS does |
|---|---|---|
| `TicketCreated` | Create an unassigned ticket | Classified, **unassigned**, `Open` ticket on the Walk in / Kiosk channel in the department mapped from `CompanyId`, with the request type mapped from `TicketTypeId`. Source = `QueueingSystem`; queue ticket id and queue number are kept on the ticket. SLA starts at `OccurredAtUtc`. No Call Center routing, no assignment rule, no Round Robin, no creator self-assignment. |
| `TicketAssigned` | Assign to the receiving employee | The employee mapped from `AssignedUserId` becomes the owner (existing assignment rules and history). An `Open` ticket moves to `InProgress` through the existing transition, performed as that employee. **Not** a first response. |
| `TicketReassigned` | Update employee assignment | Same, to the new employee. The previous assignment row is superseded (history kept). |
| `TicketReturned` | Return to waiting queue | Owner released; ticket stays in its department's queue; current assignment row superseded. **Status is unchanged** - `InProgress -> Open` is not a TigerCS transition and none was invented. History entry written. |
| `TicketCommentAdded` | Add comment | A ticket note, authored by the employee who wrote it when mapped (otherwise by the integration account, with the queue user named in the text). |
| `TicketRecalled` | Record recall in history | An audit/history entry (counter, performer, reason). Nothing else changes. |
| `TicketClosed` | Close the ticket | System closure, resolution outcome **Resolved**, owner cleared, SLA resolution clock settled. |
| `TicketCancelled` | Cancel the ticket | The same closure with resolution outcome **Cancelled** (TigerCS has no "Cancelled" status; Cancelled is an existing resolution outcome). |

Queue state is kept separately (`QueueingTicketLinks.State`: Waiting / Assigned / Closed / Cancelled) and TigerCS status is derived from it, never the reverse.

## 3. Authentication and permissions

Independent of staff sign-in:

- Header `X-Queueing-Api-Key: <key>` (name configurable).
- Only the **SHA-256 of each key** is configured (`QueueingIntegration:Authentication:ApiKeys`), compared in constant time against every configured key. Several keys may be listed at once, so a key can be rotated without downtime.
- The key authenticates a dedicated scheme (`QueueingApiKey`) that **only** `POST /api/integrations/queueing/events` accepts: the key cannot call any staff endpoint, and a staff JWT (including a System Administrator's) cannot call this one. A test pins that this is the only API-key endpoint.
- Wrong or missing key -> `401` with `WWW-Authenticate: ApiKey`. Nothing is stored.
- The integration acts internally as a configured service account (`QueueingIntegration:ServiceAccountEmployeeId`, also listed under `Authorization:ServiceIdentity:EmployeeIds`). **That account is never the assignee of a ticket**; if a queue user maps to it the event is refused.

Generate a key and its hash:

```bash
KEY=$(openssl rand -base64 36 | tr -d '\n'); echo "key:  $KEY"; printf '%s' "$KEY" | sha256sum | cut -d' ' -f1
```

## 4. API contract

`POST /api/integrations/queueing/events` - `Content-Type: application/json`. Property names are matched case-insensitively (the Queueing System's PascalCase is accepted; responses are camelCase; members with no value are omitted).

### 4.1 Request fields

| Field | Type | Notes |
|---|---|---|
| `EventId` | string (<=100) | **Required.** Idempotency key. Same `EventId` + same body = applied once. Same `EventId` + different body = `409`. |
| `EventType` | string | **Required.** One of the eight event names (case-insensitive). |
| `ExternalTicketId` | string (<=100) | **Required.** The queue ticket's id. One TigerCS ticket per value (unique index). |
| `EventSequence` | integer >= 1 | **Required.** Position in the queue ticket's own history. State-changing events apply only if newer than what was applied. |
| `OccurredAtUtc` | ISO-8601 UTC | **Required.** Not more than `MaxClockSkewSeconds` (300) in the future. Preserved on every record written for the event. |
| `QueueNumber` | string (<=100) | Required on `TicketCreated`. Kept on the ticket. |
| `CompanyId` | string | Required on `TicketCreated`. Mapped to a department. |
| `TicketTypeId` | string | Required on `TicketCreated`. Mapped to a request type. |
| `CustomerName`, `CustomerPhone`, `CustomerEmail` | string | Optional. Phone drives the CRM lookup. |
| `AssignedUserId` | string | Required on `TicketAssigned` / `TicketReassigned`. The receiving employee (queue id). |
| `PerformedByUserId` | string | Optional. Who acted (queue id); recorded in the history; author of a comment. |
| `CounterId` | string | Optional. Recorded in the history and on the queue state. |
| `Comment` | string (<=4000) | Required on `TicketCommentAdded`. |
| `Reason` | string (<=1000) | Optional. History note (reassign, return, recall, close, cancel). |

Required fields per event type:

| Event | Beyond the envelope (`EventId`, `EventType`, `ExternalTicketId`, `EventSequence`, `OccurredAtUtc`) |
|---|---|
| TicketCreated | `QueueNumber`, `CompanyId`, `TicketTypeId` |
| TicketAssigned / TicketReassigned | `AssignedUserId` |
| TicketCommentAdded | `Comment` |
| TicketReturned / TicketRecalled / TicketClosed / TicketCancelled | none |

### 4.2 Response

```json
{ "eventId": "...", "externalTicketId": "...", "status": "Processed", "outcome": "APPLIED",
  "ticketId": 1042, "ticketNumber": "TG-CS-20261117-0007", "ticketStatus": "InProgress", "retryable": false }
```

| HTTP | `status` | Meaning |
|---|---|---|
| `201` | `Processed` | The TigerCS ticket was created (`outcome: TICKET_CREATED`). |
| `200` | `Processed` | Applied, or already in that state. `outcome`: `APPLIED`, `TICKET_ALREADY_EXISTS`, `ALREADY_IN_STATE`. |
| `200` | `Duplicate` | This `EventId` was already received; the original outcome is returned, nothing is applied again. |
| `200` | `Superseded` | Older than the applied state (`STALE_EVENT`); recorded in the audit trail, ticket unchanged. |
| `200` | `Rejected` | Valid but not applicable, e.g. a state change after closure (`TICKET_ALREADY_CLOSED`). |
| `202` | `Pending` | Stored, **not applied yet**; TigerCS retries it itself. `outcome` says why (below). `retryable: true`. |
| `400` | `Invalid` | Validation failed; `errors` lists the fields. Nothing stored. |
| `401` | - | Missing / invalid API key. |
| `409` | `Conflict` | Same `EventId`, different content (`EVENT_ID_REUSED`). |
| `503` | `Disabled` / `Busy` | Integration switched off, or another request is changing the same ticket (`Retry-After: 2`; resend the same event). |

`202 Pending` outcomes: `AWAITING_TICKET_CREATED`, `COMPANY_NOT_MAPPED`, `TICKET_TYPE_NOT_MAPPED`, `REQUEST_TYPE_UNUSABLE`, `USER_NOT_MAPPED`, `USER_INACTIVE`,
`USER_NOT_IN_DEPARTMENT`, `CHANNEL_NOT_CONFIGURED`, `PRIORITY_NOT_CONFIGURED`, `SERVICE_ACCOUNT_NOT_CONFIGURED`, `CONCURRENT_UPDATE`, `PROCESSING_FAILED`, `TICKET_NOT_ELIGIBLE`.

## 5. Sample requests and responses (captured from the running API)

### TicketCreated

`201 Created`

```json
// request
{
  "EventId": "0f6c1d52-3b7a-4a55-9c53-0d6a1f1b0001",
  "EventType": "TicketCreated",
  "ExternalTicketId": "QT-20261117-0001",
  "EventSequence": 1,
  "QueueNumber": "A-017",
  "CompanyId": "company-a",
  "TicketTypeId": "type-complaint",
  "CustomerName": "Aisha Khan",
  "CustomerPhone": "+971500000123",
  "CustomerEmail": "aisha@example.test",
  "OccurredAtUtc": "2026-11-17T08:30:00Z"
}
```

```json
// response 201
{
  "eventId": "0f6c1d52-3b7a-4a55-9c53-0d6a1f1b0001",
  "externalTicketId": "QT-20261117-0001",
  "status": "Processed",
  "outcome": "TICKET_CREATED",
  "ticketId": 1,
  "ticketNumber": "TG-CS-20261117-0001",
  "ticketStatus": "Open",
  "retryable": false
}
```

### TicketAssigned

`200 OK`

```json
// request
{
  "EventId": "0f6c1d52-3b7a-4a55-9c53-0d6a1f1b0002",
  "EventType": "TicketAssigned",
  "ExternalTicketId": "QT-20261117-0001",
  "EventSequence": 2,
  "AssignedUserId": "queue-user-a",
  "PerformedByUserId": "queue-user-a",
  "CounterId": "counter-3",
  "OccurredAtUtc": "2026-11-17T08:34:00Z"
}
```

```json
// response 200
{
  "eventId": "0f6c1d52-3b7a-4a55-9c53-0d6a1f1b0002",
  "externalTicketId": "QT-20261117-0001",
  "status": "Processed",
  "outcome": "APPLIED",
  "ticketId": 1,
  "ticketNumber": "TG-CS-20261117-0001",
  "ticketStatus": "InProgress",
  "retryable": false
}
```

### TicketCommentAdded

`200 OK`

```json
// request
{
  "EventId": "0f6c1d52-3b7a-4a55-9c53-0d6a1f1b0003",
  "EventType": "TicketCommentAdded",
  "ExternalTicketId": "QT-20261117-0001",
  "EventSequence": 3,
  "PerformedByUserId": "queue-user-a",
  "CounterId": "counter-3",
  "Comment": "Customer needs the title deed copy.",
  "OccurredAtUtc": "2026-11-17T08:36:00Z"
}
```

```json
// response 200
{
  "eventId": "0f6c1d52-3b7a-4a55-9c53-0d6a1f1b0003",
  "externalTicketId": "QT-20261117-0001",
  "status": "Processed",
  "outcome": "APPLIED",
  "ticketId": 1,
  "ticketNumber": "TG-CS-20261117-0001",
  "ticketStatus": "InProgress",
  "retryable": false
}
```

### TicketRecalled

`200 OK`

```json
// request
{
  "EventId": "0f6c1d52-3b7a-4a55-9c53-0d6a1f1b0004",
  "EventType": "TicketRecalled",
  "ExternalTicketId": "QT-20261117-0001",
  "EventSequence": 4,
  "PerformedByUserId": "queue-user-a",
  "CounterId": "counter-3",
  "Reason": "Customer did not respond to the first call",
  "OccurredAtUtc": "2026-11-17T08:38:00Z"
}
```

```json
// response 200
{
  "eventId": "0f6c1d52-3b7a-4a55-9c53-0d6a1f1b0004",
  "externalTicketId": "QT-20261117-0001",
  "status": "Processed",
  "outcome": "APPLIED",
  "ticketId": 1,
  "ticketNumber": "TG-CS-20261117-0001",
  "ticketStatus": "InProgress",
  "retryable": false
}
```

### TicketReturned

`200 OK`

```json
// request
{
  "EventId": "0f6c1d52-3b7a-4a55-9c53-0d6a1f1b0005",
  "EventType": "TicketReturned",
  "ExternalTicketId": "QT-20261117-0001",
  "EventSequence": 5,
  "PerformedByUserId": "queue-user-a",
  "CounterId": "counter-3",
  "Reason": "Customer stepped away",
  "OccurredAtUtc": "2026-11-17T08:40:00Z"
}
```

```json
// response 200
{
  "eventId": "0f6c1d52-3b7a-4a55-9c53-0d6a1f1b0005",
  "externalTicketId": "QT-20261117-0001",
  "status": "Processed",
  "outcome": "APPLIED",
  "ticketId": 1,
  "ticketNumber": "TG-CS-20261117-0001",
  "ticketStatus": "InProgress",
  "retryable": false
}
```

### TicketReassigned

`200 OK`

```json
// request
{
  "EventId": "0f6c1d52-3b7a-4a55-9c53-0d6a1f1b0006",
  "EventType": "TicketReassigned",
  "ExternalTicketId": "QT-20261117-0001",
  "EventSequence": 6,
  "AssignedUserId": "queue-user-b",
  "PerformedByUserId": "queue-user-a",
  "CounterId": "counter-5",
  "Reason": "Specialist required",
  "OccurredAtUtc": "2026-11-17T08:42:00Z"
}
```

```json
// response 200
{
  "eventId": "0f6c1d52-3b7a-4a55-9c53-0d6a1f1b0006",
  "externalTicketId": "QT-20261117-0001",
  "status": "Processed",
  "outcome": "APPLIED",
  "ticketId": 1,
  "ticketNumber": "TG-CS-20261117-0001",
  "ticketStatus": "InProgress",
  "retryable": false
}
```

### TicketClosed

`200 OK`

```json
// request
{
  "EventId": "0f6c1d52-3b7a-4a55-9c53-0d6a1f1b0007",
  "EventType": "TicketClosed",
  "ExternalTicketId": "QT-20261117-0001",
  "EventSequence": 7,
  "PerformedByUserId": "queue-user-b",
  "CounterId": "counter-5",
  "Reason": "Served",
  "OccurredAtUtc": "2026-11-17T08:50:00Z"
}
```

```json
// response 200
{
  "eventId": "0f6c1d52-3b7a-4a55-9c53-0d6a1f1b0007",
  "externalTicketId": "QT-20261117-0001",
  "status": "Processed",
  "outcome": "APPLIED",
  "ticketId": 1,
  "ticketNumber": "TG-CS-20261117-0001",
  "ticketStatus": "Closed",
  "retryable": false
}
```

### TicketCancelled

`200 OK`

```json
// request
{
  "EventId": "0f6c1d52-3b7a-4a55-9c53-0d6a1f1b0011",
  "EventType": "TicketCancelled",
  "ExternalTicketId": "QT-20261117-0002",
  "EventSequence": 2,
  "PerformedByUserId": "queue-user-a",
  "CounterId": "counter-3",
  "Reason": "Customer left the branch",
  "OccurredAtUtc": "2026-11-17T08:45:00Z"
}
```

```json
// response 200
{
  "eventId": "0f6c1d52-3b7a-4a55-9c53-0d6a1f1b0011",
  "externalTicketId": "QT-20261117-0002",
  "status": "Processed",
  "outcome": "APPLIED",
  "ticketId": 2,
  "ticketNumber": "TG-CS-20261117-0002",
  "ticketStatus": "Closed",
  "retryable": false
}
```

### Other responses

**Duplicate delivery (same EventId, same body)** - The TicketClosed request above, sent again.

```json
// response 200
{
  "eventId": "0f6c1d52-3b7a-4a55-9c53-0d6a1f1b0007",
  "externalTicketId": "QT-20261117-0001",
  "status": "Duplicate",
  "outcome": "APPLIED",
  "ticketId": 1,
  "ticketNumber": "TG-CS-20261117-0001",
  "ticketStatus": "Closed",
  "retryable": false
}
```

**Event before its ticket exists** - `TicketAssigned` for `QT-20261117-0003`, which has no `TicketCreated` yet. It is stored and applied when the ticket is created.

```json
// response 202
{
  "eventId": "0f6c1d52-3b7a-4a55-9c53-0d6a1f1b0020",
  "externalTicketId": "QT-20261117-0003",
  "status": "Pending",
  "outcome": "AWAITING_TICKET_CREATED",
  "detail": "No TigerCS ticket exists yet for queue ticket \u0027QT-20261117-0003\u0027. The event is stored and will be applied when TicketCreated arrives.",
  "retryable": true
}
```

**Validation failure** - `TicketAssigned` with no `AssignedUserId` and no `OccurredAtUtc`.

```json
// response 400
{
  "eventId": "0f6c1d52-3b7a-4a55-9c53-0d6a1f1b0021",
  "externalTicketId": "QT-20261117-0004",
  "status": "Invalid",
  "outcome": "VALIDATION_FAILED",
  "detail": "The event failed validation. Nothing was stored.",
  "retryable": false,
  "errors": {
    "OccurredAtUtc": [
      "OccurredAtUtc is required."
    ],
    "AssignedUserId": [
      "AssignedUserId is required."
    ]
  }
}
```


## 6. Reliability

- **Idempotency in the database.** `QueueingEvents` has a unique index on `(Source, EventId)`; `Tickets` has a filtered unique index on `(IntegrationSource, ExternalTicketId)`; `QueueingTicketLinks` is unique on `TicketId` and on `ExternalTicketId`. Duplicates and concurrent deliveries - on one node or many - can only ever produce one row; the loser is answered with the winner's result. (Proven on real relational SQL in the test-suite.)
- **Out-of-order.** Every state-changing event carries `EventSequence`. An event not newer than the applied sequence is stored as `Superseded` (with an audit entry) and never overwrites state. An event for a ticket that does not exist yet is stored and applied **right after `TicketCreated`** (and by the retry sweep). Comments and recalls only add records, so a late one is still recorded, even after closure.
- **Unresolved events are persisted and retried.** A missing mapping, an unmapped / inactive / out-of-department employee, a missing ticket: the event is kept (`Unresolved`), nothing is changed or guessed, and it is retried with exponential backoff (30 s doubling, cap 1 h, 12 attempts) by a Hangfire job (`queueing-event-retry`, every minute, when `BackgroundJobs:Enabled`) - or immediately by a redelivery of the same `EventId`. After the last attempt it becomes `Failed`; redelivering it starts a fresh cycle.
- **Transactional consistency.** The integration's own state (the queue-state row and the inbox row) is staged on the same unit of work as the ticket change and commits in the very transaction of that change (assignment+start of work, return, close, cancel, comment, recall). If TigerCS refuses the change, the staged state is discarded and only the failure is recorded. Ticket **creation** commits the ticket (with its external id, protected by the unique index) first and the state row immediately after; a crash between them is repaired by any later delivery for the ticket (the state row is rebuilt from the ticket, nothing is created twice).
- **Per-ticket serialisation.** Events for one queue ticket are serialised in-process; across nodes the unique indexes and the state row's row version are the guard (the loser gets `503 Busy` / is retried).
- **Original timestamps.** While an event is applied, the application clock answers `OccurredAtUtc` (never later than now): ticket creation time, SLA period start, assignment time, status history, resolution time and audit entries carry when it happened in the queue. Nothing else is affected - outside an event the clock is exactly the system clock.

## 7. SLA and history

- The SLA period opens at ticket creation with the default priority (`Normal`) and the request type's existing policy, at `OccurredAtUtc`. A delayed delivery is therefore measured from when the customer actually took the ticket (see decision D3).
- **Assignment is not a first response.** Nothing here calls the first-response recorder; `FirstHumanResponseAtUtc` stays empty after assignment (tested). The existing recorders (agent reply, SLA first-response endpoint) are unchanged.
- Closing as `Resolved` settles the Resolution clock; `Cancelled` does not (existing rule for non-resolved outcomes).
- History: assignments -> `TicketAssignments`; status changes -> `TicketStatusHistory` (system actor for closure); comments -> `TicketNotes`; every applied event -> an audit entry carrying the event id, type, sequence, original time, counter and performer; return / recall / creation facts also appear on the ticket activity timeline.

## 8. Customer notifications - no duplicates

The Queueing System already notifies its customers. A queue-originated ticket (`Ticket.SuppressesCustomerNotifications`) therefore:

- never queues the creation acknowledgement email, and
- has every other customer email (assigned, in progress, resolved, closed, reopened ...) **skipped with the recorded reason `ManagedByExternalSystem`** by the notification handlers (visible on the timeline as "Email skipped"), whatever the email configuration.

TigerCS sends no customer SMS about tickets at all (SMS is used for OTP only). Internal "assigned to you" notices to staff are unchanged.

## 9. Manual changes to queue-managed tickets in TigerCS

Because the Queueing System decides, `QueueingIntegration:ManualChangePolicy`:

- **`Block` (default):** assign, reassign, transfer, take ownership, change status, resolve, close and reopen of a queue ticket from TigerCS are refused with `409 ticket-managed-by-queueing-system` ("make the change in the Queueing System"). Comments / notes, attachments, priority and classification workflows stay open.
- `Allow`: permitted as a local edit - but the next queue event overwrites it (the queue's word is final). For use only while staff cannot yet act from the Queueing System.

Queue-managed tickets are excluded from the existing unassigned-work reconcilers so no background job assigns them behind the queue's back.

## 10. Configuration and mapping setup

`appsettings` (disabled by default):

```json
"QueueingIntegration": {
  "Enabled": false,
  "ServiceAccountEmployeeId": "<employee id of the integration account>",
  "ChannelCode": "WALK_IN_KIOSK",
  "DefaultTicketPriority": "Normal",
  "UserLookupMode": "Mapping",            // Mapping | EmployeeId
  "MissingRequestTypePolicy": "Hold",     // Hold | DefaultClassification
  "ManualChangePolicy": "Block",          // Block | Allow
  "MaxRetryAttempts": 12, "RetryBaseDelaySeconds": 30, "MaxRetryDelaySeconds": 3600,
  "Authentication": { "HeaderName": "X-Queueing-Api-Key", "ApiKeys": [ { "KeyId": "queue-prod", "Sha256": "<hex sha-256 of the key>" } ] }
},
"Authorization": { "ServiceIdentity": { "EmployeeIds": [ "<the same service account id>" ] } }
```

Set keys / ids through environment variables (`QueueingIntegration__ServiceAccountEmployeeId`, `QueueingIntegration__Authentication__ApiKeys__0__Sha256`). Nothing (ids, keys, statuses) is hard-coded.

One-time setup, in order:

1. Deploy `AddQueueingIntegration.sql`.
2. Create the integration account (a CS Agent employee, never given a department assignment) and put its id in both settings above.
3. Add the mappings (ids below are examples - look them up; the unique indexes reject duplicates):

```sql
-- Company -> department (a company routes to exactly one department)
INSERT INTO QueueingCompanyMappings (CompanyId, DepartmentId, IsActive, CreatedAtUtc)
VALUES (N'company-a', (SELECT DepartmentId FROM Departments WHERE Code = N'CS'), 1, SYSUTCDATETIME());

-- (Company, ticket type) -> request type. The request type must belong to the company's department
-- and have exactly one active same-named category (how TigerCS pairs a request type with a category).
INSERT INTO QueueingTicketTypeMappings (CompanyId, TicketTypeId, RequestTypeId, IsActive, CreatedAtUtc)
VALUES (N'company-a', N'type-complaint',
        (SELECT RequestTypeId FROM RequestTypes WHERE Name = N'Complaint' AND DepartmentId = (SELECT DepartmentId FROM Departments WHERE Code = N'CS')),
        1, SYSUTCDATETIME());

-- Queue employee -> TigerCS employee (one-to-one). The employee must be an ACTIVE member of the ticket's department.
INSERT INTO QueueingUserMappings (ExternalUserId, EmployeeId, IsActive, CreatedAtUtc)
VALUES (N'queue-user-a', (SELECT Id FROM AspNetUsers WHERE UserName = N'jane.doe'), 1, SYSUTCDATETIME());
```

Alternatively set `UserLookupMode: EmployeeId` when the Queueing System is provisioned with the TigerCS employee GUIDs themselves.

4. Verify: send folder 2 of the Postman collection; the ticket shows on the Walk in / Kiosk channel, unassigned, then follows the events.

## 11. Verification

See the PR for the build and test results. The suite adds `TigerCS.Tests/QueueingIntegration/*` (QR creation, duplicate creation, assignment / reassignment, return, comment, recall, close, cancel, missing employee / ticket type / company mappings, duplicate / out-of-order / concurrent events, unauthorised requests, SLA and history, notification suppression, manual-change policy, database-level uniqueness on real relational SQL) and keeps the whole existing suite, including every Genesys test, green.

## 12. Unresolved business decisions

| # | Decision | Current behaviour (change = a configuration value or a small code change) |
|---|---|---|
| D1 | **Status after `TicketReturned`.** TigerCS has no InProgress -> Open transition. | Ticket stays `InProgress` with no owner, in the department queue. Alternative: allow InProgress -> Open for queue-managed tickets. |
| D2 | **Missing ticket-type mapping.** | `Hold` (no ticket until mapped; nothing guessed). `DefaultClassification` creates the ticket as the department's General classification and audits the gap. A customer is physically waiting, so the business may prefer the second. |
| D3 | **SLA clock for a delayed delivery.** | Starts at the event's original time. If the Queueing System is down for an hour, the ticket is created already an hour into its SLA. Alternative: start at receipt. |
| D4 | **Department settings that disable assignment** (`AllowAssignment = false`) | Honoured: the event stays unresolved (`TICKET_NOT_ELIGIBLE`). Alternative: let the integration bypass it. |
| D5 | **Customer mobile mandatory?** | Optional on `TicketCreated`. If every QR ticket carries one, make it required (validator). |
| D6 | **Automatic customer match.** | Lookup result only enriches the name/audit; a ticket is never auto-verified against a CRM customer (same conservative rule as Genesys). |
| D7 | **Reopen** of a closed queue ticket. | Not supported by the Queueing System; a later state event on a closed ticket is `Rejected`. |
| D8 | **Mappings administration UI.** | Tables + SQL only in this phase (no admin screens or endpoints). |
| D9 | **Manual-change policy** default `Block`. | Confirm with Customer Service leadership before go-live. |
| D10 | **`Failed` events** need a person. | Visible in `QueueingEvents` (`Status = 6`); no dashboard yet. |
| D11 | **Retention of the event inbox.** `QueueingEvents.Payload` keeps each event verbatim (including the customer's name / phone / email) so an unresolved event can be retried exactly as sent. | Kept indefinitely for now. Agree a purge window for settled events (the ticket and audit trail keep what the business needs). |
| D12 | **Brute-force / rate limiting of the API key.** | A wrong key is a plain `401`; no lockout or rate limit is added in this phase. Put the endpoint behind the network allow-list / gateway limits agreed for the Queueing System. |
