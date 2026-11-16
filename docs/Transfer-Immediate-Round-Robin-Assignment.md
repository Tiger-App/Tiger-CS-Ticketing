# Department transfer: immediate Round Robin assignment

## Problem
A ticket transferred to another department, with no employee named, was not handed to the next employee of the destination department's Round Robin.

## Root cause
`TicketAssignmentAppService.TransferAsync` moves the department with `Ticket.TransferToDepartment(..., preserveOwner: true)` (the documented "the CS Agent and the department are independent" rule). The re-evaluation of the assignment automation was guarded by `else if (ticket.CurrentOwnerEmployeeId is null)`, so for every ticket that already had an owner - practically all of them - nothing ran and the previous department's employee stayed on the ticket. Even for an ownerless ticket:

1. `ApplyAsync` returns immediately for a ticket with no request type and only deals when a rule says Round Robin;
2. the fallback (`ApplyRoundRobinFallbackAsync` -> `EnsureCsAgentAsync`) dealt from the **shared Call Center** pool and cursor, not from the destination department.

## Behaviour now (no named assignee)
Inside the transfer's own transaction, immediately (no background job):

1. Collections tower routing and the "back to the Call Center's Original Human Agent" return keep their own rules (unchanged).
2. Otherwise the existing `ApplyAsync` runs for the destination (a configured primary / team / Round Robin rule decides when it can).
3. If no rule decided (no request type, no rule, rule says department queue, stale assignee) the new `TicketAutoAssignmentService.AssignDepartmentRoundRobinAsync` deals the ticket with the **same** `AssignRoundRobinAsync` the Round Robin rule uses: the destination's persisted cursor (`GetForUpdateAsync`, `UPDLOCK` row lock held to the end of the transaction), the same eligibility filter (not a service identity, not locked out, operational non-read-only role, active department member), the same `TicketAssignment` row, `AutoAssign` audit entry and "assigned to you" outbox notification. No second algorithm.
4. If nobody can take it (no eligible employee, or the department's settings disable assignment) the previous department's owner is released and the ticket waits **Unassigned** in the destination queue; the reason is audited (`AutoAssign` + `Unassign`), flagged (`ManualAssignmentRequired`) and visible on the transfer history.

A named employee (`AssignToEmployeeId`) is a manual assignment: unchanged, never overridden, does not touch the cursor.

Unchanged: transfer permissions and destination lists, department access, closed-ticket refusal, status/priority/SLA (not touched), `Transfer` audit entry, transfer workflow event (now records the employee who actually received it). A Resolved ticket keeps its owner (automatic assignment never applies to resolved work).
Not changed: request-type / category **classification** routing still keeps the agent (`TicketRequestTypeRoutingService`).

## Concurrency
Ticket `RowVersion`, the unique current-assignment index, and the cursor row lock (held until commit; rolled back with the transaction) are all reused: concurrent transfers into one department queue on the cursor and are dealt consecutive slots; the loser of a RowVersion race rolls back without consuming a slot.

## Tests
`TransferRoundRobinAssignmentTests` (automatic, manual, no eligible employee, consecutive, concurrent, permissions/validation unchanged). Existing tests that asserted the old "transfer keeps the agent / uses the shared Call Center pool" contract were updated to the new contract.
