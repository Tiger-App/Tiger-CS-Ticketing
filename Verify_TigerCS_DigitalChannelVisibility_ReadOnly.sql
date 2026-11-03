/*
================================================================================
 Verify_TigerCS_DigitalChannelVisibility_ReadOnly.sql
 READ-ONLY. SELECT statements only: no INSERT / UPDATE / DELETE / DDL. Safe from a db_datareader login.

 Answers "where are the WhatsApp / Instagram / Facebook / Website Chat tickets?" from the data itself. The Tickets list's Channel filter
 matches the ORIGINATING interaction (TicketInteractions.IsOriginatingInteraction = 1) by TicketInteractions.ChannelId, so a ticket that
 never carried that exact ChannelId cannot be found under that channel, however it is routed or assigned.

   1. Channel catalogue: the rows must exist, be active and Genesys-enabled (Instagram = 10, Facebook = 11, WhatsApp = 6, Live Chat = 7).
      An inactive/missing row makes Genesys ingestion fail with 'channel-not-configured'.
   2. Genesys originating interactions per channel, Open vs Closed. Before this fix every Instagram or Facebook conversation Genesys
      reported as 'SocialMedia' sits under SOCIAL_DM (4) - they are NOT lost, only filed under the generic social channel.
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
SELECT c.Code AS ChannelCode,
       SUM(CASE WHEN t.CurrentOwnerEmployeeId IS NULL THEN 1 ELSE 0 END) AS Unowned,
       SUM(CASE WHEN t.CurrentOwnerEmployeeId IS NOT NULL AND EXISTS (
                SELECT 1 FROM dbo.UserDepartmentAssignments a JOIN dbo.Departments d ON d.DepartmentId = a.DepartmentId
                WHERE a.EmployeeId = t.CurrentOwnerEmployeeId AND d.Code = N'CC') THEN 1 ELSE 0 END) AS OwnedByCallCenterMember,
       SUM(CASE WHEN t.CurrentOwnerEmployeeId IS NOT NULL AND NOT EXISTS (
                SELECT 1 FROM dbo.UserDepartmentAssignments a JOIN dbo.Departments d ON d.DepartmentId = a.DepartmentId
                WHERE a.EmployeeId = t.CurrentOwnerEmployeeId AND d.Code = N'CC') THEN 1 ELSE 0 END) AS OwnedByOthers
FROM dbo.Tickets AS t
JOIN dbo.TicketInteractions AS i ON i.TicketId = t.TicketId AND i.IsOriginatingInteraction = 1 AND i.Source = 2
JOIN dbo.Channels AS c ON c.ChannelId = i.ChannelId
WHERE i.ChannelId <> 1
GROUP BY c.Code ORDER BY c.Code;

-- 6. Invariants - every count must be 0 -------------------------------------------------------------------------------------------------
SELECT 'ticket with two current assignments' AS Invariant, COUNT(*) AS Violations
FROM (SELECT TicketId FROM dbo.TicketAssignments WHERE IsCurrent = 1 GROUP BY TicketId HAVING COUNT(*) > 1) AS x
UNION ALL
SELECT 'owner differs from the current assignment', COUNT(*)
FROM dbo.Tickets AS t
JOIN dbo.TicketAssignments AS a ON a.TicketId = t.TicketId AND a.IsCurrent = 1
WHERE t.CurrentOwnerEmployeeId IS NULL OR t.CurrentOwnerEmployeeId <> a.AssignedEmployeeId;
