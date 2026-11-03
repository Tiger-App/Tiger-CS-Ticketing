/*
================================================================================
 Diagnose_TigerCS_CustomerHistory_ZeroCount_ReadOnly.sql     (READ-ONLY - SELECT only, nothing is written)

 Why does Ticket Details sometimes show "Previous Tickets (0)" / "Customer (0)" for a customer who has prior interactions?
 The count is the number of OTHER tickets sharing the ticket's VERIFIED identity (Tickets.CrmBuyerCustomerId, else the exact
 Tickets.CustomerVerificationSource + Tickets.ExternalCustomerId pair) that the viewer may see. Zero therefore has exactly these causes:

   NO_VERIFIED_IDENTITY   the ticket has neither a CRM id nor an external pair (typical for Genesys phone tickets that were never CRM-verified).
                          History then falls back to the IntakeRecord phone snapshot, and is empty when no IntakeRecord is linked.
                          Fix = verify the customer on the ticket (data quality), never a phone/name match.
   GENUINELY_FIRST        verified, and it really is the customer's only ticket.
   VISIBILITY_ONLY        verified, other tickets exist, but a restricted CS Agent could not see them before Customer Ticket History
                          (they only saw their own assigned tickets). Resolved by the history grant: owning an ACTIVE ticket of the customer.

 Run per environment, review, and decide on data fixes separately. Ticket status: 1 Open, 2 InProgress, 3 PendingCustomer,
 4 PendingThirdParty, 5 Resolved, 6 Closed.  "Active" = 1..4.
================================================================================
*/
SET NOCOUNT ON;
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

;WITH Active AS (
    SELECT t.TicketId, t.TicketNumber, t.CrmBuyerCustomerId, t.CustomerVerificationSource, t.ExternalCustomerId,
           t.CurrentOwnerEmployeeId, t.TicketStatus,
           CASE WHEN t.CrmBuyerCustomerId IS NOT NULL THEN 'crm:' + CAST(t.CrmBuyerCustomerId AS varchar(20))
                WHEN t.CustomerVerificationSource IS NOT NULL AND t.ExternalCustomerId IS NOT NULL
                     THEN 'ext:' + t.CustomerVerificationSource + ':' + t.ExternalCustomerId
                ELSE NULL END AS IdentityKey
    FROM dbo.Tickets t
    WHERE t.TicketStatus IN (1, 2, 3, 4)
),
Everything AS (
    SELECT t.TicketId, t.CurrentOwnerEmployeeId,
           CASE WHEN t.CrmBuyerCustomerId IS NOT NULL THEN 'crm:' + CAST(t.CrmBuyerCustomerId AS varchar(20))
                WHEN t.CustomerVerificationSource IS NOT NULL AND t.ExternalCustomerId IS NOT NULL
                     THEN 'ext:' + t.CustomerVerificationSource + ':' + t.ExternalCustomerId
                ELSE NULL END AS IdentityKey
    FROM dbo.Tickets t
)
SELECT a.TicketId, a.TicketNumber, a.CurrentOwnerEmployeeId,
       a.IdentityKey,
       OtherTickets      = (SELECT COUNT(*) FROM Everything e WHERE e.IdentityKey = a.IdentityKey AND e.TicketId <> a.TicketId),
       OtherAgentTickets = (SELECT COUNT(*) FROM Everything e WHERE e.IdentityKey = a.IdentityKey AND e.TicketId <> a.TicketId
                                 AND ISNULL(e.CurrentOwnerEmployeeId, '00000000-0000-0000-0000-000000000000') <> ISNULL(a.CurrentOwnerEmployeeId, '00000000-0000-0000-0000-000000000000')),
       Cause = CASE
                 WHEN a.IdentityKey IS NULL THEN 'NO_VERIFIED_IDENTITY'
                 WHEN NOT EXISTS (SELECT 1 FROM Everything e WHERE e.IdentityKey = a.IdentityKey AND e.TicketId <> a.TicketId) THEN 'GENUINELY_FIRST'
                 ELSE 'VISIBILITY_ONLY' END
FROM Active a
ORDER BY Cause, a.TicketId DESC;

-- Summary: how many active tickets fall in each class.
;WITH K AS (
    SELECT t.TicketId,
           CASE WHEN t.CrmBuyerCustomerId IS NOT NULL THEN 'crm:' + CAST(t.CrmBuyerCustomerId AS varchar(20))
                WHEN t.CustomerVerificationSource IS NOT NULL AND t.ExternalCustomerId IS NOT NULL
                     THEN 'ext:' + t.CustomerVerificationSource + ':' + t.ExternalCustomerId END AS IdentityKey,
           t.TicketStatus
    FROM dbo.Tickets t
)
SELECT Cause, COUNT(*) AS ActiveTickets
FROM (
    SELECT CASE WHEN a.IdentityKey IS NULL THEN 'NO_VERIFIED_IDENTITY'
                WHEN EXISTS (SELECT 1 FROM K o WHERE o.IdentityKey = a.IdentityKey AND o.TicketId <> a.TicketId) THEN 'HAS_OTHER_TICKETS'
                ELSE 'GENUINELY_FIRST' END AS Cause
    FROM K a WHERE a.TicketStatus IN (1, 2, 3, 4)
) x
GROUP BY Cause;
