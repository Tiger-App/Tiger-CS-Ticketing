# Phone end events — Genesys Architect verification checklist

Scope: why `PATCH /api/genesys/tickets/{id}` with an `ended` part (data action 04 "TigerCS - End Conversation") is not reliably sent for **Phone**
conversations. For the TigerCS side and the 181 held-back tickets see [Phone-End-Event-Investigation.md](Phone-End-Event-Investigation.md).
Nothing here changes data. Historical end times are never edited.

> **Status: the cause is NOT confirmed.** TigerCS is verified (code) and the TigerGroupWeb repo copy forwards the body verbatim. The Genesys side
> (trigger, workflow, data action) cannot be seen from this repository. Each section below says what to look at, what you should see, and what the
> finding means. Genesys menu names differ by release; the objects (trigger, workflow, flow instance, data action) are what matter.

## 0. What the Production evidence says — and what it does not

| Observation | Reading |
|---|---|
| Ever ended through PATCH: Chat/Other **69**, Phone **2** | End events work for chat; for phone they almost never arrive. |
| Phone calls with a routing-PATCH audit and no end: **0**; most phone calls have **no PATCH audit at all** | **Weaker evidence than it looks.** TigerCS writes `GenesysRoutingChanged` only when the routing *changed* ("a redelivered, unchanged routing event writes nothing at all", `GenesysTicketUpdateAppService.RecordRoutingAsync`). The workflow's "Update Routing" branch sends the queue the ticket was created with, so it leaves **no audit row**. "No audit" therefore does **not** prove "no PATCH arrived", and it does **not** rule out the Decision sending voice to the routing branch. |
| What does separate the cases | (a) the **Genesys flow-instance history** of one phone conversation (sections 1–3 below); (b) the **IIS / proxy status counts** for `PATCH /api/genesys/tickets/*` compared with the number of phone calls per day; (c) the **TigerCS log line added in PR 193** (needs that build deployed); (d) how the **2 phone ends** look (`Diagnose_TigerCS_PROD_PhoneEndAuditChannel_ReadOnly.sql` result 3: their `EndReason` and timing tell whether they came from the trigger or from something else). |

Facts about the Genesys event (from Genesys' own Journey Management reference, which lists the *Customer End* attributes as `direction, disconnectType, DNIS,
interactingDurationMs, mediaType, messageType`): `disconnectType` **is** an attribute of Customer End, `queueId` is **not** (it is on ACD Start / ACD End), and
the page does not state values, casing or voice applicability. Community answers describe `customer.end` as the event for the customer leaving, firing before the agent's wrap-up, with `disconnectType` values such as
`peer` / `client` — **community statements, to be verified in your org**.
Sources: [Analytics detail events in Journey Management](https://help.genesys.cloud/articles/analytics-detail-events-in-journey-management/),
[Trigger and disconnectType (community)](https://community.genesys.com/discussion/trigger-and-disconnecttype),
[Clarification on customer.end event (community)](https://community.genesys.com/discussion/clarification-on-customerendevent),
[Troubleshooting events not triggering (community)](https://community.genesys.com/discussion/troubleshooting-events-not-triggering),
[How is disconnect type calculated? (help)](https://help.genesys.cloud/faqs/how-is-disconnect-type-calculated/).

## 1. `customer.end` trigger for voice

Pick **one recent Phone conversation** that is still `NoEndRecorded` in TigerCS (take the id from `Diagnose_TigerCS_PROD_PhoneEndEvents_ReadOnly.sql` section E) and use it for sections 1–4.

| # | Check | Expect | If not |
|---|---|---|---|
| 1.1 | A trigger exists on topic `v2.detail.events.conversation.{id}.customer.end` and is **Active** (not Inactive / Draft / disabled). | present, active, data format **TopLevelPrimitives**, target = workflow `TigerCS - Conversation Event` | create / activate it. A deactivated trigger explains "chat ends, phone does not" only if chat uses a different path — see 1.5. |
| 1.2 | **Match criteria** include voice. Look at the exact `mediaType` condition: operator and **value casing**. | matches the value the event really carries. Community examples use **`VOICE` (upper case)**; the repo doc says `voice, message` in lower case. | If the criteria are a case-sensitive `Equal "voice"` (or `In voice,message`) while events carry `VOICE`, **voice never matches**. Use the event's real casing; if unsure add both spellings as alternatives. |
| 1.3 | No criterion points at the **conversation** JSON; criteria must target the **event** payload (a community report: a trigger stopped firing when its JSON path targeted conversation-level data). | criteria use event attributes (`mediaType`, `direction`, …) only | rewrite the criterion. |
| 1.4 | The trigger's own **execution / error status** for the test conversation (trigger invoked? workflow started?). | one invocation per ended call | none = the trigger did not fire for that call: go back to 1.1–1.3. |
| 1.5 | How do **chat** ends reach TigerCS today (69 of them)? Same trigger, or the bot flow calling "End Conversation" directly? | know which | If chat ends come from the **flow** (not the trigger), the trigger may never have worked for any channel, and voice has no equivalent — then the fix is to add the voice path, not to repair a trigger. |

## 2. Workflow `TigerCS - Conversation Event`: input variables and the Decision

Open the **flow instance / execution history** of the workflow for the test conversation.

| # | Check | Expect | If not |
|---|---|---|---|
| 2.1 | An instance exists for the conversation, started at about the hang-up time. | yes | no instance → section 1 (trigger). |
| 2.2 | Input variables are named **exactly** as in the event: `conversationId`, `mediaType`, `queueId`, `disconnectType`, each **"Input to Flow"**. Names are case-sensitive; a misspelt variable is simply empty. | the instance shows non-empty `Flow.conversationId`, `Flow.mediaType`, `Flow.disconnectType` | fix the variable name. `Flow.queueId` is **expected to be empty** for `customer.end` (the event has no queue). |
| 2.3 | `Flow.mediaType` value and the comparison `If(Flow.mediaType == "voice", "Phone", "LiveChat")`: casing. | `Task.channel = "Phone"` for the voice call | if the event says `VOICE` and the comparison is case-sensitive, `Task.channel` becomes `LiveChat`. Not fatal for the End call (the ticket already exists), but wrong if the repair-create path ever runs. Compare with `ToLower(Flow.mediaType)`. |
| 2.4 | Step "Create or Reuse Ticket": result and output `Task.ticketId`. | success, **non-empty numeric** `ticketId` (TigerCS answers 200 `AlreadyIngested` with the existing ticket) | empty `ticketId` makes the End URL `…/tickets/` (404/405): the End step then fails or is skipped. Read the data action response (section 4). |
| 2.5 | **The Decision** `IsSet(Flow.disconnectType) and Flow.disconnectType != ""`: which branch ran, and the value of `Flow.disconnectType`. | **yes** branch; a non-empty value | **no** branch ran → the workflow called **Update Routing**, which never ends a conversation. See the change in section 6. |
| 2.6 | Any other path out of the workflow (error path, failure path of a data action) that ends silently. | none | a failure path that "continues" hides failures: log them. |

## 3. End Conversation action: execution and payload

| # | Check | Expect | If not |
|---|---|---|---|
| 3.1 | The action `TigerCS - End Conversation` **executed** for the instance. | yes | not executed → 2.5. |
| 3.2 | Inputs: `ticketId` ← `Task.ticketId`, `conversationId` ← `Flow.conversationId`, `endedAtUtc`, `endReason` ← `Flow.disconnectType`. | all non-empty except possibly `endedAtUtc` | empty `ticketId`/`conversationId`: 2.2 / 2.4. |
| 3.3 | **`endedAtUtc` format.** The template inserts the string verbatim. | ISO-8601 UTC (`2026-11-05T09:41:07.123Z`) **or empty** (TigerCS uses its own clock) | `ToString(GetCurrentDateTimeUtc())` in Architect may produce a locale format that TigerCS cannot parse → **HTTP 400, nothing stored**. Send it empty (section 6). |
| 3.4 | The **rendered request body**. | `{"conversationId":"…","ended":{"endedAtUtc":…,"endReason":"…"}}` | any other shape (e.g. `endedAtUtc` at the top level) → 200 and **no end** (`conversationEnded:false`). |

## 4. Data action HTTP response and errors

For the End action's execution record, read the HTTP status and body:

| Status | Meaning | Action |
|---|---|---|
| **200**, `conversationEnded: true` | ended | done (then why is TigerCS not ended? compare ticket id / conversation id with TigerCS) |
| 200, `conversationEnded: false` | body shape wrong / no `ended` part | 3.4 |
| 400 | unparseable `endedAtUtc`, invalid JSON, blank `conversationId` | 3.3 |
| 401 | Genesys bearer not accepted by TigerGroupWeb | data action authentication (`00-custom-auth-request-config`): client id/secret, token expiry |
| 404 / 409 | ticket or conversation unknown / mismatch (wrong `ticketId` for this conversation) | 2.4 |
| 413 / 422 | body too large / business rule | read the problem-details body |
| 502 / 504 | proxy → TigerCS failure or timeout | section 5 |
| 503 | TigerCS `Genesys:Enabled = false` | TigerCS configuration |
| action marked failed although the body looks successful | `successTemplate` / `translationMap` could not map the response | harmless for the end itself (TigerCS already ended it) but it can send the workflow down the failure path |

## 5. TigerGroupWeb forwarding

| # | Check | Expect |
|---|---|---|
| 5.1 | The public route `PATCH https://tigergroup.ae/api/genesys/tickets/{ticketId}` exists and is the **patched** build (`ForwardTicketJson`). | yes. The repo's own README says the patch was *specified and compiled in isolation, not applied*; confirm what is deployed. |
| 5.2 | Proxy log, one line per call: `Genesys ticket update (TicketId N) answered by TigerCS Ticketing: <status> in <ms> ms`. Count them per day for the last 14 days and compare with phone calls per day. | PATCH lines ≈ calls that ended (+ routing/other updates) |
| 5.3 | Statuses in the proxy log: 401 → the Genesys bearer was refused; 502 → TigerCS unreachable or service token problem; 504 → budget (30 s) exceeded; 413. | none, or explainable |
| 5.4 | TigerCS IIS log for `PATCH /api/genesys/tickets/*` (snippet in the investigation doc, §3). | the same counts as the proxy |
| 5.5 | The proxy forwards the body **verbatim** (`JsonElement.GetRawText()`); an empty or non-JSON body is answered 400 by the proxy itself. | yes — it cannot drop `ended` |
| 5.6 | Proxy `ForwardBudget` (30 s) is shorter than the Genesys data-action timeout. | yes |
| 5.7 | After the PR 193 build is deployed: TigerCS log lines `Genesys ticket update applied / REJECTED / IGNORED` for the same ticket. | `applied … ended=True … conversationEnded=True` |

**Reading the counts:** proxy lines ≈ TigerCS lines ≈ chat conversations only → the voice workflow never calls PATCH (sections 1–3). Proxy lines
exist for voice with 4xx → section 4. Proxy shows 2xx but TigerCS not ended → body shape (3.4).

## 6. Smallest safe Genesys configuration change — by finding

Apply **only the row that matches the finding**. Every change is idempotent for TigerCS: the end is write-once, never changes a ticket's status or owner, and
a repeated End returns `AlreadyEnded` with the original time.

| Finding | Smallest change |
|---|---|
| No workflow instance for phone (1.1–1.4) | Fix the `customer.end` trigger: **Active**, `mediaType` criterion that matches the event's real value for voice (or both spellings), event-level JSON paths only. No workflow change. |
| Instance exists, **"no" branch** ran / `Flow.disconnectType` empty (2.5) | In the workflow, **remove the Decision**: always call End Conversation with `endReason ← If(IsSet(Flow.disconnectType) and Flow.disconnectType != "", Flow.disconnectType, "Unknown")`. Keep **Update Routing** only on the `acd.start` trigger. |
| End action ran, **400** (3.3) | Send `endedAtUtc` **empty** (the template already maps empty to `null`; TigerCS then uses its own clock). Do not format the date in Architect. |
| End action ran, **200 `conversationEnded:false`** (3.4) | Fix the template/body to nest `endedAtUtc` and `endReason` inside `ended`. |
| Empty `Task.ticketId` (2.4) | Fix the Create-or-Reuse output mapping; if Create fails for a conversation that already has a ticket, send the same `conversationId` only (no `serviceRequestId`) so TigerCS finds the existing one. |
| 401 / 502 / 504 in proxy or data action (4, 5.3) | Fix credentials / service-token renewal / budget at TigerGroupWeb; no TigerCS change. |

## 7. One Phone end-to-end test

Use an internal test number. The test creates one real ticket (normal behaviour); nothing historical is touched.

1. **Before:** apply the one change from section 6 that matches the finding; note the time (UTC).
2. **Call** the number from a test phone; let the flow answer and create the ticket; **hang up** (note: caller hang-up, and optionally a second test where the agent hangs up).
3. **Genesys:** take the `conversationId` (Interactions view). Confirm in order: trigger invoked → workflow instance started → Decision (or no Decision after the change) → `TigerCS - End Conversation` executed with a non-empty `ticketId` → HTTP **200** with `conversationEnded: true`.
4. **Proxy log:** `Genesys ticket update (TicketId N) answered by TigerCS Ticketing: 200`.
5. **TigerCS:** (after the PR 193 build is deployed) `Genesys ticket update applied: ticket N, conversation <id>; parts: ended=True …; conversationEnded=True`. Without that build, use the IIS log line and step 6.
6. **Database (read-only):**
   ```sql
   SELECT i.TicketInteractionId, i.ChannelId, i.EndedAtUtc, i.EndReason, i.GenesysConversationId
   FROM dbo.TicketInteractions i WHERE i.GenesysConversationId = N'<conversationId>';
   SELECT a.OccurredAtUtc, a.Action FROM dbo.AuditEntries a WHERE a.EntityId = N'<conversationId>' AND a.Action = N'GenesysConversationEnded';
   ```
   Pass: `ChannelId = 1`, `EndedAtUtc` within seconds of the hang-up, a plausible `EndReason`, one `GenesysConversationEnded` row, ticket status unchanged.
7. **Repeat control:** resend the End action for the same conversation: 200 `conversationEnded: true`, `EndedAtUtc` **unchanged**.
8. **Second case (abandon):** call and hang up while still in the IVR/queue. The same checks must pass (this is the "caller never reached an agent" case behind most of the 181).

**If step 3 fails**, the failing hop names the section (1, 2, 3, 4 or 5) and the matching row in section 6 is the fix. Do not change anything in TigerCS or the database for this test.
