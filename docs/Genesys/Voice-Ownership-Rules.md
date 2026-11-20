# Voice calls — ownership, closure, Round Robin, SLA and time (PR #292, second pass)

Supersedes §6–§7 of `Ticket-1878-AiEnded-Hotfix-Investigation.md` wherever they differ. Approved decisions of 20 Nov 2026.

## 1. Scope — Phone only
The new AI-ownership and answering-agent rules apply to **Phone** interactions only (`TicketInteraction.ChannelId == Phone`, or an inquiry with `channel = Phone`).
WhatsApp, e-mail, web chat and every other digital channel keep the behaviour they had on `main` (before this PR) until their own requirements are final:
Call Center first, Round Robin at creation / handoff / follow-up, the next-agent fallback when the answering agent cannot take a ticket, request-type
classification assigning an owner, `AssignStaleAi`, and the end-of-conversation promotion (`AiEndedUnresolved`) all behave as before.

**Not channel-specific (all channels):** Closed is terminal; the timestamp guard; Assignment History rows; the read-model handling states; the Dubai display helper.

## 2. The rules, as implemented

| # | Rule | Where |
|---|---|---|
| 1 | AI answers the call → the AI Agent owns the ticket (`Tickets.AiAgentOwnerEmployeeId`, no person owner). | `RecordAiAgentAssignmentAsync` (unchanged) |
| 2 | AI resolves → an explicit, valid `closedByAi: true` closes it under the AI Agent; `ended` before `closedByAi` is supported. | `GenesysTicketUpdateAppService.ApplyClosedByAiAsync` |
| 3 | AI failed + call ended + nobody answered + window passed → Round Robin by the **existing workflow** (Call Center first; an explicit workflow department keeps its own Round Robin; Collections tower routing is left alone). | `VoiceCallReconciler` |
| 4 | Direct human call → the answering employee; AI→human transfer → the first human who actually answered (`routing.agentId` = connected). | `GenesysAnsweringAgentAssignmentService` |
| 5 | Another employee continues the call → the original assignee stays; the later agent is recorded as a handler / call-transfer event only. **Automatic reassignment on transfer is disabled for voice.** | `GenesysTicketUpdateAppService` (`allowOwnerTransfer` false for voice) |
| 6 | Closed tickets are never reopened, never re-owned, by any automated flow, on any channel. | see §5 |
| 7 | A call ending never closes a ticket. | `ended` writes the end only |

### What `ended` does for voice (and what it no longer does)
* It records `EndedAtUtc` **and `EndReceivedAtUtc` (when TigerCS received it)**. It sets **no** human-follow-up flag, raises **no** handoff work item
  (`AutoRaiseHandoffOnConversationEnd` is skipped for voice), moves **nothing** and assigns **nobody**.
* An explicit `humanFollowUpRequired` / handoff request still marks the ticket "Waiting for human" and queues it in the Call Center **with the AI Agent as owner and no
  Round Robin** (a person may still answer).

## 3. Round Robin for a failed voice call — the reconciliation window
`VoiceCallReconciler` (recurring, `VoiceCallReconciliationHostedService`) concludes **"AI failed, nobody answered"** only when **all** hold:
the ticket is Open and unowned; every Genesys interaction is a Phone call; every one has **ended**; **no person answered** (no *confirmed-connection* handler, no human transcript line, no first human
response, no handoff-started event, no flagged answer); no approval is pending; the end was **received** at least `NoAnswerWindowSeconds` ago and no
more than `LookbackHours` ago. It then marks the ticket as needing a person (sticky, audited once: `GenesysVoiceNoAnswerConcluded`) and deals it through the existing engine.

| Setting (`VoiceCallReconciliation:`) | Default | Meaning |
|---|---|---|
| `Enabled` | `true` | Registered whether or not Hangfire runs. Turn off only to stop automatic assignment of failed-AI voice calls. |
| `DryRun` | `false` (**Production file: `true`**) | `true` classifies and logs, writes nothing. Production ships observe-only so the new automatic Round Robin for failed voice calls is switched on deliberately after reviewing a day of logs. |
| **`NoAnswerWindowSeconds`** | **`300`** | Time after the end was **received** before "nobody answered" is concluded. Re-read every run — no deployment needed to change it. |
| `LookbackHours` | `24` | Calls that ended longer ago are never swept up (no backlog assignment by this job). |
| `IntervalSeconds` | `60` | Run cadence (clamped 15–3600). |
| `BatchSize` / `MaxActionsPerRun` | `100` / `50` | Page size / writes per run. |

**Why 300 s and what it costs.** `routing.agentId` can arrive after `ended`. A ticket whose call failed is dealt **window + ≤ one interval after the end was received** (≈ 5–6 min),
instead of 1 s after as before. During the window the ticket is open in the Call Center queue (AI Agent owner); **SLA clocks are not delayed** (they run from creation) and an answering agent
that arrives inside the window still gets the ticket directly with the first response dated at the real connect time. Shorter (60 s) → quicker assignment, higher risk of dealing a second
person when the answer event is late. Longer → a longer unattended period. The window is anchored on **receipt** (`EndReceivedAtUtc`), never on a possibly delayed event time.

**An agent id alone is never evidence of an answer.** A top-level `agentId` on an update / end event, an *offered*, *assigned* or *ringing* agent (named or mapped), and the
`GenesysAgentConnected` event those produce are all ignored for phone-only tickets. Only a handler recorded from a confirmed connection counts (`routing.agentId` + `connectedAtUtc`,
the inquiry pickup, a human transcript line, or a legacy handler). A connected agent who maps to nobody is *flagged* (`ManualAssignmentRequired` with the Genesys user id) and is still a
human answer, so no Round Robin; it is assigned once the mapping is fixed, with the first response dated at the real connect time.

**One job per ticket.** Phone-only tickets are excluded from `CallCenterReconciliation` (class `HandledByVoiceReconciler`); `VoiceCallReconciliation` is the only job that assigns them.
`CallCenterReconciliation`, `DefaultClassificationBackfill`, `StalePhoneAssignment` and `CsAgentBackfill` stay **disabled by default** (asserted by `ProductionConfigurationTests`).

Not Round Robin, ever, for voice: at **handoff request**; while a **human answer is pending** (call not ended / window not elapsed); when a person **answered but cannot be mapped**.

## 4. Unmapped (or ineligible) answering agent
A connected agent whose Genesys User ID maps to nobody (or to an account that cannot take it): **no Round Robin**; the ticket stays queued in the Call Center, the failure is flagged
(`ManualAssignmentRequired` workflow event + `AnswerAssignmentSkipped` audit, written once per ticket/agent/reason; the note now carries `ConnectedAtUtc`). The reconciler re-resolves the
Genesys id on every run and, **once the mapping is corrected and the ticket is still Open and unowned**, assigns the ticket to that person, records them as Original Human Agent and dates
the first response at the real connect time. While the mapping is still missing it logs a warning per run and changes nothing.

## 5. Closed tickets (every channel)
Closed is terminal. Late or duplicate events (update, handoff, routing, end, transcript, `closedByAi`, document delivery) are audit evidence only: no reopen, no owner/department change,
no assignment, no SLA change. `ApplyAnsweringAgentAsync`, `RouteExistingTicketToCallCenterAsync`, the reconciler and the voice reconciler all refuse Closed/Resolved tickets. Continuing the
same conversation (same `conversationId` + `serviceRequestId`) answers the **existing closed ticket**; a new request needs a new `serviceRequestId` / conversation. **Closed is terminal on every channel, including manual Reopen**: `POST /tickets/{id}/reopen` and the Reopen Approval request/grant are refused for every role (single rule: `ReopenPolicy.IsReopenableOutcome`),
the UI never offers Reopen, and a customer who writes again gets a new linked ticket.

Closing clears only the **current-owner field** (a person); the assignment rows are superseded, never deleted, and the AI Agent owner is kept. **A closed ticket shows who held it last**
("(last responsible)" on the ticket page and in the list): the last assignment, else the AI Agent. AI closure is attributed to the AI Agent (resolution row, status history, audit actor);
a human closure to the human. `ClosedAtUtc`, SLA outcome and resolution are untouched by display code.

### The other closing rules (reviewed, **not changed**)
| Rule | Business meaning today | Effect on voice |
|---|---|---|
| **AI-only 18 h job** (`AiOnlyAutoClose`, **off by default**) | Closes a ticket only the AI ever handled, > 18 h after creation: no owner, no handler, no human line/note/handoff/follow-up flag. | Voice tickets concluded as unanswered get a person (and the flag) at ≈ 6 min, so they are never eligible. Unchanged. |
| **Chatbot inactivity** (`Genesys:CustomerInactivityTimeoutMinutes` 10, `AwaitingReplyTimeoutMinutes` 5) | Website/Chatbot: closes after N minutes of customer silence; never a Call Center ticket or a human-managed one. The original bot-prompt timer covers voice/Call Center rows that rule does not track; **`ended` clears both timers**, so call termination alone can never fire it. | Unchanged. |
| **Document delivery** | Closes the ticket behind a document request once its delivery workflow ended (Sent, nothing in flight). | Unchanged. |
| **Stale-phone assignment** (`StalePhoneAssignment`, **off**) / `AssignStaleAi` | Legacy backlog tooling that deals old AI tickets. | `AssignStaleAi` no longer acts on voice (report only); the stale-phone job is untouched and stays off. |

## 6. SLA and performance (voice)
* **First response** = `routing.connectedAtUtc` (or the inquiry's new optional `connectedAtUtc`) of the **first mapped person who answered** — recorded when the connect event is processed, not
  when the transcript arrives. `FirstResponseSource.GenesysCallAnswer`; actor = the answering employee. If Genesys sent no connect time the receipt time is used **and audited**
  (`GenesysVoiceFirstResponseConnectTimeMissing`). Write-once; a later agent or a transfer never moves it; clocks are **not reset on transfer**; queue routing never counts.
* A call is answered *before* its ticket exists: the response keeps the real connect time; Team Performance credits the person who **held the ticket from its creation** (the answering agent).
* Offer / answer / assignment / closure timestamps stay distinct: offer-answer evidence (`EmployeeOfferClassifier`) is independent of ticket assignment; assignment is dated at the connect
  event; closure at `ClosedAtUtc`.

## 7. Time
Everything is stored and compared in UTC. The Web project converts **once**, in `TigerCS.Web.Models.TicketDisplay.ToUae` (fixed UTC+4, no host dependency; the second pass's separate helper was folded into it); `ToLocalTime()` / `DateTime.Now` /
`AddHours(4)` are gone from display code and `DubaiTimeDisplayTests` fails the build if one returns. **No stored value is shifted.** Historical corrections (ticket 1878) wait for the read-only
diagnostic `Diagnose_TigerCS_PROD_Ticket1878_GenesysTimelineOwnership_ReadOnly.sql` to be run in Production (it could not be run from the build environment).

## 8. Schema and deployment
* New: `TicketInteractions.EndReceivedAtUtc` (nullable `datetime2`; migration `20261120074059_AddInteractionEndReceivedAt`; scripts `AddInteractionEndReceivedAt.sql`,
  `Deploy_TigerCS_PROD_AddInteractionEndReceivedAt.sql`). Apply **after** `AddAssignmentHistoryPreviousDepartment`, **before** deploying the build. No backfill (older calls fall back to `EndedAtUtc`).
* New optional inquiry field `connectedAtUtc` (additive; an older Genesys flow keeps working).
* New config section `VoiceCallReconciliation` (defaults above; also in `appsettings.json` / `appsettings.Production.json`).
