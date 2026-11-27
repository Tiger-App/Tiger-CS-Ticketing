# Queueing System integration - receiving API

TigerCS receives the lifecycle of QR-code queue tickets from the Queueing System and mirrors it onto **one TigerCS ticket per queue ticket**. **The Queueing System is the
source of truth** for the lifecycle of these tickets (who serves them, when they are returned, recalled, closed or cancelled); TigerCS records it and keeps its own history,
SLA and reporting consistent with it.

| | |
|---|---|
| Endpoint | `POST /api/integrations/queueing/events` |
| Receiving side | `src/` (TigerCS) - this document is its contract |
| Sending side | [`queueing-system/`](../../queueing-system/README.md) - a separate solution that references nothing from TigerCS |
| Postman | [`TigerCS-Queueing.postman_collection.json`](TigerCS-Queueing.postman_collection.json) |
| Sample JSON | [`samples/`](samples) - posted to the real API by the TigerCS test-suite and parsed by the sender's tests, so it cannot drift |
| Database script | [`/AddQueueingIntegration.sql`](../../AddQueueingIntegration.sql) (idempotent; EF migration `20261117101035_AddQueueingIntegration`) |

## 1. Event -> TigerCS behaviour (no new statuses)

| Event | What TigerCS does |
|---|---|
| `TicketCreated` | Creates a classified, **unassigned**, `Open` ticket on the existing **Walk in / Kiosk** channel in the department and with the request type resolved from `CompanyId` + `TicketTypeId` (see 1.1). Source = `QueueingSystem`; the queue ticket id and queue number are kept on the ticket. The SLA starts at `OccurredAtUtc`. No Call Center routing, no assignment rule, no Round Robin, no creator self-assignment. The customer's mobile number is looked up through the existing CRM / PACT / Tasleeh lookup (enrichment only - a match is never auto-verified). |
| `TicketAssigned` | The employee mapped from `AssignedUserId` becomes the owner (existing assignment rules and history). An `Open` ticket moves to `InProgress` through the existing transition, performed as that employee. Assignment is **not** a first response. |
| `TicketReassigned` | Same, to the new employee. The previous assignment row is superseded (history kept). |
| `TicketReturned` | The owner is released and the ticket returns to **`Open`**, in the same department. This transition exists **only for queue-managed tickets** (`Ticket.ReturnToQueue`); the generic status table is untouched (`InProgress -> Open` is still not a TigerCS transition for anyone else). The assignment row is superseded and a system status-history row (`InProgress -> Open`, with the reason) is written. A later `TicketAssigned` takes the ticket `Open -> InProgress` again, exactly as for a first assignment. |
| `TicketCommentAdded` | A ticket note, authored by the employee who wrote it when mapped (otherwise by the integration account, with the queue user named in the text). Still recorded after closure. |
| `TicketRecalled` | A history (audit + timeline) entry with counter, performer and reason. Nothing else changes. Still recorded after closure. |
| `TicketClosed` | System closure, resolution outcome **Resolved**, owner cleared, SLA resolution clock settled. On a ticket that is **Cancelled** (the Queueing System can close a cancelled queue ticket as served) the same closed ticket changes its outcome **Cancelled -> Resolved**: the Cancelled resolution row is archived (kept), a new Resolved row is added, the outcome change is on the status history and audit trail, and the Resolution SLA clock is settled. `ReopenCount` is untouched and no second closure notification is queued. |
| `TicketCancelled` | The same closure with resolution outcome **Cancelled** (TigerCS has no Cancelled status; Cancelled is an existing resolution outcome). |

**TigerCS is a one-way mirror.** It never sends anything to the Queueing System and never decides anything about a queue ticket; it only reflects the state the Queueing System reports - including the transitions out of a terminal state when they actually happen in the Queueing System: Closed/Cancelled -> `InProgress` again (`TicketAssigned` / `TicketReassigned`, see D7) and Cancelled -> Closed (`TicketClosed`). The reverse Closed -> Cancelled cannot happen in the Queueing System (it only cancels `WAITING` / `INPROGRESS` tickets) and stays `Rejected`. Comments and recalls are outside the synchronisation scope: the sender does not send them (the event types stay in the contract and are still accepted).

### 1.1 Department and request-type resolution

TigerCS **mirrors** the Queueing System: a new Queueing service never needs a TigerCS request type. Routing is decided in this order:

0. **The company mapping is mandatory** (`QueueingCompanyMappings`, active row to an active department). A `CompanyId` with no active mapping is **held** (`COMPANY_NOT_MAPPED`, HTTP 202) and retried automatically once the mapping exists. No default department is ever used and a department is **never guessed** - not even when an explicit ticket-type mapping exists for that company.
1. **Optional override - explicit (company, ticket type) mapping** (`QueueingTicketTypeMappings`, active row). It determines the **request type**, and the **department is that request type's responsible department**. One `CompanyId` can therefore carry ticket types that belong to several TigerCS departments. It is only ever needed to route a service somewhere other than the company's department, or to give it a specific request type.
2. **Otherwise (the normal case) - the company's department and its General request type.** The ticket is created in the department of the company mapping and classified with **that department's own General category / General request type and its Published workflow** (the existing default-classification rule). This is an **internal classification only**: the ticket keeps `IntegrationSource = QueueingSystem`, so it stays distinguishable in every report, and the Queueing System's own service is preserved separately (below). The creation is audited (`RequestTypeMapping=Missing(company=..;ticketType=..);Applied=General(RequestTypeId=..)`). If the department has no usable General (or the rule excludes the department - Collections) the event is held (`TICKET_TYPE_NOT_MAPPED`) - no request type is ever invented. Run `Verify_QueueingDepartmentReadiness_ReadOnly.sql` before mapping a company to a department.

An explicit mapping must point at a request type that is **active**, belongs to an **active department**, has a **Published workflow version**, and pairs with **exactly one active same-named category** in its department. A mapping that **exists but is unusable** never falls back to General (that would silently misroute the ticket): the event is held with `REQUEST_TYPE_UNUSABLE` and retried once the configuration is fixed. A mapping row with `IsActive = 0` is treated as absent.

**The Queueing service is preserved and shown.** `CompanyId`, `TicketTypeId` and the optional `TicketTypeName` (the Queueing System's own service name, exactly as sent) are stored on the queue-state row (`QueueingTicketLinks.CompanyId / ExternalTicketTypeId / ExternalTicketTypeName`) and written to the creation audit. The **ticket summary** reads `Queue ticket A-017 (Queueing System) - Lease Renewal - Aisha Khan` (the name, or `ticket type <id>` when none was sent), **Ticket Details** shows *Queueing service* and *Queueing company*, and the **activity timeline** names the service. The ticket's TigerCS request type (General, or the override) is only its internal classification. A queue ticket's classification, assignment, status and closure cannot be changed by hand while `QueueingIntegration:ManualChangePolicy = Block` (the default); comments and notes stay open.

`TicketTypeName` is **display only**: it never routes, and it is **not part of the event's idempotency identity** - the stored hash is taken over the event without it, so an event received before the field existed is still a duplicate (never a conflict) when redelivered with or without a name, and a redelivery that changes any other field is still `EVENT_ID_REUSED`. The first name received for an EventId wins.

### 1.2 How a queue ticket is shown, timed and credited

Confirmed against the local UAT of ticket 1686 (Created, Assigned, CommentAdded, Recalled, Returned, Reassigned, Closed).

| Topic | Behaviour |
|---|---|
| **Source** | The timeline reads **Source: Queueing System** (actor *Queueing System*), never "TigerCS (entered by an employee)". Queue interactions carry no Genesys wording. |
| **Interaction** | The Walk in / Kiosk interaction is **ended at the event's original `OccurredAtUtc`** (clamped to now) on `TicketClosed` (reason `QueueTicketClosed`) and `TicketCancelled` (`QueueTicketCancelled`), in the same transaction as the closure. Ending it never changes the ticket status. |
| **Closed by** | `PerformedByUserId` is resolved through `QueueingUserMappings` (the same lookup as assignment). A mapped, active employee is the **closure actor** (status history, resolution row, audit) and the closure reads *Human Agent* (or *Supervisor*). An unmapped / missing user leaves the closure a **Queueing System** action (*System Automation - Queueing System*); it is **never** attributed to the integration account and no employee is guessed. |
| **Comment author** | The mapped employee when `PerformedByUserId` resolves (the note list now carries `authorName`); otherwise the integration account with a `[Queueing System - queue user X]` prefix. |
| **Assignment history** | `TicketReturned` ends the current assignment **at the Return's time**; the following `TicketReassigned` starts a new row at its own time. The span between them is *unassigned*. Closure ends the last assignment at the closure time. Inside a queue event the assignment end is stamped with the event's time, so durations are measured on the queue's timeline, not on when TigerCS received the event. |
| **Closed ticket owner** | A closed ticket has no owner and no current assignment. Ticket Details shows the **last responsible employee** (the holder of the last assignment) as *name (last responsible)* - display only. |
| **Performance** | Credit follows the **last assignment**, not the closing actor: the employee closed-ticket counts and the *Human Agent* closure card count the final holder (a queue closure counts as Human Agent only when it named a mapped employee); Team Performance / SLA reports credit a Resolution met to whoever held the ticket at the closure instant (an assignment ended exactly at the resolution still holds it). Review eligibility needs no change (every system closure produces the single Closed email, which carries the review link; customer emails for queue tickets are skipped, see 7). |
| **Notifications** | Customer emails are recorded as *Skipped* (`ManagedByExternalSystem`); staff assignment emails are delivered. TigerCS has no SMS channel for tickets, so no customer SMS can be sent. |

**First Response SLA.** No queue event is treated as a first human response: `TicketAssigned`, `TicketCommentAdded`, `TicketRecalled` and `TicketClosed` say what happened in the *queue*, not that an employee replied to / served the customer, and a recall can mean "no answer". A closed queue ticket therefore has no recorded first response, and Ticket Details shows *No person reply recorded* instead of *Awaiting a person's reply*. **A separate `TicketServiceStarted` event is recommended** (the queue knows when the customer reached the counter and service began): it would record the first human response at its own `OccurredAtUtc` for the employee named in `PerformedByUserId`, make First Response SLA meaningful for queue tickets, and let reports distinguish waiting time from service time. It is not implemented here because it adds an event type to the integration contract.

## 2. Authentication

| | |
|---|---|
| Header name | **`X-Queueing-Api-Key`** (configurable on the TigerCS side: `QueueingIntegration:Authentication:HeaderName`) |
| Header value | The API key **exactly as issued**: an opaque, case-sensitive string. **No `Bearer` / `ApiKey` prefix, no `Authorization` header.** Send it only over HTTPS. |
| Key format | At least 32 random bytes, e.g. `openssl rand -base64 36`. TigerCS stores only **the lower-case hex SHA-256 of the key's UTF-8 bytes**, never the key. |
| Rotation | Several key hashes may be configured at once; add the new one, switch the Queueing System, remove the old one. |
| Scope | The key opens **this one endpoint** and nothing else; a staff login cannot call it. |
| Failure | Missing or wrong key -> `401`, header `WWW-Authenticate: ApiKey`, nothing stored. |

Generate a key and its hash:

```bash
KEY=$(openssl rand -base64 36 | tr -d '\n'); echo "key:  $KEY"; printf '%s' "$KEY" | sha256sum | cut -d' ' -f1
```

## 3. Request

`POST /api/integrations/queueing/events`, `Content-Type: application/json`. Member names are PascalCase (matched case-insensitively by TigerCS); `EventType` is the event's **name**, never a number; `OccurredAtUtc` is ISO-8601 UTC ending in `Z`; members with no value are omitted.

### 3.1 Event types and request DTO (the sender's actual source)

`QueueingEventType.cs`

```csharp
using System.Text.Json.Serialization;

namespace QueueingSystem.TigerCsSender;

/// <summary>
/// The eight events the Queueing System sends to TigerCS. <b>Serialised by name</b> (the wire value of <c>EventType</c> is exactly the member name, e.g.
/// <c>"TicketAssigned"</c>) - never by number.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum QueueingEventType
{
    /// <summary>A customer took a queue ticket (QR code). TigerCS creates one unassigned ticket.</summary>
    TicketCreated = 1,

    /// <summary>The queue ticket was called by an employee at a counter. TigerCS assigns it to that employee.</summary>
    TicketAssigned = 2,

    /// <summary>The queue ticket moved to another employee. TigerCS reassigns it.</summary>
    TicketReassigned = 3,

    /// <summary>The employee returned the queue ticket to the waiting queue. TigerCS releases the owner and the ticket goes back to Open.</summary>
    TicketReturned = 4,

    /// <summary>A comment was written on the queue ticket. TigerCS adds a ticket note.</summary>
    TicketCommentAdded = 5,

    /// <summary>The customer was called again. TigerCS records it in the ticket history.</summary>
    TicketRecalled = 6,

    /// <summary>The customer was served. TigerCS closes the ticket as Resolved.</summary>
    TicketClosed = 7,

    /// <summary>The queue ticket was cancelled. TigerCS closes the ticket as Cancelled.</summary>
    TicketCancelled = 8
}
```

`QueueingEventRequest.cs`

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QueueingSystem.TigerCsSender;

/// <summary>
/// The one request body of <c>POST /api/integrations/queueing/events</c>. Member names are PascalCase on the wire (exactly as declared here). Members with no
/// value are omitted from the JSON. Which members are required depends on <see cref="EventType"/>: see <see cref="Validate"/>.
/// </summary>
public sealed record QueueingEventRequest
{
    /// <summary>Unique id of this event, chosen by the Queueing System (a GUID string is ideal). The idempotency key: the same EventId with the same content is applied once, however often it is sent. Never reuse an EventId for a different event. Max 100 characters.</summary>
    public required string EventId { get; init; }

    /// <summary>One of <see cref="QueueingEventType"/>, written by name.</summary>
    public required QueueingEventType EventType { get; init; }

    /// <summary>The queue ticket's id. One TigerCS ticket exists per value. Max 100 characters.</summary>
    public required string ExternalTicketId { get; init; }

    /// <summary>
    /// The event's position in this queue ticket's own history: 1 for <see cref="QueueingEventType.TicketCreated"/>, then strictly increasing for every later event of the
    /// same queue ticket. See the sequence rules in the contract document.
    /// </summary>
    public required long EventSequence { get; init; }

    /// <summary>When the event happened in the Queueing System, in UTC (ISO-8601, ending in Z). Not more than 5 minutes in the future. Preserved by TigerCS on everything it records.</summary>
    public required DateTime OccurredAtUtc { get; init; }

    /// <summary>The number shown to the customer (e.g. A-017). Required on TicketCreated. Max 100.</summary>
    public string? QueueNumber { get; init; }

    /// <summary>The Queueing System's company id. Required on TicketCreated. Mapped by TigerCS to a department. Max 100.</summary>
    public string? CompanyId { get; init; }

    /// <summary>The Queueing System's ticket type id. Required on TicketCreated. Optionally mapped by TigerCS to a request type (an override); otherwise the company's department's General request type is used. Max 100.</summary>
    public string? TicketTypeId { get; init; }

    /// <summary>Optional. The Queueing System's own service name for the ticket type (e.g. "Lease Renewal"). Kept and shown by TigerCS exactly as sent; never used for routing. Read on TicketCreated. Max 200.</summary>
    public string? TicketTypeName { get; init; }

    /// <summary>Optional. Max 200.</summary>
    public string? CustomerName { get; init; }

    /// <summary>The customer's mobile number. Optional: when there is none the CRM phone lookup is skipped and TigerCS never invents one. Must contain digits when sent; international format (+971...) preferred. Max 32.</summary>
    public string? CustomerPhone { get; init; }

    /// <summary>Optional. Max 256.</summary>
    public string? CustomerEmail { get; init; }

    /// <summary>The Queueing System's id of the employee who now serves the ticket. Required on TicketAssigned and TicketReassigned. Max 100.</summary>
    public string? AssignedUserId { get; init; }

    /// <summary>The Queueing System's id of the employee who performed the action, when known. Optional. Recorded in the TigerCS history. Max 100.</summary>
    public string? PerformedByUserId { get; init; }

    /// <summary>The counter involved. Optional. Recorded in the TigerCS history. Max 100.</summary>
    public string? CounterId { get; init; }

    /// <summary>The comment text. Required on TicketCommentAdded. Max 4000.</summary>
    public string? Comment { get; init; }

    /// <summary>Why (reassigned, returned, recalled, closed, cancelled). Optional. Recorded as the history note. Max 1000.</summary>
    public string? Reason { get; init; }

    private static readonly JsonSerializerOptions WireOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = null,
        WriteIndented = false
    };

    /// <summary>The exact JSON that goes on the wire.</summary>
    public string ToJson() => JsonSerializer.Serialize(this with { OccurredAtUtc = DateTime.SpecifyKind(OccurredAtUtc, DateTimeKind.Utc) }, WireOptions);

    /// <summary>
    /// The members each event type needs beyond the five always-required ones (<c>EventId</c>, <c>EventType</c>, <c>ExternalTicketId</c>, <c>EventSequence</c>,
    /// <c>OccurredAtUtc</c>). The same table TigerCS validates against.
    /// </summary>
    public static IReadOnlyList<string> RequiredMembers(QueueingEventType type) => type switch
    {
        QueueingEventType.TicketCreated => ["QueueNumber", "CompanyId", "TicketTypeId"],
        QueueingEventType.TicketAssigned or QueueingEventType.TicketReassigned => ["AssignedUserId"],
        QueueingEventType.TicketCommentAdded => ["Comment"],
        _ => []
    };

    /// <summary>Checks the event against the contract before it is ever sent. Returns the problems by member name; empty when valid. TigerCS answers 400 for the same problems.</summary>
    public IReadOnlyDictionary<string, string> Validate(DateTime? nowUtc = null)
    {
        var errors = new Dictionary<string, string>();

        void Text(string member, string? value, int max, bool required)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                if (required)
                {
                    errors[member] = $"{member} is required.";
                }
            }
            else if (value.Trim().Length > max)
            {
                errors[member] = $"{member} must be at most {max} characters.";
            }
        }

        Text(nameof(EventId), EventId, 100, true);
        Text(nameof(ExternalTicketId), ExternalTicketId, 100, true);
        if (!Enum.IsDefined(EventType))
        {
            errors[nameof(EventType)] = "EventType is not a supported event.";
        }

        if (EventSequence < 1)
        {
            errors[nameof(EventSequence)] = "EventSequence must be 1 or greater.";
        }

        if (OccurredAtUtc == default)
        {
            errors[nameof(OccurredAtUtc)] = "OccurredAtUtc is required.";
        }
        else if (OccurredAtUtc.Kind == DateTimeKind.Local)
        {
            errors[nameof(OccurredAtUtc)] = "OccurredAtUtc must be UTC.";
        }
        else if (OccurredAtUtc > (nowUtc ?? DateTime.UtcNow).AddMinutes(5))
        {
            errors[nameof(OccurredAtUtc)] = "OccurredAtUtc is in the future.";
        }

        var required = RequiredMembers(EventType);
        Text(nameof(QueueNumber), QueueNumber, 100, required.Contains(nameof(QueueNumber)));
        Text(nameof(CompanyId), CompanyId, 100, required.Contains(nameof(CompanyId)));
        Text(nameof(TicketTypeId), TicketTypeId, 100, required.Contains(nameof(TicketTypeId)));
        Text(nameof(CustomerName), CustomerName, 200, false);
        Text(nameof(CustomerPhone), CustomerPhone, 32, required.Contains(nameof(CustomerPhone)));
        if (!string.IsNullOrWhiteSpace(CustomerPhone) && !CustomerPhone.Any(char.IsAsciiDigit))
        {
            errors[nameof(CustomerPhone)] = "CustomerPhone must be a phone number.";
        }

        Text(nameof(CustomerEmail), CustomerEmail, 256, false);
        Text(nameof(AssignedUserId), AssignedUserId, 100, required.Contains(nameof(AssignedUserId)));
        Text(nameof(PerformedByUserId), PerformedByUserId, 100, false);
        Text(nameof(CounterId), CounterId, 100, false);
        Text(nameof(Comment), Comment, 4000, required.Contains(nameof(Comment)));
        Text(nameof(Reason), Reason, 1000, false);
        return errors;
    }
}
```

### 3.2 Required members per event type

Always required: `EventId`, `EventType`, `ExternalTicketId`, `EventSequence`, `OccurredAtUtc`.

| Event | Also required |
|---|---|
| TicketCreated | `QueueNumber`, `CompanyId`, `TicketTypeId`. **No customer phone or email is required**: the authenticated API key plus `ExternalTicketId` and `QueueNumber` (and `CustomerName` when known) identify the queue customer. A customer with a phone, an email, both or neither is accepted; when there is no phone the CRM phone lookup is skipped and **TigerCS never generates a placeholder number**. A phone, when sent, must contain digits, and an email must contain `@`. Optional context: `CustomerName`, `CustomerPhone`, `CustomerEmail`, `ProjectCode`, `ProjectName`, `UnitCode`, and the display names `CompanyName` / `TicketTypeName` (accepted but never required; they are recorded in the audit, not matched to anything). Project and unit appear in the ticket summary and audit and are not matched to a TigerCS unit. |
| TicketAssigned / TicketReassigned | `AssignedUserId` |
| TicketCommentAdded | `Comment` |
| TicketReturned / TicketRecalled / TicketClosed / TicketCancelled | nothing |

A missing or invalid member is a `400` naming the member; nothing is stored.

## 4. EventSequence requirements

`EventSequence` is the position of an event in **one queue ticket's own history**. It is how TigerCS keeps an old event from overwriting a newer state.

1. It is an integer **>= 1**, **per `ExternalTicketId`** (every queue ticket has its own counter).
2. `TicketCreated` is **sequence 1**. Every later event of that queue ticket - **including comments and recalls** - gets the next number: 2, 3, 4 ...
3. It is assigned **when the event happens**, in the same transaction that records it in the Queueing System - **not when it is sent** - and must be **durable** (a restart must never repeat or restart a sequence).
4. It is **never reused** and **never changed**. A resend of an event carries the *same* `EventId`, `EventSequence`, `OccurredAtUtc` and content.
5. It must be **strictly increasing in the order the events happened**. Gaps are tolerated (TigerCS only compares), but a number lower than one already used for a *newer* event is a stale event.
6. **How TigerCS uses it.** For a state-changing event (`TicketAssigned`, `TicketReassigned`, `TicketReturned`, `TicketClosed`, `TicketCancelled`): if its sequence is **greater** than the last applied sequence it is applied; if it is **not greater** it is recorded as `Superseded` and never applied. `TicketCommentAdded` and `TicketRecalled` only add records, so they are recorded whatever their sequence (even after closure) and do not move the applied sequence. An event for a queue ticket TigerCS has not seen yet (`TicketCreated` not processed) is stored and applied right after `TicketCreated`.
7. **Send each queue ticket's events in order, one at a time** (different tickets in parallel is fine). TigerCS tolerates reordering, but in-order delivery means it never has to.
8. **Strict per-ticket order (in TigerCS).** A state-changing event is applied only when **no earlier event of the same queue ticket is unresolved** (Received, Unresolved or `Failed`). While one is - a mapping or an employee is missing, the event failed, or it is still being processed - every later event of that ticket is **held behind it** (`202`, `AWAITING_PREVIOUS_EVENT`, `retryable: true`): never applied around it, **never skipped**, and it consumes no retry attempt. The moment the earlier event settles (applied, superseded or rejected), the held events are applied in sequence order. Only the oldest unresolved event of a ticket (its *head*) is retried; other queue tickets are unaffected. Comments and recalls never block and are never blocked.
9. **`OccurredAtUtc` is never an ordering authority - the sequence is.** An event derived from the queue's current state (post-commit reconciliation, `Reason`: `Reconciled from Queueing state`) may carry an `OccurredAtUtc` earlier than an event already applied, or even earlier than the ticket's creation. TigerCS never rejects, supersedes or reorders an event because of its time; the only rule on it is the existing "not more than 5 minutes in the future". So that the ticket's own history cannot run backwards, the time its records are stamped with (status history, resolution, SLA periods, assignment rows, audit) is floored at the ticket's creation and at the latest time already applied; the event's original `OccurredAtUtc` is kept on the inbox row. A reconciled `TicketCreated` -> `TicketClosed` / `TicketCancelled` with no `TicketAssigned` in between closes cleanly (Resolved / Cancelled) and invents no assignment; a last owner in `PerformedByUserId` that is not mapped never holds a closure (it is only the performer).

## 5. Response

`QueueingEventResponse.cs` (the sender's DTO of the response body; camelCase on the wire, members with no value omitted)

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QueueingSystem.TigerCsSender;

/// <summary>What TigerCS did with an event. Serialised by name.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum QueueingResultStatus
{
    /// <summary>Applied (or already in that state). Delivered; nothing more to do.</summary>
    Processed,

    /// <summary>This EventId was already received; the original outcome is returned. Delivered.</summary>
    Duplicate,

    /// <summary>Stored but not applied yet (its ticket does not exist yet, or a mapping / employee is not set up). TigerCS retries it itself - do NOT resend.</summary>
    Pending,

    /// <summary>Older than the ticket's applied state; recorded, deliberately not applied. Delivered; do not resend.</summary>
    Superseded,

    /// <summary>Valid but not applicable (for example a state change after the ticket was closed). Do not resend.</summary>
    Rejected,

    /// <summary>Failed validation (HTTP 400). Nothing stored. Fix the event; do not resend as is.</summary>
    Invalid,

    /// <summary>The same EventId was already received with different content (HTTP 409). A bug in the sender.</summary>
    Conflict,

    /// <summary>TigerCS has the integration switched off (HTTP 503). Retry later.</summary>
    Disabled,

    /// <summary>Another request is changing the same ticket (HTTP 503 with Retry-After). Resend the same event.</summary>
    Busy
}

/// <summary>The response body of <c>POST /api/integrations/queueing/events</c> (camelCase on the wire; members with no value are omitted).</summary>
public sealed record QueueingEventResponse
{
    [JsonPropertyName("eventId")] public string? EventId { get; init; }

    [JsonPropertyName("externalTicketId")] public string? ExternalTicketId { get; init; }

    [JsonPropertyName("status")] public QueueingResultStatus Status { get; init; }

    /// <summary>A stable machine-readable code: TICKET_CREATED, APPLIED, TICKET_ALREADY_EXISTS, ALREADY_IN_STATE, STALE_EVENT, TICKET_ALREADY_CLOSED, AWAITING_TICKET_CREATED, COMPANY_NOT_MAPPED, TICKET_TYPE_NOT_MAPPED, REQUEST_TYPE_UNUSABLE, USER_NOT_MAPPED, USER_INACTIVE, USER_NOT_IN_DEPARTMENT, CHANNEL_NOT_CONFIGURED, PRIORITY_NOT_CONFIGURED, SERVICE_ACCOUNT_NOT_CONFIGURED, CONCURRENT_UPDATE, PROCESSING_FAILED, TICKET_NOT_ELIGIBLE, EVENT_ID_REUSED, VALIDATION_FAILED, INTEGRATION_DISABLED.</summary>
    [JsonPropertyName("outcome")] public string? Outcome { get; init; }

    [JsonPropertyName("ticketId")] public long? TicketId { get; init; }

    [JsonPropertyName("ticketNumber")] public string? TicketNumber { get; init; }

    /// <summary>The TigerCS ticket status after the event (Open, InProgress, Closed ...).</summary>
    [JsonPropertyName("ticketStatus")] public string? TicketStatus { get; init; }

    [JsonPropertyName("detail")] public string? Detail { get; init; }

    /// <summary>True when TigerCS will retry the event by itself (<see cref="QueueingResultStatus.Pending"/>) or the same event can safely be resent (<see cref="QueueingResultStatus.Busy"/>).</summary>
    [JsonPropertyName("retryable")] public bool Retryable { get; init; }

    /// <summary>Validation errors by member name (<see cref="QueueingResultStatus.Invalid"/> only).</summary>
    [JsonPropertyName("errors")] public Dictionary<string, string[]>? Errors { get; init; }

    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
```

`outcome` is a stable code: `TICKET_CREATED`, `APPLIED`, `TICKET_ALREADY_EXISTS`, `ALREADY_IN_STATE`, `STALE_EVENT`, `TICKET_ALREADY_CLOSED`, `AWAITING_TICKET_CREATED`, `COMPANY_NOT_MAPPED`, `TICKET_TYPE_NOT_MAPPED`, `REQUEST_TYPE_UNUSABLE`, `USER_NOT_MAPPED`, `USER_INACTIVE`, `USER_NOT_IN_DEPARTMENT`, `CHANNEL_NOT_CONFIGURED`, `PRIORITY_NOT_CONFIGURED`, `SERVICE_ACCOUNT_NOT_CONFIGURED`, `CONCURRENT_UPDATE`, `PROCESSING_FAILED`, `TICKET_NOT_ELIGIBLE`, `EVENT_ID_REUSED`, `VALIDATION_FAILED`, `INTEGRATION_DISABLED`.

## 6. Exact request and response JSON for every event

These are the files in [`samples/`](samples). The TigerCS test `EverySampleInTheContract_IsExactlyWhatTheApiAnswers` posts them to the real API in this order and requires the response to be identical (only `ticketId` / `ticketNumber`, which depend on the database, are normalised).

#### TicketCreated

A QR scan. Creates exactly one unassigned, `Open` ticket. `EventSequence` is 1.

Request body:

```json
{
  "EventId": "0f6c1d52-3b7a-4a55-9c53-0d6a1f1b0001",
  "EventType": "TicketCreated",
  "ExternalTicketId": "QT-20261110-0001",
  "EventSequence": 1,
  "QueueNumber": "A-017",
  "CompanyId": "company-a",
  "TicketTypeId": "type-complaint",
  "CustomerName": "Aisha Khan",
  "CustomerPhone": "+971500000123",
  "CustomerEmail": "aisha@example.test",
  "OccurredAtUtc": "2026-11-10T08:30:00Z"
}
```

Response - `201 Created`:

```json
{
  "eventId": "0f6c1d52-3b7a-4a55-9c53-0d6a1f1b0001",
  "externalTicketId": "QT-20261110-0001",
  "status": "Processed",
  "outcome": "TICKET_CREATED",
  "ticketId": 1042,
  "ticketNumber": "TG-CS-20261110-0007",
  "ticketStatus": "Open",
  "retryable": false
}
```

#### TicketAssigned

The employee `queue-user-a` called the customer to counter 3. The ticket is assigned to the mapped TigerCS employee and moves to `InProgress`.

Request body:

```json
{
  "EventId": "0f6c1d52-3b7a-4a55-9c53-0d6a1f1b0002",
  "EventType": "TicketAssigned",
  "ExternalTicketId": "QT-20261110-0001",
  "EventSequence": 2,
  "AssignedUserId": "queue-user-a",
  "PerformedByUserId": "queue-user-a",
  "CounterId": "counter-3",
  "OccurredAtUtc": "2026-11-10T08:34:00Z"
}
```

Response - `200 OK`:

```json
{
  "eventId": "0f6c1d52-3b7a-4a55-9c53-0d6a1f1b0002",
  "externalTicketId": "QT-20261110-0001",
  "status": "Processed",
  "outcome": "APPLIED",
  "ticketId": 1042,
  "ticketNumber": "TG-CS-20261110-0007",
  "ticketStatus": "InProgress",
  "retryable": false
}
```

#### TicketCommentAdded

Request body:

```json
{
  "EventId": "0f6c1d52-3b7a-4a55-9c53-0d6a1f1b0003",
  "EventType": "TicketCommentAdded",
  "ExternalTicketId": "QT-20261110-0001",
  "EventSequence": 3,
  "PerformedByUserId": "queue-user-a",
  "CounterId": "counter-3",
  "Comment": "Customer needs the title deed copy.",
  "OccurredAtUtc": "2026-11-10T08:36:00Z"
}
```

Response - `200 OK`:

```json
{
  "eventId": "0f6c1d52-3b7a-4a55-9c53-0d6a1f1b0003",
  "externalTicketId": "QT-20261110-0001",
  "status": "Processed",
  "outcome": "APPLIED",
  "ticketId": 1042,
  "ticketNumber": "TG-CS-20261110-0007",
  "ticketStatus": "InProgress",
  "retryable": false
}
```

#### TicketRecalled

Recorded in the ticket history; nothing else changes.

Request body:

```json
{
  "EventId": "0f6c1d52-3b7a-4a55-9c53-0d6a1f1b0004",
  "EventType": "TicketRecalled",
  "ExternalTicketId": "QT-20261110-0001",
  "EventSequence": 4,
  "PerformedByUserId": "queue-user-a",
  "CounterId": "counter-3",
  "Reason": "Customer did not respond to the first call",
  "OccurredAtUtc": "2026-11-10T08:38:00Z"
}
```

Response - `200 OK`:

```json
{
  "eventId": "0f6c1d52-3b7a-4a55-9c53-0d6a1f1b0004",
  "externalTicketId": "QT-20261110-0001",
  "status": "Processed",
  "outcome": "APPLIED",
  "ticketId": 1042,
  "ticketNumber": "TG-CS-20261110-0007",
  "ticketStatus": "InProgress",
  "retryable": false
}
```

#### TicketReturned

The owner is released and the ticket is `Open` again (queue-managed tickets only).

Request body:

```json
{
  "EventId": "0f6c1d52-3b7a-4a55-9c53-0d6a1f1b0005",
  "EventType": "TicketReturned",
  "ExternalTicketId": "QT-20261110-0001",
  "EventSequence": 5,
  "PerformedByUserId": "queue-user-a",
  "CounterId": "counter-3",
  "Reason": "Customer stepped away",
  "OccurredAtUtc": "2026-11-10T08:40:00Z"
}
```

Response - `200 OK`:

```json
{
  "eventId": "0f6c1d52-3b7a-4a55-9c53-0d6a1f1b0005",
  "externalTicketId": "QT-20261110-0001",
  "status": "Processed",
  "outcome": "APPLIED",
  "ticketId": 1042,
  "ticketNumber": "TG-CS-20261110-0007",
  "ticketStatus": "Open",
  "retryable": false
}
```

#### TicketReassigned

The employee is mapped and must be an active member of the ticket's department.

Request body:

```json
{
  "EventId": "0f6c1d52-3b7a-4a55-9c53-0d6a1f1b0006",
  "EventType": "TicketReassigned",
  "ExternalTicketId": "QT-20261110-0001",
  "EventSequence": 6,
  "AssignedUserId": "queue-user-b",
  "PerformedByUserId": "queue-user-a",
  "CounterId": "counter-5",
  "Reason": "Specialist required",
  "OccurredAtUtc": "2026-11-10T08:42:00Z"
}
```

Response - `200 OK`:

```json
{
  "eventId": "0f6c1d52-3b7a-4a55-9c53-0d6a1f1b0006",
  "externalTicketId": "QT-20261110-0001",
  "status": "Processed",
  "outcome": "APPLIED",
  "ticketId": 1042,
  "ticketNumber": "TG-CS-20261110-0007",
  "ticketStatus": "InProgress",
  "retryable": false
}
```

#### TicketClosed

Closed with resolution outcome `Resolved`.

Request body:

```json
{
  "EventId": "0f6c1d52-3b7a-4a55-9c53-0d6a1f1b0007",
  "EventType": "TicketClosed",
  "ExternalTicketId": "QT-20261110-0001",
  "EventSequence": 7,
  "PerformedByUserId": "queue-user-b",
  "CounterId": "counter-5",
  "Reason": "Served",
  "OccurredAtUtc": "2026-11-10T08:50:00Z"
}
```

Response - `200 OK`:

```json
{
  "eventId": "0f6c1d52-3b7a-4a55-9c53-0d6a1f1b0007",
  "externalTicketId": "QT-20261110-0001",
  "status": "Processed",
  "outcome": "APPLIED",
  "ticketId": 1042,
  "ticketNumber": "TG-CS-20261110-0007",
  "ticketStatus": "Closed",
  "retryable": false
}
```

#### TicketCancelled

A different queue ticket (`QT-20261110-0002`, created like the first one). Closed with resolution outcome `Cancelled`.

Request body:

```json
{
  "EventId": "0f6c1d52-3b7a-4a55-9c53-0d6a1f1b0011",
  "EventType": "TicketCancelled",
  "ExternalTicketId": "QT-20261110-0002",
  "EventSequence": 2,
  "PerformedByUserId": "queue-user-a",
  "CounterId": "counter-3",
  "Reason": "Customer left the branch",
  "OccurredAtUtc": "2026-11-10T08:45:00Z"
}
```

Response - `200 OK`:

```json
{
  "eventId": "0f6c1d52-3b7a-4a55-9c53-0d6a1f1b0011",
  "externalTicketId": "QT-20261110-0002",
  "status": "Processed",
  "outcome": "APPLIED",
  "ticketId": 1042,
  "ticketNumber": "TG-CS-20261110-0007",
  "ticketStatus": "Closed",
  "retryable": false
}
```

### Other answers

#### Duplicate delivery - the TicketClosed request above, sent again

The original outcome is returned; nothing is applied twice. No retry needed.

Response - `200 OK`:

```json
{
  "eventId": "0f6c1d52-3b7a-4a55-9c53-0d6a1f1b0007",
  "externalTicketId": "QT-20261110-0001",
  "status": "Duplicate",
  "outcome": "APPLIED",
  "ticketId": 1042,
  "ticketNumber": "TG-CS-20261110-0007",
  "ticketStatus": "Closed",
  "retryable": false
}
```

#### Stale event - sequence 3 arriving after the ticket is at sequence 7

Recorded in the audit trail, never applied. No retry needed.

Request body:

```json
{
  "EventId": "0f6c1d52-3b7a-4a55-9c53-0d6a1f1b0030",
  "EventType": "TicketAssigned",
  "ExternalTicketId": "QT-20261110-0001",
  "EventSequence": 3,
  "AssignedUserId": "queue-user-a",
  "CounterId": "counter-3",
  "OccurredAtUtc": "2026-11-10T08:35:00Z"
}
```

Response - `200 OK`:

```json
{
  "eventId": "0f6c1d52-3b7a-4a55-9c53-0d6a1f1b0030",
  "externalTicketId": "QT-20261110-0001",
  "status": "Superseded",
  "outcome": "STALE_EVENT",
  "ticketId": 1042,
  "ticketNumber": "TG-CS-20261110-0007",
  "ticketStatus": "Closed",
  "detail": "Sequence 3 is not newer than the applied sequence 7; the ticket was not changed.",
  "retryable": false
}
```

#### Event before its ticket exists

Stored and applied automatically once `TicketCreated` for `QT-20261110-0003` arrives. **Do not resend.**

Request body:

```json
{
  "EventId": "0f6c1d52-3b7a-4a55-9c53-0d6a1f1b0020",
  "EventType": "TicketAssigned",
  "ExternalTicketId": "QT-20261110-0003",
  "EventSequence": 2,
  "AssignedUserId": "queue-user-a",
  "OccurredAtUtc": "2026-11-10T08:31:00Z"
}
```

Response - `202 Accepted`:

```json
{
  "eventId": "0f6c1d52-3b7a-4a55-9c53-0d6a1f1b0020",
  "externalTicketId": "QT-20261110-0003",
  "status": "Pending",
  "outcome": "AWAITING_TICKET_CREATED",
  "detail": "No TigerCS ticket exists yet for queue ticket 'QT-20261110-0003'. The event is stored and will be applied when TicketCreated arrives.",
  "retryable": true
}
```

#### Validation failure

Nothing is stored. Fix the event.

Request body:

```json
{
  "EventId": "0f6c1d52-3b7a-4a55-9c53-0d6a1f1b0021",
  "EventType": "TicketAssigned",
  "ExternalTicketId": "QT-20261110-0004",
  "EventSequence": 2
}
```

Response - `400 Bad Request`:

```json
{
  "eventId": "0f6c1d52-3b7a-4a55-9c53-0d6a1f1b0021",
  "externalTicketId": "QT-20261110-0004",
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

#### Same EventId, different content (the TicketCreated request with a different `QueueNumber`)

A sender bug: events are immutable.

Request body:

```json
{
  "EventId": "0f6c1d52-3b7a-4a55-9c53-0d6a1f1b0001",
  "EventType": "TicketCreated",
  "ExternalTicketId": "QT-20261110-0001",
  "EventSequence": 1,
  "QueueNumber": "A-018",
  "CompanyId": "company-a",
  "TicketTypeId": "type-complaint",
  "CustomerName": "Aisha Khan",
  "CustomerPhone": "+971500000123",
  "CustomerEmail": "aisha@example.test",
  "OccurredAtUtc": "2026-11-10T08:30:00Z"
}
```

Response - `409 Conflict`:

```json
{
  "eventId": "0f6c1d52-3b7a-4a55-9c53-0d6a1f1b0001",
  "externalTicketId": "QT-20261110-0001",
  "status": "Conflict",
  "outcome": "EVENT_ID_REUSED",
  "ticketId": 1042,
  "detail": "EventId '0f6c1d52-3b7a-4a55-9c53-0d6a1f1b0001' was already received with different content. Events are immutable; send a new EventId for a new event.",
  "retryable": false
}
```

#### Missing or wrong API key

`401 Unauthorized` with header `WWW-Authenticate: ApiKey` and no body. Nothing is stored.


## 7. HTTP status codes and retry rules

| HTTP | `status` | Meaning | Sender must |
|---|---|---|---|
| `201` | `Processed` | The TigerCS ticket was created. | Mark sent. |
| `200` | `Processed` | Applied, or already in that state. | Mark sent. |
| `200` | `Duplicate` | This `EventId` was already received; the original outcome is returned. | Mark sent. |
| `200` | `Superseded` | Older than the applied state (`STALE_EVENT`); recorded, not applied. | Mark sent. Do not resend. |
| `200` | `Rejected` | Valid but not applicable (e.g. a state change after closure, `TICKET_ALREADY_CLOSED`). | Mark sent. Do not resend. |
| `202` | `Pending` | **Stored, not applied yet** (`AWAITING_TICKET_CREATED`, `AWAITING_PREVIOUS_EVENT`, `COMPANY_NOT_MAPPED`, `TICKET_TYPE_NOT_MAPPED`, `REQUEST_TYPE_UNUSABLE`, `USER_NOT_MAPPED`, `USER_INACTIVE`, `USER_NOT_IN_DEPARTMENT`, `CHANNEL_NOT_CONFIGURED`, `PRIORITY_NOT_CONFIGURED`, `SERVICE_ACCOUNT_NOT_CONFIGURED`, `CONCURRENT_UPDATE`, `PROCESSING_FAILED`, `TICKET_NOT_ELIGIBLE`). `retryable: true`. | Mark sent. **Do not resend** - TigerCS retries it. Alert the business when `outcome` is a mapping problem. |
| `400` | `Invalid` | Validation failed; `errors` names the members. Nothing stored. | Dead-letter and alert. Do **not** resend as is. |
| `401` / `403` | - | Missing or invalid API key. | **Stop sending, alert, keep every event.** Resume when the key is fixed. |
| `409` | `Conflict` | Same `EventId` already received with different content (`EVENT_ID_REUSED`). | Dead-letter and alert (a sender bug). |
| `503` | `Disabled` / `Busy` | The integration is switched off, or another request is changing the same ticket (`Retry-After: 2`). | Retry, **unchanged**, honouring `Retry-After`. |
| other `5xx`, `408`, `425`, `429`, network error, timeout | - | Transient. | Retry, **unchanged**. |
| any other `4xx` (404, 405, 415 ...) | - | The request is wrong (URL, content type). | Dead-letter and alert. |

**Retry rules**

1. **Resending is always safe**: TigerCS is idempotent on `EventId`. A retry must be **byte-for-byte the same event** - same `EventId`, `EventSequence`, `OccurredAtUtc` and content. Never build a new `EventId` for a retry.
2. Retry **only** network errors, timeouts, `408`, `425`, `429` and `5xx`. Never retry `200`, `201`, `202`, `400`, `401`, `403`, `409` or other `4xx`.
3. Backoff: exponential with jitter (default 1 s doubling, capped at 60 s), honouring `Retry-After`; at most `MaxAttempts` (5) HTTP attempts per call, then leave the event in the durable outbox and try again later (e.g. every minute, then every few minutes). Keep trying until it succeeds or is dead-lettered; an event must never be dropped.
4. Per queue ticket, do not send event *n+1* before event *n* has been delivered (or accepted).

The sender library implements exactly this (`TigerCsEventSender`, `SendDisposition`).

## 8. Reliability

- **Idempotency in the database.** `QueueingEvents` is unique on `(Source, EventId)`; `Tickets` has a filtered unique index on `(IntegrationSource, ExternalTicketId)`; `QueueingTicketLinks` is unique on `TicketId` and on `ExternalTicketId`. Duplicates and concurrent deliveries - on one node or many - can only ever produce one row; the loser is answered with the winner's result.
- **Atomic creation.** The ticket, its queue-state row, the customer contact, the history entry and the inbox row are written in **one database transaction**: the queue-state and inbox rows are staged inside ticket creation, immediately before its final save. There is no moment at which a ticket exists without its queue state, and **no later event is ever needed to repair an incomplete creation**; if anything fails, the whole creation rolls back (no ticket, no history, no SLA period, no outbox row) and the event stays stored for retry.
- **Atomic state changes.** For assignment + start of work, return, close and cancel, the integration's own state (queue state and inbox row) is staged on the same unit of work as the ticket change and commits in the very transaction of that change. A refused or failed change leaves the ticket **and** the queue state exactly as they were.
- **Out-of-order.** See section 4.
- **Unresolved events** are persisted and retried by a Hangfire job (`queueing-event-retry`, every minute, when `BackgroundJobs:Enabled`), only as the head of their queue ticket (section 4.8), or immediately by a redelivery of the same `EventId`. Each event is re-read before it is applied, so one event's failure never affects the rest of the batch. How a hold is retried depends on its class (the `outcome` code):

  | Class | Outcomes | Retry |
  |---|---|---|
  | **Ordering** | `AWAITING_PREVIOUS_EVENT` | No retry or attempt of its own: released, in order, when the earlier event settles (the sweep picks it up only if a race between two nodes orphaned it). |
  | **Configuration** (recoverable by setting something up, or by the missing creation event arriving) | `AWAITING_TICKET_CREATED`, `COMPANY_NOT_MAPPED`, `TICKET_TYPE_NOT_MAPPED`, `REQUEST_TYPE_UNUSABLE`, `USER_NOT_MAPPED`, `USER_INACTIVE`, `USER_NOT_IN_DEPARTMENT`, `CHANNEL_NOT_CONFIGURED`, `PRIORITY_NOT_CONFIGURED`, `SERVICE_ACCOUNT_NOT_CONFIGURED` | **Indefinitely**, exponential backoff (30 s doubling) **capped** at `MaxRetryDelaySeconds` (1 h). There is no terminal cutoff: the event waits for the configuration, and monitoring alerts on its age. |
  | **Transient** | `CONCURRENT_UPDATE` | Backoff, bounded by `MaxRetryAttempts`. |
  | **Unexplained** | `PROCESSING_FAILED`, `TICKET_NOT_ELIGIBLE`, anything else | Backoff, bounded by `MaxRetryAttempts` (12), then `Failed`. |

  A `Failed` event is **never retried automatically, never skipped and never restarted by a redelivery** (a sender that keeps resending cannot create a retry loop; the redelivery is answered `202`, `retryable: false`). Every later event of its queue ticket stays held behind it until an administrator reprocesses it (section 14).
- **Original timestamps.** While an event is applied, the application clock answers `OccurredAtUtc` (never later than now): ticket creation time, **SLA period start**, assignment time, status history, resolution time and audit entries carry when it happened in the queue. Retry scheduling and "processed at" use the real time. Nothing else is affected.

### 8.1 Payload retention

The payload of a **successfully processed** event (customer name, phone, email and the rest of the body) is erased after `QueueingIntegration:PayloadRetentionDays` (default **90**) by a daily job (`queueing-payload-retention`, 02:17 UTC). The row stays: `EventId`, `ExternalTicketId`, `EventType`, `EventSequence`, `OccurredAtUtc`, outcome, ticket link and a SHA-256 **payload hash** - so idempotency (including "same `EventId`, different content -> 409") and the ticket history are unaffected. **Unresolved, failed, superseded and rejected events are never purged by this job.** Zero or negative disables it.

## 9. SLA and history

- The SLA period opens at ticket creation with the default priority (`Normal`) and the request type's existing policy, **at `OccurredAtUtc`** - a delayed delivery is measured from when the customer actually took the ticket.
- **Assignment is not a first response**: nothing here calls the first-response recorder; `FirstHumanResponseAtUtc` stays empty after assignment.
- Closing as `Resolved` settles the Resolution clock; `Cancelled` does not (the existing rule for non-resolved outcomes).
- History: assignments -> `TicketAssignments`; status changes -> `TicketStatusHistory` (system actor for return and closure); comments -> `TicketNotes`; every applied event -> an audit entry carrying the event id, type, sequence, original time, counter and performer; creation, return and recall also appear on the ticket activity timeline.

## 10. Customer notifications and manual changes

- **No TigerCS customer email or SMS for a queue-managed ticket.** The acknowledgement is not even queued; every other customer email (assigned, in progress, resolved, closed, reopened) is **skipped with the recorded reason `ManagedByExternalSystem`**. TigerCS sends no customer SMS about tickets at all (SMS is used for OTP only). Internal "assigned to you" notices to staff are unchanged.
- **Manual changes in TigerCS are blocked** for queue-managed tickets: assign, reassign, transfer, take ownership, change status, resolve, close and reopen are refused with `409 ticket-managed-by-queueing-system`. **Comments / notes stay open.** TigerCS is a read-only mirror of the Queueing System, so this is **not configurable**: `QueueingIntegration:ManualChangePolicy` is still read (existing configuration keeps binding) but `Allow` is ignored. The block is enforced in three layers: the application services refuse with `409 ticket-managed-by-queueing-system`; every system closure path (`CloseForCustomerInactivity`, AI-only timeout, AI completion, document delivery) refuses a queue ticket; and every lifecycle / assignment mutator of the `Ticket` domain entity throws `QueueManagedTicketMutationException` unless the Queueing integration is applying one of its own events, so a job or service that forgot its own guard still cannot change a mirrored ticket. Queue-managed tickets are excluded from the unassigned-work reconcilers; a test enumerates every background job and every `Ticket` mutator and fails when a new one has not been reviewed against this rule.

## 11. Configuration keys

### 11.1 TigerCS (receiving side)

| Key | Meaning | Default |
|---|---|---|
| `QueueingIntegration:Enabled` | Master switch. `false`: the endpoint answers `503`. | `false` |
| `QueueingIntegration:ServiceAccountEmployeeId` | Employee id of the integration account (ticket creator, audit actor). Never an assignee. Also list it under `Authorization:ServiceIdentity:EmployeeIds`. | required |
| `QueueingIntegration:Authentication:HeaderName` | Header that carries the API key. | `X-Queueing-Api-Key` |
| `QueueingIntegration:Authentication:ApiKeys:<n>:KeyId` | Label of key *n* (appears in logs, never the key). | - |
| `QueueingIntegration:Authentication:ApiKeys:<n>:Sha256` | Hex SHA-256 of key *n*. | required |
| `QueueingIntegration:ChannelCode` | The channel queue tickets are recorded on. | `WALK_IN_KIOSK` |
| `QueueingIntegration:DefaultTicketPriority` | Starting priority, by name. | `Normal` |
| `QueueingIntegration:UserLookupMode` | `Mapping` (queue user -> `QueueingUserMappings`) or `EmployeeId` (the queue user id *is* the TigerCS employee id). | `Mapping` |
| `QueueingIntegration:ManualChangePolicy` | Ignored: manual lifecycle / assignment changes to a queue ticket are always blocked (section 10). | `Block` |
| `QueueingIntegration:PayloadRetentionDays` | Days a processed event's payload is kept (section 8.1). `<= 0` disables. | `90` |
| `QueueingIntegration:MaxRetryAttempts` | Attempts of a **bounded** failure (transient or unexplained, section 8) before the event is `Failed`. Configuration holds (a missing mapping / employee) are never bounded. | `12` |
| `QueueingIntegration:BlockedAlertAfterMinutes` | How long a queue ticket's oldest unresolved event may block it before monitoring reports an alert (a `Failed` head always alerts). | `15` |
| `QueueingIntegration:RetryBaseDelaySeconds` / `MaxRetryDelaySeconds` | Backoff start (doubles) and cap. | `30` / `3600` |
| `QueueingIntegration:MaxClockSkewSeconds` | How far in the future `OccurredAtUtc` may be. | `300` |
| `QueueingIntegration:MaxCommentLength` | Longest `Comment`. | `4000` |
| `BackgroundJobs:Enabled` | Runs the retry sweep and the retention job. | existing setting |

Set secrets through environment variables (`QueueingIntegration__ServiceAccountEmployeeId`, `QueueingIntegration__Authentication__ApiKeys__0__Sha256`). Nothing (ids, keys, statuses) is hard-coded.

### 11.2 Queueing System (sending side) - `TigerCs` section

| Key | Meaning | Default |
|---|---|---|
| `TigerCs:BaseUrl` | TigerCS API address, e.g. `https://tigercs.example.com`. | required |
| `TigerCs:ApiKey` | The key TigerCS issued (secret: `TigerCs__ApiKey`). | required |
| `TigerCs:ApiKeyHeaderName` | Header that carries the key. | `X-Queueing-Api-Key` |
| `TigerCs:RequestTimeoutSeconds` | Timeout of one HTTP attempt. | `30` |
| `TigerCs:MaxAttempts` | HTTP attempts per `SendAsync` call before `RetryLater`. | `5` |
| `TigerCs:BaseDelayMilliseconds` | First backoff; doubles, with jitter. | `1000` |
| `TigerCs:MaxDelaySeconds` | Longest backoff (also caps `Retry-After`). | `60` |

### 11.3 One-time mapping setup on TigerCS

1. Deploy `AddQueueingIntegration.sql`.
2. Create the integration account (a CS Agent employee with no department assignment) and put its id in both settings above.
3. Add the mappings (look the ids up; the unique indexes reject duplicates). **Onboarding a company is one row** - the company mapping. A ticket-type row is only an optional override; a new Queueing service needs nothing:

```sql
-- Company -> its TigerCS department. MANDATORY: an event of a company with no active row here is held (never guessed). Every ticket type of the company with no
-- override below is created in this department and classified with the department's own General request type.
INSERT INTO QueueingCompanyMappings (CompanyId, DepartmentId, IsActive, CreatedAtUtc)
VALUES (N'company-a', (SELECT DepartmentId FROM Departments WHERE Code = N'CS'), 1, SYSUTCDATETIME());

-- OPTIONAL override: (company, ticket type) -> request type. The request type's department IS the ticket's department, so one company can span several departments.
-- The request type must be active, have a Published workflow and exactly one active same-named category. Without a row, the company's department and its
-- General request type are used.
INSERT INTO QueueingTicketTypeMappings (CompanyId, TicketTypeId, RequestTypeId, IsActive, CreatedAtUtc)
VALUES (N'company-a', N'type-complaint',
        (SELECT RequestTypeId FROM RequestTypes WHERE Name = N'Complaint' AND DepartmentId = (SELECT DepartmentId FROM Departments WHERE Code = N'CS')),
        1, SYSUTCDATETIME());

-- Queue employee -> TigerCS employee (one-to-one). The employee must be an ACTIVE member of the ticket's department.
INSERT INTO QueueingUserMappings (ExternalUserId, EmployeeId, IsActive, CreatedAtUtc)
VALUES (N'queue-user-a', (SELECT Id FROM AspNetUsers WHERE UserName = N'jane.doe'), 1, SYSUTCDATETIME());
```

   Before mapping a company, run **`Verify_QueueingDepartmentReadiness_ReadOnly.sql`** (read-only): it confirms the department is active and not Collections, has exactly one active `General` category and one `General` request type with a Published workflow, and that General carries **no approval requirement and no request-type SLA row** (an SLA row would override the queue priority's SLA). It also lists every explicit override and the events currently held for a mapping reason.

4. *Optional, Tiger Properties only* (`CompanyId = 4`) ticket types 53-60 - explicit overrides, no longer a prerequisite for the company to work: run `ApplyQueueingTigerPropertiesMappings.sql` (idempotent data script, preview by default, validates every request type / department / published workflow before writing). It is **not** a migration - `AddQueueingIntegration.sql` is unchanged and no schema change is needed, because the department is derived from the request type.

   | TicketTypeId | RequestTypeId | DepartmentId |
   |---|---|---|
   | 53 | 1 | 1 |
   | 54 | 3 | 1 |
   | 55 | 2 | 1 |
   | 56 | 21 | 2 |
   | 57 | 17 | 1 |
   | 58 | 14 | 1 |
   | 59 | 20 | 3 |
   | 60 | 40 | 1 |

   The same script sets the company mapping: **CompanyId `4` -> DepartmentId `1` (Customer Service, `CS`)**; ticket types outside 53-60 use the department's General request type. Before writing, it confirms department 1 is active and has exactly one active `General` category and one active `General` request type with a Published workflow, and aborts without changes otherwise.

5. Verify with folder 2 of the Postman collection.

## 12. Verification

Run on 2026-11-17 against a **real SQL Server 2022** (Developer, native Linux install) with `TIGERCS_TEST_SQL_SERVER` set:

| Suite | Result |
|---|---|
| Build (`dotnet build src/TigerCS.slnx -c Release`) | 0 errors, 0 warnings |
| TigerCS - full suite (`dotnet test src/TigerCS.slnx`) | **8,915 passed, 0 failed, 56 skipped** |
| Queueing sender (`dotnet test queueing-system/QueueingSystem.Sender.slnx`) | **40 passed, 0 failed** |

- The 56 skipped tests are all `TIGERCS_PERF_SQL` performance tests (a separate gate that CI does not enable); **none** is skipped for lack of SQL Server, which is what CI's "fail if the SQL Server integration tests were skipped" step checks.
- Queueing tests: 80 in total - 71 on the InMemory host and at unit level (creation, duplicate creation, assignment / reassignment, return to Open, comment, recall, close, cancel, missing mappings, General fallback, mandatory phone, duplicate / out-of-order / concurrent events, unauthorised requests, SLA start at `OccurredAtUtc`, history, notification suppression, manual-change policy, retention, retry-sweep isolation, every sample in this document), **plus 9 on real SQL Server** (`QueueingSqlServerTests`): the shipped migrations create the unique indexes; duplicate creation across two application nodes; 24 concurrent deliveries of one event, and of one queue ticket under different EventIds, across two nodes -> exactly one ticket, one queue-state row, one inbox row; concurrent state events across two nodes settle on the newest with one current owner; **transaction rollback** - a creation that fails at its last step leaves no ticket, history, SLA period, outbox row or audit entry and then succeeds on its own retry; a return and a closure that fail at commit leave the ticket owned / unclosed with its history untouched; retention on the real schema.
- The real-SQL-Server tests found one defect the InMemory tests could not (a failed event in a retry batch detached the later events from the unit of work, losing their "processed" mark); it is fixed and has a deterministic regression test that fails against the old behaviour.
- CI (`.github/workflows/ci.yml`) runs the TigerCS suite with SQL Server and the sender solution as a separate job; `db-migration-validation.yml` pins the new tables and columns.

## 13. Decisions

**Applied** (confirmed by the business): (1) `TicketReturned` releases the employee and returns the ticket to `Open`, for queue-managed tickets only; (2) an unmapped ticket type of a mapped company uses the department's General request type, logged; the department is never guessed; (3) SLA starts at the original `OccurredAtUtc`; (4) `CustomerPhone` and `CustomerEmail` are both optional on `TicketCreated` (no placeholder is ever generated; only the Queueing endpoint is relaxed - every other ticket source keeps its own validation); (5) manual assignment, status change, close and reopen stay blocked, comments allowed; (6) 90-day configurable payload retention that keeps identifiers and history and never deletes unresolved events; (7) no TigerCS customer SMS/email for queue tickets; (8) `TicketClosed` = Resolved, `TicketCancelled` = Cancelled.

**Still open**

| # | Decision | Current behaviour |
|---|---|---|
| D4 | Department settings that disable assignment (`AllowAssignment = false`). | Honoured: the event is held (`TICKET_NOT_ELIGIBLE`). Alternative: let the integration bypass it. |
| D6 | Automatic customer match. | The lookup only enriches the name/audit; a ticket is never auto-verified against a CRM customer (same rule as Genesys). |
| D7 | Reopen of a closed queue ticket. | **Supported, as a pure mirror of what the Queueing System actually does.** A `TicketAssigned` / `TicketReassigned` with a newer sequence on a Closed (Resolved or Cancelled) queue ticket takes the *same* ticket back into service: Closed -> Open (system status-history row, audit `QueueingReopen`, no approval or reopen window), then the normal assignment takes it to `InProgress`. It is **not** a business reopen: no person decides it, and the dashboard "Reopened" count does not include queue tickets. `ReopenCount` is incremented because it is the ticket's cycle key (closure notifications, review cycles and their idempotency keys are all keyed by it - without it the second closure would collide with the first); the original SLA cycle and its results are kept, a **separate SLA reopen cycle** is opened, and the earlier resolution row is archived, never deleted. An unmapped employee is checked first and leaves the ticket Closed (`USER_NOT_MAPPED`, held and retried; later events wait behind it). A `TicketClosed` on a Cancelled ticket changes its outcome Cancelled -> Resolved (section 1); `TicketReturned` / `TicketCancelled` / a repeated `TicketClosed` on a Closed ticket stay `Rejected`. |
| D8 | Mapping administration UI. | Tables + SQL only. |
| D10 | `Failed` events need a person. | **Audited administrator reprocess** (section 14) - the only way to restart a `Failed` event; nothing is ever skipped. Monitoring and a log alert report every blocked ticket. |
| D12 | Rate limiting / lockout of the API key. | A wrong key is a plain `401`; limit it at the network gateway. |
| D13 | Retention of superseded / rejected events' payloads. | Kept (the 90-day rule covers *successfully processed* events only, as decided). Extend the rule if those payloads should also go. |
| D14 | A company mapped to the Call Center department. | Such a ticket starts `InProgress` (the existing Call Center rule) and unassigned. Avoid mapping a queue company to it. |

## 14. Monitoring

`GET /api/integrations/queueing/monitoring?take=50` - read-only, authenticated with the same `X-Queueing-Api-Key`. Returns `countsByStatus` (Received, Processed, Unresolved, Superseded, Rejected, Failed), `heldOrFailedByOutcome` (e.g. `USER_NOT_MAPPED`, `COMPANY_NOT_MAPPED`), `oldestUnresolvedReceivedAtUtc` and an `attention` list of the `Unresolved` / `Failed` events (ids, outcome, detail, attempts, next retry - never customer data). Alert when `Failed > 0`, or when `oldestUnresolvedReceivedAtUtc` is older than a few minutes (a mapping or employee is missing). The Queueing System reports its own side (pending / dead-lettered outbox rows) at `GET /TigerCsIntegration/GetMonitoring`.

The snapshot also reports the **blocked queue tickets** (a ticket is blocked while it has an unresolved state-changing event; every later event waits behind it, section 4.8): `blockedTicketCount`, `alertCount`, `oldestBlockedAgeSeconds` and `blockedTickets` - one row per ticket described by its head event (`headEventId`, `headEventSequence`, `headStatus`, `headOutcomeCode`, `failureClass` = Ordering / Configuration / Transient / Unexplained, `attemptCount`, `ageSeconds`, `heldBehind`, `alert`). `alert` is true when the head has been blocked longer than `QueueingIntegration:BlockedAlertAfterMinutes` (15) or has `Failed`. The `queueing-event-retry` job also logs a warning (`Queueing integration ALERT: ...`, counts, age and outcome code only) every minute while any ticket is alerting, so a log-based alert can fire without polling the endpoint.

### 14.1 Administrator reprocess

`POST /api/admin/queueing/events/{eventId}/reprocess` - System Administrator with `Administration.Configuration.Edit`, staff sign-in (not the Queueing API key). For a **Failed** event, or an **Unresolved** one whose cause was just fixed: it is written to the audit trail first (`QueueingEventReprocessed`: the administrator, the event, its status / outcome / attempts before), then the retry cycle restarts (attempts reset; the payload and `EventId` never change), the event is applied again under the ticket's lock and, if it settles, the events held behind it are applied in sequence order. `404` for an unknown event; `409` when the event is neither Failed nor Unresolved. There is deliberately **no skip**: an event that cannot be applied stays blocking until its cause is fixed. Reprocessing sends nothing to the Queueing System.
