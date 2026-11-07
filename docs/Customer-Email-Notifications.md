# Customer Email Notifications

**Status: implemented (full lifecycle).** Customer-facing email for the ticket lifecycle,
delivered through the Microsoft 365 SMTP account and the existing
transactional Outbox / Hangfire dispatcher (ADR-0013, ADR-0015). Ships
**switched off**; UAT/Production enable it by configuration only.

## 1. What is sent, and when

**Customer emails** — one per meaningful lifecycle moment, whoever performs the action (a CS Agent, Supervisor or Manager in TigerCS,
a department employee, the Genesys AI, or an automatic rule):

| Event | Trigger (every path that performs it) | Subject | Notification type |
|---|---|---|---|
| Ticket created | `POST /api/tickets`, `POST /api/genesys/tickets` (`TicketCreationAppService`) | `Your request has been received – Ticket {TicketNumber}` | `Acknowledgement` |
| Ticket assigned | The ticket gains an owner it did not have: manual `POST /api/tickets/{id}/assignment`, a transfer with an assignee, Take Ownership, a human agent accepting a handoff, a routing rule, Round Robin, Collections tower routing, the answering Genesys agent | `Your request has been assigned – Ticket {TicketNumber}` | `Assigned` |
| In progress | `POST /api/tickets/{id}/status` to `InProgress` (first time, and again after a waiting period) | `We are working on your request – Ticket {TicketNumber}` | `InProgress` |
| Waiting for customer | `POST /api/tickets/{id}/status` to `PendingCustomer` | `We need your input – Ticket {TicketNumber}` | `PendingCustomer` |
| Ticket resolved | `POST /api/tickets/{id}/resolution` (outcomes Resolved, Rejected, Duplicate) | `Your Ticket #{TicketNumber} Has Been Resolved` | `Resolved` |
| Ticket cancelled | `POST /api/tickets/{id}/resolution` with outcome `Cancelled` | `Your request has been cancelled – Ticket {TicketNumber}` | `Cancelled` |
| Ticket closed | `POST /api/tickets/{id}/close`; Genesys `closedByAi`; document-delivery closure; the chatbot customer-inactivity closure; the AI-only 18-hour auto-close | `Your Ticket #{TicketNumber} Has Been Closed` | `Closed` |
| Ticket reopened | `POST /api/tickets/{id}/reopen` | `Your request has been reopened – Ticket {TicketNumber}` | `Reopened` |

**Internal (staff) emails**, delivered through the same Outbox and SMTP account, never redirected by the customer test mode:

| Event | Trigger | Recipient | Notification type |
|---|---|---|---|
| Ticket assigned / transferred to you | Any assignment made by somebody other than the assignee (a person or automation). A self-assignment (Take Ownership, accepting a handoff, answering the call) tells nobody. | The new owner's account email (`AspNetUsers.Email`) | `AgentAssigned` |
| Customer reply received | A Genesys transcript delivery appends customer lines to a conversation of a ticket a person is handling (InProgress / PendingCustomer, or a human agent holds the conversation) | The ticket's owner at the time | `CustomerReplyReceived` |

Every email carries the customer's name (when one is known and valid), the ticket number, the ticket subject (request type, category or
the first line of the summary), the current status, the date, the Tiger Properties header/footer/signature, a plain-text body and a
responsive HTML body, in English or Arabic according to the customer's name script. The "closed" email always states the outcome
(`Resolved`, `Cancelled`, …) and explains an automatic closure: the inactivity rule says that no reply was received, the AI-only
18-hour rule says the request was handled by the virtual assistant and closed automatically after no further activity (the `TicketClosed`
payload carries a `closureKind` — `AiCompletion`, `AiOnlyTimeout`, `CustomerInactivity`, `DocumentDelivery` — for this). Staff emails carry a **Ticket Details link**
(`{CustomerNotifications:PublicBaseUrl}/Tickets/{id}`). Customer emails carry no ticket URL: Ticket Details is a staff-only page and
there is no customer portal (ISSUE-021); the only public page is the satisfaction review.

**Never rendered:** database ids, employee ids, queue/workflow ids, SLA state, escalation level, internal ticket notes, the customer's
contact details (in staff mail). Agent-written text — the resolution note and the "waiting for customer" reason — is included **only**
when `EmailNotifications:IncludeResolutionNote` is `true` (default `false`).

### Coalescing: one email for where the ticket is now

Events are recorded at the moment of the action; the email is rendered when the dispatcher runs (polling interval
`Notifications:Outbox:PollIntervalMinutes`). If the ticket has already moved past the state an event announces, that email is
**skipped** (reason `SupersededByLaterUpdate` / `SupersededByClosure`) and the later state's email is the one sent:

- Resolved then Closed within the window → one "closed" email, carrying the outcome. A system closure (AI, document delivery,
  inactivity) goes straight to Closed in one write and records only the Closed event in the first place.
- Assigned then started / waiting / resolved → only the latest.
- Waiting for customer, then the customer replied (back to InProgress) → only "we are working on your request".
- Reopened then closed again → only the closure.
- Assigned while an earlier assignment is still queued → only the current owner's.

The acknowledgement is always sent; a resolution or closure from an earlier cycle that was reopened later is still sent (it happened).

### Events intentionally *not* emailed

- **Reassignment between colleagues** is internal routing: the customer still has "someone on it"; only the new assignee is told.
- **Assignment in the creation transaction** (a rule or Round Robin pre-assignment at creation): the acknowledgement covers it.
- **Customer activity signals** (`customerActive`, `awaitingCustomerReply`) without transcript lines; AI-chat lines while the ticket is
  still Open and nobody is handling it.
- **Classification, priority changes, approvals, notes, attachments, SLA warnings/breaches/escalations**, department transfers that
  keep the owner, and Genesys `PATCH` updates that change none of the above.

## 2. Architecture (how it fits the existing code)

```
TicketCreationAppService  ─┐                                  ┌─ TicketAcknowledgementHandler
TicketLifecycleAppService  │  TicketNotificationEvents        │  TicketAssignedNotificationHandler
  ChangeStatus / Resolve / │  (IOutboxWriter + audit,         │  TicketInProgressNotificationHandler
  Close / Reopen / system  │   same transaction)              │  TicketPendingCustomerNotificationHandler
TicketAssignmentAppService ├──────────┐                       │  TicketResolvedNotificationHandler (Resolved | Cancelled)
TicketAutoAssignmentService│          ▼                       │  TicketClosedNotificationHandler
TicketClaim / AgentHandoff │   OutboxMessages ── Hangfire ────┤  TicketReopenedNotificationHandler
CollectionsTicketRouting   │   (Pending)   OutboxDispatchJob  │        (CustomerTicketEmailHandler base: idempotent, coalescing)
GenesysConversationEnd    ─┘                                  │  AgentAssignedNotificationHandler, CustomerReplyNotificationHandler
  (transcript → customer reply)                               │        (InternalTicketEmailHandler base: staff account email)
                                                              │              │
                                                              │   CustomerContactResolver  (name + email + language from ticket data)
                                                              │   CustomerEmailTemplates / InternalEmailTemplates
                                                              │              ▼
                                                              │   ICustomerEmailSender  →  CustomerEmailSender (customer mail)
                                                              │   IEmailSender (Audience=Internal, staff mail)
                                                              │              ▼
                                                              │   IEmailSender  →  SmtpEmailSender (Microsoft 365 SMTP)
                                                              └                 →  RecordingEmailSender (tests / dev only)
```

`TicketNotificationEvents` is the one place an operation records "somebody should hear about this". Every path that performs the same
action (a person in TigerCS, Genesys, a job) calls it, so the rules — which transitions are customer-visible, when an assignment is
news, when a customer reply is worth an email — are written once.

- **Reliability / transaction behaviour.** The lifecycle services write the
  Outbox row inside the same database transaction as the state change. A
  rolled-back create/resolve/close/reopen leaves no event, so a customer can
  never receive an email for an operation that failed. Delivery happens later
  in the Outbox dispatch job, so an SMTP outage can never fail or roll back a
  ticket operation. Transient SMTP failures are retried with backoff
  (`Notifications:Outbox`, default 5 attempts); permanent ones are
  dead-lettered and audited.
- **Idempotency.** Each occurrence has one idempotency key
  (`Ticket:{id}:{EventType}:v1:cycle{ReopenCount}` for the once-per-cycle
  events; `…:cycle{n}:{occurrence}` for assignments, status changes and
  customer replies, where the occurrence names the assignee/previous owner,
  the transition, or the transcript position), one `Notifications` row per
  `(OutboxMessageId, NotificationType)` (unique index), and the
  acknowledgement additionally guards on `Ticket.AcknowledgementSentAtUtc`.
  A retried API call, a redelivered Genesys event (the transcript append is
  itself idempotent and appends nothing new) and a redelivered Outbox message
  never send twice. Assigning a ticket to its current owner again, or a
  refused status transition, records no event at all.
- **Recipient trust.** The address comes only from persisted ticket data —
  never from a request parameter, so no API caller can direct a notification
  to an arbitrary mailbox.

### Where the customer email comes from

No new column was added. Two existing fields are consulted, in order:

1. `TicketRequesterSnapshot.SnapshotContactChannel` — the CRM-verified
   contact captured at creation. It is an untyped phone-or-email value and is
   used only when it is unambiguously a single bare email address.
2. `TicketInteraction.CustomerEmail` — the email Genesys supplied with the
   inquiry (originating interaction first, then the latest interaction that
   carries one).

The greeting name comes from `SnapshotContactDisplayName`, then
`Ticket.CrmBuyerCustomerName`, then the interaction's `CustomerName`.

## 3. Skip / failure behaviour

| Situation | Ticket operation | Notification row | Outbox message | Audit action |
|---|---|---|---|---|
| `EmailNotifications:Enabled = false` | succeeds | `Skipped` (`NotificationsDisabled`) | `Processed` | `NotificationSkipped` |
| No email on the ticket (phone-only customer, unverified ticket) | succeeds | `Skipped` (`NoCustomerEmail`) | `Processed` | `NotificationSkipped` |
| Value present but not a valid address | succeeds | `Skipped` (`InvalidCustomerEmail`) | `Processed` | `NotificationSkipped` |
| Event older than `MaxNotificationAgeHours` when first handled | succeeds | `Skipped` (`EventTooOld`) | `Processed` | `NotificationSkipped` |
| Ticket already moved past the announced state (see *Coalescing*) | succeeds | `Skipped` (`SupersededByLaterUpdate` / `SupersededByClosure`) | `Processed` | `NotificationSkipped` |
| Staff email: the assignee's account has no email / no longer exists | succeeds | `Skipped` (`NoAgentEmail` / `AgentNotFound`) | `Processed` | `NotificationSkipped` |
| SMTP transient failure (service unavailable, busy, socket/TLS) | succeeds | `Failed`, retried | `Pending`, backoff | `NotificationDeliveryFailed` / `NotificationRetryScheduled` |
| SMTP permanent failure (mailbox rejected, auth refused) | succeeds | `DeadLettered` | `DeadLettered` | `NotificationDeadLettered` |
| Sent | succeeds | `Sent` | `Processed` | `NotificationDeliverySucceeded` |

`Skipped` is deliberately distinct from `DeadLettered`: most phone-only
tickets have no email, and that expected outcome must not inflate the
dead-letter count operators are alerted on.

**Logging.** One line per attempt from `CustomerEmailSender`: notification
type, ticket number, masked recipient (`a***@example.com`), outcome, reason,
correlation id. `SmtpEmailSender` logs the exception type and SMTP status
code. Never logged or persisted: the SMTP password, the subject, the body,
or an unmasked address.

## 4. Configuration keys

Section `EmailNotifications` (bound to `EmailNotificationOptions`):

| Key | Default | Notes |
|---|---|---|
| `Enabled` | `false` | Master switch. `false` = nothing is sent; operations unchanged. |
| `Provider` | `Smtp` | `Smtp` (Microsoft 365) or `Recording` (in-memory; Development/Testing only). |
| `SmtpHost` | `smtp.office365.com` | |
| `SmtpPort` | `587` | |
| `EnableSsl` | `true` | STARTTLS on 587. |
| `Username` | — | `no_reply_tiger@tigergroup.ae` |
| `Password` | — | **Secret. Never in a committed file.** |
| `FromEmail` | — | `no_reply_tiger@tigergroup.ae` |
| `FromName` | `Tiger Properties` | |
| `TimeoutSeconds` | `30` | SMTP send timeout (minimum 5). |
| `MaxNotificationAgeHours` | `24` | Stale-backlog guard; `0` disables. |
| `IncludeResolutionNote` | `false` | Quote the agent's resolution note in the "resolved" email. |

The dispatcher itself is configured by the existing `BackgroundJobs:Enabled`
and `Notifications:Outbox` (`MaxAttempts`, `BaseRetryDelay`, `BatchSize`,
`PollIntervalMinutes`) sections — **customer emails are only delivered while
`BackgroundJobs:Enabled` is `true`** (docs/DEV-SETUP.md §9). With it `false`,
Outbox rows accumulate as `Pending`; the `MaxNotificationAgeHours` guard then
skips anything older than a day once the job is switched on.

Shipped `src/TigerCS.Api/appsettings.json`:

```json
"EmailNotifications": {
  "Enabled": false,
  "Provider": "Smtp",
  "SmtpHost": "smtp.office365.com",
  "SmtpPort": 587,
  "EnableSsl": true,
  "Username": "no_reply_tiger@tigergroup.ae",
  "Password": "",
  "FromEmail": "no_reply_tiger@tigergroup.ae",
  "FromName": "Tiger Properties",
  "MaxNotificationAgeHours": 24,
  "IncludeResolutionNote": false
}
```

## 5. Supplying the SMTP secret (UAT / Production)

The password is read through ordinary configuration binding, so any standard
ASP.NET Core secret source works. Use **environment variables** on the host
(or the platform's secret store mapped to them), with `__` as the section
separator:

```bash
EmailNotifications__Enabled=true
EmailNotifications__Password='<the no_reply_tiger mailbox password>'
# Only if they differ from appsettings.json:
# EmailNotifications__Username=...
# EmailNotifications__FromEmail=...
BackgroundJobs__Enabled=true          # the dispatcher must run
```

Startup refuses to run (with a message naming the missing *key*, never a
value) when `Enabled = true` with `Provider = Smtp` and any of `SmtpHost`,
`Username`, `Password` or `FromEmail` is missing, or when `Provider =
Recording` is enabled outside Development/Testing.

Local development: `dotnet user-secrets set "EmailNotifications:Password" "..."`
from `src/TigerCS.Api`, or keep the shipped Development override
(`Provider = Recording`, `Enabled = true`) which logs deliveries and sends
nothing.

Requirements on the Microsoft 365 side (outside this repository): the
`no_reply_tiger@tigergroup.ae` mailbox must have **Authenticated SMTP
(SMTP AUTH)** enabled and, if the tenant enforces MFA/security defaults, an
app password or a mailbox exempt from that policy. An authentication
refusal surfaces as a permanent failure (`SMTP ClientNotPermitted`) in
`OutboxMessages.LastError`.

## 6. Disabling customer notifications

Set `EmailNotifications__Enabled=false` (or omit the section — `false` is the
default) and restart. Every ticket operation continues unchanged; queued
events are recorded as `Skipped` with reason `NotificationsDisabled` and are
never sent later. To stop delivery *and* stop the Outbox from being drained,
set `BackgroundJobs__Enabled=false` as well.

## 7. Ticket Details timeline

Every notification step is audited against the ticket (`AuditEntries.EntityType = 'Notification'`, `EntityId = TicketId`) and the
Activity timeline (`GET /api/tickets/{id}/activity-timeline`, category `Notification`) lists them:

| Timeline event | Audit action | When |
|---|---|---|
| **Email queued** | `NotificationQueued` | In the operation's own transaction (names the Outbox message) |
| **Email sent** | `NotificationDeliverySucceeded` | The provider accepted the message |
| **Email failed** | `NotificationDeliveryFailed` (retry due) / `NotificationDeadLettered` (final) | Provider failure |
| **Email skipped** | `NotificationSkipped` | Deliberately not sent; the entry shows the reason in plain words |

Each entry names the notification ("ticket resolved", "assigned to agent", …) and whether it is customer or staff mail. Addresses are
never in the audit trail and so never on the page.

## 8. Extending later (other channels)

- **A new customer email** = a method on `TicketNotificationEvents` (or a
  call to an existing one) from the owning app service, a template method on
  `CustomerEmailTemplates`, and a ~20-line subclass of
  `CustomerTicketEmailHandler` registered as an `IOutboxEventHandler` — with a
  `SupersededReason` if a later state should win. No change to the sender, the
  SMTP adapter, the resolver or the dispatcher. A new staff email follows the
  same pattern on `InternalTicketEmailHandler`.
- **Another provider** (Graph API, a relay) = another `IEmailSender`
  implementation and a `Provider` value.
- **Another channel** (SMS) = a sibling of `ICustomerEmailSender`;
  `NotificationChannel` already reserves the enum.

## 9. UAT verification steps

1. Configure UAT as in §5 (`Enabled=true`, password via environment,
   `BackgroundJobs__Enabled=true`). Confirm the API starts; a missing key
   fails startup with an explicit message.
2. Create a ticket for a CRM contact whose contact channel is an email
   address you control (or via `POST /api/genesys/tickets` with
   `customerEmail`). Within `Notifications:Outbox:PollIntervalMinutes`
   (default 1) the acknowledgement arrives with the ticket number, name,
   request type and date; `Tickets.AcknowledgementSentAtUtc` is set.
3. Assign (wait for a dispatch) → "assigned" email, and the assignee receives
   "Ticket … assigned to you" with a Ticket Details link. Start work → "we are
   working on your request". Set Pending Customer → "we need your input".
   Resolve → "resolved". Close → "closed" (with the outcome). Reopen →
   "reopened". One email per transition **when each is dispatched before the
   next happens**; transitions made within one polling interval collapse to the
   email for the latest state (the earlier ones show as *Email skipped* in the
   ticket's Activity timeline). A Genesys `closedByAi` or the chatbot
   inactivity closure produces the single "closed" email.
4. Create a ticket for a phone-only contact → no email;
   `Notifications.DeliveryStatus = 5 (Skipped)`, `AuditEntries.Action =
   NotificationSkipped` with `Reason=NoCustomerEmail`; the ticket operations
   all succeed.
5. Negative path: temporarily set a wrong password → ticket operations still
   succeed; the notification dead-letters with `SMTP ClientNotPermitted` in
   `OutboxMessages.LastError` and no secret in any log line. Restore the
   password.
6. SQL spot-checks:
   ```sql
   SELECT EventType, Status, Attempts, LastError FROM OutboxMessages ORDER BY OccurredAtUtc DESC;
   SELECT TicketId, NotificationType, DeliveryStatus, RecipientAddress, RetryCount FROM Notifications ORDER BY CreatedAtUtc DESC;
   SELECT Action, EntityId, AfterValue FROM AuditEntries WHERE Action LIKE 'Notification%' ORDER BY OccurredAtUtc DESC;
   ```
   `NotificationType`: 1 Acknowledgement, 5 Resolved, 6 Closed, 7 Reopened,
   8 Assigned, 9 InProgress, 10 PendingCustomer, 11 Cancelled,
   12 AgentAssigned (staff), 13 CustomerReplyReceived (staff).
   `DeliveryStatus`: 1 Pending, 2 Sent, 3 Failed, 4 DeadLettered, 5 Skipped.

No database migration is required by this feature: the new enum values map
onto the existing `tinyint` columns and EF reports no pending model changes.
