# Voice calls — ownership, closure, the last offered agent, SLA and time (PR #292)

Supersedes §6–§7 of `Ticket-1878-AiEnded-Hotfix-Investigation.md` wherever they differ. Approved decisions of 20 Nov 2026.

> **P0 update (27 Nov 2026) — superseded rules:** rule 3 below ("no automatic assignment, no Round Robin") and the shipped `DryRun = true` caused tickets to stay assigned to the AI Agent in the
> Call Center queue for ever. A failed-AI call nobody answered is now dealt to an eligible Call Center CS Agent by the existing Round Robin and retried until one exists; the reconciler ships live
> (`DryRun = false`). Wherever §2 rule 3, §3 or the "Round Robin is not used for voice calls" paragraph say otherwise, read `P0-AI-Owned-Ticket-Assignment-Fix.md`.

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
| 3 | AI failed + call ended + nobody answered + window passed → the ticket is flagged as needing a person and **dealt to the next eligible Call Center CS Agent by the existing Round Robin** (an explicit workflow department keeps its own department and is dealt by that department's Round Robin). The AI Agent stops being the owner. With nobody eligible the ticket stays queued in the Call Center (AI Agent still the recorded owner) and is **retried every run**. The agent Genesys merely *offered* the call to is never assigned because of the offer (an offer / ringing is not a confirmed connection); the offer is audited for diagnostics only. A later **confirmed** connection (routing agent) replaces the provisional Round Robin owner with the agent who answered. | `VoiceCallReconciler` |
| 4 | Direct human call → the answering employee; AI→human transfer → the first human who actually answered (`routing.agentId` = connected). | `GenesysAnsweringAgentAssignmentService` |
| 5 | A transferred call is **actually answered** by another human (`03-update-routing`, `routing.agentId` = connected) → the **existing ticket** is reassigned to that agent (history preserved: previous and new employee). A transfer nobody answers (ringing / offered / top-level `agentId` / handoff assignee) changes nothing: the current owner keeps it. Same ticket, never a duplicate. | `GenesysTicketUpdateAppService` (`allowOwnerTransfer`, same as digital: `Genesys:ReassignOnTransfer`) |
| 6 | Closed tickets are never reopened, never re-owned, by any automated flow, on any channel. | see §5 |
| 7 | A call ending never closes a ticket. | `ended` writes the end only |

### What `ended` does for voice (and what it no longer does)
* It records `EndedAtUtc` **and `EndReceivedAtUtc` (when TigerCS received it)**. It sets **no** human-follow-up flag, raises **no** handoff work item
  (`AutoRaiseHandoffOnConversationEnd` is skipped for voice), moves **nothing** and assigns **nobody**.
* An explicit `humanFollowUpRequired` / handoff request still marks the ticket "Waiting for human" and queues it in the Call Center **with the AI Agent as owner and no
  Round Robin** (a person may still answer).

## 3. A failed voice call — the reconciliation window, the last offered agent
`VoiceCallReconciler` (recurring, `VoiceCallReconciliationHostedService`) concludes **"AI failed, nobody answered"** only when **all** hold:
the ticket is Open and unowned; every Genesys interaction is a Phone call; every one has **ended**; **no person answered** (no *confirmed-connection* handler, no human transcript line, no first human
response, no handoff-started event, no flagged answer); no approval is pending; the end was **received** at least `NoAnswerWindowSeconds` ago and no
more than `LookbackHours` ago. It then marks the ticket as needing a person (sticky, audited once: `GenesysVoiceNoAnswerConcluded`) and:

* **an agent was offered the call** → **not assigned.** An offer proves nothing about who will answer; the offer is audited (`GenesysVoiceLastOfferedAgentUnavailable`, once) for diagnostics (this replaces the earlier "last offered agent" rule, which conflicted with the Genesys waiting-state rule).
* **nobody was ever offered the call** → the ticket stays **unassigned** in the Call Center queue for follow-up (an explicit workflow department keeps its own department). **No Round Robin.**
* **no confirmation ever arrives** → the ticket stays queued and flagged ("Awaiting Genesys Update"); nothing is assigned because time passed.

**What counts as an offer.** Genesys *offer events* on a Phone conversation: a handoff request or assignment that names an agent, and an update or end event whose top-level `agentId` names an agent without reporting them *connected*.
Each distinct offer is appended to the ticket's event log (`GenesysAgentOffered`, note `[GenesysUserId=…;OfferedAtUtc=…;Source=…]`; no schema change). The same agent named again straight after itself is a redelivery and writes nothing; a different agent — or the same agent after another one — is a new offer.
"Last" is the latest offer by the time Genesys gave (receipt time when it gave none), then by arrival. **Answer events (`routing.agentId` + `connectedAtUtc`) are separate and always win:** an agent who really answered owns the ticket, whoever was offered last.

| Setting (`VoiceCallReconciliation:`) | Default | Meaning |
|---|---|---|
| `Enabled` | `true` | Registered whether or not Hangfire runs. Turn off only to stop the automatic handling of failed-AI voice calls (assignment to the last offered agent, flag-and-queue). |
| `DryRun` | **`false`** (every environment, since the P0 fix) | `true` classifies and logs, writes nothing. It used to ship `true` and was never switched, which left failed-AI calls on the AI Agent. Use `true` only as a temporary emergency stop. |
| **`NoAnswerWindowSeconds`** | **`300`** | Time after the end was **received** before "nobody answered" is concluded. Re-read every run — no deployment needed to change it. |
| `LookbackHours` | `24` | AI-handled calls that ended longer ago are never swept up. **Exception:** a ticket whose interaction is flagged `RequiresHumanFollowUp` and that has no owner is processed however old (that is a ticket stuck on the AI Agent). |
| `IntervalSeconds` | `60` | Run cadence (clamped 15–3600). |
| `BatchSize` / `MaxActionsPerRun` | `100` / `50` | Page size / writes per run. |

**Why 300 s and what it costs.** `routing.agentId` can arrive after `ended`. A ticket whose call failed is handled **window + ≤ one interval after the end was received** (≈ 5–6 min),
instead of 1 s after as before. During the window the ticket is open in the Call Center queue (AI Agent owner); **SLA clocks are not delayed** (they run from creation) and an answering agent
that arrives inside the window still gets the ticket directly with the first response dated at the real connect time. Shorter (60 s) → quicker assignment, higher risk of assigning an offered agent when the answer event is merely late (a late real answer still replaces it). Longer → a longer unattended period. The window is anchored on **receipt** (`EndReceivedAtUtc`), never on a possibly delayed event time.

**An agent id alone is never evidence of an answer** — it is an *offer*. A top-level `agentId` on an update / end event, an *offered*, *assigned* or *ringing* agent (named or mapped), and the
`GenesysAgentConnected` event those produce are all ignored for phone-only tickets. Only a handler recorded from a confirmed connection counts (`routing.agentId` + `connectedAtUtc`,
the inquiry pickup, a human transcript line, or a legacy handler). A connected agent who maps to nobody is *flagged* (`ManualAssignmentRequired` with the Genesys user id) and is still a
human answer, so no Round Robin; it is assigned once the mapping is fixed, with the first response dated at the real connect time.

**One job per ticket.** Phone-only tickets are excluded from `CallCenterReconciliation` (class `HandledByVoiceReconciler`); `VoiceCallReconciliation` is the only job that assigns them.
`CallCenterReconciliation`, `DefaultClassificationBackfill`, `StalePhoneAssignment` and `CsAgentBackfill` stay **disabled by default** (asserted by `ProductionConfigurationTests`).

**Round Robin for voice calls** runs only for a call that ended with nobody answering (after the window; or at any time for a ticket already flagged as needing a person). It is not used at handoff request time, not while a human answer is pending, and not when a person answered but cannot be mapped (that waits flagged for the mapping fix).

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


## Conversation status (PR #292)

A Genesys interaction carries a **conversation status**, separate from the Ticket Status, the owner (`Assigned To`) and Assignment History. It is **derived** at read time (`GenesysConversationStatus`, no column, no migration) and never assigns anyone:

| Status | Meaning | Evidence |
|---|---|---|
| Awaiting Genesys Update | a person is needed / expected, Genesys has not confirmed one | handling state `HumanFollowUp` / `WaitingForHuman`, open, no confirmed connection |
| Conversation In Progress | open and handled by the AI / existing owner | open, handling state `Ai` |
| Assigned | Genesys confirmed a connected human | routing agent / accepted handoff / recorded handler |
| Conversation Ended | conversation-level end recorded | `EndedAtUtc` (an agent disconnect is not an end) |

While awaiting: no human assignment, no Round Robin, no last-offered assignment, no assignment from ringing, existing AI / system ownership is kept and the ticket is never labelled "Unassigned" because of a pending update. `ReassignOnTransfer = true` is unchanged: a transfer target who actually connects takes the ticket; an offer / ringing leaves the owner. Contract limit: Genesys sends no explicit "conversation active" or "awaiting" event, so *In Progress* and *Awaiting* are inferred, not reported. The Ticket Details **Conversation status** row shows the state.
