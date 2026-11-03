/*
================================================================================
 Diagnose_TigerCS_PROD_Ticket187_ClosureTimeline_ReadOnly.sql          (SELECT ONLY - NO WRITES)
 Database : TigerCS_Ticketing_Prod

 WHY: ticket TG-FM-20261103-0001 (TicketId 187, Instagram) was closed for customer inactivity at 08:26 (Asia/Dubai) while Conversation
      History shows a human agent (Kawther Benbrahim) replying around 09:05, and the ticket still sits unassigned in the Facilities Management
      queue. This script lists, in ONE chronological result set, every stored fact that bears on that sequence, and then answers the three
      questions with evidence: separate interaction session? delayed event? or an actual premature closure?

 READ-ONLY: only SELECTs; READ UNCOMMITTED; safe to run any time from a db_datareader login. Change @TicketId to inspect another ticket.
 TIMES: stored UTC; every output also carries the Asia/Dubai (UTC+4, no daylight saving) time.
================================================================================
*/
SET NOCOUNT ON;
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

DECLARE @ExpectedDb sysname = N'TigerCS_Ticketing_Prod';
IF DB_NAME() <> @ExpectedDb
BEGIN
    DECLARE @m nvarchar(400) = N'ABORTED: connected to [' + ISNULL(DB_NAME(), N'?') + N'], this diagnostic is for [' + @ExpectedDb + N'] only.';
    THROW 50500, @m, 1;
END;

DECLARE @TicketId bigint = 187;

-- 1. The ticket as it is now
SELECT t.TicketId, t.TicketNumber, t.TicketStatus, t.CurrentDepartmentId, t.CurrentOwnerEmployeeId,
       t.CreatedAtUtc, DATEADD(HOUR, 4, t.CreatedAtUtc) AS CreatedAtDubai, t.CreatedByEmployeeId, t.FirstHumanResponseAtUtc
FROM dbo.Tickets t WHERE t.TicketId = @TicketId;

-- 2. Its interactions (separate sessions show up as separate rows / conversation ids)
SELECT i.TicketInteractionId, i.IsOriginatingInteraction, i.Source, i.ChannelId, i.GenesysConversationId, i.GenesysServiceRequestId,
       i.InteractionStartedAtUtc, DATEADD(HOUR, 4, COALESCE(i.InteractionStartedAtUtc, i.CreatedAtUtc)) AS StartedDubai,
       i.EndedAtUtc, i.EndReason, i.InactivityClosedAtUtc, DATEADD(HOUR, 4, i.InactivityClosedAtUtc) AS InactivityClosedDubai,
       i.LastCustomerInteractionAtUtc, i.RequiresHumanFollowUp, i.HandledByUserId, i.GenesysAgentId, i.GenesysAgentName, i.CreatedAtUtc
FROM dbo.TicketInteractions i WHERE i.TicketId = @TicketId ORDER BY COALESCE(i.InteractionStartedAtUtc, i.CreatedAtUtc);

-- 3. ONE chronological result set across every store
SELECT x.AtUtc, DATEADD(HOUR, 4, x.AtUtc) AS AtDubai, x.Store, x.Event, x.Detail
FROM (
    SELECT t.CreatedAtUtc AS AtUtc, 'Tickets' AS Store, 'Ticket created' AS Event, t.TicketNumber AS Detail FROM dbo.Tickets t WHERE t.TicketId = @TicketId
    UNION ALL
    SELECT m.SentAtUtc, 'Transcript (sent)', CONCAT('Message #', m.Sequence, ' by ', CASE m.Sender WHEN 1 THEN 'Customer' WHEN 2 THEN 'HumanAgent' WHEN 3 THEN 'System' WHEN 4 THEN 'VirtualAgent' END),
           CONCAT('interaction ', m.TicketInteractionId, '; sender ', COALESCE(m.SenderName, '(none)'))
    FROM dbo.TicketInteractionMessages m JOIN dbo.TicketInteractions i ON i.TicketInteractionId = m.TicketInteractionId WHERE i.TicketId = @TicketId
    UNION ALL
    SELECT a.OccurredAtUtc, 'Audit (received)', a.Action, CONCAT('entity ', a.EntityType, ':', a.EntityId, '; after: ', LEFT(a.AfterValue, 300))
    FROM dbo.AuditEntries a
    WHERE (a.EntityType = 'Ticket' AND a.EntityId = CONVERT(varchar(20), @TicketId))
       OR (a.EntityType = 'GenesysConversation' AND a.EntityId IN (SELECT GenesysConversationId FROM dbo.TicketInteractions WHERE TicketId = @TicketId))
    UNION ALL
    SELECT h.OccurredAtUtc, 'TicketStatusHistory', CONCAT('Dimension ', h.Dimension, ': ', h.OldValue, ' -> ', h.NewValue),
           CONCAT('actor ', COALESCE(CONVERT(varchar(40), h.ActorEmployeeId), 'system'), '; note: ', COALESCE(h.Note, '(none)'))
    FROM dbo.TicketStatusHistory h WHERE h.TicketId = @TicketId
    UNION ALL
    SELECT COALESCE(ih.ConnectedAtUtc, ih.RecordedAtUtc), 'InteractionHandlers', 'Human agent connected',
           CONCAT('user ', ih.HandledByUserId, '; genesys ', ih.GenesysAgentUserId, '; connected ', COALESCE(CONVERT(varchar(30), ih.ConnectedAtUtc, 126), '(not supplied)'),
                  '; recorded ', CONVERT(varchar(30), ih.RecordedAtUtc, 126))
    FROM dbo.TicketInteractionHandlers ih WHERE ih.TicketId = @TicketId
    UNION ALL
    SELECT ho.RequestedAtUtc, 'TicketAgentHandoffs', 'Handoff requested', CONCAT('status ', ho.Status, '; trigger ', COALESCE(CONVERT(varchar(10), ho.[Trigger]), '(not stated)'), '; reason ', COALESCE(ho.RequestReason, '(none)'))
    FROM dbo.TicketAgentHandoffs ho WHERE ho.TicketId = @TicketId
    UNION ALL
    SELECT ho.StartedAtUtc, 'TicketAgentHandoffs', 'Handoff accepted', CONCAT('employee ', ho.AssignedEmployeeId) FROM dbo.TicketAgentHandoffs ho WHERE ho.TicketId = @TicketId AND ho.StartedAtUtc IS NOT NULL
    UNION ALL
    SELECT w.OccurredAtUtc, 'TicketWorkflowEvents', CONCAT('Event type ', w.EventType), COALESCE(w.Note, '(no note)') FROM dbo.TicketWorkflowEvents w WHERE w.TicketId = @TicketId
    UNION ALL
    SELECT a.AssignedAtUtc, 'TicketAssignments', 'Owner assigned', CONCAT('employee ', a.AssignedEmployeeId, '; department ', a.AssignedDepartmentId, '; current ', a.IsCurrent) FROM dbo.TicketAssignments a WHERE a.TicketId = @TicketId
) x
ORDER BY x.AtUtc;

-- 4. The verdict, from the evidence above
DECLARE @ClosedAt datetime2 = (SELECT MIN(h.OccurredAtUtc) FROM dbo.TicketStatusHistory h WHERE h.TicketId = @TicketId AND h.Dimension = 1 AND h.NewValue = 6); -- 6 = Closed
SELECT @ClosedAt AS ClosedAtUtc, DATEADD(HOUR, 4, @ClosedAt) AS ClosedAtDubai;

SELECT i.TicketInteractionId, i.GenesysConversationId,
       COALESCE(i.InteractionStartedAtUtc, i.CreatedAtUtc) AS StartedAtUtc,
       (SELECT COUNT(*) FROM dbo.TicketInteractionMessages m WHERE m.TicketInteractionId = i.TicketInteractionId AND m.Sender = 2 AND m.SentAtUtc > @ClosedAt) AS HumanMessagesAfterClosure,
       (SELECT COUNT(*) FROM dbo.TicketInteractionMessages m WHERE m.TicketInteractionId = i.TicketInteractionId AND m.Sender = 2 AND m.SentAtUtc <= @ClosedAt) AS HumanMessagesBeforeClosure,
       (SELECT COUNT(*) FROM dbo.TicketInteractionMessages m WHERE m.TicketInteractionId = i.TicketInteractionId AND m.Sender = 1 AND m.SentAtUtc > @ClosedAt) AS CustomerMessagesAfterClosure,
       CASE
         WHEN COALESCE(i.InteractionStartedAtUtc, i.CreatedAtUtc) > @ClosedAt THEN 'SEPARATE SESSION: this conversation started after the closure'
         WHEN EXISTS (SELECT 1 FROM dbo.TicketInteractionHandlers ih WHERE ih.TicketInteractionId = i.TicketInteractionId AND ih.ConnectedAtUtc <= @ClosedAt AND ih.RecordedAtUtc > @ClosedAt)
           THEN 'DELAYED EVENT: an agent connected before the closure but TigerCS recorded it after'
         WHEN EXISTS (SELECT 1 FROM dbo.TicketInteractionMessages m WHERE m.TicketInteractionId = i.TicketInteractionId AND m.Sender = 2 AND m.SentAtUtc > @ClosedAt)
           THEN 'PREMATURE CLOSURE: a human replied in the SAME conversation after it was closed for inactivity'
         ELSE 'no human activity after the closure in this conversation'
       END AS Verdict
FROM dbo.TicketInteractions i WHERE i.TicketId = @TicketId;
-- Note: audit "GenesysTranscriptAppended" entries (section 3, 'Audit (received)') show when TigerCS RECEIVED each batch of transcript lines; a human
--       line SENT before the closure but RECEIVED after it is also a DELAYED EVENT (compare the 'Transcript (sent)' and 'Audit (received)' rows).
