/*
 Test fixture for the Call Center backlog-distribution scripts (scope: EVERY Open ticket, owned or not). DISPOSABLE SQL SERVER ONLY (database must be
 named TigerCS_Ticketing_Prod inside a throw-away container - the scripts' own name guard requires it). Schema comes from the repo's real migration
 scripts, not from this file.

 ELIGIBLE (99):   E-*      62  Open, unowned, no current assignment            (CC 6, SUP 20, FIN 16, LEG 12, OLD 8)
                  F-*      10  Open, unowned, carry decided approvals / ended or human-flagged / handed-off / taken / inactive AI history
                  D-*       3  Open, NO owner but a CURRENT assignment row (data drift)  -> row superseded
                  O-*      24  Open, OWNED (the owner is replaced):
                                O-FIN 6 (FIN, FINEMP, assigned by FINEMP)   O-SUP 4 (SUP, SUPEMP, assigned by SUPV; 2 carry older superseded rows)
                                O-CCA1 4 (already in CC, owned by eligible agent A1, automatic assignment)   O-CCSUPV 2 (CC, owned by the supervisor)
                                O-DEACT 2 (LEG, deactivated owner)   O-LOCK 1 (inactive dept OLD, locked owner)   O-SVC 2 (SUP, service account owner)
                                O-LEG 2 (FIN, owner but NO assignment row = legacy)   O-RESP 1 (FIN, first human response recorded, still Open)
 HELD BACK (14):  H-APR 6 + H-BOTH 1 + O-APR 1 (pending approval, O-APR is owned)  = 8     H-AI 5 + O-AI 1 (active AI conversation, O-AI is owned) = 6
 NEVER TOUCHED:   N-INP / N-PEN / N-PEN4 (InProgress / PendingCustomer / legacy 4, unowned), N-OWNINP / N-OWNPEN (owned), N-APR (InProgress with a pending
                  approval), X-RES / X-CLO (unowned Resolved / Closed), X-RESOWN / X-CLOOWN (owned Resolved / Closed)
 Counts: eligible 99, moved into CC 87 (all but the 12 already in CC), owner replaced 24, held 8 + 6, total Open 113 (26 owned).
 Eligible agents (text/.NET Guid order differs from SQL Server uniqueidentifier order on purpose):
   A1 00000000-0000-0000-0000-000000000002 (CS Agent)         A2 00000000-0000-0000-0001-000000000000 (CS Agent)
   A3 7a000000-0000-0000-0000-000000000001 (CS Agent)         DE f0000000-0000-0000-0000-000000000009 (Department Employee)
   A4 7b000000-0000-0000-0000-000000000004 (CS Agent) has an explicit DENY of Tickets.Resolve -> excluded from the pool by default
 Excluded CC members: SUPV (CS Supervisor), RO (Reporting User), CHAIR (Chairman/CEO), DEACT (deactivated), LOCK (locked out), SVC (service account)
*/
SET NOCOUNT ON; SET QUOTED_IDENTIFIER ON;

INSERT dbo.Departments (Name, Code, IsActive) VALUES (N'Call Center', N'CC', 1), (N'Support', N'SUP', 1), (N'Finance', N'FIN', 1), (N'Legal', N'LEG', 1), (N'Retired Dept', N'OLD', 0);
DECLARE @CC int = (SELECT DepartmentId FROM dbo.Departments WHERE Code = N'CC'), @SUP int = (SELECT DepartmentId FROM dbo.Departments WHERE Code = N'SUP'),
        @FIN int = (SELECT DepartmentId FROM dbo.Departments WHERE Code = N'FIN'), @LEG int = (SELECT DepartmentId FROM dbo.Departments WHERE Code = N'LEG'),
        @OLD int = (SELECT DepartmentId FROM dbo.Departments WHERE Code = N'OLD');

INSERT dbo.Priorities (PriorityId, Name, DisplayOrder) VALUES (1, N'Critical', 1), (2, N'High', 2), (3, N'Medium', 3), (4, N'Low', 4);
INSERT dbo.Categories (Name, ParentCategoryId, DepartmentId, IsActive) VALUES (N'General CC', NULL, @CC, 1), (N'General SUP', NULL, @SUP, 1), (N'General FIN', NULL, @FIN, 1);

DECLARE @Roles TABLE (Id uniqueidentifier, Name nvarchar(256));
INSERT @Roles VALUES (NEWID(), N'CS Agent'), (NEWID(), N'Department Employee'), (NEWID(), N'Department Head'), (NEWID(), N'CS Supervisor'),
                     (NEWID(), N'Reporting User'), (NEWID(), N'Chairman/CEO');
INSERT dbo.AspNetRoles (Id, Description, Name) SELECT Id, Name, Name FROM @Roles;

-- people: Key, Id, Roles, Deactivated, Locked, InCC
DECLARE @P TABLE (K varchar(8), Id uniqueidentifier, RoleName nvarchar(256), Deact bit, Locked bit, InCC bit);
INSERT @P VALUES
 ('A1',   '00000000-0000-0000-0000-000000000002', N'CS Agent',            0, 0, 1),
 ('A2',   '00000000-0000-0000-0001-000000000000', N'CS Agent',            0, 0, 1),
 ('A3',   '7a000000-0000-0000-0000-000000000001', N'CS Agent',            0, 0, 1),
 ('A4',   '7b000000-0000-0000-0000-000000000004', N'CS Agent',            0, 0, 1),
 ('DE',   'f0000000-0000-0000-0000-000000000009', N'Department Employee', 0, 0, 1),
 ('SUPV', 'a1000000-0000-0000-0000-000000000001', N'CS Supervisor',       0, 0, 1),
 ('RO',   'a2000000-0000-0000-0000-000000000001', N'Reporting User',      0, 0, 1),
 ('CHAIR','a3000000-0000-0000-0000-000000000001', N'Chairman/CEO',        0, 0, 1),
 ('DEACT','a4000000-0000-0000-0000-000000000001', N'CS Agent',            1, 0, 1),
 ('LOCK', 'a5000000-0000-0000-0000-000000000001', N'CS Agent',            0, 1, 1),
 ('SVC',  'a6000000-0000-0000-0000-000000000001', N'CS Agent',            0, 0, 1),
 ('FINEMP','b0000000-0000-0000-0000-000000000001', N'Department Employee', 0, 0, 0),
 ('SUPEMP','b1000000-0000-0000-0000-000000000001', N'Department Employee', 0, 0, 0);

INSERT dbo.AspNetUsers (Id, UserName, NormalizedUserName, Email, EmailConfirmed, PhoneNumberConfirmed, TwoFactorEnabled, LockoutEnabled, LockoutEnd, AccessFailedCount)
SELECT Id, LOWER(K) + N'@test', UPPER(K) + N'@TEST', LOWER(K) + N'@test', 1, 0, 0, 1, CASE WHEN Locked = 1 THEN DATEADD(year, 1, SYSDATETIMEOFFSET()) END, 0 FROM @P;
INSERT dbo.Employees (EmployeeId, DisplayName, IsGeynessStaff, DeactivatedAtUtc, CreatedAtUtc)
SELECT Id, K + N' Person', 0, CASE WHEN Deact = 1 THEN SYSUTCDATETIME() END, SYSUTCDATETIME() FROM @P;
INSERT dbo.AspNetUserRoles (UserId, RoleId) SELECT p.Id, r.Id FROM @P p JOIN @Roles r ON r.Name = p.RoleName;
INSERT dbo.UserDepartmentAssignments (EmployeeId, DepartmentId, IsPrimary, AssignedAtUtc) SELECT Id, @CC, 1, SYSUTCDATETIME() FROM @P WHERE InCC = 1;
INSERT dbo.UserDepartmentAssignments (EmployeeId, DepartmentId, IsPrimary, AssignedAtUtc) SELECT Id, @FIN, 1, SYSUTCDATETIME() FROM @P WHERE K = 'FINEMP';
INSERT dbo.UserDepartmentAssignments (EmployeeId, DepartmentId, IsPrimary, AssignedAtUtc) SELECT Id, @SUP, 1, SYSUTCDATETIME() FROM @P WHERE K = 'SUPEMP';

-- A4 is a CS Agent with an explicit DENY of Tickets.Resolve; A3 has an explicit GRANT (a grant is not a denial: A3 stays in the pool)
IF OBJECT_ID(N'dbo.UserPermissionOverrides', N'U') IS NOT NULL
    INSERT dbo.UserPermissionOverrides (EmployeeId, PermissionKey, Effect, CreatedAtUtc, CreatedByEmployeeId, UpdatedAtUtc, UpdatedByEmployeeId)
    VALUES ('7b000000-0000-0000-0000-000000000004', N'Tickets.Resolve', 2, SYSUTCDATETIME(), NULL, SYSUTCDATETIME(), NULL),
           ('7a000000-0000-0000-0000-000000000001', N'Tickets.Transfer', 1, SYSUTCDATETIME(), NULL, SYSUTCDATETIME(), NULL),
           ('f0000000-0000-0000-0000-000000000009', N'Tickets.Close', 2, SYSUTCDATETIME(), NULL, SYSUTCDATETIME(), NULL);   -- DE: denial, but DE is outside the default pool anyway

-- ---------------------------------------------------------------------------------------------------------------------
-- tickets
-- ---------------------------------------------------------------------------------------------------------------------
DECLARE @Base datetime2 = '2026-09-01T08:00:00';
DECLARE @FinEmp uniqueidentifier = 'b0000000-0000-0000-0000-000000000001', @SupEmp uniqueidentifier = 'b1000000-0000-0000-0000-000000000001',
        @A1 uniqueidentifier = '00000000-0000-0000-0000-000000000002', @Supv uniqueidentifier = 'a1000000-0000-0000-0000-000000000001',
        @Deact uniqueidentifier = 'a4000000-0000-0000-0000-000000000001', @Lock uniqueidentifier = 'a5000000-0000-0000-0000-000000000001',
        @Svc uniqueidentifier = 'a6000000-0000-0000-0000-000000000001';

-- E-*: ELIGIBLE (62): Open, unowned, no current assignment. Counts: CC 6, SUP 20, FIN 16, LEG 12, OLD 8.
;WITH n AS (SELECT TOP (62) ROW_NUMBER() OVER (ORDER BY (SELECT 1)) AS i FROM sys.all_objects)
INSERT dbo.Tickets (TicketNumber, OriginatingDepartmentId, CurrentDepartmentId, CurrentOwnerEmployeeId, CategoryId, PriorityId, TicketStatus,
                    VerificationStatus, EscalationLevel, SlaState, RequestSummary, ReopenCount, CreatedAtUtc)
SELECT N'E-' + RIGHT(N'000' + CONVERT(nvarchar(10), i), 4),
       CASE WHEN i BETWEEN 7 AND 11 THEN @CC ELSE d.DepartmentId END,            -- 5 SUP tickets originated in CC and were later transferred
       d.DepartmentId, NULL, CASE WHEN i % 5 = 0 THEN NULL ELSE c.CategoryId END,  -- every 5th is unclassified
       CASE WHEN i % 5 = 0 THEN NULL ELSE ((i % 4) + 1) END,
       1,
       CASE WHEN i % 2 = 0 THEN 1 ELSE 0 END, 0, ((i % 5) + 1), N'eligible ticket ' + CONVERT(nvarchar(10), i), 0,
       DATEADD(minute, (i * 37) % 101, DATEADD(day, i % 9, @Base))               -- creation order deliberately != id order
FROM n
CROSS APPLY (SELECT CASE WHEN i <= 6 THEN @CC WHEN i <= 26 THEN @SUP WHEN i <= 42 THEN @FIN WHEN i <= 54 THEN @LEG ELSE @OLD END AS DepartmentId) d
LEFT JOIN dbo.Categories c ON c.DepartmentId = d.DepartmentId;

-- a few eligible tickets carry an older SUPERSEDED assignment row (history) - still eligible, row untouched
INSERT dbo.TicketAssignments (TicketId, AssignedEmployeeId, AssignedDepartmentId, AssignedAtUtc, AssigningActorEmployeeId, IsCurrent)
SELECT TicketId, @FinEmp, CurrentDepartmentId, DATEADD(day, -3, SYSUTCDATETIME()), NULL, 0 FROM dbo.Tickets WHERE TicketNumber IN (N'E-0010', N'E-0011', N'E-0012');

-- D-*: ELIGIBLE (3): Open, no owner but a CURRENT assignment row (data drift) in SUP - the stale row is superseded, a new one inserted
INSERT dbo.Tickets (TicketNumber, OriginatingDepartmentId, CurrentDepartmentId, CurrentOwnerEmployeeId, CategoryId, PriorityId, TicketStatus, VerificationStatus, EscalationLevel, SlaState, RequestSummary, ReopenCount, CreatedAtUtc)
SELECT N'D-' + CONVERT(nvarchar(5), v.i), @SUP, @SUP, NULL, NULL, NULL, 1, 0, 0, 1, N'drift', 0, DATEADD(minute, v.i * 7, @Base) FROM (VALUES (1),(2),(3)) v(i);
INSERT dbo.TicketAssignments (TicketId, AssignedEmployeeId, AssignedDepartmentId, AssignedAtUtc, AssigningActorEmployeeId, IsCurrent)
SELECT TicketId, @FinEmp, CurrentDepartmentId, DATEADD(day, -1, SYSUTCDATETIME()), NULL, 1 FROM dbo.Tickets WHERE TicketNumber LIKE N'D-%';

-- O-*: ELIGIBLE (24): Open and OWNED
INSERT dbo.Tickets (TicketNumber, OriginatingDepartmentId, CurrentDepartmentId, CurrentOwnerEmployeeId, CategoryId, PriorityId, TicketStatus, VerificationStatus, EscalationLevel, SlaState, RequestSummary, ReopenCount, CreatedAtUtc, FirstHumanResponseAtUtc)
SELECT v.n + CONVERT(nvarchar(5), i.i), v.orig, v.cur, v.owner, NULL, ((i.i % 4) + 1), 1, 0, 0, 1, N'owned open', 0, DATEADD(minute, (v.k * 13 + i.i * 5) % 97, DATEADD(day, v.k % 5, @Base)),
       CASE WHEN v.n = N'O-RESP-' THEN DATEADD(hour, 2, DATEADD(day, v.k % 5, @Base)) END
FROM (VALUES
   (N'O-FIN-',    @FIN, @FIN, @FinEmp, 6, 1),
   (N'O-SUP-',    @SUP, @SUP, @SupEmp, 4, 2),
   (N'O-CCA1-',   @CC,  @CC,  @A1,     4, 3),
   (N'O-CCSUPV-', @CC,  @CC,  @Supv,   2, 4),
   (N'O-DEACT-',  @LEG, @LEG, @Deact,  2, 5),
   (N'O-LOCK-',   @OLD, @OLD, @Lock,   1, 6),
   (N'O-SVC-',    @SUP, @SUP, @Svc,    2, 7),
   (N'O-LEG-',    @FIN, @FIN, @FinEmp, 2, 8),
   (N'O-RESP-',   @FIN, @FIN, @FinEmp, 1, 9)) v(n, orig, cur, owner, cnt, k)
JOIN (SELECT TOP (6) ROW_NUMBER() OVER (ORDER BY (SELECT 1)) AS i FROM sys.all_objects) i ON i.i <= v.cnt;
-- tickets that moved before: 2 of the O-FIN tickets originated in SUP
UPDATE dbo.Tickets SET OriginatingDepartmentId = @SUP WHERE TicketNumber IN (N'O-FIN-1', N'O-FIN-2');
-- current assignment rows (all owned groups except the legacy O-LEG pair, which has none); O-CCA1 is an AUTOMATIC pre-assignment (system actor), the rest are human assignments
INSERT dbo.TicketAssignments (TicketId, AssignedEmployeeId, AssignedDepartmentId, AssignedAtUtc, AssigningActorEmployeeId, IsCurrent)
SELECT TicketId, CurrentOwnerEmployeeId, CurrentDepartmentId, DATEADD(hour, -6, SYSUTCDATETIME()),
       CASE WHEN TicketNumber LIKE N'O-CCA1-%' THEN NULL WHEN TicketNumber LIKE N'O-SUP-%' THEN @Supv ELSE CurrentOwnerEmployeeId END, 1
FROM dbo.Tickets WHERE TicketNumber LIKE N'O-%' AND TicketNumber NOT LIKE N'O-LEG-%';
-- two owned tickets also carry older superseded rows (earlier owners)
INSERT dbo.TicketAssignments (TicketId, AssignedEmployeeId, AssignedDepartmentId, AssignedAtUtc, AssigningActorEmployeeId, IsCurrent)
SELECT TicketId, @A1, @SUP, DATEADD(day, -4, SYSUTCDATETIME()), @Supv, 0 FROM dbo.Tickets WHERE TicketNumber IN (N'O-SUP-1', N'O-SUP-2');

-- NEVER TOUCHED: non-Open tickets (unowned and owned), Resolved / Closed
INSERT dbo.Tickets (TicketNumber, OriginatingDepartmentId, CurrentDepartmentId, CurrentOwnerEmployeeId, CategoryId, PriorityId, TicketStatus, VerificationStatus, EscalationLevel, SlaState, RequestSummary, ReopenCount, CreatedAtUtc)
SELECT v.n, v.d, v.d, v.o, NULL, NULL, v.st, 0, 0, 1, N'non-open', 0, DATEADD(minute, v.i, @Base)
FROM (VALUES
  (N'N-INP-1', @SUP, NULL, 2, 1), (N'N-INP-2', @SUP, NULL, 2, 2), (N'N-INP-3', @FIN, NULL, 2, 3), (N'N-INP-4', @CC, NULL, 2, 4),
  (N'N-PEN-1', @SUP, NULL, 3, 5), (N'N-PEN-2', @LEG, NULL, 3, 6), (N'N-PEN4-1', @FIN, NULL, 4, 7),
  (N'N-OWNINP-1', @CC, @A1, 2, 8), (N'N-OWNINP-2', @FIN, @FinEmp, 2, 9), (N'N-OWNPEN-1', @FIN, @FinEmp, 3, 10),
  (N'X-RES-1', @FIN, NULL, 5, 11), (N'X-RES-2', @FIN, NULL, 5, 12), (N'X-RES-3', @FIN, NULL, 5, 13), (N'X-RES-4', @FIN, NULL, 5, 14), (N'X-RES-5', @FIN, NULL, 5, 15),
  (N'X-CLO-1', @SUP, NULL, 6, 16), (N'X-CLO-2', @SUP, NULL, 6, 17), (N'X-CLO-3', @SUP, NULL, 6, 18),
  (N'X-RESOWN-1', @FIN, @FinEmp, 5, 19), (N'X-RESOWN-2', @SUP, @SupEmp, 5, 20), (N'X-CLOOWN-1', @FIN, @FinEmp, 6, 21)) v(n, d, o, st, i);
INSERT dbo.TicketAssignments (TicketId, AssignedEmployeeId, AssignedDepartmentId, AssignedAtUtc, AssigningActorEmployeeId, IsCurrent)
SELECT TicketId, CurrentOwnerEmployeeId, CurrentDepartmentId, SYSUTCDATETIME(), CurrentOwnerEmployeeId, 1 FROM dbo.Tickets WHERE CurrentOwnerEmployeeId IS NOT NULL AND TicketNumber LIKE N'[NX]-%';

-- ---------------------------------------------------------------------------------------------------------------------
-- HOLD-BACK RULES: pending approvals and active AI conversations (Open tickets only count)
-- ---------------------------------------------------------------------------------------------------------------------
DECLARE @TkCols TABLE (n nvarchar(20), orig int, cur int, own uniqueidentifier, st tinyint);
INSERT @TkCols VALUES
 -- HELD (pending approval): 3 SUP, 2 FIN, 1 CC, + 1 owned (O-APR-1, FIN)
 (N'H-APR-1', @SUP, @SUP, NULL, 1), (N'H-APR-2', @SUP, @SUP, NULL, 1), (N'H-APR-3', @SUP, @SUP, NULL, 1), (N'H-APR-4', @FIN, @FIN, NULL, 1), (N'H-APR-5', @FIN, @FIN, NULL, 1), (N'H-APR-6', @CC, @CC, NULL, 1),
 (N'O-APR-1', @FIN, @FIN, @FinEmp, 1),
 -- HELD (active AI conversation only): 3 SUP, 2 LEG, + 1 owned (O-AI-1, SUP)
 (N'H-AI-1', @SUP, @SUP, NULL, 1), (N'H-AI-2', @SUP, @SUP, NULL, 1), (N'H-AI-3', @SUP, @SUP, NULL, 1), (N'H-AI-4', @LEG, @LEG, NULL, 1), (N'H-AI-5', @LEG, @LEG, NULL, 1),
 (N'O-AI-1', @SUP, @SUP, @SupEmp, 1),
 -- HELD (both): counted once, under the approval
 (N'H-BOTH-1', @FIN, @FIN, NULL, 1),
 -- NOT Open although they carry a pending approval / a live AI conversation: out of scope by status, never counted as held back
 (N'N-APR-1', @SUP, @SUP, NULL, 2), (N'N-AI-1', @SUP, @SUP, NULL, 2),
 -- ELIGIBLE although they carry approval / AI history
 (N'F-DEC-1', @SUP, @SUP, NULL, 1), (N'F-DEC-2', @FIN, @FIN, NULL, 1), (N'F-DEC-3', @LEG, @LEG, NULL, 1),                 -- approvals already Approved / Rejected / Cancelled
 (N'F-AIEND-1', @SUP, @SUP, NULL, 1), (N'F-AIEND-2', @FIN, @FIN, NULL, 1),                                           -- AI conversation ended
 (N'F-AIHUM-1', @LEG, @LEG, NULL, 1), (N'F-AIHUM-2', @SUP, @SUP, NULL, 1),                                           -- AI ticket flagged RequiresHumanFollowUp
 (N'F-AIHAND-1', @SUP, @SUP, NULL, 1),                                                                         -- AI conversation but a human handoff is open
 (N'F-AITAKEN-1', @FIN, @FIN, NULL, 1),                                                                        -- AI conversation already taken by a person
 (N'F-AIINACT-1', @LEG, @LEG, NULL, 1);                                                                        -- AI conversation closed for inactivity
INSERT dbo.Tickets (TicketNumber, OriginatingDepartmentId, CurrentDepartmentId, CurrentOwnerEmployeeId, CategoryId, PriorityId, TicketStatus, VerificationStatus, EscalationLevel, SlaState, RequestSummary, ReopenCount, CreatedAtUtc)
SELECT n, orig, cur, own, NULL, NULL, st, 0, 0, 1, N'hold-back fixture', 0, DATEADD(minute, ROW_NUMBER() OVER (ORDER BY n), @Base) FROM @TkCols;
INSERT dbo.TicketAssignments (TicketId, AssignedEmployeeId, AssignedDepartmentId, AssignedAtUtc, AssigningActorEmployeeId, IsCurrent)
SELECT TicketId, CurrentOwnerEmployeeId, CurrentDepartmentId, DATEADD(hour, -5, SYSUTCDATETIME()), CurrentOwnerEmployeeId, 1 FROM dbo.Tickets WHERE TicketNumber IN (N'O-APR-1', N'O-AI-1');

-- approvals: type 1 Accounting. Status 1 Pending / 2 Approved / 3 Rejected / 4 Cancelled
INSERT dbo.TicketApprovals (TicketId, ApprovalType, Status, TargetKind, RequestedByEmployeeId, RequestedAtUtc, IsCurrent, CorrelationId)
SELECT TicketId, 1, CASE WHEN TicketNumber LIKE N'H-%' OR TicketNumber LIKE N'O-APR-%' OR TicketNumber = N'N-APR-1' THEN 1 WHEN TicketNumber = N'F-DEC-1' THEN 2 WHEN TicketNumber = N'F-DEC-2' THEN 3 ELSE 4 END, 1, @FinEmp,
       DATEADD(day, -2, SYSUTCDATETIME()), 1, NEWID()
FROM dbo.Tickets WHERE TicketNumber LIKE N'H-APR-%' OR TicketNumber = N'H-BOTH-1' OR TicketNumber LIKE N'F-DEC-%' OR TicketNumber = N'O-APR-1' OR TicketNumber = N'N-APR-1';

-- Genesys (Source 2) chat (ChannelId 2) interactions
INSERT dbo.TicketInteractions (TicketId, IsOriginatingInteraction, Source, ChannelId, CustomerPhone, GenesysConversationId, CreatedAtUtc, RequiresHumanFollowUp, EndedAtUtc, InactivityClosedAtUtc, HandledByUserId)
SELECT TicketId, 1, 2, 2, N'0500000000', N'conv-' + TicketNumber, DATEADD(day, -1, SYSUTCDATETIME()),
       CASE WHEN TicketNumber LIKE N'F-AIHUM-%' THEN 1 ELSE 0 END,
       CASE WHEN TicketNumber LIKE N'F-AIEND-%' THEN DATEADD(hour, -5, SYSUTCDATETIME()) END,
       CASE WHEN TicketNumber LIKE N'F-AIINACT-%' THEN DATEADD(hour, -4, SYSUTCDATETIME()) END,
       CASE WHEN TicketNumber LIKE N'F-AITAKEN-%' THEN @A1 END
FROM dbo.Tickets WHERE TicketNumber LIKE N'H-AI-%' OR TicketNumber = N'H-BOTH-1' OR TicketNumber LIKE N'F-AI%' OR TicketNumber = N'O-AI-1' OR TicketNumber = N'N-AI-1';
INSERT dbo.TicketAgentHandoffs (TicketId, TicketInteractionId, DepartmentId, ChannelId, Status, RequestedAtUtc, CreatedAtUtc)
SELECT t.TicketId, i.TicketInteractionId, t.CurrentDepartmentId, 2, 2, SYSUTCDATETIME(), SYSUTCDATETIME()
FROM dbo.Tickets t JOIN dbo.TicketInteractions i ON i.TicketId = t.TicketId WHERE t.TicketNumber = N'F-AIHAND-1';

-- existing history that must survive: status history, notes, audit, workflow events
INSERT dbo.TicketStatusHistory (TicketId, Dimension, OldValue, NewValue, ActorEmployeeId, ActorIsSystem, Note, CorrelationId, OccurredAtUtc)
SELECT TOP (12) TicketId, 1, NULL, 1, NULL, 1, N'created', NEWID(), CreatedAtUtc FROM dbo.Tickets WHERE TicketNumber LIKE N'E-%' OR TicketNumber LIKE N'O-%' ORDER BY TicketId;
INSERT dbo.TicketNotes (TicketId, NoteText, AuthorEmployeeId, CreatedAtUtc)
SELECT TOP (3) TicketId, N'pre-existing note', @FinEmp, DATEADD(day, 1, CreatedAtUtc) FROM dbo.Tickets WHERE TicketNumber LIKE N'E-%' OR TicketNumber LIKE N'O-FIN-%' ORDER BY TicketId;
INSERT dbo.AuditEntries (ActorEmployeeId, Action, EntityType, EntityId, BeforeValue, AfterValue, CorrelationId, OccurredAtUtc)
SELECT NULL, N'Create', N'Ticket', CONVERT(nvarchar(100), TicketId), NULL, N'seed', NEWID(), CreatedAtUtc FROM dbo.Tickets;
INSERT dbo.AuditEntries (ActorEmployeeId, Action, EntityType, EntityId, BeforeValue, AfterValue, CorrelationId, OccurredAtUtc)
SELECT @Supv, N'Assign', N'Ticket', CONVERT(nvarchar(100), TicketId), NULL, LOWER(CONVERT(nvarchar(36), CurrentOwnerEmployeeId)), NEWID(), DATEADD(hour, -6, SYSUTCDATETIME()) FROM dbo.Tickets WHERE TicketNumber LIKE N'O-SUP-%';
INSERT dbo.TicketWorkflowEvents (TicketId, EventType, OccurredAtUtc, ActorEmployeeId, Note, CorrelationId, FromDepartmentId, ToDepartmentId, TransferReasonCode)
SELECT TicketId, 17, DATEADD(hour, 1, CreatedAtUtc), NULL, N'earlier routing', NEWID(), @CC, CurrentDepartmentId, 5 FROM dbo.Tickets WHERE TicketNumber IN (N'E-0007', N'E-0008', N'E-0009', N'E-0010', N'E-0011');

SELECT (SELECT COUNT(*) FROM dbo.Tickets) AS Tickets,
       (SELECT COUNT(*) FROM dbo.Tickets WHERE TicketNumber LIKE N'E-%' OR TicketNumber LIKE N'F-%' OR TicketNumber LIKE N'D-%' OR TicketNumber LIKE N'O-%' AND TicketNumber NOT IN (N'O-APR-1', N'O-AI-1')) AS Eligible,
       (SELECT COUNT(*) FROM dbo.Tickets WHERE TicketNumber LIKE N'H-%' OR TicketNumber IN (N'O-APR-1', N'O-AI-1')) AS HeldBack,
       (SELECT COUNT(*) FROM dbo.Employees) AS Employees;
