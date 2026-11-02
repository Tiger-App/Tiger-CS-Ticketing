/*
 Test fixture for the Call Center backlog-distribution scripts. DISPOSABLE SQL SERVER ONLY (database must be named TigerCS_Ticketing_Prod
 inside a throw-away container - the scripts' own name guard requires it). Schema comes from the repo's real migration scripts, not from this file.

 Eligible agents (text/.NET Guid order differs from SQL Server uniqueidentifier order on purpose):
   A1 00000000-0000-0000-0000-000000000002 (CS Agent)         A2 00000000-0000-0000-0001-000000000000 (CS Agent)
   A3 7a000000-0000-0000-0000-000000000001 (CS Agent)         DE f0000000-0000-0000-0000-000000000009 (Department Employee)
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
 ('DE',   'f0000000-0000-0000-0000-000000000009', N'Department Employee', 0, 0, 1),
 ('SUPV', 'a1000000-0000-0000-0000-000000000001', N'CS Supervisor',       0, 0, 1),
 ('RO',   'a2000000-0000-0000-0000-000000000001', N'Reporting User',      0, 0, 1),
 ('CHAIR','a3000000-0000-0000-0000-000000000001', N'Chairman/CEO',        0, 0, 1),
 ('DEACT','a4000000-0000-0000-0000-000000000001', N'CS Agent',            1, 0, 1),
 ('LOCK', 'a5000000-0000-0000-0000-000000000001', N'CS Agent',            0, 1, 1),
 ('SVC',  'a6000000-0000-0000-0000-000000000001', N'CS Agent',            0, 0, 1),
 ('FINEMP','b0000000-0000-0000-0000-000000000001', N'Department Employee', 0, 0, 0);

INSERT dbo.AspNetUsers (Id, UserName, NormalizedUserName, Email, EmailConfirmed, PhoneNumberConfirmed, TwoFactorEnabled, LockoutEnabled, LockoutEnd, AccessFailedCount)
SELECT Id, LOWER(K) + N'@test', UPPER(K) + N'@TEST', LOWER(K) + N'@test', 1, 0, 0, 1, CASE WHEN Locked = 1 THEN DATEADD(year, 1, SYSDATETIMEOFFSET()) END, 0 FROM @P;
INSERT dbo.Employees (EmployeeId, DisplayName, IsGeynessStaff, DeactivatedAtUtc, CreatedAtUtc)
SELECT Id, K + N' Person', 0, CASE WHEN Deact = 1 THEN SYSUTCDATETIME() END, SYSUTCDATETIME() FROM @P;
INSERT dbo.AspNetUserRoles (UserId, RoleId) SELECT p.Id, r.Id FROM @P p JOIN @Roles r ON r.Name = p.RoleName;
INSERT dbo.UserDepartmentAssignments (EmployeeId, DepartmentId, IsPrimary, AssignedAtUtc) SELECT Id, @CC, 1, SYSUTCDATETIME() FROM @P WHERE InCC = 1;
INSERT dbo.UserDepartmentAssignments (EmployeeId, DepartmentId, IsPrimary, AssignedAtUtc) SELECT Id, @FIN, 1, SYSUTCDATETIME() FROM @P WHERE K = 'FINEMP';

-- ---------------------------------------------------------------------------------------------------------------------
-- tickets
-- ---------------------------------------------------------------------------------------------------------------------
DECLARE @Base datetime2 = '2026-09-01T08:00:00';
DECLARE @FinEmp uniqueidentifier = 'b0000000-0000-0000-0000-000000000001', @A1 uniqueidentifier = '00000000-0000-0000-0000-000000000002';

-- ELIGIBLE (62): unowned, Open/InProgress/Pending, no current assignment. Counts: CC 6, SUP 20, FIN 16, LEG 12, OLD 8.
;WITH n AS (SELECT TOP (62) ROW_NUMBER() OVER (ORDER BY (SELECT 1)) AS i FROM sys.all_objects)
INSERT dbo.Tickets (TicketNumber, OriginatingDepartmentId, CurrentDepartmentId, CurrentOwnerEmployeeId, CategoryId, PriorityId, TicketStatus,
                    VerificationStatus, EscalationLevel, SlaState, RequestSummary, ReopenCount, CreatedAtUtc)
SELECT N'E-' + RIGHT(N'000' + CONVERT(nvarchar(10), i), 4),
       CASE WHEN i BETWEEN 7 AND 11 THEN @CC ELSE d.DepartmentId END,            -- 5 SUP tickets originated in CC and were later transferred
       d.DepartmentId, NULL, CASE WHEN i % 5 = 0 THEN NULL ELSE c.CategoryId END,  -- every 5th is unclassified
       CASE WHEN i % 5 = 0 THEN NULL ELSE ((i % 4) + 1) END,
       (i % 4) + 1,                                                               -- 1 Open, 2 InProgress, 3 PendingCustomer, 4 PendingThirdParty(legacy)
       CASE WHEN i % 2 = 0 THEN 1 ELSE 0 END, 0, ((i % 5) + 1), N'eligible ticket ' + CONVERT(nvarchar(10), i), 0,
       DATEADD(minute, (i * 37) % 101, DATEADD(day, i % 9, @Base))               -- creation order deliberately != id order
FROM n
CROSS APPLY (SELECT CASE WHEN i <= 6 THEN @CC WHEN i <= 26 THEN @SUP WHEN i <= 42 THEN @FIN WHEN i <= 54 THEN @LEG ELSE @OLD END AS DepartmentId) d
LEFT JOIN dbo.Categories c ON c.DepartmentId = d.DepartmentId;

-- a few eligible tickets carry an older SUPERSEDED assignment row (history) - still eligible
INSERT dbo.TicketAssignments (TicketId, AssignedEmployeeId, AssignedDepartmentId, AssignedAtUtc, AssigningActorEmployeeId, IsCurrent)
SELECT TicketId, @FinEmp, CurrentDepartmentId, DATEADD(day, -3, SYSUTCDATETIME()), NULL, 0 FROM dbo.Tickets WHERE TicketNumber IN (N'E-0010', N'E-0011', N'E-0012');

-- EXCLUDED: unowned Resolved (5) / Closed (6)
INSERT dbo.Tickets (TicketNumber, OriginatingDepartmentId, CurrentDepartmentId, CurrentOwnerEmployeeId, CategoryId, PriorityId, TicketStatus, VerificationStatus, EscalationLevel, SlaState, RequestSummary, ReopenCount, CreatedAtUtc)
SELECT N'X-RES-' + CONVERT(nvarchar(5), v.i), @FIN, @FIN, NULL, NULL, NULL, 5, 0, 0, 3, N'resolved', 0, @Base FROM (VALUES (1),(2),(3),(4),(5)) v(i);
INSERT dbo.Tickets (TicketNumber, OriginatingDepartmentId, CurrentDepartmentId, CurrentOwnerEmployeeId, CategoryId, PriorityId, TicketStatus, VerificationStatus, EscalationLevel, SlaState, RequestSummary, ReopenCount, CreatedAtUtc)
SELECT N'X-CLO-' + CONVERT(nvarchar(5), v.i), @SUP, @SUP, NULL, NULL, NULL, 6, 0, 0, 3, N'closed', 0, @Base FROM (VALUES (1),(2),(3)) v(i);

-- EXCLUDED: already OWNED by an employee, with a current assignment (6 in FIN, 4 in CC)
INSERT dbo.Tickets (TicketNumber, OriginatingDepartmentId, CurrentDepartmentId, CurrentOwnerEmployeeId, CategoryId, PriorityId, TicketStatus, VerificationStatus, EscalationLevel, SlaState, RequestSummary, ReopenCount, CreatedAtUtc)
SELECT N'X-OWN-FIN-' + CONVERT(nvarchar(5), v.i), @FIN, @FIN, @FinEmp, NULL, NULL, 1, 0, 0, 1, N'owned', 0, @Base FROM (VALUES (1),(2),(3),(4),(5),(6)) v(i);
INSERT dbo.Tickets (TicketNumber, OriginatingDepartmentId, CurrentDepartmentId, CurrentOwnerEmployeeId, CategoryId, PriorityId, TicketStatus, VerificationStatus, EscalationLevel, SlaState, RequestSummary, ReopenCount, CreatedAtUtc)
SELECT N'X-OWN-CC-' + CONVERT(nvarchar(5), v.i), @CC, @CC, @A1, NULL, NULL, 2, 0, 0, 1, N'owned', 0, @Base FROM (VALUES (1),(2),(3),(4)) v(i);
INSERT dbo.TicketAssignments (TicketId, AssignedEmployeeId, AssignedDepartmentId, AssignedAtUtc, AssigningActorEmployeeId, IsCurrent)
SELECT TicketId, CurrentOwnerEmployeeId, CurrentDepartmentId, SYSUTCDATETIME(), CurrentOwnerEmployeeId, 1 FROM dbo.Tickets WHERE TicketNumber LIKE N'X-OWN-%';

-- EXCLUDED: owned but no assignment row (legacy data) x2
INSERT dbo.Tickets (TicketNumber, OriginatingDepartmentId, CurrentDepartmentId, CurrentOwnerEmployeeId, CategoryId, PriorityId, TicketStatus, VerificationStatus, EscalationLevel, SlaState, RequestSummary, ReopenCount, CreatedAtUtc)
SELECT N'X-LEGACY-' + CONVERT(nvarchar(5), v.i), @FIN, @FIN, @FinEmp, NULL, NULL, 1, 0, 0, 1, N'legacy owned', 0, @Base FROM (VALUES (1),(2)) v(i);

-- EXCLUDED: data drift - no owner but a CURRENT assignment row x3 (never touched, never given a second current row)
INSERT dbo.Tickets (TicketNumber, OriginatingDepartmentId, CurrentDepartmentId, CurrentOwnerEmployeeId, CategoryId, PriorityId, TicketStatus, VerificationStatus, EscalationLevel, SlaState, RequestSummary, ReopenCount, CreatedAtUtc)
SELECT N'X-DRIFT-' + CONVERT(nvarchar(5), v.i), @SUP, @SUP, NULL, NULL, NULL, 1, 0, 0, 1, N'drift', 0, @Base FROM (VALUES (1),(2),(3)) v(i);
INSERT dbo.TicketAssignments (TicketId, AssignedEmployeeId, AssignedDepartmentId, AssignedAtUtc, AssigningActorEmployeeId, IsCurrent)
SELECT TicketId, @FinEmp, CurrentDepartmentId, SYSUTCDATETIME(), NULL, 1 FROM dbo.Tickets WHERE TicketNumber LIKE N'X-DRIFT-%';

-- existing history that must survive: status history, notes, audit, workflow events
INSERT dbo.TicketStatusHistory (TicketId, Dimension, OldValue, NewValue, ActorEmployeeId, ActorIsSystem, Note, CorrelationId, OccurredAtUtc)
SELECT TOP (12) TicketId, 1, NULL, 1, NULL, 1, N'created', NEWID(), CreatedAtUtc FROM dbo.Tickets WHERE TicketNumber LIKE N'E-%' ORDER BY TicketId;
INSERT dbo.TicketNotes (TicketId, NoteText, AuthorEmployeeId, CreatedAtUtc)
SELECT TOP (3) TicketId, N'pre-existing note', @FinEmp, DATEADD(day, 1, CreatedAtUtc) FROM dbo.Tickets WHERE TicketNumber LIKE N'E-%' ORDER BY TicketId;
INSERT dbo.AuditEntries (ActorEmployeeId, Action, EntityType, EntityId, BeforeValue, AfterValue, CorrelationId, OccurredAtUtc)
SELECT NULL, N'Create', N'Ticket', CONVERT(nvarchar(100), TicketId), NULL, N'seed', NEWID(), CreatedAtUtc FROM dbo.Tickets;
INSERT dbo.TicketWorkflowEvents (TicketId, EventType, OccurredAtUtc, ActorEmployeeId, Note, CorrelationId, FromDepartmentId, ToDepartmentId, TransferReasonCode)
SELECT TicketId, 17, DATEADD(hour, 1, CreatedAtUtc), NULL, N'earlier routing', NEWID(), @CC, CurrentDepartmentId, 5 FROM dbo.Tickets WHERE TicketNumber IN (N'E-0007', N'E-0008', N'E-0009', N'E-0010', N'E-0011');

SELECT (SELECT COUNT(*) FROM dbo.Tickets) AS Tickets, (SELECT COUNT(*) FROM dbo.Tickets WHERE TicketNumber LIKE N'E-%') AS Eligible, (SELECT COUNT(*) FROM dbo.Employees) AS Employees;
