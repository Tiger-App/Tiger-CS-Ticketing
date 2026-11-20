# Genesys ticket ownership, closure, SLA and time — final rules for PR #292 (all channels)

Supersedes §6–§7 of `Ticket-1878-AiEnded-Hotfix-Investigation.md` and the earlier "Voice-Ownership-Rules" draft of the second pass, which scoped the new rules to Phone
only and let the voice no-answer job deal calls by Round Robin. Both conflicted with the mandatory business rules below and were reconciled in the final review of PR #292.

## 1. Mandatory rules — identical for voice, WhatsApp and every other channel
Single switch: `GenesysOwnershipRules.AppliesTo(channelId)` (no channel is exempt today; a channel is exempted only by adding it there with an explicitly approved requirement).

| # | Rule | Where |
|---|---|---|
| 1 | The AI Agent owns a ticket it handles first (`Tickets.AiAgentOwnerEmployeeId`; no person owner). | `RecordAiAgentAssignmentAsync` |
| 2 | An AI conversation ending never triggers Round Robin, on any path (creation, handoff, follow-up flag, end event, reconciler, answering-agent fallback, `AssignStaleAi`). | `GenesysAgentHandoffAppService`, `GenesysTicketUpdateAppService`, `TicketCreationAppService`, `CallCenterUnassignedWorkReconciler`, `VoiceCallReconciler` |
| 3 | An unresolved / "needs a person" ticket waits in the Call Center queue (an explicit-workflow department such as Collections/Legal keeps it), flagged, AI Agent still the owner, until valid human acceptance or an authorised manual assignment. | `RouteExistingTicketToCallCenterAsync(dealToAgent:false)` |
| 4 | A person gets the ticket only by actually accepting/answering it (mapped, eligible employee reported *connected*), by a human transcript line while `Genesys:AssignOnTranscriptEvidence` is on, or by an authorised manual assignment / Take Ownership. The assignee is the **actual** employee. | `GenesysAnsweringAgentAssignmentService`, `TicketAssignmentAppService` |
| 5 | An accepting agent who cannot take it (unmapped, not a CS Agent, locked) → ticket stays queued and **flagged** (`ManualAssignmentRequired`, audit `AnswerAssignmentSkipped`). **Never another employee instead.** Once the mapping is fixed the reconciler gives it to that person. | `ApplyAnsweringAgentAsync`, `VoiceCallReconciler` |
| 6 | Closed (and Resolved) tickets never reopen automatically, whatever closed them; late/duplicate events (update, handoff, routing, end, transcript, `closedByAi`, documents) are audit evidence only: no owner, department, status, SLA or assignment change. | `ClosedTicketGuard` |
| 7 | A genuinely new conversation (new `conversationId` + `serviceRequestId`) creates a new ticket; continuing the same one answers the existing (closed) ticket. | ingestion |
| 8 | AI replies, queue routing and employee assignment are **never** a human first response. First response = a human reply, or (voice) the real connect time of the first mapped person who answered; write-once; transfers and queue routing never reset SLA clocks. | `FirstResponse…`, `TryRecordVoiceFirstResponseAsync` |
| 9 | Timestamps are stored in UTC; the UI converts to UAE (UTC+4, no DST) exactly once. | `GenesysUtcDateTimeConverter`, `TigerCsDbContext` guard, `TicketDisplay.ToUae` |

## 2. What is deliberately different for a phone call (and why) — to confirm
| Difference | Reason | Setting |
|---|---|---|
| **"The call ended" is not evidence on its own.** The end sets `EndedAtUtc` and `EndReceivedAtUtc` and nothing else; a no-answer is concluded by `VoiceCallReconciler` after a window. Digital channels flag-and-queue at the end event. | `routing.agentId` can arrive after `ended`; flagging at once would also forbid a later valid AI closure (`closedByAi` after `ended`). The **outcome is identical** (flagged, queued, AI Agent owner, nobody assigned). | `VoiceCallReconciliation:NoAnswerWindowSeconds` (300) |
| **First response from the connect event** (`GenesysCallAnswer`, at the real connect time). | A call is answered before any transcript exists. | none |
| **AI closure not refused because a call ticket is in the Call Center.** | Phone tickets are created and queued there. | none |

> **Transfers (same for every channel).** When Genesys reports a different employee connected to a live conversation, the person who **actually accepted** it owns it
> (`Genesys:ReassignOnTransfer`, default `true`, as on `main`; history records every step; a stale/duplicate event never overwrites a newer assignment). The second pass had switched this off
> for voice ("the first person who answered keeps it", described there as approved on 20 Nov). That could not be confirmed and contradicts the mandatory "assign to the employee who accepted"
> rule, so it is now the **same option for every channel**. If the business wants "first answerer keeps it", set `Genesys:ReassignOnTransfer = false` (applies to all channels).

## 3. `VoiceCallReconciler` (recurring, `VoiceCallReconciliationHostedService`)
Concludes "AI failed, nobody answered" only when the ticket is Open and unowned, every Genesys interaction is a Phone call, every one has ended, nobody answered (no handler, human line,
first human response, connected / transferred / handoff-started event, flagged answer), no approval is pending, the end was **received** ≥ `NoAnswerWindowSeconds` ago and ≤ `LookbackHours` ago.
It then **flags the ticket "waiting for a person" (once, audited `GenesysVoiceNoAnswerConcluded`) and queues it in the Call Center — and assigns nobody.** It also gives a ticket to a person
who *did* answer but could not be assigned (unmapped id) once the mapping is valid — that is acceptance, not Round Robin.

## 4. Closed tickets, SLA, performance
As in the earlier draft (unchanged and verified): closure never reopens; "(last responsible)" shown on closed tickets; AI closure attributed to the AI Agent; AI-only 18 h auto-close
(`AiOnlyAutoClose`) stays **off**; chatbot inactivity unchanged; `StalePhoneAssignment` stays **off** (and, with these rules, never assigns a Genesys-handled ticket).
Team Performance credits the person who held the ticket from its creation (the answering agent) for a voice first response.

## 5. Time
Stored in UTC; `TicketDisplay.ToUae` (fixed UTC+4) is the single conversion (the second pass's separate helper was folded into it); `ToLocalTime`/`DateTime.Now`/`AddHours(4)` are banned in display
code by test. No stored value is shifted; historical corrections only through the separately approved remediation script.

## 6. Schema
`TicketAssignmentHistory.PreviousDepartmentId` and `TicketInteractions.EndReceivedAtUtc` — see `docs/releases/PR-292-Final-Report.md`.
