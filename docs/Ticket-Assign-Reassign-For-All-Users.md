# Ticket Assign / Reassign for every ticket user

**Endpoint:** `POST /api/tickets/{ticketId}/reassign` (`TicketAssignmentAppService.ReassignAsync`)
**UI:** Ticket Details → Actions → **Assign / Reassign** (dialog).

## Who can use it
Authority is **access to the ticket**, not a role. Anyone who can open *and modify* the ticket under the existing visibility rules
(`TicketVisibilityRule.CanModifyTicketAsync`) may assign it to themselves or a colleague, reassign it, or move it to another department:
CS Agent / Department Employee on a ticket assigned to them, Department Head / Supervisor in their departments, CS Manager / System Administrator everywhere.

Unchanged: ticket visibility, closed tickets are immutable, read-only roles (Chairman/CEO, Reporting User) and service identities (the Genesys integration account) are
refused, an explicit per-user **deny** of `Tickets.Assign` / `Tickets.Transfer` still blocks, and department workflow settings (`AllowAssignment`,
`AllowInternalReassignment`, `AllowTransferToOtherDepartments`) still narrow it. The existing **Assign** and **Transfer** actions and endpoints are untouched
(their role rules, reason requirement and destination lists still apply to them).

## Request
`{ targetDepartmentId, rowVersion, assignedEmployeeId?, comment?, collectionsTowerId?, collectionsUnitRef? }`

| Target department | Employee | Result |
|---|---|---|
| current | given | Assign / reassign (same path as `Assign`) |
| current | none | 422 `assignment-target-required` |
| another active | given | Transfer + assign in one step |
| another active | none | Department-only transfer (existing transfer rules pick/keep the owner) |

Server-side validation: department exists and is active; the employee is an **active** member of the *resulting* department (Collections: active member of the
Collections team); the ticket is not Closed; `rowVersion` matches (409 otherwise).

## What is recorded
Superseding `TicketAssignment` rows (assignment history, with the acting user), a `TicketTransferred` workflow event for hand-overs and transfers, and audit
entries (`Assign` / `Transfer`) carrying the optional comment — all in one transaction. Status, SLA, classification and the Original Human Agent are not reset.

## Genesys
AI-to-human assignment, the automatic assignment when a Genesys call reaches a human agent, and interaction ownership are separate code paths and are not
changed. Manual reassignment does not depend on Genesys.
