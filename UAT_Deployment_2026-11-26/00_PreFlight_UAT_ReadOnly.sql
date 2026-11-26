-- ############################################################################################
-- ## 00_PreFlight_UAT_ReadOnly.sql      TigerCS UAT - READ-ONLY pre-deployment checks
-- ## Database : TigerCsTicketing (any other database aborts).  Writes NOTHING (only a table variable in tempdb).
-- ## Run BEFORE step 01. One batch, no GO. Every row must be PASS (or INFO) before you continue.
-- ############################################################################################
SET NOCOUNT ON;
SET LOCK_TIMEOUT 10000;

IF DB_NAME() IS NULL OR DB_NAME() <> N'TigerCsTicketing'
    THROW 50600, N'ABORTED: this pre-flight is for database TigerCsTicketing (UAT). Connect to it and run again.', 1;

DECLARE @R TABLE (Id int IDENTITY(1,1), CheckName nvarchar(200), Expected nvarchar(400), Actual nvarchar(400), Status varchar(8));
DECLARE @Chain TABLE (Ord int PRIMARY KEY, MigrationId nvarchar(150));
INSERT @Chain VALUES (1, N'20261118120000_AddCustomerProfileOverrides'), (2, N'20261119081103_AddCustomerProfileAuditAndProperties'),
                     (3, N'20261119120249_AddAssignmentHistoryPreviousDepartment'), (4, N'20261120074059_AddInteractionEndReceivedAt'),
                     (5, N'20261124071552_AddEmployeeReviews');
DECLARE @Hist TABLE (MigrationId nvarchar(150) PRIMARY KEY);

IF OBJECT_ID(N'dbo.__EFMigrationsHistory', N'U') IS NULL
    INSERT @R (CheckName, Expected, Actual, Status) VALUES (N'History table', N'exists', N'MISSING', 'FAIL');
ELSE
    INSERT @Hist (MigrationId) SELECT MigrationId FROM dbo.__EFMigrationsHistory;

DECLARE @nHist int = (SELECT COUNT(*) FROM @Hist);
DECLARE @nPendingChain int = (SELECT COUNT(*) FROM @Chain c WHERE NOT EXISTS (SELECT 1 FROM @Hist h WHERE h.MigrationId = c.MigrationId));

INSERT @R (CheckName, Expected, Actual, Status)
VALUES (N'Applied migrations in history', N'55 before deployment (60 after)', CAST(@nHist AS nvarchar(10)), 'INFO');
INSERT @R (CheckName, Expected, Actual, Status)
SELECT N'Baseline migration recorded', N'recorded', CASE WHEN EXISTS (SELECT 1 FROM @Hist WHERE MigrationId = N'20261118124858_AddTicketAiAgentOwner') THEN N'recorded' ELSE N'MISSING' END,
       CASE WHEN EXISTS (SELECT 1 FROM @Hist WHERE MigrationId = N'20261118124858_AddTicketAiAgentOwner') THEN 'PASS' ELSE 'FAIL' END;
INSERT @R (CheckName, Expected, Actual, Status)
SELECT N'Chain migration ' + CAST(c.Ord AS nvarchar(2)) + N': ' + c.MigrationId, N'not recorded yet (pending)',
       CASE WHEN h.MigrationId IS NULL THEN N'pending' ELSE N'already recorded' END, CASE WHEN h.MigrationId IS NULL THEN 'PASS' ELSE 'INFO' END
FROM @Chain c LEFT JOIN @Hist h ON h.MigrationId = c.MigrationId;
INSERT @R (CheckName, Expected, Actual, Status)
SELECT N'History row newer than the last chain migration', N'none', ISNULL(MAX(h.MigrationId), N'none'), CASE WHEN COUNT(*) = 0 THEN 'PASS' ELSE 'FAIL' END
FROM @Hist h WHERE h.MigrationId > N'20261124071552_AddEmployeeReviews';
INSERT @R (CheckName, Expected, Actual, Status)
SELECT N'History row unknown to main (other than the chain)', N'none', ISNULL(MIN(h.MigrationId), N'none'), CASE WHEN COUNT(*) = 0 THEN 'PASS' ELSE 'WARN' END
FROM @Hist h WHERE h.MigrationId NOT IN (SELECT MigrationId FROM @Chain) AND h.MigrationId > N'20261118124858_AddTicketAiAgentOwner';

-- tables the five migrations build on
INSERT @R (CheckName, Expected, Actual, Status)
SELECT N'Prerequisite table dbo.' + n, N'exists', CASE WHEN OBJECT_ID(N'dbo.' + n, N'U') IS NULL THEN N'MISSING' ELSE N'exists' END, CASE WHEN OBJECT_ID(N'dbo.' + n, N'U') IS NULL THEN 'FAIL' ELSE 'PASS' END
FROM (VALUES (N'Employees'), (N'TicketAssignmentHistory'), (N'TicketInteractions'), (N'Tickets')) v(n);
INSERT @R (CheckName, Expected, Actual, Status)
SELECT N'Employees primary key EmployeeId (uniqueidentifier)', N'exists',
       CASE WHEN EXISTS (SELECT 1 FROM sys.key_constraints k JOIN sys.index_columns ic ON ic.object_id = k.parent_object_id AND ic.index_id = k.unique_index_id
                         JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id JOIN sys.types t ON t.user_type_id = c.user_type_id
                         WHERE k.parent_object_id = OBJECT_ID(N'dbo.Employees') AND k.type = 'PK' AND c.name = N'EmployeeId' AND t.name = N'uniqueidentifier') THEN N'exists' ELSE N'MISSING' END,
       CASE WHEN EXISTS (SELECT 1 FROM sys.key_constraints k JOIN sys.index_columns ic ON ic.object_id = k.parent_object_id AND ic.index_id = k.unique_index_id
                         JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id JOIN sys.types t ON t.user_type_id = c.user_type_id
                         WHERE k.parent_object_id = OBJECT_ID(N'dbo.Employees') AND k.type = 'PK' AND c.name = N'EmployeeId' AND t.name = N'uniqueidentifier') THEN 'PASS' ELSE 'FAIL' END;

-- prerequisite of step 03: the filtered unique index (one current assignment per ticket) that migration 03 validates before it changes anything
INSERT @R (CheckName, Expected, Actual, Status)
VALUES (N'dbo.TicketAssignments filtered unique index (TicketId WHERE IsCurrent = 1)', N'exists',
        CASE WHEN EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.TicketAssignments') AND is_unique = 1 AND has_filter = 1 AND filter_definition LIKE N'%IsCurrent%') THEN N'exists' ELSE N'MISSING' END,
        CASE WHEN EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.TicketAssignments') AND is_unique = 1 AND has_filter = 1 AND filter_definition LIKE N'%IsCurrent%') THEN 'PASS' ELSE 'FAIL' END);

-- objects the pending migrations create must NOT exist yet (an object without its history row = hand-made / partial state)
INSERT @R (CheckName, Expected, Actual, Status)
SELECT N'Not yet present: ' + o.Obj, N'absent (migration is pending)', CASE WHEN o.Present = 1 THEN N'ALREADY EXISTS' ELSE N'absent' END,
       CASE WHEN o.Present = 0 THEN 'PASS' WHEN EXISTS (SELECT 1 FROM @Hist h WHERE h.MigrationId = o.Mig) THEN 'PASS' ELSE 'FAIL' END
FROM (VALUES
  (N'table CustomerProfileOverrides',            CASE WHEN OBJECT_ID(N'dbo.CustomerProfileOverrides', N'U') IS NULL THEN 0 ELSE 1 END, N'20261118120000_AddCustomerProfileOverrides'),
  (N'table CustomerProfileChanges',              CASE WHEN OBJECT_ID(N'dbo.CustomerProfileChanges', N'U') IS NULL THEN 0 ELSE 1 END, N'20261119081103_AddCustomerProfileAuditAndProperties'),
  (N'table CustomerPropertyAssociations',        CASE WHEN OBJECT_ID(N'dbo.CustomerPropertyAssociations', N'U') IS NULL THEN 0 ELSE 1 END, N'20261119081103_AddCustomerProfileAuditAndProperties'),
  (N'trigger TR_CustomerProfileChanges_AppendOnly', CASE WHEN EXISTS (SELECT 1 FROM sys.triggers WHERE name = N'TR_CustomerProfileChanges_AppendOnly') THEN 1 ELSE 0 END, N'20261119081103_AddCustomerProfileAuditAndProperties'),
  (N'column TicketAssignmentHistory.PreviousDepartmentId', CASE WHEN COL_LENGTH(N'dbo.TicketAssignmentHistory', N'PreviousDepartmentId') IS NULL THEN 0 ELSE 1 END, N'20261119120249_AddAssignmentHistoryPreviousDepartment'),
  (N'column TicketInteractions.EndReceivedAtUtc',  CASE WHEN COL_LENGTH(N'dbo.TicketInteractions', N'EndReceivedAtUtc') IS NULL THEN 0 ELSE 1 END, N'20261120074059_AddInteractionEndReceivedAt'),
  (N'table EmployeeReviewDevices',               CASE WHEN OBJECT_ID(N'dbo.EmployeeReviewDevices', N'U') IS NULL THEN 0 ELSE 1 END, N'20261124071552_AddEmployeeReviews'),
  (N'table EmployeeReviews',                     CASE WHEN OBJECT_ID(N'dbo.EmployeeReviews', N'U') IS NULL THEN 0 ELSE 1 END, N'20261124071552_AddEmployeeReviews')
) o(Obj, Present, Mig);

-- Collections objects (step 06)
INSERT @R (CheckName, Expected, Actual, Status)
VALUES (N'dbo.usp_CollectionsRcv_GetUnits (R004) exists', N'exists', CASE WHEN OBJECT_ID(N'dbo.usp_CollectionsRcv_GetUnits', N'P') IS NULL THEN N'MISSING' ELSE N'exists' END,
        CASE WHEN OBJECT_ID(N'dbo.usp_CollectionsRcv_GetUnits', N'P') IS NULL THEN 'FAIL' ELSE 'PASS' END),
       (N'dbo.fn_CollectionsUnitFloor (V015)', N'missing = step 06 needed', CASE WHEN OBJECT_ID(N'dbo.fn_CollectionsUnitFloor') IS NULL THEN N'missing' ELSE N'exists' END, 'INFO');

-- permissions and backup
INSERT @R (CheckName, Expected, Actual, Status)
VALUES (N'Permission ALTER on database', N'1', CAST(ISNULL(HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'ALTER'), 0) AS nvarchar(5)), CASE WHEN ISNULL(HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'ALTER'), 0) = 1 THEN 'PASS' ELSE 'FAIL' END),
       (N'Permission CREATE TABLE', N'1', CAST(ISNULL(HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'CREATE TABLE'), 0) AS nvarchar(5)), CASE WHEN ISNULL(HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'CREATE TABLE'), 0) = 1 THEN 'PASS' ELSE 'FAIL' END),
       (N'Permission CREATE FUNCTION', N'1', CAST(ISNULL(HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'CREATE FUNCTION'), 0) AS nvarchar(5)), CASE WHEN ISNULL(HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'CREATE FUNCTION'), 0) = 1 THEN 'PASS' ELSE 'FAIL' END);
BEGIN TRY
    DECLARE @lastFull datetime;
    SELECT @lastFull = MAX(b.backup_finish_date) FROM msdb.dbo.backupset b WHERE b.database_name = DB_NAME() AND b.type = 'D';
    INSERT @R (CheckName, Expected, Actual, Status)
    VALUES (N'Last full backup (msdb)', N'within 24 hours - take one before step 01', ISNULL(CONVERT(nvarchar(30), @lastFull, 120), N'none found'),
            CASE WHEN @lastFull >= DATEADD(HOUR, -24, GETDATE()) THEN 'PASS' ELSE 'WARN' END);
END TRY
BEGIN CATCH
    INSERT @R (CheckName, Expected, Actual, Status) VALUES (N'Last full backup (msdb)', N'within 24 hours', N'could not read msdb: ' + LEFT(ERROR_MESSAGE(), 200), 'WARN');
END CATCH;

-- sizes, so the person running it knows what is "existing data"
INSERT @R (CheckName, Expected, Actual, Status)
SELECT N'Rows (approx.) ' + t.name, N'(information)', CAST(SUM(p.rows) AS nvarchar(20)), 'INFO'
FROM sys.tables t JOIN sys.partitions p ON p.object_id = t.object_id AND p.index_id IN (0, 1)
WHERE t.name IN (N'Tickets', N'TicketInteractions', N'TicketAssignmentHistory', N'Employees', N'AuditEntries', N'__EFMigrationsHistory') GROUP BY t.name;

SELECT CASE WHEN SUM(CASE WHEN Status = 'FAIL' THEN 1 ELSE 0 END) = 0 AND @nPendingChain = 5
            THEN 'PRE-FLIGHT PASSED: deploy steps 01 - 05 (and 06). Warnings, if any, are listed below.'
            WHEN SUM(CASE WHEN Status = 'FAIL' THEN 1 ELSE 0 END) = 0 THEN 'PRE-FLIGHT PASSED, but ' + CAST(5 - @nPendingChain AS varchar(2)) + ' chain migration(s) are already recorded: run only the steps still needed'
            ELSE 'PRE-FLIGHT FAILED: do not deploy until every FAIL row is understood' END AS Verdict,
       SUM(CASE WHEN Status = 'PASS' THEN 1 ELSE 0 END) AS [PASS], SUM(CASE WHEN Status = 'FAIL' THEN 1 ELSE 0 END) AS [FAIL], SUM(CASE WHEN Status = 'WARN' THEN 1 ELSE 0 END) AS [WARN],
       @nHist AS AppliedMigrations, @nPendingChain AS PendingChainMigrations
FROM @R;
SELECT CheckName, Expected, Actual, Status FROM @R ORDER BY CASE Status WHEN 'FAIL' THEN 1 WHEN 'WARN' THEN 2 WHEN 'PASS' THEN 3 ELSE 4 END, Id;
