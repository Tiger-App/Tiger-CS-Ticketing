/*
================================================================================
 Verify_TigerCS_DigitalChannelVisibility_ReadOnly.sql
 READ-ONLY. SELECT statements only: no INSERT / UPDATE / DELETE / DDL. Safe from a db_datareader login.

 Answers "where are the WhatsApp / Instagram / Facebook / Website Chat tickets?" from the data itself. The Tickets list's Channel filter
 matches the ORIGINATING interaction (TicketInteractions.IsOriginatingInteraction = 1) by TicketInteractions.ChannelId, so a ticket that
 never carried that exact ChannelId cannot be found under that channel, however it is routed or assigned.

   1. Channel catalogue (1b: PASS/FAIL verdict for Instagram = 10 and Facebook = 11): the rows must exist, be active and Genesys-enabled (Instagram = 10, Facebook = 11, WhatsApp = 6, Live Chat = 7).
      An inactive/missing row makes Genesys ingestion fail with 'channel-not-configured'.
   2. Genesys originating interactions per channel, Open vs Closed. Before this fix every Instagram or Facebook conversation Genesys
      reported as 'SocialMedia' sits under SOCIAL_DM (4) - they are NOT lost, only filed under the generic social channel.
   2b. Generic SOCIAL_DM tickets by Genesys queue: shows whether Architect still sends 'SocialMedia' for Instagram/Facebook.
   3. Originating interactions with a channel id that is not in the catalogue, and Genesys tickets with NO originating interaction
      (invisible to every channel filter). Both counts must be 0.
   4. Digital Genesys tickets that need a person but have no owner (Open, in Call Center) - waiting for manual assignment or Round Robin.
   5. Digital Genesys tickets per channel owned by a Call Center member vs by someone who is not a member (transferred on).
   6. Invariants (must be 0): two current assignments; owner differing from the current assignment.
================================================================================
*/
SET NOCOUNT ON;
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

-- 1. Channel catalogue ---------------------------------------------------------------------------------------------------------
SELECT c.ChannelId, c.Code, c.Name, c.IsActive, c.IsGenesysEnabled,
       CASE WHEN c.ChannelId IN (6, 7, 10, 11, 4, 1) AND (c.IsActive = 0 OR c.IsGenesysEnabled = 0)
            THEN 'PROBLEM: Genesys cannot file tickets under this channel' ELSE 'ok' END AS Status
FROM dbo.Channels AS c
ORDER BY c.DisplayOrder;

-- 1b. Verdict for the two channels this release depends on: exactly one row each, and PASS only if active AND Genesys-enabled ------------
SELECT want.ChannelId, want.Code AS ExpectedCode, c.Name, c.Code AS ActualCode, c.IsActive, c.IsGenesysEnabled,
       CASE WHEN c.ChannelId IS NULL THEN 'FAIL: row missing'
            WHEN c.Code <> want.Code THEN 'FAIL: unexpected code'
            WHEN c.IsActive = 0 THEN 'FAIL: inactive'
            WHEN c.IsGenesysEnabled = 0 THEN 'FAIL: not Genesys-enabled'
            ELSE 'PASS' END AS Verdict
FROM (VALUES (10, N'INSTAGRAM'), (11, N'FACEBOOK')) AS want(ChannelId, Code)
LEFT JOIN dbo.Channels AS c ON c.ChannelId = want.ChannelId;

-- 2. Genesys tickets by ORIGINATING channel, Open vs Closed ------------------------------------------------------------------------
SELECT ISNULL(c.Code, CONCAT('(unknown id ', i.ChannelId, ')')) AS ChannelCode,
       SUM(CASE WHEN t.TicketStatus IN (1, 2, 3, 4) THEN 1 ELSE 0 END) AS ActiveTickets,  -- Open, InProgress, PendingCustomer, PendingThirdParty
       SUM(CASE WHEN t.TicketStatus = 6 THEN 1 ELSE 0 END)               AS ClosedTickets,
       COUNT(*)                                                          AS AllTickets
FROM dbo.TicketInteractions AS i
JOIN dbo.Tickets AS t ON t.TicketId = i.TicketId
LEFT JOIN dbo.Channels AS c ON c.ChannelId = i.ChannelId
WHERE i.Source = 2 AND i.IsOriginatingInteraction = 1
GROUP BY ISNULL(c.Code, CONCAT('(unknown id ', i.ChannelId, ')'))
ORDER BY AllTickets DESC;

-- 2b. Is Genesys Architect sending Instagram / Facebook separately? ----------------------------------------------------------------------
-- TigerCS stores the NORMALIZED channel, so the raw value Architect sent is not kept. The proxy is the queue: tickets still filed under the
-- generic SOCIAL_DM channel (id 4) whose Genesys queue is an Instagram/Facebook/Messenger queue mean the flow still sends 'SocialMedia'.
-- After the fix + a flow change this list should show NEW rows only with queues that are neither Instagram nor Facebook.
SELECT ISNULL(i.GenesysQueueName, N'(no queue)') AS GenesysQueueName,
       COUNT(*) AS SocialDmTickets,
       MIN(t.CreatedAtUtc) AS FirstCreatedUtc, MAX(t.CreatedAtUtc) AS LastCreatedUtc,
       CASE WHEN i.GenesysQueueName LIKE N'%insta%' OR i.GenesysQueueName LIKE N'%facebook%' OR i.GenesysQueueName LIKE N'%messenger%'
            THEN 'LIKELY MIS-FILED: Instagram/Facebook queue under generic Social Media' ELSE '' END AS Note
FROM dbo.TicketInteractions AS i
JOIN dbo.Tickets AS t ON t.TicketId = i.TicketId
WHERE i.Source = 2 AND i.IsOriginatingInteraction = 1 AND i.ChannelId = 4
GROUP BY i.GenesysQueueName
ORDER BY COUNT(*) DESC;

-- 3. Tickets no channel filter can find --------------------------------------------------------------------------------------------
SELECT 'originating interaction with a channel id missing from the catalogue' AS Problem, COUNT(*) AS Tickets
FROM dbo.TicketInteractions AS i
WHERE i.IsOriginatingInteraction = 1 AND NOT EXISTS (SELECT 1 FROM dbo.Channels AS c WHERE c.ChannelId = i.ChannelId)
UNION ALL
SELECT 'Genesys ticket with NO originating interaction', COUNT(DISTINCT i.TicketId)
FROM dbo.TicketInteractions AS i
WHERE i.Source = 2
  AND NOT EXISTS (SELECT 1 FROM dbo.TicketInteractions AS o WHERE o.TicketId = i.TicketId AND o.IsOriginatingInteraction = 1);

-- 4. Digital tickets waiting for a person (Call Center, active, unowned) --------------------------------------------------------------
SELECT c.Code AS ChannelCode, COUNT(*) AS UnownedInCallCenter
FROM dbo.Tickets AS t
JOIN dbo.Departments AS d ON d.DepartmentId = t.CurrentDepartmentId AND d.Code = N'CC'
JOIN dbo.TicketInteractions AS i ON i.TicketId = t.TicketId AND i.IsOriginatingInteraction = 1 AND i.Source = 2
JOIN dbo.Channels AS c ON c.ChannelId = i.ChannelId
WHERE t.CurrentOwnerEmployeeId IS NULL AND t.TicketStatus IN (1, 2, 3, 4) AND i.ChannelId <> 1
GROUP BY c.Code ORDER BY UnownedInCallCenter DESC;

-- 5. Digital tickets by channel and owner type ----------------------------------------------------------------------------------------
SELECT x.ChannelCode,
       SUM(CASE WHEN x.OwnerId IS NULL THEN 1 ELSE 0 END)                         AS Unowned,
       SUM(CASE WHEN x.OwnerId IS NOT NULL AND x.OwnerInCallCenter = 1 THEN 1 ELSE 0 END) AS OwnedByCallCenterMember,
       SUM(CASE WHEN x.OwnerId IS NOT NULL AND x.OwnerInCallCenter = 0 THEN 1 ELSE 0 END) AS OwnedByOthers
FROM (
    SELECT c.Code AS ChannelCode, t.CurrentOwnerEmployeeId AS OwnerId,
           CASE WHEN EXISTS (SELECT 1 FROM dbo.UserDepartmentAssignments a JOIN dbo.Departments d ON d.DepartmentId = a.DepartmentId
                             WHERE a.EmployeeId = t.CurrentOwnerEmployeeId AND d.Code = N'CC') THEN 1 ELSE 0 END AS OwnerInCallCenter
    FROM dbo.Tickets AS t
    JOIN dbo.TicketInteractions AS i ON i.TicketId = t.TicketId AND i.IsOriginatingInteraction = 1 AND i.Source = 2
    JOIN dbo.Channels AS c ON c.ChannelId = i.ChannelId
    WHERE i.ChannelId <> 1
) AS x
GROUP BY x.ChannelCode ORDER BY x.ChannelCode;

-- 6. Invariants - every count must be 0 -------------------------------------------------------------------------------------------------
SELECT 'ticket with two current assignments' AS Invariant, COUNT(*) AS Violations
FROM (SELECT TicketId FROM dbo.TicketAssignments WHERE IsCurrent = 1 GROUP BY TicketId HAVING COUNT(*) > 1) AS x
UNION ALL
SELECT 'owner differs from the current assignment', COUNT(*)
FROM dbo.Tickets AS t
JOIN dbo.TicketAssignments AS a ON a.TicketId = t.TicketId AND a.IsCurrent = 1
WHERE t.CurrentOwnerEmployeeId IS NULL OR t.CurrentOwnerEmployeeId <> a.AssignedEmployeeId;

-- 7. Ownership vs the agent who ACTUALLY received the interaction (review list - changes nothing) -----------------------------------------
-- Rule: the human who actually receives/accepts the interaction owns the ticket. TicketInteractionHandlers records who Genesys reported as handling.
-- 'Unowned' and 'OwnerIsNotAHandler' rows are candidates to review; a row where the owner is not a handler is legitimate after an intentional
-- Transfer (see HasPersonTransfer). IMPORTANT: the one-time Call Center backlog redistribution (Execute_TigerCS_PROD_CallCenterBacklogDistribution.sql)
-- would re-deal exactly these tickets to Round Robin owners; do NOT run it as-is while this list is non-empty (see the PR notes).
SELECT x.Finding, COUNT(*) AS Tickets
FROM (
    SELECT CASE WHEN t.CurrentOwnerEmployeeId IS NULL THEN 'Unowned (a handler is recorded)'
                ELSE 'OwnerIsNotAHandler' END AS Finding
    FROM dbo.Tickets AS t
    WHERE t.TicketStatus IN (1, 2, 3, 4)
      AND EXISTS (SELECT 1 FROM dbo.TicketInteractionHandlers h WHERE h.TicketId = t.TicketId)
      AND (t.CurrentOwnerEmployeeId IS NULL
           OR NOT EXISTS (SELECT 1 FROM dbo.TicketInteractionHandlers h WHERE h.TicketId = t.TicketId AND h.HandledByUserId = t.CurrentOwnerEmployeeId))
) AS x
GROUP BY x.Finding;

SELECT TOP (50) t.TicketNumber, t.TicketStatus, t.CurrentOwnerEmployeeId AS OwnerId,
       (SELECT STRING_AGG(CONVERT(nvarchar(36), h.HandledByUserId), N', ') FROM dbo.TicketInteractionHandlers h WHERE h.TicketId = t.TicketId) AS RecordedHandlers,
       CASE WHEN EXISTS (SELECT 1 FROM dbo.TicketWorkflowEvents e WHERE e.TicketId = t.TicketId AND e.EventType = 17 AND ISNULL(e.TransferReasonCode, 0) <> 5)
            THEN 'person transfer on record' ELSE '' END AS HasPersonTransfer
FROM dbo.Tickets AS t
WHERE t.TicketStatus IN (1, 2, 3, 4)
  AND EXISTS (SELECT 1 FROM dbo.TicketInteractionHandlers h WHERE h.TicketId = t.TicketId)
  AND (t.CurrentOwnerEmployeeId IS NULL
       OR NOT EXISTS (SELECT 1 FROM dbo.TicketInteractionHandlers h WHERE h.TicketId = t.TicketId AND h.HandledByUserId = t.CurrentOwnerEmployeeId))
ORDER BY t.CreatedAtUtc DESC;

