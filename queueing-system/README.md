# Queueing System - sending side

A small, self-contained .NET library that sends the Queueing System's events to TigerCS (`POST /api/integrations/queueing/events`). It is a **separate solution**
(`QueueingSystem.Sender.slnx`) and references nothing from TigerCS: the only coupling is the HTTP contract in
[`docs/QueueingIntegration`](../docs/QueueingIntegration/Queueing-Integration.md) and the sample JSON files there, which this project's tests also check.

| Piece | What it is |
|---|---|
| `QueueingEventType` | The eight events, serialised by name. |
| `QueueingEventRequest` | The request DTO (PascalCase JSON) with `Validate()` - the same required-member table TigerCS enforces - and `ToJson()` - the exact bytes on the wire. |
| `QueueingEventResponse` / `QueueingResultStatus` | The response DTO. |
| `IEventSequenceProvider` / `QueueingEventFactory` | Builds events that satisfy the contract: fresh EventId, the queue ticket's next `EventSequence`, `OccurredAtUtc`, the required members. |
| `TigerCsEventSender` | Sends one event: API-key header, the retry rules, and a `SendDisposition` telling the caller what to do next. |
| `TigerCsSenderOptions` | Configuration (`TigerCs` section). |

## Wiring it into the Queueing System

```csharp
services.Configure<TigerCsSenderOptions>(configuration.GetSection(TigerCsSenderOptions.SectionName));
services.AddSingleton(sp => sp.GetRequiredService<IOptions<TigerCsSenderOptions>>().Value);
services.AddHttpClient<TigerCsEventSender>();                       // the HttpClient is resolved from the factory
services.AddSingleton<IEventSequenceProvider, YourDurableSequenceProvider>();   // see below - NOT the in-memory one
services.AddSingleton<QueueingEventFactory>();
```

(`TigerCsEventSender` takes `HttpClient` + `TigerCsSenderOptions`; register it however your host prefers.)

## The three things the Queueing System must get right

1. **Record the event when it happens, send it later, resend it unchanged.** Build the event with `QueueingEventFactory` inside the queue's own transaction and store it in an
   outbox table in that same transaction. A background worker reads the outbox, calls `TigerCsEventSender.SendAsync`, and acts on the disposition (below). An event is
   *never rebuilt* for a resend - same `EventId`, same `EventSequence`, same `OccurredAtUtc`, same content.
2. **`EventSequence` must be durable.** `IEventSequenceProvider.NextAsync` must return 1, 2, 3 ... per queue ticket, incremented in the same transaction that records the event, and
   never repeat or restart. `InMemoryEventSequenceProvider` exists for tests only.
3. **Send each queue ticket's events in order, one at a time** (different queue tickets may be sent in parallel). TigerCS tolerates reordering - an out-of-order event is held
   or recorded as superseded - but in-order delivery means it never has to.

## What to do with each result

| `SendDisposition` | HTTP | Do |
|---|---|---|
| `Delivered` | 200 / 201 | Mark sent. Never resend. (Includes `Duplicate`, `Superseded`, `Rejected` - TigerCS has the event and decided.) |
| `Accepted` | 202 | Mark sent. **Do not resend** - TigerCS stored it and retries it itself (its ticket does not exist yet, or a mapping / employee is missing). Alert the business if `Outcome` is a mapping problem. |
| `RetryLater` | network, 408, 425, 429, 5xx (503 incl.) after `MaxAttempts` | Leave it in the outbox; resend later (e.g. every minute, then growing), **unchanged**. |
| `Unauthorized` | 401 / 403 | Stop sending, alert, keep every event. Resume when the key is fixed. |
| `PermanentFailure` | 400, 409, other 4xx | Move to a dead-letter store and alert: the event itself is wrong (validation, `EventId` reused with different content, wrong URL). Resending it as is cannot work. |

## Configuration (`TigerCs` section)

| Key | Meaning | Default |
|---|---|---|
| `TigerCs:BaseUrl` | TigerCS API address, e.g. `https://tigercs.example.com` | required |
| `TigerCs:ApiKey` | The API key TigerCS issued (secret - environment variable `TigerCs__ApiKey`) | required |
| `TigerCs:ApiKeyHeaderName` | Header that carries the key | `X-Queueing-Api-Key` |
| `TigerCs:RequestTimeoutSeconds` | Timeout of one HTTP attempt | 30 |
| `TigerCs:MaxAttempts` | HTTP attempts per `SendAsync` call before `RetryLater` | 5 |
| `TigerCs:BaseDelayMilliseconds` | First backoff; doubles each attempt, with jitter | 1000 |
| `TigerCs:MaxDelaySeconds` | Longest single backoff (also caps `Retry-After`) | 60 |

## Build and test

```bash
cd queueing-system
dotnet test QueueingSystem.Sender.slnx
```
