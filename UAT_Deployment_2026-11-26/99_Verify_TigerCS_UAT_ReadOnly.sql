/*
================================================================================
 Verify_TigerCS_UAT_ReadOnly.sql
 TigerCS Ticketing - UAT database verification (READ-ONLY)
--------------------------------------------------------------------------------
 Generated against origin/main beff5b6 (60 EF Core migrations).
 Target   : database TigerCsTicketing (UAT). Aborts on any other database name.
 Baseline : 56 applied migrations, latest = 20261118124858_AddTicketAiAgentOwner.

 READ-ONLY GUARANTEE
   * No INSERT/UPDATE/DELETE/DDL against any TigerCsTicketing object. The only writes are to
     #temp tables in tempdb (findings + expected-schema lists), dropped when the session ends.
   * No linked-server / PACT query is issued (metadata only). No transaction is opened.
   * Requires only VIEW DEFINITION / metadata visibility + SELECT on __EFMigrationsHistory and
     the small configuration tables read in section 7. Run it as ONE batch (there is no GO).

 HOW TO READ THE RESULT (last result sets)
   1. VERDICT, 2. SUMMARY (PASS/PENDING/FAIL/DRIFT/WARN per section), 3. ACTION ITEMS (not PASS/INFO),
   4. MIGRATION MAP (60 rows), 5. DETAIL (everything).
 Status legend (PASS / PENDING / FAIL / DRIFT / WARN are counted in the SUMMARY; INFO is context only)
   PASS     object exists exactly as the EF model / script expects.
   PENDING  object belongs to a migration NOT recorded in history and does not exist yet (expected before deploy).
   DRIFT    object belongs to an UNRECORDED migration but ALREADY EXISTS (manual change): the migration script
            would fail / double-apply. Must be reconciled before deploying.
   FAIL     object is missing / wrong although it should exist (history says applied, or it is baseline).
   WARN     not blocking by itself, needs a human decision.
   INFO     context only.

 RE-RUNNABLE: every temp table is dropped and recreated at the start, so a run that failed half-way in the same SSMS
   session can simply be run again. Each section runs in its own TRY/CATCH: an error is recorded as a '0 Script error' FAIL
   row and the verdict becomes NOT READY (a verdict of READY needs all 8 sections to complete with zero errors).

 Compatible with SQL Server 2022 (uses only 2008+ T-SQL; no STRING_AGG, no features needing a higher compat level).

 NOTE: this script was generated from the repository; it has NOT been executed against UAT by the author.
 Review it, then run it once in SSMS / sqlcmd (-b is not needed; it never writes).
================================================================================
*/
SET NOCOUNT ON;
SET LOCK_TIMEOUT 10000;

IF DB_NAME() <> N'TigerCsTicketing'
    THROW 50001, N'ABORTED: this verification script is for database TigerCsTicketing (UAT). Connect to it and run again.', 1;

-- Variables shared by every section (declared once; sections assign them).
DECLARE @NApplied int = 0, @LatestApplied nvarchar(150) = NULL, @Err int = 0, @Done int = 0;

---------------------------------------------------------------------------------------------------
-- 0. SETUP: drop every temp table left by a previous (possibly failed) run in this session, then create them fresh.
--    All text columns are deliberately generous (identifiers 200, key lists 2000, messages 1000) so no expected value
--    or diagnostic message can be truncated. Primary keys stay below the 900-byte index-key limit (2 x 200 x 2 = 800).
---------------------------------------------------------------------------------------------------
IF OBJECT_ID('tempdb..#F')          IS NOT NULL DROP TABLE #F;
IF OBJECT_ID('tempdb..#ExpMig')     IS NOT NULL DROP TABLE #ExpMig;
IF OBJECT_ID('tempdb..#Applied')    IS NOT NULL DROP TABLE #Applied;
IF OBJECT_ID('tempdb..#ExpTables')  IS NOT NULL DROP TABLE #ExpTables;
IF OBJECT_ID('tempdb..#ExpCols')    IS NOT NULL DROP TABLE #ExpCols;
IF OBJECT_ID('tempdb..#ExpIdx')     IS NOT NULL DROP TABLE #ExpIdx;
IF OBJECT_ID('tempdb..#ExpChk')     IS NOT NULL DROP TABLE #ExpChk;
IF OBJECT_ID('tempdb..#ActCols')    IS NOT NULL DROP TABLE #ActCols;
IF OBJECT_ID('tempdb..#ActIdx')     IS NOT NULL DROP TABLE #ActIdx;
IF OBJECT_ID('tempdb..#CollTables') IS NOT NULL DROP TABLE #CollTables;
IF OBJECT_ID('tempdb..#CollFn')     IS NOT NULL DROP TABLE #CollFn;
IF OBJECT_ID('tempdb..#CollPr')     IS NOT NULL DROP TABLE #CollPr;
IF OBJECT_ID('tempdb..#CollIx')     IS NOT NULL DROP TABLE #CollIx;
IF OBJECT_ID('tempdb..#LegPr')      IS NOT NULL DROP TABLE #LegPr;

CREATE TABLE #F
(
    Id        int IDENTITY(1,1) PRIMARY KEY,
    Section   varchar(40)    NOT NULL,
    CheckName varchar(200)   NOT NULL,
    Obj       nvarchar(600)  NULL,
    Owner     nvarchar(300)  NULL,
    Expected  nvarchar(1000) NULL,
    Actual    nvarchar(1000) NULL,
    Status    varchar(12)    NOT NULL
);
CREATE TABLE #ExpMig    (Ord int NOT NULL PRIMARY KEY, MigrationId nvarchar(300) NOT NULL);
CREATE TABLE #Applied   (MigrationId nvarchar(300) NOT NULL PRIMARY KEY, ProductVersion nvarchar(100) NULL);
CREATE TABLE #ExpTables (TableName nvarchar(200) NOT NULL PRIMARY KEY, Owner nvarchar(300) NULL);
CREATE TABLE #ExpCols   (TableName nvarchar(200) NOT NULL, ColName nvarchar(200) NOT NULL, ColType nvarchar(128) NOT NULL, IsNullable bit NOT NULL, Owner nvarchar(300) NULL, PRIMARY KEY (TableName, ColName));
CREATE TABLE #ExpIdx    (TableName nvarchar(200) NOT NULL, IndexName nvarchar(200) NOT NULL, KeyCols nvarchar(2000) NOT NULL, IsUnique bit NOT NULL, HasFilter bit NOT NULL, Owner nvarchar(300) NULL, PRIMARY KEY (TableName, IndexName));
CREATE TABLE #ExpChk    (TableName nvarchar(200) NOT NULL, CheckName nvarchar(200) NOT NULL, PRIMARY KEY (TableName, CheckName));
CREATE TABLE #CollTables(Name nvarchar(200) NOT NULL PRIMARY KEY);
CREATE TABLE #CollFn    (Name nvarchar(200) NOT NULL PRIMARY KEY);
CREATE TABLE #CollPr    (Name nvarchar(200) NOT NULL PRIMARY KEY);
CREATE TABLE #CollIx    (TableName nvarchar(200) NOT NULL, IndexName nvarchar(200) NOT NULL, PRIMARY KEY (TableName, IndexName));
CREATE TABLE #LegPr     (Name nvarchar(200) NOT NULL PRIMARY KEY);


BEGIN TRY
---------------------------------------------------------------------------------------------------
-- 1. ENVIRONMENT
---------------------------------------------------------------------------------------------------
INSERT #F (Section, CheckName, Obj, Expected, Actual, Status)
SELECT '1 Environment', 'Server / database', @@SERVERNAME, N'TigerCsTicketing', DB_NAME(), 'INFO';
INSERT #F (Section, CheckName, Obj, Actual, Status)
SELECT '1 Environment', 'SQL Server version', N'', LEFT(REPLACE(CAST(@@VERSION AS nvarchar(max)), CHAR(10), N' '), 290), 'INFO';
INSERT #F (Section, CheckName, Obj, Actual, Status)
SELECT '1 Environment', 'Database options', N'', N'compat=' + CAST(compatibility_level AS nvarchar(10)) + N'; collation=' + collation_name
       + N'; recovery=' + recovery_model_desc + N'; RCSI=' + CAST(is_read_committed_snapshot_on AS nvarchar(1)) + N'; state=' + state_desc, 'INFO'
FROM sys.databases WHERE name = DB_NAME();
INSERT #F (Section, CheckName, Obj, Expected, Actual, Status)
SELECT '1 Environment', 'Deploy permission: ALTER on database', SUSER_SNAME(), N'1', CAST(ISNULL(HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'ALTER'), 0) AS nvarchar(5)),
       CASE WHEN ISNULL(HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'ALTER'), 0) = 1 THEN 'PASS' ELSE 'WARN' END;
INSERT #F (Section, CheckName, Obj, Expected, Actual, Status)
SELECT '1 Environment', 'Deploy permission: CREATE TABLE', SUSER_SNAME(), N'1', CAST(ISNULL(HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'CREATE TABLE'), 0) AS nvarchar(5)),
       CASE WHEN ISNULL(HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'CREATE TABLE'), 0) = 1 THEN 'PASS' ELSE 'WARN' END;
INSERT #F (Section, CheckName, Obj, Actual, Status)
SELECT '1 Environment', 'Database size (MB, data/log)', N'',
       CAST(SUM(CASE WHEN type = 0 THEN size END) / 128 AS nvarchar(20)) + N' / ' + CAST(SUM(CASE WHEN type = 1 THEN size END) / 128 AS nvarchar(20)), 'INFO'
FROM sys.database_files;
SET @Done = @Done + 1;
END TRY
BEGIN CATCH
    SET @Err = @Err + 1;
    INSERT #F (Section, CheckName, Obj, Expected, Actual, Status)
    VALUES ('0 Script error', 'Section 1 did not complete', N'line ' + CAST(ERROR_LINE() AS nvarchar(20)) + N', error ' + CAST(ERROR_NUMBER() AS nvarchar(20)), N'section completes', LEFT(ERROR_MESSAGE(), 900), 'FAIL');
END CATCH;

BEGIN TRY
---------------------------------------------------------------------------------------------------
-- 2. EF MIGRATION HISTORY  (list below is generated from main)
---------------------------------------------------------------------------------------------------
INSERT #ExpMig (Ord, MigrationId) VALUES
    (1,N'20260819072347_InitialIdentityAndAccess'),
    (2,N'20260820072044_AddCustomerVerification'),
    (3,N'20260820140000_AddTicketing'),
    (4,N'20260821090000_AddTicketOperations'),
    (5,N'20260821130000_AddSlaAndEscalation'),
    (6,N'20260822090000_AddNotificationsAndOutbox'),
    (7,N'20260825053743_AddIntakeRecordPhoneNumber'),
    (8,N'20260825061341_AddDepartmentCustomerLookupSource'),
    (9,N'20260825090047_AddDepartmentCustomerLookupSources'),
    (10,N'20260827090146_AddCrmBuyerLookupToTickets'),
    (11,N'20260901103309_AddExternalCustomerVerificationToTickets'),
    (12,N'20260903164723_AddWorkflowConfiguration'),
    (13,N'20260904075902_AddWorkflowAutomation'),
    (14,N'20260904082241_ReplaceInteractionContextWithTicketInteractions'),
    (15,N'20260904091143_AddApprovalWorkflow'),
    (16,N'20260908054944_AddWorkflowVersioning'),
    (17,N'20260909084839_AddChannels'),
    (18,N'20260910080219_AddGenesysIntegration'),
    (19,N'20260911105154_AddGenesysAgentMappingAndInteractionOwnership'),
    (20,N'20260912110119_AddExternalCustomerSnapshotToTickets'),
    (21,N'20260921133507_AddAgentHandoffTriggerAndConcurrency'),
    (22,N'20260928132618_AddGenesysScreenPopLaunches'),
    (23,N'20261005072511_AddCollectionsReminders'),
    (24,N'20261007093644_AddChatbotInactivityAndCrmDocumentCopies'),
    (25,N'20261007103153_AddResolutionClosedForCustomerInactivity'),
    (26,N'20261007180009_AddCustomerOtpVerification'),
    (27,N'20261008184722_AddSlaPauseAndPriorityDowngrade'),
    (28,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (29,N'20261009074521_AddDispatchSuppressionAndVoiceEligibility'),
    (30,N'20261009102002_AddSmsChannelToCustomerOtp'),
    (31,N'20261012124810_AddAgentDeskDocumentDelivery'),
    (32,N'20261020090000_AddGenesysCampaignToDispatchBatch'),
    (33,N'20261020105026_AddLastCustomerInteractionToTicketInteractions'),
    (34,N'20261021090000_AddTowerToReviewRecords'),
    (35,N'20261021120000_AddAdminEmailToCollectionsDispatch'),
    (36,N'20261022081031_AddCustomerSatisfactionReviews'),
    (37,N'20261022090000_AddGenesysServiceRequestToTicketInteractions'),
    (38,N'20261030061255_AddTicketInteractionHandlers'),
    (39,N'20261030062642_AddTicketTransferTracking'),
    (40,N'20261030073857_AddTicketCreatedBy'),
    (41,N'20261031094748_AddDepartmentAssignmentRotation'),
    (42,N'20261102065459_AddUserPermissionOverrides'),
    (43,N'20261104130835_AddCollectionsTowerAssignments'),
    (44,N'20261105094644_AddCustomerContactDetails'),
    (45,N'20261106044011_AddDirectSendInstalments'),
    (46,N'20261109074858_AddCollectionsGenesysDeliveryStats'),
    (47,N'20261109120816_AddCollectionsExceptionInclusions'),
    (48,N'20261109123253_AddManualGenesysDispatchItemFields'),
    (49,N'20261110070152_AddUserTransferDepartments'),
    (50,N'20261111073632_AddTicketOriginalHumanAgent'),
    (51,N'20261111132429_AddOriginalAgentWorkflowCutoff'),
    (52,N'20261112065853_AddGenesysAssignmentHistorySource'),
    (53,N'20261112071745_AddTicketAssignmentSupersededAt'),
    (54,N'20261117101035_AddQueueingIntegration'),
    (55,N'20261118120000_AddCustomerProfileOverrides'),
    (56,N'20261118124858_AddTicketAiAgentOwner'),
    (57,N'20261119081103_AddCustomerProfileAuditAndProperties'),
    (58,N'20261119120249_AddAssignmentHistoryPreviousDepartment'),
    (59,N'20261120074059_AddInteractionEndReceivedAt'),
    (60,N'20261124071552_AddEmployeeReviews');


IF OBJECT_ID(N'dbo.__EFMigrationsHistory') IS NULL
    INSERT #F (Section, CheckName, Obj, Expected, Actual, Status)
    VALUES ('2 Migrations', 'History table', N'dbo.__EFMigrationsHistory', N'exists', N'MISSING', 'FAIL')
ELSE
    EXEC(N'INSERT #Applied (MigrationId, ProductVersion) SELECT MigrationId, ProductVersion FROM dbo.__EFMigrationsHistory;');

SET @NApplied = (SELECT COUNT(*) FROM #Applied);
SET @LatestApplied = (SELECT MAX(MigrationId) FROM #Applied);

INSERT #F (Section, CheckName, Obj, Expected, Actual, Status)
VALUES ('2 Migrations', 'Migrations in main', N'', CAST(60 AS nvarchar(10)), CAST((SELECT COUNT(*) FROM #ExpMig) AS nvarchar(10)), 'INFO');
INSERT #F (Section, CheckName, Obj, Expected, Actual, Status)
SELECT '2 Migrations', 'Applied count = migrations of main that are recorded', N'', CAST(COUNT(*) AS nvarchar(10)), CAST(@NApplied AS nvarchar(10)), CASE WHEN COUNT(*) = @NApplied THEN 'PASS' ELSE 'WARN' END
FROM #ExpMig e WHERE EXISTS (SELECT 1 FROM #Applied a WHERE a.MigrationId = e.MigrationId);
INSERT #F (Section, CheckName, Obj, Expected, Actual, Status)
VALUES ('2 Migrations', 'Latest applied (information)', N'', N'(information)', ISNULL(@LatestApplied, N'(none)'), 'INFO');
INSERT #F (Section, CheckName, Obj, Expected, Actual, Status)
SELECT '2 Migrations', 'ProductVersion', ProductVersion, N'one EF version (10.0.11)', CAST(COUNT(*) AS nvarchar(10)) + N' migration(s)', 'INFO'
FROM #Applied GROUP BY ProductVersion;

-- applied but unknown to main (renamed / deleted / from another branch)
INSERT #F (Section, CheckName, Obj, Expected, Actual, Status)
SELECT '2 Migrations', 'Applied but NOT in main', a.MigrationId, N'absent', N'recorded in history', 'FAIL'
FROM #Applied a WHERE NOT EXISTS (SELECT 1 FROM #ExpMig e WHERE e.MigrationId = a.MigrationId);

-- in main, older than the latest applied, but not applied  => gap / out-of-order
INSERT #F (Section, CheckName, Obj, Expected, Actual, Status)
SELECT '2 Migrations', 'GAP: older than latest applied, not applied', e.MigrationId, N'applied', N'NOT in history', 'FAIL'
FROM #ExpMig e
WHERE e.MigrationId <= ISNULL(@LatestApplied, N'')
  AND NOT EXISTS (SELECT 1 FROM #Applied a WHERE a.MigrationId = e.MigrationId);

-- in main, newer than the latest applied => pending
INSERT #F (Section, CheckName, Obj, Expected, Actual, Status)
SELECT '2 Migrations', 'Pending (newer than latest applied)', e.MigrationId, N'pending', N'NOT in history',
       'PENDING'
FROM #ExpMig e
WHERE e.MigrationId > ISNULL(@LatestApplied, N'')
  AND NOT EXISTS (SELECT 1 FROM #Applied a WHERE a.MigrationId = e.MigrationId);
SET @Done = @Done + 1;
END TRY
BEGIN CATCH
    SET @Err = @Err + 1;
    INSERT #F (Section, CheckName, Obj, Expected, Actual, Status)
    VALUES ('0 Script error', 'Section 2 did not complete', N'line ' + CAST(ERROR_LINE() AS nvarchar(20)) + N', error ' + CAST(ERROR_NUMBER() AS nvarchar(20)), N'section completes', LEFT(ERROR_MESSAGE(), 900), 'FAIL');
END CATCH;

BEGIN TRY
---------------------------------------------------------------------------------------------------
-- 3. EXPECTED SCHEMA  (generated from TigerCsDbContextModelSnapshot at main; Owner = migration that created it)
---------------------------------------------------------------------------------------------------
INSERT #ExpTables (TableName,Owner) VALUES
    (N'AspNetRoleClaims',N'20260819072347_InitialIdentityAndAccess'),
    (N'AspNetRoles',N'20260819072347_InitialIdentityAndAccess'),
    (N'AspNetUserClaims',N'20260819072347_InitialIdentityAndAccess'),
    (N'AspNetUserLogins',N'20260819072347_InitialIdentityAndAccess'),
    (N'AspNetUserRoles',N'20260819072347_InitialIdentityAndAccess'),
    (N'AspNetUserTokens',N'20260819072347_InitialIdentityAndAccess'),
    (N'AspNetUsers',N'20260819072347_InitialIdentityAndAccess'),
    (N'AuditEntries',N'20260820072044_AddCustomerVerification'),
    (N'BusinessCalendarWorkingDays',N'20260821130000_AddSlaAndEscalation'),
    (N'BusinessCalendars',N'20260821130000_AddSlaAndEscalation'),
    (N'Categories',N'20260820140000_AddTicketing'),
    (N'Channels',N'20260909084839_AddChannels'),
    (N'CollectionsDispatchItems',N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsDispatches',N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsExceptionInclusions',N'20261109120816_AddCollectionsExceptionInclusions'),
    (N'CollectionsGenesysBatches',N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsGenesysMessageResults',N'20261109074858_AddCollectionsGenesysDeliveryStats'),
    (N'CollectionsGenesysSyncStates',N'20261109074858_AddCollectionsGenesysDeliveryStats'),
    (N'CollectionsReminderChannels',N'20261005072511_AddCollectionsReminders'),
    (N'CollectionsReminderEvents',N'20261005072511_AddCollectionsReminders'),
    (N'CollectionsReminders',N'20261005072511_AddCollectionsReminders'),
    (N'CollectionsReviewRecords',N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsReviewRuns',N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsTowerAssignmentHistory',N'20261104130835_AddCollectionsTowerAssignments'),
    (N'CollectionsTowerAssignments',N'20261104130835_AddCollectionsTowerAssignments'),
    (N'ContactReferences',N'20260820072044_AddCustomerVerification'),
    (N'CrmDocumentDeliveryRequests',N'20261007093644_AddChatbotInactivityAndCrmDocumentCopies'),
    (N'CustomerContactDetails',N'20261105094644_AddCustomerContactDetails'),
    (N'CustomerOtpChallenges',N'20261007180009_AddCustomerOtpVerification'),
    (N'CustomerProfileChanges',N'20261119081103_AddCustomerProfileAuditAndProperties'),
    (N'CustomerProfileOverrides',N'20261118120000_AddCustomerProfileOverrides'),
    (N'CustomerPropertyAssociations',N'20261119081103_AddCustomerProfileAuditAndProperties'),
    (N'DepartmentAssignmentRotations',N'20261031094748_AddDepartmentAssignmentRotation'),
    (N'DepartmentCustomerLookupSources',N'20260825061341_AddDepartmentCustomerLookupSource'),
    (N'DepartmentWorkflowSettings',N'20260903164723_AddWorkflowConfiguration'),
    (N'Departments',N'20260819072347_InitialIdentityAndAccess'),
    (N'EmployeeReviewDevices',N'20261124071552_AddEmployeeReviews'),
    (N'EmployeeReviews',N'20261124071552_AddEmployeeReviews'),
    (N'Employees',N'20260819072347_InitialIdentityAndAccess'),
    (N'GenesysQueueMappings',N'20260910080219_AddGenesysIntegration'),
    (N'GenesysScreenPopLaunches',N'20260928132618_AddGenesysScreenPopLaunches'),
    (N'Holidays',N'20260821130000_AddSlaAndEscalation'),
    (N'IdempotencyRecords',N'20260821130000_AddSlaAndEscalation'),
    (N'IntakeRecords',N'20260820140000_AddTicketing'),
    (N'Notifications',N'20260822090000_AddNotificationsAndOutbox'),
    (N'OutboxMessages',N'20260822090000_AddNotificationsAndOutbox'),
    (N'Priorities',N'20260820140000_AddTicketing'),
    (N'PriorityDowngradeRequests',N'20261008184722_AddSlaPauseAndPriorityDowngrade'),
    (N'QueueingCompanyMappings',N'20261117101035_AddQueueingIntegration'),
    (N'QueueingEvents',N'20261117101035_AddQueueingIntegration'),
    (N'QueueingTicketLinks',N'20261117101035_AddQueueingIntegration'),
    (N'QueueingTicketTypeMappings',N'20261117101035_AddQueueingIntegration'),
    (N'QueueingUserMappings',N'20261117101035_AddQueueingIntegration'),
    (N'RequestTypeApprovalRequirements',N'20260904091143_AddApprovalWorkflow'),
    (N'RequestTypeAssignmentRuleMembers',N'20260904075902_AddWorkflowAutomation'),
    (N'RequestTypeAssignmentRules',N'20260904075902_AddWorkflowAutomation'),
    (N'RequestTypeSlaPolicies',N'20260903164723_AddWorkflowConfiguration'),
    (N'RequestTypes',N'20260903164723_AddWorkflowConfiguration'),
    (N'SlaPolicies',N'20260821130000_AddSlaAndEscalation'),
    (N'TicketAgentHandoffs',N'20260910080219_AddGenesysIntegration'),
    (N'TicketApprovals',N'20260904091143_AddApprovalWorkflow'),
    (N'TicketAssignmentHistory',N'20261104130835_AddCollectionsTowerAssignments'),
    (N'TicketAssignments',N'20260821090000_AddTicketOperations'),
    (N'TicketCollectionsUnits',N'20261104130835_AddCollectionsTowerAssignments'),
    (N'TicketEscalations',N'20260821130000_AddSlaAndEscalation'),
    (N'TicketInteractionHandlers',N'20261030061255_AddTicketInteractionHandlers'),
    (N'TicketInteractionMessages',N'20260910080219_AddGenesysIntegration'),
    (N'TicketInteractions',N'20260904082241_ReplaceInteractionContextWithTicketInteractions'),
    (N'TicketNotes',N'20260821090000_AddTicketOperations'),
    (N'TicketPendingRecords',N'20260904075902_AddWorkflowAutomation'),
    (N'TicketRequesterSnapshots',N'20260820140000_AddTicketing'),
    (N'TicketResolutions',N'20260821090000_AddTicketOperations'),
    (N'TicketReviewInvitations',N'20261022081031_AddCustomerSatisfactionReviews'),
    (N'TicketReviews',N'20261022081031_AddCustomerSatisfactionReviews'),
    (N'TicketSlaInstances',N'20260821130000_AddSlaAndEscalation'),
    (N'TicketSlaPausePeriods',N'20261008184722_AddSlaPauseAndPriorityDowngrade'),
    (N'TicketStatusHistory',N'20260820140000_AddTicketing'),
    (N'TicketWorkflowEvents',N'20260904091143_AddApprovalWorkflow'),
    (N'Tickets',N'20260820140000_AddTicketing'),
    (N'UnitReferences',N'20260820072044_AddCustomerVerification'),
    (N'UserDepartmentAssignments',N'20260819072347_InitialIdentityAndAccess'),
    (N'UserPermissionOverrides',N'20261102065459_AddUserPermissionOverrides'),
    (N'UserTransferDepartments',N'20261110070152_AddUserTransferDepartments'),
    (N'VerificationSessions',N'20260820072044_AddCustomerVerification'),
    (N'WorkflowStepTransitions',N'20260908054944_AddWorkflowVersioning'),
    (N'WorkflowTemplateSteps',N'20260903164723_AddWorkflowConfiguration'),
    (N'WorkflowTemplates',N'20260903164723_AddWorkflowConfiguration'),
    (N'Workflows',N'20260908054944_AddWorkflowVersioning');
INSERT #ExpCols (TableName,ColName,ColType,IsNullable,Owner) VALUES
    (N'AspNetRoleClaims',N'ClaimType',N'nvarchar(max)',1,N'20260819072347_InitialIdentityAndAccess'),
    (N'AspNetRoleClaims',N'ClaimValue',N'nvarchar(max)',1,N'20260819072347_InitialIdentityAndAccess'),
    (N'AspNetRoleClaims',N'Id',N'int',0,N'20260819072347_InitialIdentityAndAccess'),
    (N'AspNetRoleClaims',N'RoleId',N'uniqueidentifier',0,N'20260819072347_InitialIdentityAndAccess'),
    (N'AspNetRoles',N'ConcurrencyStamp',N'nvarchar(max)',1,N'20260819072347_InitialIdentityAndAccess'),
    (N'AspNetRoles',N'Description',N'nvarchar(500)',0,N'20260819072347_InitialIdentityAndAccess'),
    (N'AspNetRoles',N'Id',N'uniqueidentifier',0,N'20260819072347_InitialIdentityAndAccess'),
    (N'AspNetRoles',N'Name',N'nvarchar(256)',1,N'20260819072347_InitialIdentityAndAccess'),
    (N'AspNetRoles',N'NormalizedName',N'nvarchar(256)',1,N'20260819072347_InitialIdentityAndAccess'),
    (N'AspNetUserClaims',N'ClaimType',N'nvarchar(max)',1,N'20260819072347_InitialIdentityAndAccess'),
    (N'AspNetUserClaims',N'ClaimValue',N'nvarchar(max)',1,N'20260819072347_InitialIdentityAndAccess'),
    (N'AspNetUserClaims',N'Id',N'int',0,N'20260819072347_InitialIdentityAndAccess'),
    (N'AspNetUserClaims',N'UserId',N'uniqueidentifier',0,N'20260819072347_InitialIdentityAndAccess'),
    (N'AspNetUserLogins',N'LoginProvider',N'nvarchar(450)',0,N'20260819072347_InitialIdentityAndAccess'),
    (N'AspNetUserLogins',N'ProviderDisplayName',N'nvarchar(max)',1,N'20260819072347_InitialIdentityAndAccess'),
    (N'AspNetUserLogins',N'ProviderKey',N'nvarchar(450)',0,N'20260819072347_InitialIdentityAndAccess'),
    (N'AspNetUserLogins',N'UserId',N'uniqueidentifier',0,N'20260819072347_InitialIdentityAndAccess'),
    (N'AspNetUserRoles',N'RoleId',N'uniqueidentifier',0,N'20260819072347_InitialIdentityAndAccess'),
    (N'AspNetUserRoles',N'UserId',N'uniqueidentifier',0,N'20260819072347_InitialIdentityAndAccess'),
    (N'AspNetUserTokens',N'LoginProvider',N'nvarchar(450)',0,N'20260819072347_InitialIdentityAndAccess'),
    (N'AspNetUserTokens',N'Name',N'nvarchar(450)',0,N'20260819072347_InitialIdentityAndAccess'),
    (N'AspNetUserTokens',N'UserId',N'uniqueidentifier',0,N'20260819072347_InitialIdentityAndAccess'),
    (N'AspNetUserTokens',N'Value',N'nvarchar(max)',1,N'20260819072347_InitialIdentityAndAccess'),
    (N'AspNetUsers',N'AccessFailedCount',N'int',0,N'20260819072347_InitialIdentityAndAccess'),
    (N'AspNetUsers',N'ConcurrencyStamp',N'nvarchar(max)',1,N'20260819072347_InitialIdentityAndAccess'),
    (N'AspNetUsers',N'Email',N'nvarchar(256)',1,N'20260819072347_InitialIdentityAndAccess'),
    (N'AspNetUsers',N'EmailConfirmed',N'bit',0,N'20260819072347_InitialIdentityAndAccess'),
    (N'AspNetUsers',N'GenesysEmail',N'nvarchar(256)',1,N'20260911105154_AddGenesysAgentMappingAndInteractionOwnership'),
    (N'AspNetUsers',N'GenesysUserId',N'nvarchar(64)',1,N'20260911105154_AddGenesysAgentMappingAndInteractionOwnership'),
    (N'AspNetUsers',N'Id',N'uniqueidentifier',0,N'20260819072347_InitialIdentityAndAccess'),
    (N'AspNetUsers',N'LockoutEnabled',N'bit',0,N'20260819072347_InitialIdentityAndAccess'),
    (N'AspNetUsers',N'LockoutEnd',N'datetimeoffset',1,N'20260819072347_InitialIdentityAndAccess'),
    (N'AspNetUsers',N'NormalizedEmail',N'nvarchar(256)',1,N'20260819072347_InitialIdentityAndAccess'),
    (N'AspNetUsers',N'NormalizedUserName',N'nvarchar(256)',1,N'20260819072347_InitialIdentityAndAccess'),
    (N'AspNetUsers',N'PasswordHash',N'nvarchar(max)',1,N'20260819072347_InitialIdentityAndAccess'),
    (N'AspNetUsers',N'PhoneNumber',N'nvarchar(max)',1,N'20260819072347_InitialIdentityAndAccess'),
    (N'AspNetUsers',N'PhoneNumberConfirmed',N'bit',0,N'20260819072347_InitialIdentityAndAccess'),
    (N'AspNetUsers',N'SecurityStamp',N'nvarchar(max)',1,N'20260819072347_InitialIdentityAndAccess'),
    (N'AspNetUsers',N'TwoFactorEnabled',N'bit',0,N'20260819072347_InitialIdentityAndAccess'),
    (N'AspNetUsers',N'UserName',N'nvarchar(256)',1,N'20260819072347_InitialIdentityAndAccess'),
    (N'AuditEntries',N'Action',N'nvarchar(100)',0,N'20260820072044_AddCustomerVerification'),
    (N'AuditEntries',N'ActorEmployeeId',N'uniqueidentifier',1,N'20260820072044_AddCustomerVerification'),
    (N'AuditEntries',N'AfterValue',N'nvarchar(max)',1,N'20260820072044_AddCustomerVerification'),
    (N'AuditEntries',N'AuditEntryId',N'bigint',0,N'20260820072044_AddCustomerVerification'),
    (N'AuditEntries',N'BeforeValue',N'nvarchar(max)',1,N'20260820072044_AddCustomerVerification'),
    (N'AuditEntries',N'CorrelationId',N'uniqueidentifier',0,N'20260820072044_AddCustomerVerification'),
    (N'AuditEntries',N'EntityId',N'nvarchar(100)',1,N'20260820072044_AddCustomerVerification'),
    (N'AuditEntries',N'EntityType',N'nvarchar(100)',0,N'20260820072044_AddCustomerVerification'),
    (N'AuditEntries',N'OccurredAtUtc',N'datetime2',0,N'20260820072044_AddCustomerVerification'),
    (N'BusinessCalendarWorkingDays',N'BusinessCalendarId',N'int',0,N'20260821130000_AddSlaAndEscalation'),
    (N'BusinessCalendarWorkingDays',N'BusinessCalendarWorkingDayId',N'int',0,N'20260821130000_AddSlaAndEscalation'),
    (N'BusinessCalendarWorkingDays',N'DayOfWeek',N'tinyint',0,N'20260821130000_AddSlaAndEscalation'),
    (N'BusinessCalendarWorkingDays',N'IsWorkingDay',N'bit',0,N'20260821130000_AddSlaAndEscalation'),
    (N'BusinessCalendars',N'BusinessCalendarId',N'int',0,N'20260821130000_AddSlaAndEscalation'),
    (N'BusinessCalendars',N'BusinessDayEndLocal',N'time',0,N'20260821130000_AddSlaAndEscalation'),
    (N'BusinessCalendars',N'BusinessDayStartLocal',N'time',0,N'20260821130000_AddSlaAndEscalation'),
    (N'BusinessCalendars',N'EffectiveFromUtc',N'datetime2',0,N'20260821130000_AddSlaAndEscalation'),
    (N'BusinessCalendars',N'IsActive',N'bit',0,N'20260821130000_AddSlaAndEscalation'),
    (N'BusinessCalendars',N'Name',N'nvarchar(100)',0,N'20260821130000_AddSlaAndEscalation'),
    (N'BusinessCalendars',N'TimeZone',N'nvarchar(50)',0,N'20260821130000_AddSlaAndEscalation'),
    (N'Categories',N'CategoryId',N'int',0,N'20260820140000_AddTicketing'),
    (N'Categories',N'DepartmentId',N'int',0,N'20260820140000_AddTicketing'),
    (N'Categories',N'IsActive',N'bit',0,N'20260820140000_AddTicketing'),
    (N'Categories',N'Name',N'nvarchar(100)',0,N'20260820140000_AddTicketing'),
    (N'Categories',N'ParentCategoryId',N'int',1,N'20260820140000_AddTicketing'),
    (N'Channels',N'ChannelId',N'tinyint',0,N'20260909084839_AddChannels'),
    (N'Channels',N'Code',N'nvarchar(50)',0,N'20260909084839_AddChannels'),
    (N'Channels',N'DisplayOrder',N'int',0,N'20260909084839_AddChannels'),
    (N'Channels',N'IsActive',N'bit',0,N'20260909084839_AddChannels'),
    (N'Channels',N'IsGenesysEnabled',N'bit',0,N'20260909084839_AddChannels'),
    (N'Channels',N'Name',N'nvarchar(100)',0,N'20260909084839_AddChannels'),
    (N'Channels',N'RequiresPhone',N'bit',0,N'20260909084839_AddChannels'),
    (N'CollectionsDispatchItems',N'Amount',N'decimal(19,4)',0,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsDispatchItems',N'BalanceCheckedAtUtc',N'datetime2',1,N'20261009074521_AddDispatchSuppressionAndVoiceEligibility'),
    (N'CollectionsDispatchItems',N'BatchPosition',N'int',1,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsDispatchItems',N'CollectionsDispatchId',N'bigint',0,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsDispatchItems',N'CollectionsDispatchItemId',N'bigint',0,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsDispatchItems',N'CollectionsGenesysBatchId',N'bigint',1,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsDispatchItems',N'CompanyId',N'int',0,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsDispatchItems',N'Currency',N'char(3)',0,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsDispatchItems',N'CustomerName',N'nvarchar(300)',0,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsDispatchItems',N'DueDate',N'date',0,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsDispatchItems',N'Email',N'nvarchar(320)',0,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsDispatchItems',N'GenesysContactId',N'varchar(64)',1,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsDispatchItems',N'Phone',N'varchar(20)',0,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsDispatchItems',N'ProjectName',N'nvarchar(200)',0,N'20261109123253_AddManualGenesysDispatchItemFields'),
    (N'CollectionsDispatchItems',N'RecordKey',N'char(64)',0,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsDispatchItems',N'ReminderType',N'nvarchar(16)',0,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsDispatchItems',N'Status',N'nvarchar(24)',0,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsDispatchItems',N'StatusReason',N'nvarchar(500)',1,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsDispatchItems',N'SuppressedAtUtc',N'datetime2',1,N'20261009074521_AddDispatchSuppressionAndVoiceEligibility'),
    (N'CollectionsDispatchItems',N'SuppressionError',N'nvarchar(500)',1,N'20261009074521_AddDispatchSuppressionAndVoiceEligibility'),
    (N'CollectionsDispatchItems',N'SuppressionStatus',N'nvarchar(16)',0,N'20261009074521_AddDispatchSuppressionAndVoiceEligibility'),
    (N'CollectionsDispatchItems',N'TenantId',N'nvarchar(50)',0,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsDispatchItems',N'TowerName',N'nvarchar(200)',0,N'20261109123253_AddManualGenesysDispatchItemFields'),
    (N'CollectionsDispatchItems',N'UnitCode',N'nvarchar(60)',0,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsDispatchItems',N'UnitId',N'int',1,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsDispatchItems',N'UploadedAtUtc',N'datetime2',1,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsDispatchItems',N'VoiceEligible',N'bit',0,N'20261009074521_AddDispatchSuppressionAndVoiceEligibility'),
    (N'CollectionsDispatches',N'AcknowledgedActiveCampaignRisk',N'bit',0,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsDispatches',N'AdminEmailAttemptedAtUtc',N'datetime2',1,N'20261021120000_AddAdminEmailToCollectionsDispatch'),
    (N'CollectionsDispatches',N'AdminEmailAttempts',N'int',0,N'20261021120000_AddAdminEmailToCollectionsDispatch'),
    (N'CollectionsDispatches',N'AdminEmailError',N'nvarchar(500)',1,N'20261021120000_AddAdminEmailToCollectionsDispatch'),
    (N'CollectionsDispatches',N'AdminEmailRecipientCount',N'int',0,N'20261021120000_AddAdminEmailToCollectionsDispatch'),
    (N'CollectionsDispatches',N'AdminEmailStatus',N'nvarchar(16)',0,N'20261021120000_AddAdminEmailToCollectionsDispatch'),
    (N'CollectionsDispatches',N'ApprovedCount',N'int',0,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsDispatches',N'ApprovedTotalsJson',N'nvarchar(500)',0,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsDispatches',N'CollectionsDispatchId',N'bigint',0,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsDispatches',N'CompletedAtUtc',N'datetime2',1,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsDispatches',N'ExcludedAtDispatchCount',N'int',0,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsDispatches',N'FilterJson',N'nvarchar(2000)',0,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsDispatches',N'Fingerprint',N'char(64)',0,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsDispatches',N'IdempotencyKey',N'nvarchar(100)',0,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsDispatches',N'InitiatedAtUtc',N'datetime2',0,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsDispatches',N'InitiatedByEmployeeId',N'uniqueidentifier',0,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsDispatches',N'LeaseExpiresAtUtc',N'datetime2',1,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsDispatches',N'LeaseOwner',N'uniqueidentifier',1,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsDispatches',N'Phase',N'nvarchar(200)',1,N'20261009074521_AddDispatchSuppressionAndVoiceEligibility'),
    (N'CollectionsDispatches',N'PublicId',N'uniqueidentifier',0,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsDispatches',N'RevalidationMs',N'bigint',1,N'20261009074521_AddDispatchSuppressionAndVoiceEligibility'),
    (N'CollectionsDispatches',N'ReviewRunId',N'bigint',0,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsDispatches',N'SelectionMode',N'nvarchar(24)',0,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsDispatches',N'StartedAtUtc',N'datetime2',1,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsDispatches',N'Status',N'nvarchar(24)',0,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsDispatches',N'StatusReason',N'nvarchar(500)',1,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsExceptionInclusions',N'CampaignKey',N'varchar(80)',0,N'20261109120816_AddCollectionsExceptionInclusions'),
    (N'CollectionsExceptionInclusions',N'CollectionsExceptionInclusionId',N'bigint',0,N'20261109120816_AddCollectionsExceptionInclusions'),
    (N'CollectionsExceptionInclusions',N'CompanyId',N'int',0,N'20261109120816_AddCollectionsExceptionInclusions'),
    (N'CollectionsExceptionInclusions',N'IncludedAtUtc',N'datetime2',0,N'20261109120816_AddCollectionsExceptionInclusions'),
    (N'CollectionsExceptionInclusions',N'IncludedByEmployeeId',N'uniqueidentifier',0,N'20261109120816_AddCollectionsExceptionInclusions'),
    (N'CollectionsExceptionInclusions',N'RemovedAtUtc',N'datetime2',1,N'20261109120816_AddCollectionsExceptionInclusions'),
    (N'CollectionsExceptionInclusions',N'RemovedByEmployeeId',N'uniqueidentifier',1,N'20261109120816_AddCollectionsExceptionInclusions'),
    (N'CollectionsExceptionInclusions',N'TenantId',N'nvarchar(50)',0,N'20261109120816_AddCollectionsExceptionInclusions'),
    (N'CollectionsExceptionInclusions',N'UnitCode',N'nvarchar(60)',0,N'20261109120816_AddCollectionsExceptionInclusions'),
    (N'CollectionsGenesysBatches',N'AttemptCount',N'int',0,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsGenesysBatches',N'CampaignAction',N'nvarchar(24)',1,N'20261020090000_AddGenesysCampaignToDispatchBatch'),
    (N'CollectionsGenesysBatches',N'CollectionsDispatchId',N'bigint',0,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsGenesysBatches',N'CollectionsGenesysBatchId',N'bigint',0,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsGenesysBatches',N'CompletedAtUtc',N'datetime2',1,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsGenesysBatches',N'ContactCount',N'int',0,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsGenesysBatches',N'ContactListId',N'varchar(36)',0,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsGenesysBatches',N'Error',N'nvarchar(500)',1,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsGenesysBatches',N'GenesysCampaignId',N'varchar(64)',1,N'20261020090000_AddGenesysCampaignToDispatchBatch'),
    (N'CollectionsGenesysBatches',N'GenesysCampaignName',N'nvarchar(200)',1,N'20261020090000_AddGenesysCampaignToDispatchBatch'),
    (N'CollectionsGenesysBatches',N'HttpStatus',N'int',1,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsGenesysBatches',N'ReconciledAtUtc',N'datetime2',1,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsGenesysBatches',N'ReconciledByEmployeeId',N'uniqueidentifier',1,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsGenesysBatches',N'ReconciliationNote',N'nvarchar(500)',1,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsGenesysBatches',N'ReminderType',N'nvarchar(16)',0,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsGenesysBatches',N'RequestHash',N'varchar(64)',1,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsGenesysBatches',N'ReturnedContactCount',N'int',1,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsGenesysBatches',N'Sequence',N'int',0,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsGenesysBatches',N'StageName',N'nvarchar(200)',1,N'20261020090000_AddGenesysCampaignToDispatchBatch'),
    (N'CollectionsGenesysBatches',N'StartedAtUtc',N'datetime2',1,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsGenesysBatches',N'Status',N'nvarchar(24)',0,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsGenesysMessageResults',N'CollectionsDispatchId',N'bigint',0,N'20261109074858_AddCollectionsGenesysDeliveryStats'),
    (N'CollectionsGenesysMessageResults',N'CollectionsDispatchItemId',N'bigint',0,N'20261109074858_AddCollectionsGenesysDeliveryStats'),
    (N'CollectionsGenesysMessageResults',N'CollectionsGenesysMessageResultId',N'bigint',0,N'20261109074858_AddCollectionsGenesysDeliveryStats'),
    (N'CollectionsGenesysMessageResults',N'DeliveredAtUtc',N'datetime2',1,N'20261109074858_AddCollectionsGenesysDeliveryStats'),
    (N'CollectionsGenesysMessageResults',N'FailureReason',N'nvarchar(300)',1,N'20261109074858_AddCollectionsGenesysDeliveryStats'),
    (N'CollectionsGenesysMessageResults',N'GenesysContactId',N'varchar(64)',1,N'20261109074858_AddCollectionsGenesysDeliveryStats'),
    (N'CollectionsGenesysMessageResults',N'GenesysConversationId',N'varchar(64)',0,N'20261109074858_AddCollectionsGenesysDeliveryStats'),
    (N'CollectionsGenesysMessageResults',N'GenesysMessageId',N'varchar(128)',0,N'20261109074858_AddCollectionsGenesysDeliveryStats'),
    (N'CollectionsGenesysMessageResults',N'SentAtUtc',N'datetime2',1,N'20261109074858_AddCollectionsGenesysDeliveryStats'),
    (N'CollectionsGenesysMessageResults',N'Status',N'varchar(16)',0,N'20261109074858_AddCollectionsGenesysDeliveryStats'),
    (N'CollectionsGenesysMessageResults',N'SyncedAtUtc',N'datetime2',0,N'20261109074858_AddCollectionsGenesysDeliveryStats'),
    (N'CollectionsGenesysSyncStates',N'CollectionsDispatchId',N'bigint',0,N'20261109074858_AddCollectionsGenesysDeliveryStats'),
    (N'CollectionsGenesysSyncStates',N'Contacts',N'int',0,N'20261109074858_AddCollectionsGenesysDeliveryStats'),
    (N'CollectionsGenesysSyncStates',N'ConversationsFound',N'int',0,N'20261109074858_AddCollectionsGenesysDeliveryStats'),
    (N'CollectionsGenesysSyncStates',N'Delivered',N'int',0,N'20261109074858_AddCollectionsGenesysDeliveryStats'),
    (N'CollectionsGenesysSyncStates',N'Error',N'nvarchar(500)',1,N'20261109074858_AddCollectionsGenesysDeliveryStats'),
    (N'CollectionsGenesysSyncStates',N'Failed',N'int',0,N'20261109074858_AddCollectionsGenesysDeliveryStats'),
    (N'CollectionsGenesysSyncStates',N'LastCompletedAtUtc',N'datetime2',1,N'20261109074858_AddCollectionsGenesysDeliveryStats'),
    (N'CollectionsGenesysSyncStates',N'LastSyncedAtUtc',N'datetime2',0,N'20261109074858_AddCollectionsGenesysDeliveryStats'),
    (N'CollectionsGenesysSyncStates',N'MessageAttempts',N'int',0,N'20261109074858_AddCollectionsGenesysDeliveryStats'),
    (N'CollectionsGenesysSyncStates',N'NotSent',N'int',0,N'20261109074858_AddCollectionsGenesysDeliveryStats'),
    (N'CollectionsGenesysSyncStates',N'Outcome',N'varchar(16)',0,N'20261109074858_AddCollectionsGenesysDeliveryStats'),
    (N'CollectionsGenesysSyncStates',N'Pending',N'int',0,N'20261109074858_AddCollectionsGenesysDeliveryStats'),
    (N'CollectionsGenesysSyncStates',N'Sent',N'int',0,N'20261109074858_AddCollectionsGenesysDeliveryStats'),
    (N'CollectionsReminderChannels',N'Attempts',N'int',0,N'20261005072511_AddCollectionsReminders'),
    (N'CollectionsReminderChannels',N'Channel',N'nvarchar(16)',0,N'20261005072511_AddCollectionsReminders'),
    (N'CollectionsReminderChannels',N'CollectionsReminderChannelId',N'bigint',0,N'20261005072511_AddCollectionsReminders'),
    (N'CollectionsReminderChannels',N'CollectionsReminderId',N'bigint',0,N'20261005072511_AddCollectionsReminders'),
    (N'CollectionsReminderChannels',N'DeduplicationKey',N'nvarchar(200)',0,N'20261005072511_AddCollectionsReminders'),
    (N'CollectionsReminderChannels',N'DispatchAmount',N'decimal(19,4)',1,N'20261005072511_AddCollectionsReminders'),
    (N'CollectionsReminderChannels',N'DispatchSourceAsOfUtc',N'datetime2',1,N'20261005072511_AddCollectionsReminders'),
    (N'CollectionsReminderChannels',N'LastEventAtUtc',N'datetime2',1,N'20261005072511_AddCollectionsReminders'),
    (N'CollectionsReminderChannels',N'ProviderMessageId',N'nvarchar(128)',1,N'20261005072511_AddCollectionsReminders'),
    (N'CollectionsReminderChannels',N'Status',N'nvarchar(16)',0,N'20261005072511_AddCollectionsReminders'),
    (N'CollectionsReminderChannels',N'StatusReason',N'nvarchar(500)',1,N'20261005072511_AddCollectionsReminders'),
    (N'CollectionsReminderEvents',N'Channel',N'nvarchar(16)',1,N'20261005072511_AddCollectionsReminders'),
    (N'CollectionsReminderEvents',N'CollectionsReminderEventId',N'bigint',0,N'20261005072511_AddCollectionsReminders'),
    (N'CollectionsReminderEvents',N'CollectionsReminderId',N'bigint',0,N'20261005072511_AddCollectionsReminders'),
    (N'CollectionsReminderEvents',N'ConversationId',N'nvarchar(128)',1,N'20261005072511_AddCollectionsReminders'),
    (N'CollectionsReminderEvents',N'CustomerIntent',N'nvarchar(24)',1,N'20261005072511_AddCollectionsReminders'),
    (N'CollectionsReminderEvents',N'CustomerPhone',N'nvarchar(32)',1,N'20261005072511_AddCollectionsReminders'),
    (N'CollectionsReminderEvents',N'CustomerResponded',N'bit',0,N'20261005072511_AddCollectionsReminders'),
    (N'CollectionsReminderEvents',N'DeliveryStatus',N'nvarchar(16)',1,N'20261005072511_AddCollectionsReminders'),
    (N'CollectionsReminderEvents',N'Detail',N'nvarchar(1000)',1,N'20261005072511_AddCollectionsReminders'),
    (N'CollectionsReminderEvents',N'ExternalEventId',N'nvarchar(128)',0,N'20261005072511_AddCollectionsReminders'),
    (N'CollectionsReminderEvents',N'IdempotencyKey',N'nvarchar(128)',1,N'20261005072511_AddCollectionsReminders'),
    (N'CollectionsReminderEvents',N'OccurredAtUtc',N'datetime2',0,N'20261005072511_AddCollectionsReminders'),
    (N'CollectionsReminderEvents',N'ProviderMessageId',N'nvarchar(128)',1,N'20261005072511_AddCollectionsReminders'),
    (N'CollectionsReminderEvents',N'RecordedAtUtc',N'datetime2',0,N'20261005072511_AddCollectionsReminders'),
    (N'CollectionsReminderEvents',N'ReportedByEmployeeId',N'uniqueidentifier',1,N'20261005072511_AddCollectionsReminders'),
    (N'CollectionsReminderEvents',N'RequestHash',N'char(64)',1,N'20261005072511_AddCollectionsReminders'),
    (N'CollectionsReminderEvents',N'RequiresHumanFollowUp',N'bit',0,N'20261005072511_AddCollectionsReminders'),
    (N'CollectionsReminderEvents',N'TicketAttempts',N'int',0,N'20261005072511_AddCollectionsReminders'),
    (N'CollectionsReminderEvents',N'TicketId',N'bigint',1,N'20261005072511_AddCollectionsReminders'),
    (N'CollectionsReminderEvents',N'TicketLastError',N'nvarchar(500)',1,N'20261005072511_AddCollectionsReminders'),
    (N'CollectionsReminderEvents',N'TicketLinkedAtUtc',N'datetime2',1,N'20261005072511_AddCollectionsReminders'),
    (N'CollectionsReminderEvents',N'TicketNumber',N'nvarchar(64)',1,N'20261005072511_AddCollectionsReminders'),
    (N'CollectionsReminderEvents',N'TicketResult',N'nvarchar(16)',0,N'20261005072511_AddCollectionsReminders'),
    (N'CollectionsReminderEvents',N'VerificationFollowUpRequired',N'bit',0,N'20261005072511_AddCollectionsReminders'),
    (N'CollectionsReminders',N'AccountId',N'nvarchar(64)',0,N'20261005072511_AddCollectionsReminders'),
    (N'CollectionsReminders',N'Amount',N'decimal(19,4)',0,N'20261005072511_AddCollectionsReminders'),
    (N'CollectionsReminders',N'AmountBasis',N'nvarchar(80)',0,N'20261005072511_AddCollectionsReminders'),
    (N'CollectionsReminders',N'CollectionsReminderId',N'bigint',0,N'20261005072511_AddCollectionsReminders'),
    (N'CollectionsReminders',N'CrmCustomerId',N'bigint',0,N'20261005072511_AddCollectionsReminders'),
    (N'CollectionsReminders',N'Currency',N'char(3)',0,N'20261005072511_AddCollectionsReminders'),
    (N'CollectionsReminders',N'CycleKey',N'nvarchar(40)',0,N'20261005072511_AddCollectionsReminders'),
    (N'CollectionsReminders',N'IdempotencyKey',N'nvarchar(128)',1,N'20261005072511_AddCollectionsReminders'),
    (N'CollectionsReminders',N'InstalmentIds',N'nvarchar(1000)',0,N'20261005072511_AddCollectionsReminders'),
    (N'CollectionsReminders',N'Language',N'varchar(2)',0,N'20261005072511_AddCollectionsReminders'),
    (N'CollectionsReminders',N'QueuedAtUtc',N'datetime2',0,N'20261005072511_AddCollectionsReminders'),
    (N'CollectionsReminders',N'RequestHash',N'char(64)',1,N'20261005072511_AddCollectionsReminders'),
    (N'CollectionsReminders',N'RequestedByEmployeeId',N'uniqueidentifier',1,N'20261005072511_AddCollectionsReminders'),
    (N'CollectionsReminders',N'SourceAsOfUtc',N'datetime2',0,N'20261005072511_AddCollectionsReminders'),
    (N'CollectionsReminders',N'Trigger',N'nvarchar(16)',0,N'20261005072511_AddCollectionsReminders'),
    (N'CollectionsReminders',N'Type',N'nvarchar(24)',0,N'20261005072511_AddCollectionsReminders'),
    (N'CollectionsReminders',N'UnitId',N'bigint',1,N'20261005072511_AddCollectionsReminders'),
    (N'CollectionsReviewRecords',N'CollectionsReviewRecordId',N'bigint',0,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsReviewRecords',N'CollectionsReviewRunId',N'bigint',0,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsReviewRecords',N'CompanyId',N'int',0,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsReviewRecords',N'Currency',N'char(3)',0,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsReviewRecords',N'CustomerName',N'nvarchar(300)',0,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsReviewRecords',N'CycleKey',N'varchar(40)',0,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsReviewRecords',N'DueDate',N'date',1,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsReviewRecords',N'Email',N'nvarchar(320)',0,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsReviewRecords',N'InstalmentCount',N'int',0,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsReviewRecords',N'InstalmentsJson',N'nvarchar(max)',1,N'20261106044011_AddDirectSendInstalments'),
    (N'CollectionsReviewRecords',N'PaymentStatus',N'nvarchar(16)',0,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsReviewRecords',N'Phone',N'varchar(20)',0,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsReviewRecords',N'ProjectCode',N'nvarchar(60)',0,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsReviewRecords',N'RawRemainingAmount',N'decimal(19,4)',1,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsReviewRecords',N'Reasons',N'nvarchar(600)',0,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsReviewRecords',N'RecordKey',N'char(64)',0,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsReviewRecords',N'RemainingAmount',N'decimal(19,4)',1,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsReviewRecords',N'ReminderType',N'nvarchar(16)',0,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsReviewRecords',N'Source',N'nvarchar(200)',0,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsReviewRecords',N'SourceReadAtUtc',N'datetime2',0,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsReviewRecords',N'SourceStatus',N'nvarchar(100)',0,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsReviewRecords',N'TenantId',N'nvarchar(50)',0,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsReviewRecords',N'TowerId',N'int',1,N'20261021090000_AddTowerToReviewRecords'),
    (N'CollectionsReviewRecords',N'TowerName',N'nvarchar(200)',1,N'20261021090000_AddTowerToReviewRecords'),
    (N'CollectionsReviewRecords',N'TowerNumber',N'nvarchar(20)',1,N'20261021090000_AddTowerToReviewRecords'),
    (N'CollectionsReviewRecords',N'UnitCode',N'nvarchar(60)',0,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsReviewRecords',N'UnitId',N'int',1,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsReviewRecords',N'ValidationStatus',N'nvarchar(16)',0,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsReviewRuns',N'AsOfDate',N'date',0,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsReviewRuns',N'CollectionsReviewRunId',N'bigint',0,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsReviewRuns',N'CompanyId',N'int',1,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsReviewRuns',N'CompletedAtUtc',N'datetime2',1,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsReviewRuns',N'DueFrom',N'date',0,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsReviewRuns',N'DueTo',N'date',0,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsReviewRuns',N'Error',N'nvarchar(500)',1,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsReviewRuns',N'IsActive',N'bit',0,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsReviewRuns',N'IsCurrent',N'bit',0,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsReviewRuns',N'Phase',N'nvarchar(100)',0,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsReviewRuns',N'ProgressPercent',N'int',0,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsReviewRuns',N'RecordCount',N'int',0,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsReviewRuns',N'RequestedAtUtc',N'datetime2',0,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsReviewRuns',N'RequestedByEmployeeId',N'uniqueidentifier',0,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsReviewRuns',N'Source',N'nvarchar(200)',0,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsReviewRuns',N'SourceProcedureSuffix',N'varchar(16)',0,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsReviewRuns',N'SourceReadAtUtc',N'datetime2',1,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsReviewRuns',N'SourceReconciled',N'bit',0,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsReviewRuns',N'SourceRowCount',N'int',0,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsReviewRuns',N'StartedAtUtc',N'datetime2',1,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsReviewRuns',N'Status',N'nvarchar(16)',0,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsTowerAssignmentHistory',N'Action',N'tinyint',0,N'20261104130835_AddCollectionsTowerAssignments'),
    (N'CollectionsTowerAssignmentHistory',N'ChangedAtUtc',N'datetime2',0,N'20261104130835_AddCollectionsTowerAssignments'),
    (N'CollectionsTowerAssignmentHistory',N'ChangedByEmployeeId',N'uniqueidentifier',0,N'20261104130835_AddCollectionsTowerAssignments'),
    (N'CollectionsTowerAssignmentHistory',N'CollectionsTowerAssignmentHistoryId',N'bigint',0,N'20261104130835_AddCollectionsTowerAssignments'),
    (N'CollectionsTowerAssignmentHistory',N'Month',N'int',0,N'20261104130835_AddCollectionsTowerAssignments'),
    (N'CollectionsTowerAssignmentHistory',N'NewEmployeeId',N'uniqueidentifier',1,N'20261104130835_AddCollectionsTowerAssignments'),
    (N'CollectionsTowerAssignmentHistory',N'PreviousEmployeeId',N'uniqueidentifier',1,N'20261104130835_AddCollectionsTowerAssignments'),
    (N'CollectionsTowerAssignmentHistory',N'TowerCode',N'nvarchar(20)',0,N'20261104130835_AddCollectionsTowerAssignments'),
    (N'CollectionsTowerAssignmentHistory',N'TowerId',N'int',0,N'20261104130835_AddCollectionsTowerAssignments'),
    (N'CollectionsTowerAssignmentHistory',N'TowerName',N'nvarchar(200)',0,N'20261104130835_AddCollectionsTowerAssignments'),
    (N'CollectionsTowerAssignmentHistory',N'Year',N'int',0,N'20261104130835_AddCollectionsTowerAssignments'),
    (N'CollectionsTowerAssignments',N'ActivatedAtUtc',N'datetime2',1,N'20261104130835_AddCollectionsTowerAssignments'),
    (N'CollectionsTowerAssignments',N'ActivatedByEmployeeId',N'uniqueidentifier',1,N'20261104130835_AddCollectionsTowerAssignments'),
    (N'CollectionsTowerAssignments',N'CollectionsTowerAssignmentId',N'bigint',0,N'20261104130835_AddCollectionsTowerAssignments'),
    (N'CollectionsTowerAssignments',N'CompanyId',N'int',0,N'20261104130835_AddCollectionsTowerAssignments'),
    (N'CollectionsTowerAssignments',N'CreatedAtUtc',N'datetime2',0,N'20261104130835_AddCollectionsTowerAssignments'),
    (N'CollectionsTowerAssignments',N'CreatedByEmployeeId',N'uniqueidentifier',0,N'20261104130835_AddCollectionsTowerAssignments'),
    (N'CollectionsTowerAssignments',N'EmployeeId',N'uniqueidentifier',0,N'20261104130835_AddCollectionsTowerAssignments'),
    (N'CollectionsTowerAssignments',N'IsPrimary',N'bit',0,N'20261104130835_AddCollectionsTowerAssignments'),
    (N'CollectionsTowerAssignments',N'Month',N'int',0,N'20261104130835_AddCollectionsTowerAssignments');
INSERT #ExpCols (TableName,ColName,ColType,IsNullable,Owner) VALUES
    (N'CollectionsTowerAssignments',N'Status',N'tinyint',0,N'20261104130835_AddCollectionsTowerAssignments'),
    (N'CollectionsTowerAssignments',N'TowerCode',N'nvarchar(20)',0,N'20261104130835_AddCollectionsTowerAssignments'),
    (N'CollectionsTowerAssignments',N'TowerId',N'int',0,N'20261104130835_AddCollectionsTowerAssignments'),
    (N'CollectionsTowerAssignments',N'TowerName',N'nvarchar(200)',0,N'20261104130835_AddCollectionsTowerAssignments'),
    (N'CollectionsTowerAssignments',N'UpdatedAtUtc',N'datetime2',0,N'20261104130835_AddCollectionsTowerAssignments'),
    (N'CollectionsTowerAssignments',N'UpdatedByEmployeeId',N'uniqueidentifier',0,N'20261104130835_AddCollectionsTowerAssignments'),
    (N'CollectionsTowerAssignments',N'Year',N'int',0,N'20261104130835_AddCollectionsTowerAssignments'),
    (N'ContactReferences',N'AuthorizedRepresentativeOfContactReferenceId',N'int',1,N'20260820072044_AddCustomerVerification'),
    (N'ContactReferences',N'ContactChannel',N'nvarchar(200)',1,N'20260820072044_AddCustomerVerification'),
    (N'ContactReferences',N'ContactReferenceId',N'int',0,N'20260820072044_AddCustomerVerification'),
    (N'ContactReferences',N'ContactType',N'int',0,N'20260820072044_AddCustomerVerification'),
    (N'ContactReferences',N'CrmContactId',N'nvarchar(64)',0,N'20260820072044_AddCustomerVerification'),
    (N'ContactReferences',N'DisplayName',N'nvarchar(200)',1,N'20260820072044_AddCustomerVerification'),
    (N'ContactReferences',N'LastSyncedAtUtc',N'datetime2',0,N'20260820072044_AddCustomerVerification'),
    (N'ContactReferences',N'UnitReferenceId',N'int',0,N'20260820072044_AddCustomerVerification'),
    (N'CrmDocumentDeliveryRequests',N'AttemptCount',N'int',0,N'20261007093644_AddChatbotInactivityAndCrmDocumentCopies'),
    (N'CrmDocumentDeliveryRequests',N'CallerEmployeeId',N'uniqueidentifier',0,N'20261007093644_AddChatbotInactivityAndCrmDocumentCopies'),
    (N'CrmDocumentDeliveryRequests',N'Channel',N'tinyint',0,N'20261007093644_AddChatbotInactivityAndCrmDocumentCopies'),
    (N'CrmDocumentDeliveryRequests',N'CreatedAtUtc',N'datetime2',0,N'20261007093644_AddChatbotInactivityAndCrmDocumentCopies'),
    (N'CrmDocumentDeliveryRequests',N'CrmCustomerId',N'int',1,N'20261012124810_AddAgentDeskDocumentDelivery'),
    (N'CrmDocumentDeliveryRequests',N'CrmDocumentDeliveryRequestId',N'bigint',0,N'20261007093644_AddChatbotInactivityAndCrmDocumentCopies'),
    (N'CrmDocumentDeliveryRequests',N'CrmLeadId',N'int',1,N'20261012124810_AddAgentDeskDocumentDelivery'),
    (N'CrmDocumentDeliveryRequests',N'CrmRecordId',N'nvarchar(100)',0,N'20261007093644_AddChatbotInactivityAndCrmDocumentCopies'),
    (N'CrmDocumentDeliveryRequests',N'DocumentType',N'tinyint',0,N'20261007093644_AddChatbotInactivityAndCrmDocumentCopies'),
    (N'CrmDocumentDeliveryRequests',N'FailureCode',N'nvarchar(64)',1,N'20261007093644_AddChatbotInactivityAndCrmDocumentCopies'),
    (N'CrmDocumentDeliveryRequests',N'Fingerprint',N'nvarchar(64)',0,N'20261007093644_AddChatbotInactivityAndCrmDocumentCopies'),
    (N'CrmDocumentDeliveryRequests',N'IdempotencyKey',N'nvarchar(128)',0,N'20261007093644_AddChatbotInactivityAndCrmDocumentCopies'),
    (N'CrmDocumentDeliveryRequests',N'MaskedDestination',N'nvarchar(120)',1,N'20261007093644_AddChatbotInactivityAndCrmDocumentCopies'),
    (N'CrmDocumentDeliveryRequests',N'Origin',N'tinyint',0,N'20261012124810_AddAgentDeskDocumentDelivery'),
    (N'CrmDocumentDeliveryRequests',N'SentAtUtc',N'datetime2',1,N'20261007093644_AddChatbotInactivityAndCrmDocumentCopies'),
    (N'CrmDocumentDeliveryRequests',N'Status',N'tinyint',0,N'20261007093644_AddChatbotInactivityAndCrmDocumentCopies'),
    (N'CrmDocumentDeliveryRequests',N'UpdatedAtUtc',N'datetime2',0,N'20261007093644_AddChatbotInactivityAndCrmDocumentCopies'),
    (N'CrmDocumentDeliveryRequests',N'VerificationSessionId',N'uniqueidentifier',1,N'20261007093644_AddChatbotInactivityAndCrmDocumentCopies'),
    (N'CustomerContactDetails',N'CreatedAtUtc',N'datetime2',0,N'20261105094644_AddCustomerContactDetails'),
    (N'CustomerContactDetails',N'CustomerContactDetailId',N'bigint',0,N'20261105094644_AddCustomerContactDetails'),
    (N'CustomerContactDetails',N'Email',N'nvarchar(256)',1,N'20261105094644_AddCustomerContactDetails'),
    (N'CustomerContactDetails',N'Name',N'nvarchar(200)',0,N'20261105094644_AddCustomerContactDetails'),
    (N'CustomerContactDetails',N'PhoneKey',N'nvarchar(32)',0,N'20261105094644_AddCustomerContactDetails'),
    (N'CustomerContactDetails',N'RowVersion',N'rowversion',0,N'20261105094644_AddCustomerContactDetails'),
    (N'CustomerContactDetails',N'UpdatedAtUtc',N'datetime2',0,N'20261105094644_AddCustomerContactDetails'),
    (N'CustomerContactDetails',N'UpdatedByEmployeeId',N'uniqueidentifier',0,N'20261105094644_AddCustomerContactDetails'),
    (N'CustomerOtpChallenges',N'CallerEmployeeId',N'uniqueidentifier',0,N'20261007180009_AddCustomerOtpVerification'),
    (N'CustomerOtpChallenges',N'Channel',N'tinyint',0,N'20261009102002_AddSmsChannelToCustomerOtp'),
    (N'CustomerOtpChallenges',N'CodeHash',N'varbinary(32)',0,N'20261007180009_AddCustomerOtpVerification'),
    (N'CustomerOtpChallenges',N'ContactReferenceId',N'int',0,N'20261007180009_AddCustomerOtpVerification'),
    (N'CustomerOtpChallenges',N'CreatedAtUtc',N'datetime2',0,N'20261007180009_AddCustomerOtpVerification'),
    (N'CustomerOtpChallenges',N'CrmCustomerId',N'int',0,N'20261007180009_AddCustomerOtpVerification'),
    (N'CustomerOtpChallenges',N'CrmLeadId',N'int',0,N'20261007180009_AddCustomerOtpVerification'),
    (N'CustomerOtpChallenges',N'CustomerOtpChallengeId',N'uniqueidentifier',0,N'20261007180009_AddCustomerOtpVerification'),
    (N'CustomerOtpChallenges',N'DeliveryState',N'tinyint',0,N'20261009102002_AddSmsChannelToCustomerOtp'),
    (N'CustomerOtpChallenges',N'ExpiresAtUtc',N'datetime2',0,N'20261007180009_AddCustomerOtpVerification'),
    (N'CustomerOtpChallenges',N'FailedAttempts',N'int',0,N'20261007180009_AddCustomerOtpVerification'),
    (N'CustomerOtpChallenges',N'Language',N'nvarchar(2)',0,N'20261009102002_AddSmsChannelToCustomerOtp'),
    (N'CustomerOtpChallenges',N'LastSentAtUtc',N'datetime2',0,N'20261007180009_AddCustomerOtpVerification'),
    (N'CustomerOtpChallenges',N'MaskedDestination',N'nvarchar(120)',0,N'20261007180009_AddCustomerOtpVerification'),
    (N'CustomerOtpChallenges',N'Salt',N'varbinary(32)',0,N'20261007180009_AddCustomerOtpVerification'),
    (N'CustomerOtpChallenges',N'SendCount',N'int',0,N'20261007180009_AddCustomerOtpVerification'),
    (N'CustomerOtpChallenges',N'Status',N'tinyint',0,N'20261007180009_AddCustomerOtpVerification'),
    (N'CustomerOtpChallenges',N'UnitReferenceId',N'int',0,N'20261007180009_AddCustomerOtpVerification'),
    (N'CustomerOtpChallenges',N'VerificationSessionId',N'uniqueidentifier',1,N'20261007180009_AddCustomerOtpVerification'),
    (N'CustomerOtpChallenges',N'VerifiedAtUtc',N'datetime2',1,N'20261007180009_AddCustomerOtpVerification'),
    (N'CustomerProfileChanges',N'ChangeKey',N'nchar(64)',0,N'20261119081103_AddCustomerProfileAuditAndProperties'),
    (N'CustomerProfileChanges',N'ChangeReason',N'nvarchar(500)',1,N'20261119081103_AddCustomerProfileAuditAndProperties'),
    (N'CustomerProfileChanges',N'CorrelationId',N'uniqueidentifier',0,N'20261119081103_AddCustomerProfileAuditAndProperties'),
    (N'CustomerProfileChanges',N'CustomerKey',N'nvarchar(300)',0,N'20261119081103_AddCustomerProfileAuditAndProperties'),
    (N'CustomerProfileChanges',N'CustomerProfileChangeId',N'bigint',0,N'20261119081103_AddCustomerProfileAuditAndProperties'),
    (N'CustomerProfileChanges',N'CustomerProfileId',N'uniqueidentifier',0,N'20261119081103_AddCustomerProfileAuditAndProperties'),
    (N'CustomerProfileChanges',N'FieldName',N'nvarchar(100)',0,N'20261119081103_AddCustomerProfileAuditAndProperties'),
    (N'CustomerProfileChanges',N'ModifiedAtUtc',N'datetime2',0,N'20261119081103_AddCustomerProfileAuditAndProperties'),
    (N'CustomerProfileChanges',N'ModifiedByEmployeeId',N'uniqueidentifier',0,N'20261119081103_AddCustomerProfileAuditAndProperties'),
    (N'CustomerProfileChanges',N'ModifiedByName',N'nvarchar(200)',0,N'20261119081103_AddCustomerProfileAuditAndProperties'),
    (N'CustomerProfileChanges',N'NewValue',N'nvarchar(1000)',1,N'20261119081103_AddCustomerProfileAuditAndProperties'),
    (N'CustomerProfileChanges',N'OldValue',N'nvarchar(1000)',1,N'20261119081103_AddCustomerProfileAuditAndProperties'),
    (N'CustomerProfileChanges',N'RelatedTicketId',N'bigint',1,N'20261119081103_AddCustomerProfileAuditAndProperties'),
    (N'CustomerProfileChanges',N'Source',N'nvarchar(50)',0,N'20261119081103_AddCustomerProfileAuditAndProperties'),
    (N'CustomerProfileOverrides',N'AdditionalPhones',N'nvarchar(400)',1,N'20261119081103_AddCustomerProfileAuditAndProperties'),
    (N'CustomerProfileOverrides',N'Country',N'nvarchar(100)',1,N'20261119081103_AddCustomerProfileAuditAndProperties'),
    (N'CustomerProfileOverrides',N'CreatedAtUtc',N'datetime2',0,N'20261118120000_AddCustomerProfileOverrides'),
    (N'CustomerProfileOverrides',N'CrmBuyerCustomerId',N'int',1,N'20261118120000_AddCustomerProfileOverrides'),
    (N'CustomerProfileOverrides',N'CustomerKey',N'nvarchar(300)',0,N'20261118120000_AddCustomerProfileOverrides'),
    (N'CustomerProfileOverrides',N'CustomerProfileId',N'uniqueidentifier',0,N'20261119081103_AddCustomerProfileAuditAndProperties'),
    (N'CustomerProfileOverrides',N'CustomerProfileOverrideId',N'bigint',0,N'20261118120000_AddCustomerProfileOverrides'),
    (N'CustomerProfileOverrides',N'Email',N'nvarchar(256)',1,N'20261119081103_AddCustomerProfileAuditAndProperties'),
    (N'CustomerProfileOverrides',N'Emirate',N'nvarchar(100)',1,N'20261119081103_AddCustomerProfileAuditAndProperties'),
    (N'CustomerProfileOverrides',N'ExternalCustomerId',N'nvarchar(200)',1,N'20261118120000_AddCustomerProfileOverrides'),
    (N'CustomerProfileOverrides',N'ExternalSource',N'nvarchar(200)',1,N'20261118120000_AddCustomerProfileOverrides'),
    (N'CustomerProfileOverrides',N'FullName',N'nvarchar(200)',1,N'20261119081103_AddCustomerProfileAuditAndProperties'),
    (N'CustomerProfileOverrides',N'IdentityKind',N'int',0,N'20261118120000_AddCustomerProfileOverrides'),
    (N'CustomerProfileOverrides',N'IdentityPhone',N'nvarchar(32)',1,N'20261118120000_AddCustomerProfileOverrides'),
    (N'CustomerProfileOverrides',N'LandlineNumber',N'nvarchar(32)',1,N'20261119081103_AddCustomerProfileAuditAndProperties'),
    (N'CustomerProfileOverrides',N'Location',N'nvarchar(200)',1,N'20261119081103_AddCustomerProfileAuditAndProperties'),
    (N'CustomerProfileOverrides',N'Nationality',N'nvarchar(100)',1,N'20261119081103_AddCustomerProfileAuditAndProperties'),
    (N'CustomerProfileOverrides',N'PhoneLocation',N'tinyint',1,N'20261118120000_AddCustomerProfileOverrides'),
    (N'CustomerProfileOverrides',N'PhoneNumber',N'nvarchar(32)',1,N'20261118120000_AddCustomerProfileOverrides'),
    (N'CustomerProfileOverrides',N'PreferredLanguage',N'nvarchar(100)',1,N'20261119081103_AddCustomerProfileAuditAndProperties'),
    (N'CustomerProfileOverrides',N'ProjectName',N'nvarchar(200)',1,N'20261118120000_AddCustomerProfileOverrides'),
    (N'CustomerProfileOverrides',N'RowVersion',N'rowversion',0,N'20261118120000_AddCustomerProfileOverrides'),
    (N'CustomerProfileOverrides',N'UnitNumber',N'nvarchar(100)',1,N'20261118120000_AddCustomerProfileOverrides'),
    (N'CustomerProfileOverrides',N'UpdatedAtUtc',N'datetime2',0,N'20261118120000_AddCustomerProfileOverrides'),
    (N'CustomerProfileOverrides',N'UpdatedByEmployeeId',N'uniqueidentifier',0,N'20261118120000_AddCustomerProfileOverrides'),
    (N'CustomerPropertyAssociations',N'CreatedAtUtc',N'datetime2',0,N'20261119081103_AddCustomerProfileAuditAndProperties'),
    (N'CustomerPropertyAssociations',N'CreatedByEmployeeId',N'uniqueidentifier',0,N'20261119081103_AddCustomerProfileAuditAndProperties'),
    (N'CustomerPropertyAssociations',N'CustomerKey',N'nvarchar(300)',0,N'20261119081103_AddCustomerProfileAuditAndProperties'),
    (N'CustomerPropertyAssociations',N'CustomerProfileId',N'uniqueidentifier',0,N'20261119081103_AddCustomerProfileAuditAndProperties'),
    (N'CustomerPropertyAssociations',N'CustomerPropertyAssociationId',N'bigint',0,N'20261119081103_AddCustomerProfileAuditAndProperties'),
    (N'CustomerPropertyAssociations',N'ProjectName',N'nvarchar(200)',0,N'20261119081103_AddCustomerProfileAuditAndProperties'),
    (N'CustomerPropertyAssociations',N'Relationship',N'tinyint',0,N'20261119081103_AddCustomerProfileAuditAndProperties'),
    (N'CustomerPropertyAssociations',N'RowVersion',N'rowversion',0,N'20261119081103_AddCustomerProfileAuditAndProperties'),
    (N'CustomerPropertyAssociations',N'UnitNumber',N'nvarchar(100)',0,N'20261119081103_AddCustomerProfileAuditAndProperties'),
    (N'CustomerPropertyAssociations',N'UpdatedAtUtc',N'datetime2',0,N'20261119081103_AddCustomerProfileAuditAndProperties'),
    (N'CustomerPropertyAssociations',N'UpdatedByEmployeeId',N'uniqueidentifier',0,N'20261119081103_AddCustomerProfileAuditAndProperties'),
    (N'DepartmentAssignmentRotations',N'DepartmentId',N'int',0,N'20261031094748_AddDepartmentAssignmentRotation'),
    (N'DepartmentAssignmentRotations',N'LastAssignedEmployeeId',N'uniqueidentifier',1,N'20261031094748_AddDepartmentAssignmentRotation'),
    (N'DepartmentAssignmentRotations',N'RowVersion',N'rowversion',0,N'20261031094748_AddDepartmentAssignmentRotation'),
    (N'DepartmentAssignmentRotations',N'UpdatedAtUtc',N'datetime2',0,N'20261031094748_AddDepartmentAssignmentRotation'),
    (N'DepartmentCustomerLookupSources',N'DepartmentCustomerLookupSourceId',N'int',0,N'20260825061341_AddDepartmentCustomerLookupSource'),
    (N'DepartmentCustomerLookupSources',N'DepartmentId',N'int',0,N'20260825061341_AddDepartmentCustomerLookupSource'),
    (N'DepartmentCustomerLookupSources',N'Source',N'tinyint',0,N'20260825061341_AddDepartmentCustomerLookupSource'),
    (N'DepartmentWorkflowSettings',N'AllowAssignment',N'bit',0,N'20260903164723_AddWorkflowConfiguration'),
    (N'DepartmentWorkflowSettings',N'AllowInternalReassignment',N'bit',0,N'20260903164723_AddWorkflowConfiguration'),
    (N'DepartmentWorkflowSettings',N'AllowTransferToOtherDepartments',N'bit',0,N'20260903164723_AddWorkflowConfiguration'),
    (N'DepartmentWorkflowSettings',N'DepartmentId',N'int',0,N'20260903164723_AddWorkflowConfiguration'),
    (N'DepartmentWorkflowSettings',N'HeadRoleName',N'nvarchar(64)',0,N'20260903164723_AddWorkflowConfiguration'),
    (N'DepartmentWorkflowSettings',N'SupervisorRoleName',N'nvarchar(64)',0,N'20260904075902_AddWorkflowAutomation'),
    (N'Departments',N'Code',N'nvarchar(10)',0,N'20260819072347_InitialIdentityAndAccess'),
    (N'Departments',N'DepartmentId',N'int',0,N'20260819072347_InitialIdentityAndAccess'),
    (N'Departments',N'IsActive',N'bit',0,N'20260819072347_InitialIdentityAndAccess'),
    (N'Departments',N'Name',N'nvarchar(100)',0,N'20260819072347_InitialIdentityAndAccess'),
    (N'EmployeeReviewDevices',N'CreatedAtUtc',N'datetime2',0,N'20261124071552_AddEmployeeReviews'),
    (N'EmployeeReviewDevices',N'DeviceName',N'nvarchar(100)',0,N'20261124071552_AddEmployeeReviews'),
    (N'EmployeeReviewDevices',N'EmployeeId',N'uniqueidentifier',0,N'20261124071552_AddEmployeeReviews'),
    (N'EmployeeReviewDevices',N'Id',N'uniqueidentifier',0,N'20261124071552_AddEmployeeReviews'),
    (N'EmployeeReviewDevices',N'IsActive',N'bit',0,N'20261124071552_AddEmployeeReviews'),
    (N'EmployeeReviewDevices',N'LastSeenAtUtc',N'datetime2',1,N'20261124071552_AddEmployeeReviews'),
    (N'EmployeeReviewDevices',N'TokenHash',N'varbinary(32)',1,N'20261124071552_AddEmployeeReviews'),
    (N'EmployeeReviewDevices',N'UpdatedAtUtc',N'datetime2',0,N'20261124071552_AddEmployeeReviews'),
    (N'EmployeeReviews',N'Comment',N'nvarchar(1000)',1,N'20261124071552_AddEmployeeReviews'),
    (N'EmployeeReviews',N'CreatedAtUtc',N'datetime2',0,N'20261124071552_AddEmployeeReviews'),
    (N'EmployeeReviews',N'DeviceId',N'uniqueidentifier',0,N'20261124071552_AddEmployeeReviews'),
    (N'EmployeeReviews',N'EmployeeId',N'uniqueidentifier',0,N'20261124071552_AddEmployeeReviews'),
    (N'EmployeeReviews',N'Id',N'uniqueidentifier',0,N'20261124071552_AddEmployeeReviews'),
    (N'EmployeeReviews',N'Rating',N'tinyint',0,N'20261124071552_AddEmployeeReviews'),
    (N'EmployeeReviews',N'SubmissionId',N'uniqueidentifier',0,N'20261124071552_AddEmployeeReviews'),
    (N'Employees',N'CreatedAtUtc',N'datetime2',0,N'20260819072347_InitialIdentityAndAccess'),
    (N'Employees',N'DeactivatedAtUtc',N'datetime2',1,N'20260819072347_InitialIdentityAndAccess'),
    (N'Employees',N'DisplayName',N'nvarchar(200)',0,N'20260819072347_InitialIdentityAndAccess'),
    (N'Employees',N'EmployeeId',N'uniqueidentifier',0,N'20260819072347_InitialIdentityAndAccess'),
    (N'Employees',N'IsGeynessStaff',N'bit',0,N'20260819072347_InitialIdentityAndAccess'),
    (N'GenesysQueueMappings',N'CreatedAtUtc',N'datetime2',0,N'20260910080219_AddGenesysIntegration'),
    (N'GenesysQueueMappings',N'DepartmentId',N'int',0,N'20260910080219_AddGenesysIntegration'),
    (N'GenesysQueueMappings',N'GenesysQueueMappingId',N'int',0,N'20260910080219_AddGenesysIntegration'),
    (N'GenesysQueueMappings',N'IsActive',N'bit',0,N'20260910080219_AddGenesysIntegration'),
    (N'GenesysQueueMappings',N'QueueId',N'nvarchar(64)',0,N'20260910080219_AddGenesysIntegration'),
    (N'GenesysQueueMappings',N'QueueName',N'nvarchar(200)',1,N'20260910080219_AddGenesysIntegration'),
    (N'GenesysQueueMappings',N'UpdatedAtUtc',N'datetime2',0,N'20260910080219_AddGenesysIntegration'),
    (N'GenesysScreenPopLaunches',N'ConversationId',N'nvarchar(128)',1,N'20260928132618_AddGenesysScreenPopLaunches'),
    (N'GenesysScreenPopLaunches',N'ExpiresAtUtc',N'datetime2',0,N'20260928132618_AddGenesysScreenPopLaunches'),
    (N'GenesysScreenPopLaunches',N'GenesysScreenPopLaunchId',N'bigint',0,N'20260928132618_AddGenesysScreenPopLaunches'),
    (N'GenesysScreenPopLaunches',N'GenesysUserId',N'nvarchar(64)',0,N'20260928132618_AddGenesysScreenPopLaunches'),
    (N'GenesysScreenPopLaunches',N'IssuedAtUtc',N'datetime2',0,N'20260928132618_AddGenesysScreenPopLaunches'),
    (N'GenesysScreenPopLaunches',N'IssuedByEmployeeId',N'uniqueidentifier',0,N'20260928132618_AddGenesysScreenPopLaunches'),
    (N'GenesysScreenPopLaunches',N'RedeemedAtUtc',N'datetime2',1,N'20260928132618_AddGenesysScreenPopLaunches'),
    (N'GenesysScreenPopLaunches',N'TargetPath',N'nvarchar(512)',0,N'20260928132618_AddGenesysScreenPopLaunches'),
    (N'GenesysScreenPopLaunches',N'TicketId',N'bigint',1,N'20260928132618_AddGenesysScreenPopLaunches'),
    (N'GenesysScreenPopLaunches',N'TokenHash',N'char(64)',0,N'20260928132618_AddGenesysScreenPopLaunches'),
    (N'GenesysScreenPopLaunches',N'UserId',N'uniqueidentifier',0,N'20260928132618_AddGenesysScreenPopLaunches'),
    (N'Holidays',N'BusinessCalendarId',N'int',0,N'20260821130000_AddSlaAndEscalation'),
    (N'Holidays',N'ConfirmedAtUtc',N'datetime2',1,N'20260821130000_AddSlaAndEscalation'),
    (N'Holidays',N'ConfirmedByEmployeeId',N'uniqueidentifier',1,N'20260821130000_AddSlaAndEscalation'),
    (N'Holidays',N'Description',N'nvarchar(200)',1,N'20260821130000_AddSlaAndEscalation'),
    (N'Holidays',N'EnteredByEmployeeId',N'uniqueidentifier',0,N'20260821130000_AddSlaAndEscalation'),
    (N'Holidays',N'HolidayDate',N'date',0,N'20260821130000_AddSlaAndEscalation'),
    (N'Holidays',N'HolidayId',N'int',0,N'20260821130000_AddSlaAndEscalation'),
    (N'IdempotencyRecords',N'ExpiresAtUtc',N'datetime2',1,N'20260821130000_AddSlaAndEscalation'),
    (N'IdempotencyRecords',N'FirstSeenAtUtc',N'datetime2',0,N'20260821130000_AddSlaAndEscalation'),
    (N'IdempotencyRecords',N'IdempotencyKey',N'nvarchar(300)',0,N'20260821130000_AddSlaAndEscalation'),
    (N'IdempotencyRecords',N'IdempotencyRecordId',N'bigint',0,N'20260821130000_AddSlaAndEscalation'),
    (N'IdempotencyRecords',N'LastSeenAtUtc',N'datetime2',0,N'20260821130000_AddSlaAndEscalation'),
    (N'IdempotencyRecords',N'ResultReference',N'nvarchar(200)',1,N'20260821130000_AddSlaAndEscalation'),
    (N'IdempotencyRecords',N'Scope',N'nvarchar(50)',0,N'20260821130000_AddSlaAndEscalation'),
    (N'IntakeRecords',N'ChannelId',N'tinyint',0,N'20260820140000_AddTicketing'),
    (N'IntakeRecords',N'CreatedByEmployeeId',N'uniqueidentifier',0,N'20260820140000_AddTicketing'),
    (N'IntakeRecords',N'CrmVerificationStatus',N'tinyint',0,N'20260820140000_AddTicketing'),
    (N'IntakeRecords',N'DepartmentId',N'int',1,N'20260825061341_AddDepartmentCustomerLookupSource'),
    (N'IntakeRecords',N'IntakeRecordId',N'bigint',0,N'20260820140000_AddTicketing'),
    (N'IntakeRecords',N'IsUnitRelated',N'bit',0,N'20260820140000_AddTicketing'),
    (N'IntakeRecords',N'LinkedTicketId',N'bigint',1,N'20260820140000_AddTicketing'),
    (N'IntakeRecords',N'PhoneNumber',N'nvarchar(30)',0,N'20260825053743_AddIntakeRecordPhoneNumber'),
    (N'IntakeRecords',N'PriorityHint',N'tinyint',1,N'20260820140000_AddTicketing'),
    (N'IntakeRecords',N'RawUnitNumberEntered',N'nvarchar(50)',1,N'20260820140000_AddTicketing'),
    (N'IntakeRecords',N'ReceivedAtUtc',N'datetime2',0,N'20260820140000_AddTicketing'),
    (N'Notifications',N'Channel',N'tinyint',0,N'20260822090000_AddNotificationsAndOutbox'),
    (N'Notifications',N'CorrelationId',N'uniqueidentifier',0,N'20260822090000_AddNotificationsAndOutbox'),
    (N'Notifications',N'CreatedAtUtc',N'datetime2',0,N'20260822090000_AddNotificationsAndOutbox'),
    (N'Notifications',N'DeliveryStatus',N'tinyint',0,N'20260822090000_AddNotificationsAndOutbox'),
    (N'Notifications',N'NotificationId',N'bigint',0,N'20260822090000_AddNotificationsAndOutbox'),
    (N'Notifications',N'NotificationType',N'tinyint',0,N'20260822090000_AddNotificationsAndOutbox'),
    (N'Notifications',N'OutboxMessageId',N'uniqueidentifier',1,N'20260822090000_AddNotificationsAndOutbox'),
    (N'Notifications',N'RecipientAddress',N'nvarchar(320)',1,N'20260822090000_AddNotificationsAndOutbox'),
    (N'Notifications',N'RecipientEmployeeId',N'uniqueidentifier',1,N'20260822090000_AddNotificationsAndOutbox'),
    (N'Notifications',N'RetryCount',N'int',0,N'20260822090000_AddNotificationsAndOutbox'),
    (N'Notifications',N'TicketId',N'bigint',1,N'20260822090000_AddNotificationsAndOutbox'),
    (N'OutboxMessages',N'Attempts',N'int',0,N'20260822090000_AddNotificationsAndOutbox'),
    (N'OutboxMessages',N'CorrelationId',N'uniqueidentifier',0,N'20260822090000_AddNotificationsAndOutbox'),
    (N'OutboxMessages',N'EventType',N'nvarchar(200)',0,N'20260822090000_AddNotificationsAndOutbox'),
    (N'OutboxMessages',N'IdempotencyRecordId',N'bigint',0,N'20260822090000_AddNotificationsAndOutbox'),
    (N'OutboxMessages',N'LastError',N'nvarchar(2000)',1,N'20260822090000_AddNotificationsAndOutbox'),
    (N'OutboxMessages',N'OccurredAtUtc',N'datetime2',0,N'20260822090000_AddNotificationsAndOutbox'),
    (N'OutboxMessages',N'OutboxMessageId',N'uniqueidentifier',0,N'20260822090000_AddNotificationsAndOutbox'),
    (N'OutboxMessages',N'Payload',N'nvarchar(max)',0,N'20260822090000_AddNotificationsAndOutbox'),
    (N'OutboxMessages',N'ProcessedAtUtc',N'datetime2',1,N'20260822090000_AddNotificationsAndOutbox'),
    (N'OutboxMessages',N'Status',N'tinyint',0,N'20260822090000_AddNotificationsAndOutbox'),
    (N'Priorities',N'DisplayOrder',N'tinyint',0,N'20260820140000_AddTicketing'),
    (N'Priorities',N'Name',N'nvarchar(20)',0,N'20260820140000_AddTicketing'),
    (N'Priorities',N'PriorityId',N'tinyint',0,N'20260820140000_AddTicketing'),
    (N'PriorityDowngradeRequests',N'CurrentPriorityId',N'tinyint',0,N'20261008184722_AddSlaPauseAndPriorityDowngrade'),
    (N'PriorityDowngradeRequests',N'DecidedAtUtc',N'datetime2',1,N'20261008184722_AddSlaPauseAndPriorityDowngrade'),
    (N'PriorityDowngradeRequests',N'DecidedByEmployeeId',N'uniqueidentifier',1,N'20261008184722_AddSlaPauseAndPriorityDowngrade'),
    (N'PriorityDowngradeRequests',N'DecisionNote',N'nvarchar(1000)',1,N'20261008184722_AddSlaPauseAndPriorityDowngrade'),
    (N'PriorityDowngradeRequests',N'ExpiresAtUtc',N'datetime2',0,N'20261008184722_AddSlaPauseAndPriorityDowngrade'),
    (N'PriorityDowngradeRequests',N'PriorityDowngradeRequestId',N'bigint',0,N'20261008184722_AddSlaPauseAndPriorityDowngrade'),
    (N'PriorityDowngradeRequests',N'Reason',N'nvarchar(1000)',0,N'20261008184722_AddSlaPauseAndPriorityDowngrade'),
    (N'PriorityDowngradeRequests',N'RequestedAtUtc',N'datetime2',0,N'20261008184722_AddSlaPauseAndPriorityDowngrade'),
    (N'PriorityDowngradeRequests',N'RequestedByEmployeeId',N'uniqueidentifier',0,N'20261008184722_AddSlaPauseAndPriorityDowngrade'),
    (N'PriorityDowngradeRequests',N'RequestedPriorityId',N'tinyint',0,N'20261008184722_AddSlaPauseAndPriorityDowngrade'),
    (N'PriorityDowngradeRequests',N'RowVersion',N'rowversion',0,N'20261008184722_AddSlaPauseAndPriorityDowngrade'),
    (N'PriorityDowngradeRequests',N'Status',N'tinyint',0,N'20261008184722_AddSlaPauseAndPriorityDowngrade'),
    (N'PriorityDowngradeRequests',N'TicketId',N'bigint',0,N'20261008184722_AddSlaPauseAndPriorityDowngrade'),
    (N'QueueingCompanyMappings',N'CompanyId',N'nvarchar(100)',0,N'20261117101035_AddQueueingIntegration'),
    (N'QueueingCompanyMappings',N'CreatedAtUtc',N'datetime2',0,N'20261117101035_AddQueueingIntegration'),
    (N'QueueingCompanyMappings',N'DepartmentId',N'int',0,N'20261117101035_AddQueueingIntegration'),
    (N'QueueingCompanyMappings',N'IsActive',N'bit',0,N'20261117101035_AddQueueingIntegration'),
    (N'QueueingCompanyMappings',N'QueueingCompanyMappingId',N'int',0,N'20261117101035_AddQueueingIntegration'),
    (N'QueueingEvents',N'AttemptCount',N'int',0,N'20261117101035_AddQueueingIntegration'),
    (N'QueueingEvents',N'EventId',N'nvarchar(100)',0,N'20261117101035_AddQueueingIntegration'),
    (N'QueueingEvents',N'EventSequence',N'bigint',0,N'20261117101035_AddQueueingIntegration'),
    (N'QueueingEvents',N'EventType',N'tinyint',0,N'20261117101035_AddQueueingIntegration'),
    (N'QueueingEvents',N'ExternalTicketId',N'nvarchar(100)',0,N'20261117101035_AddQueueingIntegration'),
    (N'QueueingEvents',N'NextRetryAtUtc',N'datetime2',1,N'20261117101035_AddQueueingIntegration'),
    (N'QueueingEvents',N'OccurredAtUtc',N'datetime2',0,N'20261117101035_AddQueueingIntegration'),
    (N'QueueingEvents',N'OutcomeCode',N'nvarchar(64)',1,N'20261117101035_AddQueueingIntegration'),
    (N'QueueingEvents',N'OutcomeDetail',N'nvarchar(1000)',1,N'20261117101035_AddQueueingIntegration'),
    (N'QueueingEvents',N'Payload',N'nvarchar(max)',1,N'20261117101035_AddQueueingIntegration'),
    (N'QueueingEvents',N'PayloadHash',N'char(64)',0,N'20261117101035_AddQueueingIntegration'),
    (N'QueueingEvents',N'PayloadPurgedAtUtc',N'datetime2',1,N'20261117101035_AddQueueingIntegration'),
    (N'QueueingEvents',N'ProcessedAtUtc',N'datetime2',1,N'20261117101035_AddQueueingIntegration'),
    (N'QueueingEvents',N'QueueingEventId',N'bigint',0,N'20261117101035_AddQueueingIntegration'),
    (N'QueueingEvents',N'ReceivedAtUtc',N'datetime2',0,N'20261117101035_AddQueueingIntegration'),
    (N'QueueingEvents',N'Source',N'nvarchar(50)',0,N'20261117101035_AddQueueingIntegration'),
    (N'QueueingEvents',N'Status',N'tinyint',0,N'20261117101035_AddQueueingIntegration'),
    (N'QueueingEvents',N'TicketId',N'bigint',1,N'20261117101035_AddQueueingIntegration'),
    (N'QueueingTicketLinks',N'AssignedExternalUserId',N'nvarchar(100)',1,N'20261117101035_AddQueueingIntegration'),
    (N'QueueingTicketLinks',N'CompanyId',N'nvarchar(100)',1,N'20261117101035_AddQueueingIntegration'),
    (N'QueueingTicketLinks',N'CounterId',N'nvarchar(100)',1,N'20261117101035_AddQueueingIntegration'),
    (N'QueueingTicketLinks',N'CreatedAtUtc',N'datetime2',0,N'20261117101035_AddQueueingIntegration'),
    (N'QueueingTicketLinks',N'ExternalTicketId',N'nvarchar(100)',0,N'20261117101035_AddQueueingIntegration'),
    (N'QueueingTicketLinks',N'ExternalTicketTypeId',N'nvarchar(100)',1,N'20261117101035_AddQueueingIntegration'),
    (N'QueueingTicketLinks',N'LastAppliedOccurredAtUtc',N'datetime2',0,N'20261117101035_AddQueueingIntegration'),
    (N'QueueingTicketLinks',N'LastAppliedSequence',N'bigint',0,N'20261117101035_AddQueueingIntegration'),
    (N'QueueingTicketLinks',N'QueueNumber',N'nvarchar(100)',1,N'20261117101035_AddQueueingIntegration'),
    (N'QueueingTicketLinks',N'QueueingTicketLinkId',N'bigint',0,N'20261117101035_AddQueueingIntegration'),
    (N'QueueingTicketLinks',N'RowVersion',N'rowversion',0,N'20261117101035_AddQueueingIntegration'),
    (N'QueueingTicketLinks',N'State',N'tinyint',0,N'20261117101035_AddQueueingIntegration'),
    (N'QueueingTicketLinks',N'TicketId',N'bigint',0,N'20261117101035_AddQueueingIntegration'),
    (N'QueueingTicketTypeMappings',N'CompanyId',N'nvarchar(100)',0,N'20261117101035_AddQueueingIntegration'),
    (N'QueueingTicketTypeMappings',N'CreatedAtUtc',N'datetime2',0,N'20261117101035_AddQueueingIntegration'),
    (N'QueueingTicketTypeMappings',N'IsActive',N'bit',0,N'20261117101035_AddQueueingIntegration'),
    (N'QueueingTicketTypeMappings',N'QueueingTicketTypeMappingId',N'int',0,N'20261117101035_AddQueueingIntegration'),
    (N'QueueingTicketTypeMappings',N'RequestTypeId',N'int',0,N'20261117101035_AddQueueingIntegration'),
    (N'QueueingTicketTypeMappings',N'TicketTypeId',N'nvarchar(100)',0,N'20261117101035_AddQueueingIntegration'),
    (N'QueueingUserMappings',N'CreatedAtUtc',N'datetime2',0,N'20261117101035_AddQueueingIntegration'),
    (N'QueueingUserMappings',N'EmployeeId',N'uniqueidentifier',0,N'20261117101035_AddQueueingIntegration'),
    (N'QueueingUserMappings',N'ExternalUserId',N'nvarchar(100)',0,N'20261117101035_AddQueueingIntegration'),
    (N'QueueingUserMappings',N'IsActive',N'bit',0,N'20261117101035_AddQueueingIntegration'),
    (N'QueueingUserMappings',N'QueueingUserMappingId',N'int',0,N'20261117101035_AddQueueingIntegration'),
    (N'RequestTypeApprovalRequirements',N'ApprovalType',N'tinyint',0,N'20260904091143_AddApprovalWorkflow'),
    (N'RequestTypeApprovalRequirements',N'BlocksWorkUntilApproved',N'bit',0,N'20260904091143_AddApprovalWorkflow'),
    (N'RequestTypeApprovalRequirements',N'IsActive',N'bit',0,N'20260904091143_AddApprovalWorkflow'),
    (N'RequestTypeApprovalRequirements',N'RequestTypeApprovalRequirementId',N'int',0,N'20260904091143_AddApprovalWorkflow'),
    (N'RequestTypeApprovalRequirements',N'RequestTypeId',N'int',0,N'20260904091143_AddApprovalWorkflow'),
    (N'RequestTypeApprovalRequirements',N'TargetDepartmentId',N'int',1,N'20260904091143_AddApprovalWorkflow'),
    (N'RequestTypeApprovalRequirements',N'TargetEmployeeId',N'uniqueidentifier',1,N'20260904091143_AddApprovalWorkflow'),
    (N'RequestTypeApprovalRequirements',N'TargetKind',N'tinyint',0,N'20260904091143_AddApprovalWorkflow'),
    (N'RequestTypeApprovalRequirements',N'TargetRoleName',N'nvarchar(64)',1,N'20260904091143_AddApprovalWorkflow'),
    (N'RequestTypeAssignmentRuleMembers',N'EmployeeId',N'uniqueidentifier',0,N'20260904075902_AddWorkflowAutomation'),
    (N'RequestTypeAssignmentRuleMembers',N'RequestTypeAssignmentRuleId',N'int',0,N'20260904075902_AddWorkflowAutomation'),
    (N'RequestTypeAssignmentRuleMembers',N'RequestTypeAssignmentRuleMemberId',N'int',0,N'20260904075902_AddWorkflowAutomation'),
    (N'RequestTypeAssignmentRules',N'IsActive',N'bit',0,N'20260904075902_AddWorkflowAutomation'),
    (N'RequestTypeAssignmentRules',N'Mode',N'tinyint',0,N'20260904075902_AddWorkflowAutomation'),
    (N'RequestTypeAssignmentRules',N'PrimaryEmployeeId',N'uniqueidentifier',1,N'20260904075902_AddWorkflowAutomation'),
    (N'RequestTypeAssignmentRules',N'RequestTypeAssignmentRuleId',N'int',0,N'20260904075902_AddWorkflowAutomation'),
    (N'RequestTypeAssignmentRules',N'RequestTypeId',N'int',0,N'20260904075902_AddWorkflowAutomation'),
    (N'RequestTypeAssignmentRules',N'TeamName',N'nvarchar(100)',1,N'20260904075902_AddWorkflowAutomation'),
    (N'RequestTypeSlaPolicies',N'ClockBasis',N'tinyint',1,N'20260903164723_AddWorkflowConfiguration'),
    (N'RequestTypeSlaPolicies',N'FirstResponseMaximumValue',N'int',1,N'20260903164723_AddWorkflowConfiguration'),
    (N'RequestTypeSlaPolicies',N'FirstResponseTargetValue',N'int',1,N'20260903164723_AddWorkflowConfiguration'),
    (N'RequestTypeSlaPolicies',N'IsActive',N'bit',0,N'20260903164723_AddWorkflowConfiguration'),
    (N'RequestTypeSlaPolicies',N'IsImmediate',N'bit',0,N'20260903164723_AddWorkflowConfiguration'),
    (N'RequestTypeSlaPolicies',N'PausesOnPendingCustomer',N'bit',1,N'20260903164723_AddWorkflowConfiguration'),
    (N'RequestTypeSlaPolicies',N'PausesOnPendingInternal',N'bit',1,N'20260903164723_AddWorkflowConfiguration');
INSERT #ExpCols (TableName,ColName,ColType,IsNullable,Owner) VALUES
    (N'RequestTypeSlaPolicies',N'PriorityId',N'tinyint',0,N'20260903164723_AddWorkflowConfiguration'),
    (N'RequestTypeSlaPolicies',N'RequestTypeId',N'int',0,N'20260903164723_AddWorkflowConfiguration'),
    (N'RequestTypeSlaPolicies',N'RequestTypeSlaPolicyId',N'int',0,N'20260903164723_AddWorkflowConfiguration'),
    (N'RequestTypeSlaPolicies',N'ResolutionMaximumValue',N'int',1,N'20260903164723_AddWorkflowConfiguration'),
    (N'RequestTypeSlaPolicies',N'ResolutionTargetValue',N'int',1,N'20260903164723_AddWorkflowConfiguration'),
    (N'RequestTypeSlaPolicies',N'Trigger',N'tinyint',0,N'20260903164723_AddWorkflowConfiguration'),
    (N'RequestTypeSlaPolicies',N'Unit',N'tinyint',0,N'20260903164723_AddWorkflowConfiguration'),
    (N'RequestTypeSlaPolicies',N'WarningThresholdPercent',N'decimal(5,2)',1,N'20260903164723_AddWorkflowConfiguration'),
    (N'RequestTypes',N'AllowAgentPriorityChange',N'bit',0,N'20260903164723_AddWorkflowConfiguration'),
    (N'RequestTypes',N'AllowPendingCustomer',N'bit',0,N'20260903164723_AddWorkflowConfiguration'),
    (N'RequestTypes',N'AllowPendingInternal',N'bit',0,N'20260903164723_AddWorkflowConfiguration'),
    (N'RequestTypes',N'AllowReopen',N'bit',0,N'20260903164723_AddWorkflowConfiguration'),
    (N'RequestTypes',N'DefaultPriorityId',N'tinyint',0,N'20260903164723_AddWorkflowConfiguration'),
    (N'RequestTypes',N'DepartmentId',N'int',0,N'20260903164723_AddWorkflowConfiguration'),
    (N'RequestTypes',N'IsActive',N'bit',0,N'20260903164723_AddWorkflowConfiguration'),
    (N'RequestTypes',N'Name',N'nvarchar(100)',0,N'20260903164723_AddWorkflowConfiguration'),
    (N'RequestTypes',N'RequestTypeId',N'int',0,N'20260903164723_AddWorkflowConfiguration'),
    (N'RequestTypes',N'RequiredFieldsJson',N'nvarchar(2000)',1,N'20260903164723_AddWorkflowConfiguration'),
    (N'RequestTypes',N'WorkflowId',N'int',0,NULL),
    (N'SlaPolicies',N'ClockBasis',N'tinyint',0,N'20260821130000_AddSlaAndEscalation'),
    (N'SlaPolicies',N'FirstResponseTargetMinutes',N'int',0,N'20260821130000_AddSlaAndEscalation'),
    (N'SlaPolicies',N'IsActive',N'bit',0,N'20260821130000_AddSlaAndEscalation'),
    (N'SlaPolicies',N'Level2ToGmWindowUnit',N'tinyint',0,N'20260821130000_AddSlaAndEscalation'),
    (N'SlaPolicies',N'Level2ToGmWindowValue',N'int',0,N'20260821130000_AddSlaAndEscalation'),
    (N'SlaPolicies',N'PriorityId',N'tinyint',0,N'20260821130000_AddSlaAndEscalation'),
    (N'SlaPolicies',N'ResolutionTargetMinutes',N'int',0,N'20260821130000_AddSlaAndEscalation'),
    (N'SlaPolicies',N'WarningThresholdPercent',N'decimal(5,2)',0,N'20260821130000_AddSlaAndEscalation'),
    (N'TicketAgentHandoffs',N'AssignedAtUtc',N'datetime2',1,N'20260910080219_AddGenesysIntegration'),
    (N'TicketAgentHandoffs',N'AssignedEmployeeId',N'uniqueidentifier',1,N'20260910080219_AddGenesysIntegration'),
    (N'TicketAgentHandoffs',N'ChannelId',N'tinyint',0,N'20260910080219_AddGenesysIntegration'),
    (N'TicketAgentHandoffs',N'CompletedAtUtc',N'datetime2',1,N'20260910080219_AddGenesysIntegration'),
    (N'TicketAgentHandoffs',N'CreatedAtUtc',N'datetime2',0,N'20260910080219_AddGenesysIntegration'),
    (N'TicketAgentHandoffs',N'DepartmentId',N'int',0,N'20260910080219_AddGenesysIntegration'),
    (N'TicketAgentHandoffs',N'ExternalWorkItemId',N'nvarchar(64)',1,N'20260910080219_AddGenesysIntegration'),
    (N'TicketAgentHandoffs',N'GenesysAgentId',N'nvarchar(64)',1,N'20260910080219_AddGenesysIntegration'),
    (N'TicketAgentHandoffs',N'Mode',N'tinyint',1,N'20260910080219_AddGenesysIntegration'),
    (N'TicketAgentHandoffs',N'RequestReason',N'nvarchar(500)',1,N'20260910080219_AddGenesysIntegration'),
    (N'TicketAgentHandoffs',N'RequestedAtUtc',N'datetime2',0,N'20260910080219_AddGenesysIntegration'),
    (N'TicketAgentHandoffs',N'ResolutionNote',N'nvarchar(500)',1,N'20260910080219_AddGenesysIntegration'),
    (N'TicketAgentHandoffs',N'ResolvedAtUtc',N'datetime2',1,N'20260910080219_AddGenesysIntegration'),
    (N'TicketAgentHandoffs',N'RowVersion',N'rowversion',0,N'20260921133507_AddAgentHandoffTriggerAndConcurrency'),
    (N'TicketAgentHandoffs',N'StartedAtUtc',N'datetime2',1,N'20260910080219_AddGenesysIntegration'),
    (N'TicketAgentHandoffs',N'Status',N'tinyint',0,N'20260910080219_AddGenesysIntegration'),
    (N'TicketAgentHandoffs',N'TicketAgentHandoffId',N'bigint',0,N'20260910080219_AddGenesysIntegration'),
    (N'TicketAgentHandoffs',N'TicketId',N'bigint',0,N'20260910080219_AddGenesysIntegration'),
    (N'TicketAgentHandoffs',N'TicketInteractionId',N'bigint',0,N'20260910080219_AddGenesysIntegration'),
    (N'TicketAgentHandoffs',N'Trigger',N'tinyint',1,N'20260921133507_AddAgentHandoffTriggerAndConcurrency'),
    (N'TicketApprovals',N'ApprovalType',N'tinyint',0,N'20260904091143_AddApprovalWorkflow'),
    (N'TicketApprovals',N'CorrelationId',N'uniqueidentifier',0,N'20260904091143_AddApprovalWorkflow'),
    (N'TicketApprovals',N'DecidedByEmployeeId',N'uniqueidentifier',1,N'20260904091143_AddApprovalWorkflow'),
    (N'TicketApprovals',N'DecisionAtUtc',N'datetime2',1,N'20260904091143_AddApprovalWorkflow'),
    (N'TicketApprovals',N'DecisionComment',N'nvarchar(1000)',1,N'20260904091143_AddApprovalWorkflow'),
    (N'TicketApprovals',N'IsCurrent',N'bit',0,N'20260904091143_AddApprovalWorkflow'),
    (N'TicketApprovals',N'RequestComment',N'nvarchar(1000)',1,N'20260904091143_AddApprovalWorkflow'),
    (N'TicketApprovals',N'RequestedAtUtc',N'datetime2',0,N'20260904091143_AddApprovalWorkflow'),
    (N'TicketApprovals',N'RequestedByEmployeeId',N'uniqueidentifier',0,N'20260904091143_AddApprovalWorkflow'),
    (N'TicketApprovals',N'Status',N'tinyint',0,N'20260904091143_AddApprovalWorkflow'),
    (N'TicketApprovals',N'TargetDepartmentId',N'int',1,N'20260904091143_AddApprovalWorkflow'),
    (N'TicketApprovals',N'TargetEmployeeId',N'uniqueidentifier',1,N'20260904091143_AddApprovalWorkflow'),
    (N'TicketApprovals',N'TargetKind',N'tinyint',0,N'20260904091143_AddApprovalWorkflow'),
    (N'TicketApprovals',N'TargetRoleName',N'nvarchar(64)',1,N'20260904091143_AddApprovalWorkflow'),
    (N'TicketApprovals',N'TicketApprovalId',N'bigint',0,N'20260904091143_AddApprovalWorkflow'),
    (N'TicketApprovals',N'TicketId',N'bigint',0,N'20260904091143_AddApprovalWorkflow'),
    (N'TicketAssignmentHistory',N'ChangedAtUtc',N'datetime2',0,N'20261104130835_AddCollectionsTowerAssignments'),
    (N'TicketAssignmentHistory',N'ChangedByEmployeeId',N'uniqueidentifier',1,N'20261104130835_AddCollectionsTowerAssignments'),
    (N'TicketAssignmentHistory',N'DepartmentId',N'int',0,N'20261104130835_AddCollectionsTowerAssignments'),
    (N'TicketAssignmentHistory',N'GenesysConversationId',N'nvarchar(64)',1,N'20261112065853_AddGenesysAssignmentHistorySource'),
    (N'TicketAssignmentHistory',N'Kind',N'tinyint',0,N'20261104130835_AddCollectionsTowerAssignments'),
    (N'TicketAssignmentHistory',N'NewEmployeeId',N'uniqueidentifier',0,N'20261104130835_AddCollectionsTowerAssignments'),
    (N'TicketAssignmentHistory',N'PreviousDepartmentId',N'int',1,N'20261119120249_AddAssignmentHistoryPreviousDepartment'),
    (N'TicketAssignmentHistory',N'PreviousEmployeeId',N'uniqueidentifier',1,N'20261104130835_AddCollectionsTowerAssignments'),
    (N'TicketAssignmentHistory',N'Reason',N'nvarchar(500)',1,N'20261104130835_AddCollectionsTowerAssignments'),
    (N'TicketAssignmentHistory',N'Source',N'tinyint',1,N'20261112065853_AddGenesysAssignmentHistorySource'),
    (N'TicketAssignmentHistory',N'SourceEventAtUtc',N'datetime2',1,N'20261112065853_AddGenesysAssignmentHistorySource'),
    (N'TicketAssignmentHistory',N'TicketAssignmentHistoryEntryId',N'bigint',0,N'20261104130835_AddCollectionsTowerAssignments'),
    (N'TicketAssignmentHistory',N'TicketId',N'bigint',0,N'20261104130835_AddCollectionsTowerAssignments'),
    (N'TicketAssignmentHistory',N'TowerCode',N'nvarchar(20)',1,N'20261104130835_AddCollectionsTowerAssignments'),
    (N'TicketAssignmentHistory',N'TowerId',N'int',1,N'20261104130835_AddCollectionsTowerAssignments'),
    (N'TicketAssignmentHistory',N'TowerName',N'nvarchar(200)',1,N'20261104130835_AddCollectionsTowerAssignments'),
    (N'TicketAssignmentHistory',N'UnitNumber',N'nvarchar(100)',1,N'20261104130835_AddCollectionsTowerAssignments'),
    (N'TicketAssignments',N'AssignedAtUtc',N'datetime2',0,N'20260821090000_AddTicketOperations'),
    (N'TicketAssignments',N'AssignedDepartmentId',N'int',0,N'20260821090000_AddTicketOperations'),
    (N'TicketAssignments',N'AssignedEmployeeId',N'uniqueidentifier',0,N'20260821090000_AddTicketOperations'),
    (N'TicketAssignments',N'AssigningActorEmployeeId',N'uniqueidentifier',1,N'20260821090000_AddTicketOperations'),
    (N'TicketAssignments',N'IsCurrent',N'bit',0,N'20260821090000_AddTicketOperations'),
    (N'TicketAssignments',N'SupersededAtUtc',N'datetime2',1,N'20261112071745_AddTicketAssignmentSupersededAt'),
    (N'TicketAssignments',N'TicketAssignmentId',N'bigint',0,N'20260821090000_AddTicketOperations'),
    (N'TicketAssignments',N'TicketId',N'bigint',0,N'20260821090000_AddTicketOperations'),
    (N'TicketCollectionsUnits',N'CompanyId',N'int',0,N'20261104130835_AddCollectionsTowerAssignments'),
    (N'TicketCollectionsUnits',N'SelectedAtUtc',N'datetime2',0,N'20261104130835_AddCollectionsTowerAssignments'),
    (N'TicketCollectionsUnits',N'SelectedByEmployeeId',N'uniqueidentifier',0,N'20261104130835_AddCollectionsTowerAssignments'),
    (N'TicketCollectionsUnits',N'TicketId',N'bigint',0,N'20261104130835_AddCollectionsTowerAssignments'),
    (N'TicketCollectionsUnits',N'TowerCode',N'nvarchar(20)',0,N'20261104130835_AddCollectionsTowerAssignments'),
    (N'TicketCollectionsUnits',N'TowerId',N'int',0,N'20261104130835_AddCollectionsTowerAssignments'),
    (N'TicketCollectionsUnits',N'TowerName',N'nvarchar(200)',0,N'20261104130835_AddCollectionsTowerAssignments'),
    (N'TicketCollectionsUnits',N'UnitNumber',N'nvarchar(100)',0,N'20261104130835_AddCollectionsTowerAssignments'),
    (N'TicketCollectionsUnits',N'UnitRef',N'nvarchar(100)',0,N'20261104130835_AddCollectionsTowerAssignments'),
    (N'TicketEscalations',N'Level',N'tinyint',0,N'20260821130000_AddSlaAndEscalation'),
    (N'TicketEscalations',N'NotifiedRoles',N'nvarchar(200)',1,N'20260821130000_AddSlaAndEscalation'),
    (N'TicketEscalations',N'RaisedAtUtc',N'datetime2',0,N'20260821130000_AddSlaAndEscalation'),
    (N'TicketEscalations',N'RespondedAtUtc',N'datetime2',1,N'20260821130000_AddSlaAndEscalation'),
    (N'TicketEscalations',N'RespondingEmployeeId',N'uniqueidentifier',1,N'20260821130000_AddSlaAndEscalation'),
    (N'TicketEscalations',N'TicketEscalationId',N'bigint',0,N'20260821130000_AddSlaAndEscalation'),
    (N'TicketEscalations',N'TicketId',N'bigint',0,N'20260821130000_AddSlaAndEscalation'),
    (N'TicketEscalations',N'TriggerType',N'tinyint',0,N'20260821130000_AddSlaAndEscalation'),
    (N'TicketInteractionHandlers',N'ConnectedAtUtc',N'datetime2',1,N'20261030061255_AddTicketInteractionHandlers'),
    (N'TicketInteractionHandlers',N'GenesysAgentUserId',N'nvarchar(64)',0,N'20261030061255_AddTicketInteractionHandlers'),
    (N'TicketInteractionHandlers',N'HandledByUserId',N'uniqueidentifier',0,N'20261030061255_AddTicketInteractionHandlers'),
    (N'TicketInteractionHandlers',N'RecordedAtUtc',N'datetime2',0,N'20261030061255_AddTicketInteractionHandlers'),
    (N'TicketInteractionHandlers',N'Source',N'tinyint',0,N'20261030061255_AddTicketInteractionHandlers'),
    (N'TicketInteractionHandlers',N'TicketId',N'bigint',0,N'20261030061255_AddTicketInteractionHandlers'),
    (N'TicketInteractionHandlers',N'TicketInteractionHandlerId',N'bigint',0,N'20261030061255_AddTicketInteractionHandlers'),
    (N'TicketInteractionHandlers',N'TicketInteractionId',N'bigint',0,N'20261030061255_AddTicketInteractionHandlers'),
    (N'TicketInteractionMessages',N'Body',N'nvarchar(max)',0,N'20260910080219_AddGenesysIntegration'),
    (N'TicketInteractionMessages',N'ExternalMessageId',N'nvarchar(64)',1,N'20260910080219_AddGenesysIntegration'),
    (N'TicketInteractionMessages',N'Sender',N'tinyint',0,N'20260910080219_AddGenesysIntegration'),
    (N'TicketInteractionMessages',N'SenderId',N'nvarchar(64)',1,N'20260910080219_AddGenesysIntegration'),
    (N'TicketInteractionMessages',N'SenderName',N'nvarchar(200)',1,N'20260910080219_AddGenesysIntegration'),
    (N'TicketInteractionMessages',N'SentAtUtc',N'datetime2',0,N'20260910080219_AddGenesysIntegration'),
    (N'TicketInteractionMessages',N'Sequence',N'int',0,N'20260910080219_AddGenesysIntegration'),
    (N'TicketInteractionMessages',N'TicketInteractionId',N'bigint',0,N'20260910080219_AddGenesysIntegration'),
    (N'TicketInteractionMessages',N'TicketInteractionMessageId',N'bigint',0,N'20260910080219_AddGenesysIntegration'),
    (N'TicketInteractions',N'AwaitingCustomerReplyReportedByEmployeeId',N'uniqueidentifier',1,N'20261007093644_AddChatbotInactivityAndCrmDocumentCopies'),
    (N'TicketInteractions',N'AwaitingCustomerReplySinceUtc',N'datetime2',1,N'20261007093644_AddChatbotInactivityAndCrmDocumentCopies'),
    (N'TicketInteractions',N'CalledNumber',N'nvarchar(32)',1,N'20260904082241_ReplaceInteractionContextWithTicketInteractions'),
    (N'TicketInteractions',N'ChannelId',N'tinyint',0,N'20260904082241_ReplaceInteractionContextWithTicketInteractions'),
    (N'TicketInteractions',N'CreatedAtUtc',N'datetime2',0,N'20260904082241_ReplaceInteractionContextWithTicketInteractions'),
    (N'TicketInteractions',N'CustomerEmail',N'nvarchar(256)',1,N'20260910080219_AddGenesysIntegration'),
    (N'TicketInteractions',N'CustomerName',N'nvarchar(200)',1,N'20260910080219_AddGenesysIntegration'),
    (N'TicketInteractions',N'CustomerPhone',N'nvarchar(32)',0,N'20260904082241_ReplaceInteractionContextWithTicketInteractions'),
    (N'TicketInteractions',N'Direction',N'nvarchar(32)',1,N'20260904082241_ReplaceInteractionContextWithTicketInteractions'),
    (N'TicketInteractions',N'EndReason',N'nvarchar(100)',1,N'20260910080219_AddGenesysIntegration'),
    (N'TicketInteractions',N'EndReceivedAtUtc',N'datetime2',1,N'20261120074059_AddInteractionEndReceivedAt'),
    (N'TicketInteractions',N'EndedAtUtc',N'datetime2',1,N'20260910080219_AddGenesysIntegration'),
    (N'TicketInteractions',N'GenesysAgentId',N'nvarchar(64)',1,N'20260904082241_ReplaceInteractionContextWithTicketInteractions'),
    (N'TicketInteractions',N'GenesysAgentName',N'nvarchar(200)',1,N'20260904082241_ReplaceInteractionContextWithTicketInteractions'),
    (N'TicketInteractions',N'GenesysAgentUserId',N'nvarchar(64)',1,N'20260911105154_AddGenesysAgentMappingAndInteractionOwnership'),
    (N'TicketInteractions',N'GenesysConversationId',N'nvarchar(64)',1,N'20260904082241_ReplaceInteractionContextWithTicketInteractions'),
    (N'TicketInteractions',N'GenesysQueueId',N'nvarchar(64)',1,N'20260904082241_ReplaceInteractionContextWithTicketInteractions'),
    (N'TicketInteractions',N'GenesysQueueName',N'nvarchar(200)',1,N'20260904082241_ReplaceInteractionContextWithTicketInteractions'),
    (N'TicketInteractions',N'GenesysServiceRequestId',N'nvarchar(64)',1,N'20261022090000_AddGenesysServiceRequestToTicketInteractions'),
    (N'TicketInteractions',N'HandledByUserId',N'uniqueidentifier',1,N'20260911105154_AddGenesysAgentMappingAndInteractionOwnership'),
    (N'TicketInteractions',N'InactivityClosedAtUtc',N'datetime2',1,N'20261007093644_AddChatbotInactivityAndCrmDocumentCopies'),
    (N'TicketInteractions',N'InteractionStartedAtUtc',N'datetime2',1,N'20260904082241_ReplaceInteractionContextWithTicketInteractions'),
    (N'TicketInteractions',N'IsOriginatingInteraction',N'bit',0,N'20260904082241_ReplaceInteractionContextWithTicketInteractions'),
    (N'TicketInteractions',N'LastCustomerInteractionAtUtc',N'datetime2',1,N'20261020105026_AddLastCustomerInteractionToTicketInteractions'),
    (N'TicketInteractions',N'LastCustomerInteractionReportedByEmployeeId',N'uniqueidentifier',1,N'20261020105026_AddLastCustomerInteractionToTicketInteractions'),
    (N'TicketInteractions',N'RequiresHumanFollowUp',N'bit',0,N'20261022090000_AddGenesysServiceRequestToTicketInteractions'),
    (N'TicketInteractions',N'Source',N'tinyint',0,N'20260904082241_ReplaceInteractionContextWithTicketInteractions'),
    (N'TicketInteractions',N'TicketId',N'bigint',0,N'20260904082241_ReplaceInteractionContextWithTicketInteractions'),
    (N'TicketInteractions',N'TicketInteractionId',N'bigint',0,N'20260904082241_ReplaceInteractionContextWithTicketInteractions'),
    (N'TicketNotes',N'AuthorEmployeeId',N'uniqueidentifier',0,N'20260821090000_AddTicketOperations'),
    (N'TicketNotes',N'CreatedAtUtc',N'datetime2',0,N'20260821090000_AddTicketOperations'),
    (N'TicketNotes',N'NoteText',N'nvarchar(2000)',0,N'20260821090000_AddTicketOperations'),
    (N'TicketNotes',N'TicketId',N'bigint',0,N'20260821090000_AddTicketOperations'),
    (N'TicketNotes',N'TicketNoteId',N'bigint',0,N'20260821090000_AddTicketOperations'),
    (N'TicketPendingRecords',N'CorrelationId',N'uniqueidentifier',0,N'20260904075902_AddWorkflowAutomation'),
    (N'TicketPendingRecords',N'Kind',N'tinyint',0,N'20260904075902_AddWorkflowAutomation'),
    (N'TicketPendingRecords',N'PreviousStatus',N'tinyint',0,N'20260904075902_AddWorkflowAutomation'),
    (N'TicketPendingRecords',N'Reason',N'nvarchar(500)',0,N'20260904075902_AddWorkflowAutomation'),
    (N'TicketPendingRecords',N'ResumedAtUtc',N'datetime2',1,N'20260904075902_AddWorkflowAutomation'),
    (N'TicketPendingRecords',N'ResumedByEmployeeId',N'uniqueidentifier',1,N'20260904075902_AddWorkflowAutomation'),
    (N'TicketPendingRecords',N'StartedAtUtc',N'datetime2',0,N'20260904075902_AddWorkflowAutomation'),
    (N'TicketPendingRecords',N'StartedByEmployeeId',N'uniqueidentifier',0,N'20260904075902_AddWorkflowAutomation'),
    (N'TicketPendingRecords',N'TicketId',N'bigint',0,N'20260904075902_AddWorkflowAutomation'),
    (N'TicketPendingRecords',N'TicketPendingRecordId',N'bigint',0,N'20260904075902_AddWorkflowAutomation'),
    (N'TicketRequesterSnapshots',N'CapturedAtUtc',N'datetime2',0,N'20260820140000_AddTicketing'),
    (N'TicketRequesterSnapshots',N'SnapshotContactChannel',N'nvarchar(200)',1,N'20260820140000_AddTicketing'),
    (N'TicketRequesterSnapshots',N'SnapshotContactDisplayName',N'nvarchar(200)',1,N'20260820140000_AddTicketing'),
    (N'TicketRequesterSnapshots',N'SnapshotPropertyName',N'nvarchar(200)',1,N'20260820140000_AddTicketing'),
    (N'TicketRequesterSnapshots',N'SnapshotTowerName',N'nvarchar(200)',1,N'20260820140000_AddTicketing'),
    (N'TicketRequesterSnapshots',N'SnapshotUnitNumber',N'nvarchar(50)',0,N'20260820140000_AddTicketing'),
    (N'TicketRequesterSnapshots',N'SnapshotUnitType',N'nvarchar(50)',1,N'20260820140000_AddTicketing'),
    (N'TicketRequesterSnapshots',N'TicketId',N'bigint',0,N'20260820140000_AddTicketing'),
    (N'TicketResolutions',N'ClosedForCustomerInactivity',N'bit',0,N'20261007103153_AddResolutionClosedForCustomerInactivity'),
    (N'TicketResolutions',N'DuplicateOfTicketId',N'bigint',1,N'20260821090000_AddTicketOperations'),
    (N'TicketResolutions',N'IsCurrent',N'bit',0,N'20260821090000_AddTicketOperations'),
    (N'TicketResolutions',N'ReasonCode',N'tinyint',1,N'20260821090000_AddTicketOperations'),
    (N'TicketResolutions',N'ResolutionNote',N'nvarchar(4000)',0,N'20260821090000_AddTicketOperations'),
    (N'TicketResolutions',N'ResolutionOutcome',N'tinyint',0,N'20260821090000_AddTicketOperations'),
    (N'TicketResolutions',N'ResolvedAtUtc',N'datetime2',0,N'20260821090000_AddTicketOperations'),
    (N'TicketResolutions',N'ResolvingEmployeeId',N'uniqueidentifier',0,N'20260821090000_AddTicketOperations'),
    (N'TicketResolutions',N'TicketId',N'bigint',0,N'20260821090000_AddTicketOperations'),
    (N'TicketResolutions',N'TicketResolutionId',N'bigint',0,N'20260821090000_AddTicketOperations'),
    (N'TicketReviewInvitations',N'CreatedAtUtc',N'datetime2',0,N'20261022081031_AddCustomerSatisfactionReviews'),
    (N'TicketReviewInvitations',N'ExpiresAtUtc',N'datetime2',0,N'20261022081031_AddCustomerSatisfactionReviews'),
    (N'TicketReviewInvitations',N'IsTest',N'bit',0,N'20261022081031_AddCustomerSatisfactionReviews'),
    (N'TicketReviewInvitations',N'ReopenCycle',N'int',0,N'20261022081031_AddCustomerSatisfactionReviews'),
    (N'TicketReviewInvitations',N'TicketId',N'bigint',0,N'20261022081031_AddCustomerSatisfactionReviews'),
    (N'TicketReviewInvitations',N'TicketReviewInvitationId',N'bigint',0,N'20261022081031_AddCustomerSatisfactionReviews'),
    (N'TicketReviewInvitations',N'TokenHash',N'varbinary(32)',0,N'20261022081031_AddCustomerSatisfactionReviews'),
    (N'TicketReviews',N'Comment',N'nvarchar(1000)',1,N'20261022081031_AddCustomerSatisfactionReviews'),
    (N'TicketReviews',N'IsTest',N'bit',0,N'20261022081031_AddCustomerSatisfactionReviews'),
    (N'TicketReviews',N'Rating',N'tinyint',0,N'20261022081031_AddCustomerSatisfactionReviews'),
    (N'TicketReviews',N'ReopenCycle',N'int',0,N'20261022081031_AddCustomerSatisfactionReviews'),
    (N'TicketReviews',N'SubmittedAtUtc',N'datetime2',0,N'20261022081031_AddCustomerSatisfactionReviews'),
    (N'TicketReviews',N'TicketId',N'bigint',0,N'20261022081031_AddCustomerSatisfactionReviews'),
    (N'TicketReviews',N'TicketReviewId',N'bigint',0,N'20261022081031_AddCustomerSatisfactionReviews'),
    (N'TicketReviews',N'TicketReviewInvitationId',N'bigint',0,N'20261022081031_AddCustomerSatisfactionReviews'),
    (N'TicketSlaInstances',N'AppliedFirstResponseTargetMinutes',N'int',1,N'20261008184722_AddSlaPauseAndPriorityDowngrade'),
    (N'TicketSlaInstances',N'AppliedResolutionTargetMinutes',N'int',1,N'20261008184722_AddSlaPauseAndPriorityDowngrade'),
    (N'TicketSlaInstances',N'ApprovedByEmployeeId',N'uniqueidentifier',1,N'20260821130000_AddSlaAndEscalation'),
    (N'TicketSlaInstances',N'ChangeReason',N'tinyint',0,N'20260821130000_AddSlaAndEscalation'),
    (N'TicketSlaInstances',N'FirstResponseBreached',N'bit',0,N'20260821130000_AddSlaAndEscalation'),
    (N'TicketSlaInstances',N'FirstResponseDueAtUtc',N'datetime2',0,N'20260821130000_AddSlaAndEscalation'),
    (N'TicketSlaInstances',N'PausesOnPendingCustomerOverride',N'bit',1,N'20261008184722_AddSlaPauseAndPriorityDowngrade'),
    (N'TicketSlaInstances',N'PeriodEndAtUtc',N'datetime2',1,N'20260821130000_AddSlaAndEscalation'),
    (N'TicketSlaInstances',N'PeriodStartAtUtc',N'datetime2',0,N'20260821130000_AddSlaAndEscalation'),
    (N'TicketSlaInstances',N'PriorityId',N'tinyint',0,N'20260821130000_AddSlaAndEscalation'),
    (N'TicketSlaInstances',N'RequestTypeSlaNote',N'nvarchar(500)',1,N'20261008184722_AddSlaPauseAndPriorityDowngrade'),
    (N'TicketSlaInstances',N'RequestTypeSlaPolicyId',N'int',1,N'20261008184722_AddSlaPauseAndPriorityDowngrade'),
    (N'TicketSlaInstances',N'ResolutionBreached',N'bit',0,N'20260821130000_AddSlaAndEscalation'),
    (N'TicketSlaInstances',N'ResolutionClockBasis',N'tinyint',1,N'20261008184722_AddSlaPauseAndPriorityDowngrade'),
    (N'TicketSlaInstances',N'ResolutionDueAtUtc',N'datetime2',0,N'20260821130000_AddSlaAndEscalation'),
    (N'TicketSlaInstances',N'TicketId',N'bigint',0,N'20260821130000_AddSlaAndEscalation'),
    (N'TicketSlaInstances',N'TicketSlaInstanceId',N'bigint',0,N'20260821130000_AddSlaAndEscalation'),
    (N'TicketSlaPausePeriods',N'EndedByResolution',N'bit',0,N'20261008184722_AddSlaPauseAndPriorityDowngrade'),
    (N'TicketSlaPausePeriods',N'Reason',N'tinyint',0,N'20261008184722_AddSlaPauseAndPriorityDowngrade'),
    (N'TicketSlaPausePeriods',N'ResolutionDueAfterAtUtc',N'datetime2',1,N'20261008184722_AddSlaPauseAndPriorityDowngrade'),
    (N'TicketSlaPausePeriods',N'ResolutionDueBeforeAtUtc',N'datetime2',0,N'20261008184722_AddSlaPauseAndPriorityDowngrade'),
    (N'TicketSlaPausePeriods',N'ResumedAtUtc',N'datetime2',1,N'20261008184722_AddSlaPauseAndPriorityDowngrade'),
    (N'TicketSlaPausePeriods',N'StartedAtUtc',N'datetime2',0,N'20261008184722_AddSlaPauseAndPriorityDowngrade'),
    (N'TicketSlaPausePeriods',N'TicketId',N'bigint',0,N'20261008184722_AddSlaPauseAndPriorityDowngrade'),
    (N'TicketSlaPausePeriods',N'TicketSlaInstanceId',N'bigint',0,N'20261008184722_AddSlaPauseAndPriorityDowngrade'),
    (N'TicketSlaPausePeriods',N'TicketSlaPausePeriodId',N'bigint',0,N'20261008184722_AddSlaPauseAndPriorityDowngrade'),
    (N'TicketStatusHistory',N'ActorEmployeeId',N'uniqueidentifier',1,N'20260820140000_AddTicketing'),
    (N'TicketStatusHistory',N'ActorIsSystem',N'bit',0,N'20260820140000_AddTicketing'),
    (N'TicketStatusHistory',N'CorrelationId',N'uniqueidentifier',0,N'20260820140000_AddTicketing'),
    (N'TicketStatusHistory',N'Dimension',N'tinyint',0,N'20260820140000_AddTicketing'),
    (N'TicketStatusHistory',N'NewValue',N'tinyint',0,N'20260820140000_AddTicketing'),
    (N'TicketStatusHistory',N'Note',N'nvarchar(1000)',1,N'20260820140000_AddTicketing'),
    (N'TicketStatusHistory',N'OccurredAtUtc',N'datetime2',0,N'20260820140000_AddTicketing'),
    (N'TicketStatusHistory',N'OldValue',N'tinyint',1,N'20260820140000_AddTicketing'),
    (N'TicketStatusHistory',N'TicketId',N'bigint',0,N'20260820140000_AddTicketing'),
    (N'TicketStatusHistory',N'TicketStatusHistoryId',N'bigint',0,N'20260820140000_AddTicketing'),
    (N'TicketWorkflowEvents',N'ActorEmployeeId',N'uniqueidentifier',1,N'20260904091143_AddApprovalWorkflow'),
    (N'TicketWorkflowEvents',N'CorrelationId',N'uniqueidentifier',0,N'20260904091143_AddApprovalWorkflow'),
    (N'TicketWorkflowEvents',N'EventType',N'tinyint',0,N'20260904091143_AddApprovalWorkflow'),
    (N'TicketWorkflowEvents',N'FromDepartmentId',N'int',1,N'20261030062642_AddTicketTransferTracking'),
    (N'TicketWorkflowEvents',N'Note',N'nvarchar(500)',1,N'20260904091143_AddApprovalWorkflow'),
    (N'TicketWorkflowEvents',N'OccurredAtUtc',N'datetime2',0,N'20260904091143_AddApprovalWorkflow'),
    (N'TicketWorkflowEvents',N'TicketApprovalId',N'bigint',1,N'20260904091143_AddApprovalWorkflow'),
    (N'TicketWorkflowEvents',N'TicketId',N'bigint',0,N'20260904091143_AddApprovalWorkflow'),
    (N'TicketWorkflowEvents',N'TicketWorkflowEventId',N'bigint',0,N'20260904091143_AddApprovalWorkflow'),
    (N'TicketWorkflowEvents',N'ToDepartmentId',N'int',1,N'20261030062642_AddTicketTransferTracking'),
    (N'TicketWorkflowEvents',N'ToEmployeeId',N'uniqueidentifier',1,N'20261030062642_AddTicketTransferTracking'),
    (N'TicketWorkflowEvents',N'TransferReasonCode',N'tinyint',1,N'20261030062642_AddTicketTransferTracking'),
    (N'Tickets',N'AcknowledgementSentAtUtc',N'datetime2',1,N'20260820140000_AddTicketing'),
    (N'Tickets',N'AiAgentOwnerEmployeeId',N'uniqueidentifier',1,N'20261118124858_AddTicketAiAgentOwner'),
    (N'Tickets',N'CategoryId',N'int',1,N'20260820140000_AddTicketing'),
    (N'Tickets',N'ContactReferenceId',N'int',1,N'20260820140000_AddTicketing'),
    (N'Tickets',N'CreatedAtUtc',N'datetime2',0,N'20260820140000_AddTicketing'),
    (N'Tickets',N'CreatedByEmployeeId',N'uniqueidentifier',1,N'20261030073857_AddTicketCreatedBy'),
    (N'Tickets',N'CrmBuyerCustomerId',N'int',1,N'20260827090146_AddCrmBuyerLookupToTickets'),
    (N'Tickets',N'CrmBuyerCustomerName',N'nvarchar(200)',1,N'20260827090146_AddCrmBuyerLookupToTickets'),
    (N'Tickets',N'CrmBuyerLeadId',N'int',1,N'20260827090146_AddCrmBuyerLookupToTickets'),
    (N'Tickets',N'CrmBuyerProjectId',N'int',1,N'20260827090146_AddCrmBuyerLookupToTickets'),
    (N'Tickets',N'CrmBuyerProjectName',N'nvarchar(200)',1,N'20260827090146_AddCrmBuyerLookupToTickets'),
    (N'Tickets',N'CrmBuyerUnitId',N'int',1,N'20260827090146_AddCrmBuyerLookupToTickets'),
    (N'Tickets',N'CrmBuyerUnitNumber',N'nvarchar(50)',1,N'20260827090146_AddCrmBuyerLookupToTickets'),
    (N'Tickets',N'CurrentDepartmentId',N'int',0,N'20260820140000_AddTicketing'),
    (N'Tickets',N'CurrentOwnerEmployeeId',N'uniqueidentifier',1,N'20260820140000_AddTicketing'),
    (N'Tickets',N'CustomerVerificationSource',N'nvarchar(32)',1,N'20260901103309_AddExternalCustomerVerificationToTickets'),
    (N'Tickets',N'DuplicateOfTicketId',N'bigint',1,N'20260820140000_AddTicketing'),
    (N'Tickets',N'EscalationLevel',N'tinyint',0,N'20260820140000_AddTicketing'),
    (N'Tickets',N'ExternalCustomerEmail',N'nvarchar(256)',1,N'20260912110119_AddExternalCustomerSnapshotToTickets'),
    (N'Tickets',N'ExternalCustomerId',N'nvarchar(64)',1,N'20260901103309_AddExternalCustomerVerificationToTickets'),
    (N'Tickets',N'ExternalCustomerName',N'nvarchar(200)',1,N'20260912110119_AddExternalCustomerSnapshotToTickets'),
    (N'Tickets',N'ExternalReference',N'nvarchar(100)',1,N'20261117101035_AddQueueingIntegration'),
    (N'Tickets',N'ExternalTicketId',N'nvarchar(100)',1,N'20261117101035_AddQueueingIntegration'),
    (N'Tickets',N'ExternalUnitId',N'nvarchar(64)',1,N'20260901103309_AddExternalCustomerVerificationToTickets'),
    (N'Tickets',N'FirstHumanResponseAtUtc',N'datetime2',1,N'20260820140000_AddTicketing'),
    (N'Tickets',N'IntegrationSource',N'nvarchar(50)',1,N'20261117101035_AddQueueingIntegration'),
    (N'Tickets',N'ManualProjectName',N'nvarchar(200)',1,N'20260827090146_AddCrmBuyerLookupToTickets'),
    (N'Tickets',N'ManualUnitNumber',N'nvarchar(50)',1,N'20260827090146_AddCrmBuyerLookupToTickets'),
    (N'Tickets',N'OriginalAgentWorkflowEnabled',N'bit',0,N'20261111132429_AddOriginalAgentWorkflowCutoff'),
    (N'Tickets',N'OriginalHumanAgentId',N'uniqueidentifier',1,N'20261111073632_AddTicketOriginalHumanAgent'),
    (N'Tickets',N'OriginatingDepartmentId',N'int',0,N'20260820140000_AddTicketing'),
    (N'Tickets',N'PriorityId',N'tinyint',1,N'20260820140000_AddTicketing'),
    (N'Tickets',N'ReopenCount',N'int',0,N'20260820140000_AddTicketing'),
    (N'Tickets',N'RequestSummary',N'nvarchar(2000)',0,N'20260820140000_AddTicketing'),
    (N'Tickets',N'RequestTypeId',N'int',1,N'20260904075902_AddWorkflowAutomation'),
    (N'Tickets',N'ResolutionOutcome',N'tinyint',1,N'20260820140000_AddTicketing'),
    (N'Tickets',N'RowVersion',N'rowversion',0,N'20260821090000_AddTicketOperations'),
    (N'Tickets',N'SlaState',N'tinyint',0,N'20260820140000_AddTicketing'),
    (N'Tickets',N'TicketId',N'bigint',0,N'20260820140000_AddTicketing'),
    (N'Tickets',N'TicketNumber',N'nvarchar(40)',0,N'20260820140000_AddTicketing'),
    (N'Tickets',N'TicketStatus',N'tinyint',0,N'20260820140000_AddTicketing'),
    (N'Tickets',N'UnitReferenceId',N'int',1,N'20260820140000_AddTicketing'),
    (N'Tickets',N'VerificationStatus',N'tinyint',0,N'20260820140000_AddTicketing'),
    (N'Tickets',N'WorkflowTemplateId',N'int',1,N'20260908054944_AddWorkflowVersioning'),
    (N'UnitReferences',N'CrmUnitId',N'nvarchar(64)',0,N'20260820072044_AddCustomerVerification'),
    (N'UnitReferences',N'LastSyncedAtUtc',N'datetime2',0,N'20260820072044_AddCustomerVerification'),
    (N'UnitReferences',N'PropertyName',N'nvarchar(200)',1,N'20260820072044_AddCustomerVerification'),
    (N'UnitReferences',N'TowerName',N'nvarchar(200)',1,N'20260820072044_AddCustomerVerification'),
    (N'UnitReferences',N'UnitNumber',N'nvarchar(50)',0,N'20260820072044_AddCustomerVerification'),
    (N'UnitReferences',N'UnitReferenceId',N'int',0,N'20260820072044_AddCustomerVerification'),
    (N'UnitReferences',N'UnitType',N'nvarchar(50)',1,N'20260820072044_AddCustomerVerification'),
    (N'UserDepartmentAssignments',N'AssignedAtUtc',N'datetime2',0,N'20260819072347_InitialIdentityAndAccess'),
    (N'UserDepartmentAssignments',N'AssignedByEmployeeId',N'uniqueidentifier',1,N'20260819072347_InitialIdentityAndAccess');
INSERT #ExpCols (TableName,ColName,ColType,IsNullable,Owner) VALUES
    (N'UserDepartmentAssignments',N'DepartmentId',N'int',0,N'20260819072347_InitialIdentityAndAccess'),
    (N'UserDepartmentAssignments',N'EmployeeId',N'uniqueidentifier',0,N'20260819072347_InitialIdentityAndAccess'),
    (N'UserDepartmentAssignments',N'IsPrimary',N'bit',0,N'20260819072347_InitialIdentityAndAccess'),
    (N'UserDepartmentAssignments',N'UserDepartmentAssignmentId',N'int',0,N'20260819072347_InitialIdentityAndAccess'),
    (N'UserPermissionOverrides',N'CreatedAtUtc',N'datetime2',0,N'20261102065459_AddUserPermissionOverrides'),
    (N'UserPermissionOverrides',N'CreatedByEmployeeId',N'uniqueidentifier',1,N'20261102065459_AddUserPermissionOverrides'),
    (N'UserPermissionOverrides',N'Effect',N'tinyint',0,N'20261102065459_AddUserPermissionOverrides'),
    (N'UserPermissionOverrides',N'EmployeeId',N'uniqueidentifier',0,N'20261102065459_AddUserPermissionOverrides'),
    (N'UserPermissionOverrides',N'PermissionKey',N'nvarchar(100)',0,N'20261102065459_AddUserPermissionOverrides'),
    (N'UserPermissionOverrides',N'UpdatedAtUtc',N'datetime2',0,N'20261102065459_AddUserPermissionOverrides'),
    (N'UserPermissionOverrides',N'UpdatedByEmployeeId',N'uniqueidentifier',1,N'20261102065459_AddUserPermissionOverrides'),
    (N'UserPermissionOverrides',N'UserPermissionOverrideId',N'bigint',0,N'20261102065459_AddUserPermissionOverrides'),
    (N'UserTransferDepartments',N'DepartmentId',N'int',0,N'20261110070152_AddUserTransferDepartments'),
    (N'UserTransferDepartments',N'EmployeeId',N'uniqueidentifier',0,N'20261110070152_AddUserTransferDepartments'),
    (N'UserTransferDepartments',N'GrantedAtUtc',N'datetime2',0,N'20261110070152_AddUserTransferDepartments'),
    (N'UserTransferDepartments',N'GrantedByEmployeeId',N'uniqueidentifier',1,N'20261110070152_AddUserTransferDepartments'),
    (N'UserTransferDepartments',N'UserTransferDepartmentId',N'int',0,N'20261110070152_AddUserTransferDepartments'),
    (N'VerificationSessions',N'AgentEmployeeId',N'uniqueidentifier',0,N'20260820072044_AddCustomerVerification'),
    (N'VerificationSessions',N'Confirmed',N'bit',0,N'20260820072044_AddCustomerVerification'),
    (N'VerificationSessions',N'ConfirmedAtUtc',N'datetime2',1,N'20260820072044_AddCustomerVerification'),
    (N'VerificationSessions',N'ConsumedAtUtc',N'datetime2',1,N'20260820072044_AddCustomerVerification'),
    (N'VerificationSessions',N'ConsumedByTicketId',N'bigint',1,N'20260820072044_AddCustomerVerification'),
    (N'VerificationSessions',N'ContactReferenceId',N'int',0,N'20260820072044_AddCustomerVerification'),
    (N'VerificationSessions',N'CreatedAtUtc',N'datetime2',0,N'20260820072044_AddCustomerVerification'),
    (N'VerificationSessions',N'CrmBuyerCustomerId',N'int',1,N'20261007180009_AddCustomerOtpVerification'),
    (N'VerificationSessions',N'CrmBuyerLeadId',N'int',1,N'20261007180009_AddCustomerOtpVerification'),
    (N'VerificationSessions',N'ExpiresAtUtc',N'datetime2',0,N'20260820072044_AddCustomerVerification'),
    (N'VerificationSessions',N'IdempotencyKey',N'nvarchar(300)',1,N'20260820072044_AddCustomerVerification'),
    (N'VerificationSessions',N'ProofChallengeId',N'uniqueidentifier',1,N'20261007180009_AddCustomerOtpVerification'),
    (N'VerificationSessions',N'SnapshotContactChannel',N'nvarchar(200)',1,N'20260820072044_AddCustomerVerification'),
    (N'VerificationSessions',N'SnapshotContactDisplayName',N'nvarchar(200)',1,N'20260820072044_AddCustomerVerification'),
    (N'VerificationSessions',N'SnapshotPropertyName',N'nvarchar(200)',1,N'20260820072044_AddCustomerVerification'),
    (N'VerificationSessions',N'SnapshotTowerName',N'nvarchar(200)',1,N'20260820072044_AddCustomerVerification'),
    (N'VerificationSessions',N'SnapshotUnitNumber',N'nvarchar(50)',1,N'20260820072044_AddCustomerVerification'),
    (N'VerificationSessions',N'SnapshotUnitType',N'nvarchar(50)',1,N'20260820072044_AddCustomerVerification'),
    (N'VerificationSessions',N'Status',N'int',0,N'20260820072044_AddCustomerVerification'),
    (N'VerificationSessions',N'UnitReferenceId',N'int',0,N'20260820072044_AddCustomerVerification'),
    (N'VerificationSessions',N'VerificationMethod',N'int',1,N'20260820072044_AddCustomerVerification'),
    (N'VerificationSessions',N'VerificationSessionId',N'uniqueidentifier',0,N'20260820072044_AddCustomerVerification'),
    (N'WorkflowStepTransitions',N'Outcome',N'tinyint',0,N'20260908054944_AddWorkflowVersioning'),
    (N'WorkflowStepTransitions',N'TargetWorkflowTemplateStepId',N'int',0,N'20260908054944_AddWorkflowVersioning'),
    (N'WorkflowStepTransitions',N'WorkflowStepTransitionId',N'int',0,N'20260908054944_AddWorkflowVersioning'),
    (N'WorkflowStepTransitions',N'WorkflowTemplateStepId',N'int',0,N'20260908054944_AddWorkflowVersioning'),
    (N'WorkflowTemplateSteps',N'ApprovalType',N'tinyint',1,N'20260908054944_AddWorkflowVersioning'),
    (N'WorkflowTemplateSteps',N'IsOptional',N'bit',0,N'20260903164723_AddWorkflowConfiguration'),
    (N'WorkflowTemplateSteps',N'Kind',N'tinyint',0,N'20260903164723_AddWorkflowConfiguration'),
    (N'WorkflowTemplateSteps',N'Name',N'nvarchar(100)',0,N'20260903164723_AddWorkflowConfiguration'),
    (N'WorkflowTemplateSteps',N'Sequence',N'tinyint',0,N'20260903164723_AddWorkflowConfiguration'),
    (N'WorkflowTemplateSteps',N'WorkflowTemplateId',N'int',0,N'20260903164723_AddWorkflowConfiguration'),
    (N'WorkflowTemplateSteps',N'WorkflowTemplateStepId',N'int',0,N'20260903164723_AddWorkflowConfiguration'),
    (N'WorkflowTemplates',N'AllowsPendingCustomer',N'bit',0,N'20260903164723_AddWorkflowConfiguration'),
    (N'WorkflowTemplates',N'AllowsPendingInternal',N'bit',0,N'20260903164723_AddWorkflowConfiguration'),
    (N'WorkflowTemplates',N'Code',N'nvarchar(32)',0,N'20260903164723_AddWorkflowConfiguration'),
    (N'WorkflowTemplates',N'CreatedAtUtc',N'datetime2',0,N'20260908054944_AddWorkflowVersioning'),
    (N'WorkflowTemplates',N'CreatedByEmployeeId',N'uniqueidentifier',1,N'20260908054944_AddWorkflowVersioning'),
    (N'WorkflowTemplates',N'Description',N'nvarchar(500)',1,N'20260903164723_AddWorkflowConfiguration'),
    (N'WorkflowTemplates',N'IsActive',N'bit',0,N'20260903164723_AddWorkflowConfiguration'),
    (N'WorkflowTemplates',N'Name',N'nvarchar(100)',0,N'20260903164723_AddWorkflowConfiguration'),
    (N'WorkflowTemplates',N'PublishedAtUtc',N'datetime2',1,N'20260908054944_AddWorkflowVersioning'),
    (N'WorkflowTemplates',N'PublishedByEmployeeId',N'uniqueidentifier',1,N'20260908054944_AddWorkflowVersioning'),
    (N'WorkflowTemplates',N'RequiresApproval',N'bit',0,N'20260903164723_AddWorkflowConfiguration'),
    (N'WorkflowTemplates',N'Status',N'tinyint',0,N'20260908054944_AddWorkflowVersioning'),
    (N'WorkflowTemplates',N'VersionNumber',N'int',0,N'20260908054944_AddWorkflowVersioning'),
    (N'WorkflowTemplates',N'WorkflowId',N'int',0,N'20260908054944_AddWorkflowVersioning'),
    (N'WorkflowTemplates',N'WorkflowTemplateId',N'int',0,N'20260903164723_AddWorkflowConfiguration'),
    (N'Workflows',N'Code',N'nvarchar(24)',0,N'20260908054944_AddWorkflowVersioning'),
    (N'Workflows',N'CreatedAtUtc',N'datetime2',0,N'20260908054944_AddWorkflowVersioning'),
    (N'Workflows',N'Description',N'nvarchar(500)',1,N'20260908054944_AddWorkflowVersioning'),
    (N'Workflows',N'IsActive',N'bit',0,N'20260908054944_AddWorkflowVersioning'),
    (N'Workflows',N'Name',N'nvarchar(100)',0,N'20260908054944_AddWorkflowVersioning'),
    (N'Workflows',N'WorkflowId',N'int',0,N'20260908054944_AddWorkflowVersioning');
INSERT #ExpIdx (TableName,IndexName,KeyCols,IsUnique,HasFilter,Owner) VALUES
    (N'AspNetRoleClaims',N'IX_AspNetRoleClaims_RoleId',N'RoleId',0,0,N'20260819072347_InitialIdentityAndAccess'),
    (N'AspNetRoles',N'RoleNameIndex',N'NormalizedName',1,1,N'20260819072347_InitialIdentityAndAccess'),
    (N'AspNetUserClaims',N'IX_AspNetUserClaims_UserId',N'UserId',0,0,N'20260819072347_InitialIdentityAndAccess'),
    (N'AspNetUserLogins',N'IX_AspNetUserLogins_UserId',N'UserId',0,0,N'20260819072347_InitialIdentityAndAccess'),
    (N'AspNetUserRoles',N'IX_AspNetUserRoles_RoleId',N'RoleId',0,0,N'20260819072347_InitialIdentityAndAccess'),
    (N'AspNetUsers',N'EmailIndex',N'NormalizedEmail',0,0,N'20260819072347_InitialIdentityAndAccess'),
    (N'AspNetUsers',N'UserNameIndex',N'NormalizedUserName',1,1,N'20260819072347_InitialIdentityAndAccess'),
    (N'AspNetUsers',N'UX_AspNetUsers_GenesysUserId',N'GenesysUserId',1,1,N'20260911105154_AddGenesysAgentMappingAndInteractionOwnership'),
    (N'BusinessCalendarWorkingDays',N'IX_BusinessCalendarWorkingDays_BusinessCalendarId_DayOfWeek',N'BusinessCalendarId,DayOfWeek',1,0,N'20260821130000_AddSlaAndEscalation'),
    (N'Categories',N'IX_Categories_DepartmentId',N'DepartmentId',0,0,N'20260820140000_AddTicketing'),
    (N'Categories',N'IX_Categories_ParentCategoryId',N'ParentCategoryId',0,0,N'20260820140000_AddTicketing'),
    (N'Channels',N'IX_Channels_Code',N'Code',1,0,N'20260909084839_AddChannels'),
    (N'CollectionsDispatchItems',N'IX_CollectionsDispatchItems_CollectionsDispatchId',N'CollectionsDispatchId',0,0,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsDispatchItems',N'IX_CollectionsDispatchItems_Batch',N'CollectionsGenesysBatchId',0,0,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsDispatchItems',N'IX_CollectionsDispatchItems_RecordKey',N'RecordKey',0,0,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsDispatchItems',N'UX_CollectionsDispatchItems_LiveRecord',N'RecordKey',1,1,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsDispatches',N'UX_CollectionsDispatches_IdempotencyKey',N'IdempotencyKey',1,0,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsDispatches',N'UX_CollectionsDispatches_PublicId',N'PublicId',1,0,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsExceptionInclusions',N'IX_CollectionsExceptionInclusions_Campaign',N'CampaignKey',0,0,N'20261109120816_AddCollectionsExceptionInclusions'),
    (N'CollectionsExceptionInclusions',N'UX_CollectionsExceptionInclusions_Active',N'CampaignKey,CompanyId,TenantId,UnitCode',1,1,N'20261109120816_AddCollectionsExceptionInclusions'),
    (N'CollectionsGenesysBatches',N'UX_CollectionsGenesysBatches_Dispatch_Sequence',N'CollectionsDispatchId,Sequence',1,0,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsGenesysMessageResults',N'IX_CollectionsGenesysMessageResults_Dispatch_Item',N'CollectionsDispatchId,CollectionsDispatchItemId',0,0,N'20261109074858_AddCollectionsGenesysDeliveryStats'),
    (N'CollectionsGenesysMessageResults',N'UX_CollectionsGenesysMessageResults_Message',N'GenesysConversationId,GenesysMessageId',1,0,N'20261109074858_AddCollectionsGenesysDeliveryStats'),
    (N'CollectionsReminderChannels',N'IX_CollectionsReminderChannels_CollectionsReminderId',N'CollectionsReminderId',0,0,N'20261005072511_AddCollectionsReminders'),
    (N'CollectionsReminderChannels',N'UX_CollectionsReminderChannels_DeduplicationKey',N'DeduplicationKey',1,0,N'20261005072511_AddCollectionsReminders'),
    (N'CollectionsReminderEvents',N'IX_CollectionsReminderEvents_TicketId',N'TicketId',0,0,N'20261005072511_AddCollectionsReminders'),
    (N'CollectionsReminderEvents',N'UX_CollectionsReminderEvents_Reminder_EventId',N'CollectionsReminderId,ExternalEventId',1,0,N'20261005072511_AddCollectionsReminders'),
    (N'CollectionsReminders',N'UX_CollectionsReminders_IdempotencyKey',N'IdempotencyKey',1,1,N'20261005072511_AddCollectionsReminders'),
    (N'CollectionsReminders',N'IX_CollectionsReminders_Customer_Queued',N'CrmCustomerId,QueuedAtUtc',0,0,N'20261005072511_AddCollectionsReminders'),
    (N'CollectionsReviewRecords',N'IX_CollectionsReviewRecords_Run_Due',N'CollectionsReviewRunId,DueDate',0,0,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsReviewRecords',N'UX_CollectionsReviewRecords_Run_Key',N'CollectionsReviewRunId,RecordKey',1,0,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsReviewRecords',N'IX_CollectionsReviewRecords_Run_Type_Status',N'CollectionsReviewRunId,ReminderType,ValidationStatus',0,0,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsReviewRecords',N'IX_CollectionsReviewRecords_Run_Customer',N'CollectionsReviewRunId,CompanyId,TenantId,UnitCode',0,0,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsReviewRuns',N'UX_CollectionsReviewRuns_Active',N'IsActive',1,1,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsReviewRuns',N'UX_CollectionsReviewRuns_Current',N'IsCurrent',1,1,N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'CollectionsTowerAssignmentHistory',N'IX_CollectionsTowerAssignmentHistory_ChangedByEmployeeId',N'ChangedByEmployeeId',0,0,N'20261104130835_AddCollectionsTowerAssignments'),
    (N'CollectionsTowerAssignmentHistory',N'IX_CollectionsTowerAssignmentHistory_NewEmployeeId',N'NewEmployeeId',0,0,N'20261104130835_AddCollectionsTowerAssignments'),
    (N'CollectionsTowerAssignmentHistory',N'IX_CollectionsTowerAssignmentHistory_PreviousEmployeeId',N'PreviousEmployeeId',0,0,N'20261104130835_AddCollectionsTowerAssignments'),
    (N'CollectionsTowerAssignmentHistory',N'IX_CollectionsTowerAssignmentHistory_Month_Tower',N'Year,Month,TowerId',0,0,N'20261104130835_AddCollectionsTowerAssignments'),
    (N'CollectionsTowerAssignments',N'IX_CollectionsTowerAssignments_EmployeeId',N'EmployeeId',0,0,N'20261104130835_AddCollectionsTowerAssignments'),
    (N'CollectionsTowerAssignments',N'UX_CollectionsTowerAssignments_Tower_Month_Primary',N'TowerId,Year,Month',1,1,N'20261104130835_AddCollectionsTowerAssignments'),
    (N'CollectionsTowerAssignments',N'IX_CollectionsTowerAssignments_Month_Employee',N'Year,Month,EmployeeId',0,0,N'20261104130835_AddCollectionsTowerAssignments'),
    (N'ContactReferences',N'IX_ContactReferences_AuthorizedRepresentativeOfContactReferenceId',N'AuthorizedRepresentativeOfContactReferenceId',0,0,N'20260820072044_AddCustomerVerification'),
    (N'ContactReferences',N'IX_ContactReferences_CrmContactId',N'CrmContactId',1,0,N'20260820072044_AddCustomerVerification'),
    (N'ContactReferences',N'IX_ContactReferences_UnitReferenceId',N'UnitReferenceId',0,0,N'20260820072044_AddCustomerVerification'),
    (N'CrmDocumentDeliveryRequests',N'IX_CrmDocumentDeliveryRequests_AgentDocument',N'CrmCustomerId,CrmLeadId,DocumentType,CrmRecordId,Channel,Status',0,0,N'20261012124810_AddAgentDeskDocumentDelivery'),
    (N'CrmDocumentDeliveryRequests',N'IX_CrmDocumentDeliveryRequests_SessionDocument',N'VerificationSessionId,DocumentType,CrmRecordId,Channel,Status',0,0,N'20261007093644_AddChatbotInactivityAndCrmDocumentCopies'),
    (N'CrmDocumentDeliveryRequests',N'UX_CrmDocumentDeliveryRequests_CallerKey',N'CallerEmployeeId,IdempotencyKey',1,0,N'20261007093644_AddChatbotInactivityAndCrmDocumentCopies'),
    (N'CustomerContactDetails',N'IX_CustomerContactDetails_PhoneKey',N'PhoneKey',1,0,N'20261105094644_AddCustomerContactDetails'),
    (N'CustomerOtpChallenges',N'IX_CustomerOtpChallenges_ContactReferenceId',N'ContactReferenceId',0,0,N'20261007180009_AddCustomerOtpVerification'),
    (N'CustomerOtpChallenges',N'IX_CustomerOtpChallenges_UnitReferenceId',N'UnitReferenceId',0,0,N'20261007180009_AddCustomerOtpVerification'),
    (N'CustomerOtpChallenges',N'IX_CustomerOtpChallenges_CallerLead',N'CallerEmployeeId,CrmCustomerId,CrmLeadId,Status',0,0,N'20261007180009_AddCustomerOtpVerification'),
    (N'CustomerOtpChallenges',N'IX_CustomerOtpChallenges_Customer',N'CrmCustomerId,CreatedAtUtc',0,0,N'20261007180009_AddCustomerOtpVerification'),
    (N'CustomerProfileChanges',N'IX_CustomerProfileChanges_ChangeKey',N'ChangeKey',1,0,N'20261119081103_AddCustomerProfileAuditAndProperties'),
    (N'CustomerProfileChanges',N'IX_CustomerProfileChanges_CorrelationId',N'CorrelationId',0,0,N'20261119081103_AddCustomerProfileAuditAndProperties'),
    (N'CustomerProfileChanges',N'IX_CustomerProfileChanges_CustomerKey',N'CustomerKey',0,0,N'20261119081103_AddCustomerProfileAuditAndProperties'),
    (N'CustomerProfileChanges',N'IX_CustomerProfileChanges_CustomerProfileId_ModifiedAtUtc',N'CustomerProfileId,ModifiedAtUtc',0,0,N'20261119081103_AddCustomerProfileAuditAndProperties'),
    (N'CustomerProfileOverrides',N'IX_CustomerProfileOverrides_CustomerKey',N'CustomerKey',1,0,N'20261118120000_AddCustomerProfileOverrides'),
    (N'CustomerProfileOverrides',N'IX_CustomerProfileOverrides_CustomerProfileId',N'CustomerProfileId',1,0,N'20261119081103_AddCustomerProfileAuditAndProperties'),
    (N'CustomerPropertyAssociations',N'IX_CustomerPropertyAssociations_CustomerKey',N'CustomerKey',0,0,N'20261119081103_AddCustomerProfileAuditAndProperties'),
    (N'CustomerPropertyAssociations',N'IX_CustomerPropertyAssociations_CustomerProfileId_ProjectName_UnitNumber',N'CustomerProfileId,ProjectName,UnitNumber',1,0,N'20261119081103_AddCustomerProfileAuditAndProperties'),
    (N'DepartmentCustomerLookupSources',N'IX_DepartmentCustomerLookupSources_DepartmentId_Source',N'DepartmentId,Source',1,0,N'20260825061341_AddDepartmentCustomerLookupSource'),
    (N'Departments',N'IX_Departments_Code',N'Code',1,0,N'20260819072347_InitialIdentityAndAccess'),
    (N'Departments',N'IX_Departments_Name',N'Name',1,0,N'20260819072347_InitialIdentityAndAccess'),
    (N'EmployeeReviewDevices',N'IX_EmployeeReviewDevices_EmployeeId',N'EmployeeId',0,0,N'20261124071552_AddEmployeeReviews'),
    (N'EmployeeReviewDevices',N'UX_EmployeeReviewDevices_TokenHash',N'TokenHash',1,1,N'20261124071552_AddEmployeeReviews'),
    (N'EmployeeReviews',N'IX_EmployeeReviews_CreatedAtUtc',N'CreatedAtUtc',0,0,N'20261124071552_AddEmployeeReviews'),
    (N'EmployeeReviews',N'IX_EmployeeReviews_DeviceId',N'DeviceId',0,0,N'20261124071552_AddEmployeeReviews'),
    (N'EmployeeReviews',N'UX_EmployeeReviews_SubmissionId',N'SubmissionId',1,0,N'20261124071552_AddEmployeeReviews'),
    (N'EmployeeReviews',N'IX_EmployeeReviews_EmployeeId_CreatedAtUtc',N'EmployeeId,CreatedAtUtc',0,0,N'20261124071552_AddEmployeeReviews'),
    (N'GenesysQueueMappings',N'IX_GenesysQueueMappings_DepartmentId',N'DepartmentId',0,0,N'20260910080219_AddGenesysIntegration'),
    (N'GenesysQueueMappings',N'UX_GenesysQueueMappings_QueueId',N'QueueId',1,0,N'20260910080219_AddGenesysIntegration'),
    (N'GenesysScreenPopLaunches',N'UX_GenesysScreenPopLaunches_TokenHash',N'TokenHash',1,0,N'20260928132618_AddGenesysScreenPopLaunches'),
    (N'Holidays',N'IX_Holidays_ConfirmedByEmployeeId',N'ConfirmedByEmployeeId',0,0,N'20260821130000_AddSlaAndEscalation'),
    (N'Holidays',N'IX_Holidays_EnteredByEmployeeId',N'EnteredByEmployeeId',0,0,N'20260821130000_AddSlaAndEscalation'),
    (N'Holidays',N'IX_Holidays_BusinessCalendarId_HolidayDate',N'BusinessCalendarId,HolidayDate',1,0,N'20260821130000_AddSlaAndEscalation'),
    (N'IdempotencyRecords',N'UX_IdempotencyRecords_IdempotencyKey',N'IdempotencyKey',1,0,N'20260821130000_AddSlaAndEscalation'),
    (N'IntakeRecords',N'IX_IntakeRecords_ChannelId',N'ChannelId',0,0,N'20260909084839_AddChannels'),
    (N'IntakeRecords',N'IX_IntakeRecords_CreatedByEmployeeId',N'CreatedByEmployeeId',0,0,N'20260820140000_AddTicketing'),
    (N'IntakeRecords',N'IX_IntakeRecords_DepartmentId',N'DepartmentId',0,0,N'20260825061341_AddDepartmentCustomerLookupSource'),
    (N'IntakeRecords',N'IX_IntakeRecords_LinkedTicketId',N'LinkedTicketId',0,0,N'20260820140000_AddTicketing'),
    (N'IntakeRecords',N'IX_IntakeRecords_PriorityHint',N'PriorityHint',0,0,N'20260820140000_AddTicketing'),
    (N'Notifications',N'IX_Notifications_DeliveryStatus',N'DeliveryStatus',0,0,N'20260822090000_AddNotificationsAndOutbox'),
    (N'Notifications',N'IX_Notifications_TicketId',N'TicketId',0,0,N'20260822090000_AddNotificationsAndOutbox'),
    (N'Notifications',N'UX_Notifications_OutboxMessageId_NotificationType',N'OutboxMessageId,NotificationType',1,1,N'20260822090000_AddNotificationsAndOutbox'),
    (N'OutboxMessages',N'IX_OutboxMessages_CorrelationId',N'CorrelationId',0,0,N'20260822090000_AddNotificationsAndOutbox'),
    (N'OutboxMessages',N'IX_OutboxMessages_IdempotencyRecordId',N'IdempotencyRecordId',0,0,N'20260822090000_AddNotificationsAndOutbox'),
    (N'OutboxMessages',N'IX_OutboxMessages_DeadLettered',N'Status',0,1,N'20260822090000_AddNotificationsAndOutbox'),
    (N'OutboxMessages',N'IX_OutboxMessages_Status_OccurredAtUtc',N'Status,OccurredAtUtc',0,1,N'20260822090000_AddNotificationsAndOutbox'),
    (N'PriorityDowngradeRequests',N'IX_PriorityDowngradeRequests_CurrentPriorityId',N'CurrentPriorityId',0,0,N'20261008184722_AddSlaPauseAndPriorityDowngrade'),
    (N'PriorityDowngradeRequests',N'IX_PriorityDowngradeRequests_DecidedByEmployeeId',N'DecidedByEmployeeId',0,0,N'20261008184722_AddSlaPauseAndPriorityDowngrade'),
    (N'PriorityDowngradeRequests',N'IX_PriorityDowngradeRequests_RequestedByEmployeeId',N'RequestedByEmployeeId',0,0,N'20261008184722_AddSlaPauseAndPriorityDowngrade'),
    (N'PriorityDowngradeRequests',N'IX_PriorityDowngradeRequests_RequestedPriorityId',N'RequestedPriorityId',0,0,N'20261008184722_AddSlaPauseAndPriorityDowngrade'),
    (N'PriorityDowngradeRequests',N'UX_PriorityDowngradeRequests_OnePendingPerTicket',N'TicketId',1,1,N'20261008184722_AddSlaPauseAndPriorityDowngrade'),
    (N'PriorityDowngradeRequests',N'IX_PriorityDowngradeRequests_StatusExpiry',N'Status,ExpiresAtUtc',0,0,N'20261008184722_AddSlaPauseAndPriorityDowngrade'),
    (N'QueueingCompanyMappings',N'UX_QueueingCompanyMappings_CompanyId',N'CompanyId',1,0,N'20261117101035_AddQueueingIntegration'),
    (N'QueueingCompanyMappings',N'IX_QueueingCompanyMappings_DepartmentId',N'DepartmentId',0,0,N'20261117101035_AddQueueingIntegration'),
    (N'QueueingEvents',N'IX_QueueingEvents_PurgeablePayloads',N'ProcessedAtUtc',0,1,N'20261117101035_AddQueueingIntegration'),
    (N'QueueingEvents',N'UX_QueueingEvents_Source_EventId',N'Source,EventId',1,0,N'20261117101035_AddQueueingIntegration'),
    (N'QueueingEvents',N'IX_QueueingEvents_Status_NextRetry',N'Status,NextRetryAtUtc',0,0,N'20261117101035_AddQueueingIntegration'),
    (N'QueueingEvents',N'IX_QueueingEvents_Ticket_Sequence',N'Source,ExternalTicketId,EventSequence',0,0,N'20261117101035_AddQueueingIntegration'),
    (N'QueueingTicketLinks',N'UX_QueueingTicketLinks_ExternalTicketId',N'ExternalTicketId',1,0,N'20261117101035_AddQueueingIntegration'),
    (N'QueueingTicketLinks',N'UX_QueueingTicketLinks_TicketId',N'TicketId',1,0,N'20261117101035_AddQueueingIntegration'),
    (N'QueueingTicketTypeMappings',N'UX_QueueingTicketTypeMappings_Company_Type',N'CompanyId,TicketTypeId',1,0,N'20261117101035_AddQueueingIntegration'),
    (N'QueueingUserMappings',N'UX_QueueingUserMappings_EmployeeId',N'EmployeeId',1,0,N'20261117101035_AddQueueingIntegration'),
    (N'QueueingUserMappings',N'UX_QueueingUserMappings_ExternalUserId',N'ExternalUserId',1,0,N'20261117101035_AddQueueingIntegration'),
    (N'RequestTypeApprovalRequirements',N'IX_RequestTypeApprovalRequirements_TargetDepartmentId',N'TargetDepartmentId',0,0,N'20260904091143_AddApprovalWorkflow'),
    (N'RequestTypeApprovalRequirements',N'IX_RequestTypeApprovalRequirements_TargetEmployeeId',N'TargetEmployeeId',0,0,N'20260904091143_AddApprovalWorkflow'),
    (N'RequestTypeApprovalRequirements',N'IX_RequestTypeApprovalRequirements_RequestTypeId_ApprovalType',N'RequestTypeId,ApprovalType',1,0,N'20260904091143_AddApprovalWorkflow'),
    (N'RequestTypeAssignmentRuleMembers',N'IX_RequestTypeAssignmentRuleMembers_EmployeeId',N'EmployeeId',0,0,N'20260904075902_AddWorkflowAutomation'),
    (N'RequestTypeAssignmentRuleMembers',N'IX_RequestTypeAssignmentRuleMembers_RequestTypeAssignmentRuleId_EmployeeId',N'RequestTypeAssignmentRuleId,EmployeeId',1,0,N'20260904075902_AddWorkflowAutomation'),
    (N'RequestTypeAssignmentRules',N'IX_RequestTypeAssignmentRules_PrimaryEmployeeId',N'PrimaryEmployeeId',0,0,N'20260904075902_AddWorkflowAutomation'),
    (N'RequestTypeAssignmentRules',N'IX_RequestTypeAssignmentRules_RequestTypeId',N'RequestTypeId',1,0,N'20260904075902_AddWorkflowAutomation'),
    (N'RequestTypeSlaPolicies',N'IX_RequestTypeSlaPolicies_PriorityId',N'PriorityId',0,0,N'20260903164723_AddWorkflowConfiguration'),
    (N'RequestTypeSlaPolicies',N'IX_RequestTypeSlaPolicies_RequestTypeId_PriorityId',N'RequestTypeId,PriorityId',1,0,N'20260903164723_AddWorkflowConfiguration'),
    (N'RequestTypes',N'IX_RequestTypes_DefaultPriorityId',N'DefaultPriorityId',0,0,N'20260903164723_AddWorkflowConfiguration'),
    (N'RequestTypes',N'IX_RequestTypes_WorkflowId',N'WorkflowId',0,0,NULL),
    (N'RequestTypes',N'IX_RequestTypes_DepartmentId_Name',N'DepartmentId,Name',1,0,N'20260903164723_AddWorkflowConfiguration'),
    (N'TicketAgentHandoffs',N'IX_TicketAgentHandoffs_ChannelId',N'ChannelId',0,0,N'20260910080219_AddGenesysIntegration'),
    (N'TicketAgentHandoffs',N'IX_TicketAgentHandoffs_AssignedEmployeeId',N'AssignedEmployeeId',0,0,N'20260910080219_AddGenesysIntegration'),
    (N'TicketAgentHandoffs',N'IX_TicketAgentHandoffs_OpenByDepartment',N'DepartmentId,RequestedAtUtc',0,1,N'20260910080219_AddGenesysIntegration'),
    (N'TicketAgentHandoffs',N'IX_TicketAgentHandoffs_TicketId',N'TicketId',0,0,N'20260910080219_AddGenesysIntegration'),
    (N'TicketAgentHandoffs',N'UX_TicketAgentHandoffs_ExternalWorkItemId',N'ExternalWorkItemId',1,1,N'20260910080219_AddGenesysIntegration'),
    (N'TicketAgentHandoffs',N'UX_TicketAgentHandoffs_OpenPerInteraction',N'TicketInteractionId',1,1,N'20260910080219_AddGenesysIntegration'),
    (N'TicketApprovals',N'IX_TicketApprovals_DecidedByEmployeeId',N'DecidedByEmployeeId',0,0,N'20260904091143_AddApprovalWorkflow'),
    (N'TicketApprovals',N'IX_TicketApprovals_RequestedByEmployeeId',N'RequestedByEmployeeId',0,0,N'20260904091143_AddApprovalWorkflow'),
    (N'TicketApprovals',N'IX_TicketApprovals_TargetDepartmentId',N'TargetDepartmentId',0,0,N'20260904091143_AddApprovalWorkflow'),
    (N'TicketApprovals',N'IX_TicketApprovals_TargetEmployeeId',N'TargetEmployeeId',0,0,N'20260904091143_AddApprovalWorkflow'),
    (N'TicketApprovals',N'UX_TicketApprovals_OneCurrentPerType',N'TicketId,ApprovalType',1,1,N'20260904091143_AddApprovalWorkflow'),
    (N'TicketApprovals',N'UX_TicketApprovals_OnePendingPerType',N'TicketId,ApprovalType',1,1,N'20260904091143_AddApprovalWorkflow'),
    (N'TicketAssignmentHistory',N'IX_TicketAssignmentHistory_ChangedByEmployeeId',N'ChangedByEmployeeId',0,0,N'20261104130835_AddCollectionsTowerAssignments'),
    (N'TicketAssignmentHistory',N'IX_TicketAssignmentHistory_DepartmentId',N'DepartmentId',0,0,N'20261104130835_AddCollectionsTowerAssignments'),
    (N'TicketAssignmentHistory',N'IX_TicketAssignmentHistory_NewEmployeeId',N'NewEmployeeId',0,0,N'20261104130835_AddCollectionsTowerAssignments'),
    (N'TicketAssignmentHistory',N'IX_TicketAssignmentHistory_PreviousEmployeeId',N'PreviousEmployeeId',0,0,N'20261104130835_AddCollectionsTowerAssignments'),
    (N'TicketAssignmentHistory',N'IX_TicketAssignmentHistory_Ticket_Changed',N'TicketId,ChangedAtUtc',0,0,N'20261104130835_AddCollectionsTowerAssignments'),
    (N'TicketAssignments',N'IX_TicketAssignments_AssignedDepartmentId',N'AssignedDepartmentId',0,0,N'20260821090000_AddTicketOperations'),
    (N'TicketAssignments',N'IX_TicketAssignments_AssignedEmployeeId',N'AssignedEmployeeId',0,0,N'20260821090000_AddTicketOperations'),
    (N'TicketAssignments',N'IX_TicketAssignments_AssigningActorEmployeeId',N'AssigningActorEmployeeId',0,0,N'20260821090000_AddTicketOperations'),
    (N'TicketAssignments',N'IX_TicketAssignments_TicketId',N'TicketId',1,1,N'20260821090000_AddTicketOperations'),
    (N'TicketCollectionsUnits',N'IX_TicketCollectionsUnits_SelectedByEmployeeId',N'SelectedByEmployeeId',0,0,N'20261104130835_AddCollectionsTowerAssignments'),
    (N'TicketEscalations',N'IX_TicketEscalations_RespondingEmployeeId',N'RespondingEmployeeId',0,0,N'20260821130000_AddSlaAndEscalation'),
    (N'TicketEscalations',N'UX_TicketEscalations_OneAutoBreachPerTicket',N'TicketId',1,1,N'20260821130000_AddSlaAndEscalation'),
    (N'TicketEscalations',N'IX_TicketEscalations_TicketRaisedAt',N'TicketId,RaisedAtUtc',0,0,N'20260821130000_AddSlaAndEscalation'),
    (N'TicketInteractionHandlers',N'IX_TicketInteractionHandlers_TicketId',N'TicketId',0,0,N'20261030061255_AddTicketInteractionHandlers'),
    (N'TicketInteractionHandlers',N'IX_TicketInteractionHandlers_HandledByUserId_TicketId',N'HandledByUserId,TicketId',0,0,N'20261030061255_AddTicketInteractionHandlers'),
    (N'TicketInteractionHandlers',N'UX_TicketInteractionHandlers_InteractionId_UserId',N'TicketInteractionId,HandledByUserId',1,0,N'20261030061255_AddTicketInteractionHandlers'),
    (N'TicketInteractionMessages',N'UX_TicketInteractionMessages_InteractionSequence',N'TicketInteractionId,Sequence',1,0,N'20260910080219_AddGenesysIntegration'),
    (N'TicketInteractions',N'IX_TicketInteractions_ChannelId',N'ChannelId',0,0,N'20260909084839_AddChannels'),
    (N'TicketInteractions',N'IX_TicketInteractions_AwaitingCustomerReplySinceUtc',N'AwaitingCustomerReplySinceUtc',0,1,N'20261007093644_AddChatbotInactivityAndCrmDocumentCopies'),
    (N'TicketInteractions',N'IX_TicketInteractions_GenesysConversationId',N'GenesysConversationId',0,1,N'20261022090000_AddGenesysServiceRequestToTicketInteractions'),
    (N'TicketInteractions',N'IX_TicketInteractions_HandledByUserId',N'HandledByUserId',0,0,N'20260911105154_AddGenesysAgentMappingAndInteractionOwnership'),
    (N'TicketInteractions',N'IX_TicketInteractions_LastCustomerInteractionAtUtc',N'LastCustomerInteractionAtUtc',0,1,N'20261020105026_AddLastCustomerInteractionToTicketInteractions'),
    (N'TicketInteractions',N'IX_TicketInteractions_TicketId',N'TicketId',0,0,N'20260904082241_ReplaceInteractionContextWithTicketInteractions'),
    (N'TicketInteractions',N'UX_TicketInteractions_GenesysConversationId_ServiceRequestId',N'GenesysConversationId,GenesysServiceRequestId',1,1,N'20261022090000_AddGenesysServiceRequestToTicketInteractions'),
    (N'TicketInteractions',N'UX_TicketInteractions_OneOriginatingPerTicket',N'TicketId',1,1,N'20260904082241_ReplaceInteractionContextWithTicketInteractions'),
    (N'TicketNotes',N'IX_TicketNotes_AuthorEmployeeId',N'AuthorEmployeeId',0,0,N'20260821090000_AddTicketOperations'),
    (N'TicketNotes',N'IX_TicketNotes_TicketId',N'TicketId',0,0,N'20260821090000_AddTicketOperations'),
    (N'TicketPendingRecords',N'IX_TicketPendingRecords_ResumedByEmployeeId',N'ResumedByEmployeeId',0,0,N'20260904075902_AddWorkflowAutomation'),
    (N'TicketPendingRecords',N'IX_TicketPendingRecords_StartedByEmployeeId',N'StartedByEmployeeId',0,0,N'20260904075902_AddWorkflowAutomation'),
    (N'TicketPendingRecords',N'UX_TicketPendingRecords_OpenPerTicket',N'TicketId',1,1,N'20260904075902_AddWorkflowAutomation'),
    (N'TicketResolutions',N'IX_TicketResolutions_DuplicateOfTicketId',N'DuplicateOfTicketId',0,0,N'20260821090000_AddTicketOperations'),
    (N'TicketResolutions',N'IX_TicketResolutions_ResolvingEmployeeId',N'ResolvingEmployeeId',0,0,N'20260821090000_AddTicketOperations'),
    (N'TicketResolutions',N'IX_TicketResolutions_TicketId',N'TicketId',1,1,N'20260821090000_AddTicketOperations'),
    (N'TicketReviewInvitations',N'UX_TicketReviewInvitations_TokenHash',N'TokenHash',1,0,N'20261022081031_AddCustomerSatisfactionReviews'),
    (N'TicketReviewInvitations',N'IX_TicketReviewInvitations_TicketId_ReopenCycle',N'TicketId,ReopenCycle',0,0,N'20261022081031_AddCustomerSatisfactionReviews'),
    (N'TicketReviews',N'IX_TicketReviews_TicketReviewInvitationId',N'TicketReviewInvitationId',0,0,N'20261022081031_AddCustomerSatisfactionReviews'),
    (N'TicketReviews',N'IX_TicketReviews_IsTest_SubmittedAtUtc',N'IsTest,SubmittedAtUtc',0,0,N'20261022081031_AddCustomerSatisfactionReviews'),
    (N'TicketReviews',N'UX_TicketReviews_TicketId_ReopenCycle_IsTest',N'TicketId,ReopenCycle,IsTest',1,0,N'20261022081031_AddCustomerSatisfactionReviews'),
    (N'TicketSlaInstances',N'IX_TicketSlaInstances_ApprovedByEmployeeId',N'ApprovedByEmployeeId',0,0,N'20260821130000_AddSlaAndEscalation'),
    (N'TicketSlaInstances',N'IX_TicketSlaInstances_PriorityId',N'PriorityId',0,0,N'20260821130000_AddSlaAndEscalation'),
    (N'TicketSlaInstances',N'UX_TicketSlaInstances_CurrentPeriodPerTicket',N'TicketId',1,1,N'20260821130000_AddSlaAndEscalation'),
    (N'TicketSlaInstances',N'IX_TicketSlaInstances_FirstResponseSweep',N'PeriodEndAtUtc,FirstResponseBreached,FirstResponseDueAtUtc',0,0,N'20260821130000_AddSlaAndEscalation'),
    (N'TicketSlaInstances',N'IX_TicketSlaInstances_ResolutionSweep',N'PeriodEndAtUtc,ResolutionBreached,ResolutionDueAtUtc',0,0,N'20260821130000_AddSlaAndEscalation'),
    (N'TicketSlaPausePeriods',N'UX_TicketSlaPausePeriods_OneOpenPerTicket',N'TicketId',1,1,N'20261008184722_AddSlaPauseAndPriorityDowngrade'),
    (N'TicketSlaPausePeriods',N'IX_TicketSlaPausePeriods_Instance',N'TicketSlaInstanceId',0,0,N'20261008184722_AddSlaPauseAndPriorityDowngrade'),
    (N'TicketStatusHistory',N'IX_TicketStatusHistory_ActorEmployeeId',N'ActorEmployeeId',0,0,N'20260820140000_AddTicketing'),
    (N'TicketStatusHistory',N'IX_TicketStatusHistory_TicketId',N'TicketId',0,0,N'20260820140000_AddTicketing'),
    (N'TicketWorkflowEvents',N'IX_TicketWorkflowEvents_ActorEmployeeId',N'ActorEmployeeId',0,0,N'20260904091143_AddApprovalWorkflow'),
    (N'TicketWorkflowEvents',N'IX_TicketWorkflowEvents_TicketApprovalId',N'TicketApprovalId',0,0,N'20260904091143_AddApprovalWorkflow'),
    (N'TicketWorkflowEvents',N'IX_TicketWorkflowEvents_EventType_TransferReasonCode',N'EventType,TransferReasonCode',0,0,N'20261030062642_AddTicketTransferTracking'),
    (N'TicketWorkflowEvents',N'IX_TicketWorkflowEvents_TicketId_EventType',N'TicketId,EventType',0,0,N'20260904091143_AddApprovalWorkflow'),
    (N'Tickets',N'IX_Tickets_CategoryId',N'CategoryId',0,0,N'20260820140000_AddTicketing'),
    (N'Tickets',N'IX_Tickets_ContactReferenceId',N'ContactReferenceId',0,0,N'20260820140000_AddTicketing'),
    (N'Tickets',N'IX_Tickets_CurrentDepartmentId',N'CurrentDepartmentId',0,0,N'20260820140000_AddTicketing'),
    (N'Tickets',N'IX_Tickets_CurrentOwnerEmployeeId',N'CurrentOwnerEmployeeId',0,0,N'20260820140000_AddTicketing'),
    (N'Tickets',N'IX_Tickets_DuplicateOfTicketId',N'DuplicateOfTicketId',0,0,N'20260820140000_AddTicketing'),
    (N'Tickets',N'IX_Tickets_OriginatingDepartmentId',N'OriginatingDepartmentId',0,0,N'20260820140000_AddTicketing'),
    (N'Tickets',N'IX_Tickets_PriorityId',N'PriorityId',0,0,N'20260820140000_AddTicketing'),
    (N'Tickets',N'IX_Tickets_RequestTypeId',N'RequestTypeId',0,0,N'20260904075902_AddWorkflowAutomation'),
    (N'Tickets',N'IX_Tickets_TicketNumber',N'TicketNumber',1,0,N'20260820140000_AddTicketing'),
    (N'Tickets',N'IX_Tickets_UnitReferenceId',N'UnitReferenceId',0,0,N'20260820140000_AddTicketing'),
    (N'Tickets',N'IX_Tickets_WorkflowTemplateId',N'WorkflowTemplateId',0,0,N'20260908054944_AddWorkflowVersioning'),
    (N'Tickets',N'IX_Tickets_CustomerVerificationSource_ExternalCustomerId',N'CustomerVerificationSource,ExternalCustomerId',0,1,N'20260901103309_AddExternalCustomerVerificationToTickets'),
    (N'Tickets',N'UX_Tickets_IntegrationSource_ExternalTicketId',N'IntegrationSource,ExternalTicketId',1,1,N'20261117101035_AddQueueingIntegration'),
    (N'Tickets',N'IX_Tickets_CreatedByEmployeeId',N'CreatedByEmployeeId',0,0,N'20261030073857_AddTicketCreatedBy'),
    (N'Tickets',N'IX_Tickets_OriginalHumanAgentId',N'OriginalHumanAgentId',0,0,N'20261111073632_AddTicketOriginalHumanAgent'),
    (N'UnitReferences',N'IX_UnitReferences_CrmUnitId',N'CrmUnitId',1,0,N'20260820072044_AddCustomerVerification'),
    (N'UserDepartmentAssignments',N'IX_UserDepartmentAssignments_DepartmentId',N'DepartmentId',0,0,N'20260819072347_InitialIdentityAndAccess'),
    (N'UserDepartmentAssignments',N'IX_UserDepartmentAssignments_EmployeeId_PrimaryOnly',N'EmployeeId',1,1,N'20260819072347_InitialIdentityAndAccess'),
    (N'UserDepartmentAssignments',N'IX_UserDepartmentAssignments_EmployeeId_DepartmentId',N'EmployeeId,DepartmentId',1,0,N'20260819072347_InitialIdentityAndAccess'),
    (N'UserPermissionOverrides',N'IX_UserPermissionOverrides_EmployeeId_PermissionKey',N'EmployeeId,PermissionKey',1,0,N'20261102065459_AddUserPermissionOverrides'),
    (N'UserTransferDepartments',N'IX_UserTransferDepartments_DepartmentId',N'DepartmentId',0,0,N'20261110070152_AddUserTransferDepartments'),
    (N'UserTransferDepartments',N'IX_UserTransferDepartments_EmployeeId_DepartmentId',N'EmployeeId,DepartmentId',1,0,N'20261110070152_AddUserTransferDepartments'),
    (N'VerificationSessions',N'IX_VerificationSessions_ContactReferenceId',N'ContactReferenceId',0,0,N'20260820072044_AddCustomerVerification'),
    (N'VerificationSessions',N'IX_VerificationSessions_UnitReferenceId',N'UnitReferenceId',0,0,N'20260820072044_AddCustomerVerification'),
    (N'VerificationSessions',N'IX_VerificationSessions_AgentEmployeeId_IdempotencyKey',N'AgentEmployeeId,IdempotencyKey',1,1,N'20260820072044_AddCustomerVerification'),
    (N'VerificationSessions',N'UX_VerificationSessions_ProofChallengeId',N'ProofChallengeId',1,1,N'20261007180009_AddCustomerOtpVerification'),
    (N'WorkflowStepTransitions',N'IX_WorkflowStepTransitions_TargetWorkflowTemplateStepId',N'TargetWorkflowTemplateStepId',0,0,N'20260908054944_AddWorkflowVersioning'),
    (N'WorkflowStepTransitions',N'IX_WorkflowStepTransitions_WorkflowTemplateStepId_Outcome',N'WorkflowTemplateStepId,Outcome',1,0,N'20260908054944_AddWorkflowVersioning'),
    (N'WorkflowTemplateSteps',N'IX_WorkflowTemplateSteps_WorkflowTemplateId_Sequence',N'WorkflowTemplateId,Sequence',0,0,N'20260908054944_AddWorkflowVersioning'),
    (N'WorkflowTemplates',N'IX_WorkflowTemplates_Code',N'Code',1,0,N'20260903164723_AddWorkflowConfiguration'),
    (N'WorkflowTemplates',N'IX_WorkflowTemplates_CreatedByEmployeeId',N'CreatedByEmployeeId',0,0,N'20260908054944_AddWorkflowVersioning'),
    (N'WorkflowTemplates',N'IX_WorkflowTemplates_PublishedByEmployeeId',N'PublishedByEmployeeId',0,0,N'20260908054944_AddWorkflowVersioning'),
    (N'WorkflowTemplates',N'IX_WorkflowTemplates_WorkflowId_VersionNumber',N'WorkflowId,VersionNumber',1,0,N'20260908054944_AddWorkflowVersioning'),
    (N'WorkflowTemplates',N'UX_WorkflowTemplates_OneDraftPerWorkflow',N'WorkflowId',1,1,N'20260908054944_AddWorkflowVersioning'),
    (N'WorkflowTemplates',N'UX_WorkflowTemplates_OnePublishedPerWorkflow',N'WorkflowId',1,1,N'20260908054944_AddWorkflowVersioning'),
    (N'Workflows',N'IX_Workflows_Code',N'Code',1,0,N'20260908054944_AddWorkflowVersioning');
INSERT #ExpChk (TableName,CheckName) VALUES
    (N'BusinessCalendarWorkingDays',N'CK_BusinessCalendarWorkingDays_DayOfWeekRange'),
    (N'BusinessCalendars',N'CK_BusinessCalendars_WindowOrder'),
    (N'CrmDocumentDeliveryRequests',N'CK_CrmDocumentDeliveryRequests_Origin'),
    (N'EmployeeReviews',N'CK_EmployeeReviews_Rating'),
    (N'PriorityDowngradeRequests',N'CK_PriorityDowngradeRequests_DecisionConsistent'),
    (N'PriorityDowngradeRequests',N'CK_PriorityDowngradeRequests_IsDowngrade'),
    (N'SlaPolicies',N'CK_SlaPolicies_PositiveTargets'),
    (N'TicketEscalations',N'CK_TicketEscalations_Level4IsManualOnly'),
    (N'TicketEscalations',N'CK_TicketEscalations_LevelRange'),
    (N'TicketReviews',N'CK_TicketReviews_Rating'),
    (N'TicketSlaInstances',N'CK_TicketSlaInstances_DowngradeRequiresApprover'),
    (N'TicketSlaInstances',N'CK_TicketSlaInstances_PeriodOrder'),
    (N'TicketSlaPausePeriods',N'CK_TicketSlaPausePeriods_Order');

-- actual schema, normalised to the EF column-type spelling
IF OBJECT_ID('tempdb..#ActCols') IS NOT NULL DROP TABLE #ActCols;
SELECT t.name AS TableName, c.name AS ColName, c.is_nullable AS IsNullable,
       CASE
         WHEN ty.name IN (N'nvarchar', N'nchar')           THEN ty.name + N'(' + CASE WHEN c.max_length = -1 THEN N'max' ELSE CAST(c.max_length / 2 AS nvarchar(10)) END + N')'
         WHEN ty.name IN (N'varchar', N'char', N'varbinary', N'binary') THEN ty.name + N'(' + CASE WHEN c.max_length = -1 THEN N'max' ELSE CAST(c.max_length AS nvarchar(10)) END + N')'
         WHEN ty.name IN (N'decimal', N'numeric')           THEN ty.name + N'(' + CAST(c.precision AS nvarchar(5)) + N',' + CAST(c.scale AS nvarchar(5)) + N')'
         WHEN ty.name IN (N'datetime2', N'datetimeoffset', N'time') THEN CASE WHEN c.scale = 7 THEN ty.name ELSE ty.name + N'(' + CAST(c.scale AS nvarchar(5)) + N')' END
         WHEN ty.name = N'timestamp'                        THEN N'rowversion'
         ELSE ty.name END AS ColType
INTO #ActCols
FROM sys.tables t
JOIN sys.schemas s ON s.schema_id = t.schema_id AND s.name = N'dbo'
JOIN sys.columns c ON c.object_id = t.object_id
JOIN sys.types ty ON ty.user_type_id = c.user_type_id;

-- 3a tables
INSERT #F (Section, CheckName, Obj, Owner, Expected, Actual, Status)
SELECT '3 Schema/tables', 'Table', e.TableName, e.Owner, N'exists',
       CASE WHEN t.object_id IS NULL THEN N'missing' ELSE N'exists' END,
       CASE WHEN e.Owner IS NULL THEN CASE WHEN t.object_id IS NULL THEN 'FAIL' ELSE 'PASS' END
            WHEN EXISTS (SELECT 1 FROM #Applied a WHERE a.MigrationId = e.Owner) THEN CASE WHEN t.object_id IS NULL THEN 'FAIL' ELSE 'PASS' END
            ELSE CASE WHEN t.object_id IS NULL THEN 'PENDING' ELSE 'DRIFT' END END
FROM #ExpTables e
LEFT JOIN sys.tables t ON t.name = e.TableName AND t.schema_id = SCHEMA_ID(N'dbo');

-- 3b columns (existence, type, nullability) - skipped for tables that do not exist yet
INSERT #F (Section, CheckName, Obj, Owner, Expected, Actual, Status)
SELECT '3 Schema/columns',
       CASE WHEN a.ColName IS NULL THEN 'Column' WHEN e.ColType <> N'' AND a.ColType <> e.ColType THEN 'Column type' WHEN a.IsNullable <> e.IsNullable THEN 'Column nullability' ELSE 'Column' END,
       e.TableName + N'.' + e.ColName, e.Owner,
       CASE WHEN e.ColType = N'' THEN N'(type not asserted)' ELSE e.ColType END + CASE WHEN e.IsNullable = 1 THEN N' NULL' ELSE N' NOT NULL' END,
       CASE WHEN a.ColName IS NULL THEN N'missing' ELSE a.ColType + CASE WHEN a.IsNullable = 1 THEN N' NULL' ELSE N' NOT NULL' END END,
       CASE
         WHEN a.ColName IS NOT NULL AND (e.ColType = N'' OR a.ColType = e.ColType) AND a.IsNullable = e.IsNullable THEN
              CASE WHEN e.Owner IS NULL OR EXISTS (SELECT 1 FROM #Applied x WHERE x.MigrationId = e.Owner) THEN 'PASS' ELSE 'DRIFT' END
         WHEN a.ColName IS NOT NULL THEN CASE WHEN e.Owner IS NOT NULL AND NOT EXISTS (SELECT 1 FROM #Applied x WHERE x.MigrationId = e.Owner) THEN 'DRIFT' ELSE 'FAIL' END
         WHEN e.Owner IS NOT NULL AND NOT EXISTS (SELECT 1 FROM #Applied x WHERE x.MigrationId = e.Owner) THEN 'PENDING'
         ELSE 'FAIL' END
FROM #ExpCols e
LEFT JOIN #ActCols a ON a.TableName = e.TableName AND a.ColName = e.ColName
WHERE EXISTS (SELECT 1 FROM sys.tables t WHERE t.name = e.TableName AND t.schema_id = SCHEMA_ID(N'dbo'));
-- (OK rows are kept on purpose so the detail proves each column was compared)

-- 3c columns present in the database but not in the model (manual change / drift)
INSERT #F (Section, CheckName, Obj, Expected, Actual, Status)
SELECT '3 Schema/columns', 'Column not in EF model', a.TableName + N'.' + a.ColName, N'absent', a.ColType, 'WARN'
FROM #ActCols a
WHERE EXISTS (SELECT 1 FROM #ExpTables t WHERE t.TableName = a.TableName)
  AND NOT EXISTS (SELECT 1 FROM #ExpCols c WHERE c.TableName = a.TableName AND c.ColName = a.ColName);

-- 3d tables in dbo that neither EF nor the Collections scripts know
INSERT #CollTables (Name) VALUES (N'CollectionsTowers'),(N'CollectionsRcvRun'),(N'CollectionsRcvCompany'),(N'CollectionsRcvLine'),(N'CollectionsRcvAccountUnit'),(N'CollectionsCrmOwnerState'),(N'CollectionsCrmProjectMap'),(N'CollectionsCrmProjectMapEvidence'),(N'CollectionsCrmUnitOwner');
INSERT #F (Section, CheckName, Obj, Expected, Actual, Status)
SELECT '3 Schema/tables', 'Table not in EF model / Collections scripts', t.name, N'absent',
       CASE WHEN t.name LIKE N'Bak[_]%' THEN N'legacy backup table' WHEN t.name LIKE N'Collections%' THEN N'legacy Collections snapshot table (M03 optional drop)' ELSE N'unknown' END,
       CASE WHEN t.name = N'__EFMigrationsHistory' THEN 'INFO' WHEN t.name LIKE N'Bak[_]%' OR t.name LIKE N'Collections%' THEN 'INFO' ELSE 'WARN' END
FROM sys.tables t
WHERE t.schema_id = SCHEMA_ID(N'dbo') AND t.is_ms_shipped = 0
  AND t.name NOT IN (SELECT TableName FROM #ExpTables) AND t.name NOT IN (SELECT Name FROM #CollTables);

-- 3e indexes
IF OBJECT_ID('tempdb..#ActIdx') IS NOT NULL DROP TABLE #ActIdx;
SELECT t.name AS TableName, i.name AS IndexName, i.is_unique AS IsUnique, i.has_filter AS HasFilter,
       STUFF((SELECT N',' + c.name FROM sys.index_columns ic JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
              WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0 ORDER BY ic.key_ordinal FOR XML PATH(N'')), 1, 1, N'') AS KeyCols
INTO #ActIdx
FROM sys.indexes i JOIN sys.tables t ON t.object_id = i.object_id AND t.schema_id = SCHEMA_ID(N'dbo')
WHERE i.name IS NOT NULL AND i.is_primary_key = 0 AND i.type > 0;

INSERT #F (Section, CheckName, Obj, Owner, Expected, Actual, Status)
SELECT '3 Schema/indexes', 'Index', e.TableName + N'.' + e.IndexName, e.Owner,
       e.KeyCols + CASE WHEN e.IsUnique = 1 THEN N' UNIQUE' ELSE N'' END + CASE WHEN e.HasFilter = 1 THEN N' FILTERED' ELSE N'' END,
       CASE WHEN a.IndexName IS NULL THEN N'missing' ELSE a.KeyCols + CASE WHEN a.IsUnique = 1 THEN N' UNIQUE' ELSE N'' END + CASE WHEN a.HasFilter = 1 THEN N' FILTERED' ELSE N'' END END,
       CASE
         WHEN a.IndexName IS NOT NULL AND a.KeyCols = e.KeyCols AND a.IsUnique = e.IsUnique AND a.HasFilter = e.HasFilter THEN
              CASE WHEN e.Owner IS NULL OR EXISTS (SELECT 1 FROM #Applied x WHERE x.MigrationId = e.Owner) THEN 'PASS' ELSE 'DRIFT' END
         WHEN a.IndexName IS NOT NULL THEN CASE WHEN e.Owner IS NOT NULL AND NOT EXISTS (SELECT 1 FROM #Applied x WHERE x.MigrationId = e.Owner) THEN 'DRIFT' ELSE 'FAIL' END
         WHEN e.Owner IS NOT NULL AND NOT EXISTS (SELECT 1 FROM #Applied x WHERE x.MigrationId = e.Owner) THEN 'PENDING'
         ELSE 'FAIL' END
FROM #ExpIdx e
LEFT JOIN #ActIdx a ON a.TableName = e.TableName AND a.IndexName = e.IndexName
WHERE EXISTS (SELECT 1 FROM sys.tables t WHERE t.name = e.TableName AND t.schema_id = SCHEMA_ID(N'dbo'))
   OR e.Owner IS NOT NULL;

-- 3f check constraints
INSERT #F (Section, CheckName, Obj, Expected, Actual, Status)
SELECT '3 Schema/checks', 'Check constraint', e.TableName + N'.' + e.CheckName, N'exists',
       CASE WHEN cc.object_id IS NULL THEN N'missing' ELSE N'exists' END,
       CASE WHEN cc.object_id IS NOT NULL THEN 'PASS'
            WHEN NOT EXISTS (SELECT 1 FROM sys.tables t WHERE t.name = e.TableName AND t.schema_id = SCHEMA_ID(N'dbo')) THEN 'PENDING' ELSE 'FAIL' END
FROM #ExpChk e
LEFT JOIN sys.check_constraints cc ON cc.name = e.CheckName AND cc.parent_object_id = OBJECT_ID(N'dbo.' + QUOTENAME(e.TableName));

-- 3g foreign keys that are disabled / not trusted (a manual bulk load or a half-applied fix leaves these)
INSERT #F (Section, CheckName, Obj, Expected, Actual, Status)
SELECT '3 Schema/FKs', 'Foreign key disabled or not trusted', OBJECT_NAME(fk.parent_object_id) + N'.' + fk.name, N'enabled + trusted',
       CASE WHEN fk.is_disabled = 1 THEN N'DISABLED' ELSE N'NOT TRUSTED' END, 'WARN'
FROM sys.foreign_keys fk WHERE fk.is_disabled = 1 OR fk.is_not_trusted = 1;
INSERT #F (Section, CheckName, Obj, Expected, Actual, Status)
SELECT '3 Schema/FKs', 'Foreign key count', N'dbo', N'(info)', CAST(COUNT(*) AS nvarchar(10)), 'INFO' FROM sys.foreign_keys;

-- self-check: the generated expected-schema lists were loaded completely (a silent short load would hide missing checks)
INSERT #F (Section, CheckName, Obj, Expected, Actual, Status)
SELECT '3 Schema/loaded', x.n, N'#Exp lists', CAST(x.e AS nvarchar(20)), CAST(x.a AS nvarchar(20)), CASE WHEN x.e = x.a THEN 'PASS' ELSE 'FAIL' END
FROM (VALUES ('Migrations loaded', 60, (SELECT COUNT(*) FROM #ExpMig)),
             ('Tables loaded',  88, (SELECT COUNT(*) FROM #ExpTables)),
             ('Columns loaded', 971, (SELECT COUNT(*) FROM #ExpCols)),
             ('Indexes loaded', 217, (SELECT COUNT(*) FROM #ExpIdx)),
             ('Check constraints loaded', 13, (SELECT COUNT(*) FROM #ExpChk))) x(n, e, a);
SET @Done = @Done + 1;
END TRY
BEGIN CATCH
    SET @Err = @Err + 1;
    INSERT #F (Section, CheckName, Obj, Expected, Actual, Status)
    VALUES ('0 Script error', 'Section 3 did not complete', N'line ' + CAST(ERROR_LINE() AS nvarchar(20)) + N', error ' + CAST(ERROR_NUMBER() AS nvarchar(20)), N'section completes', LEFT(ERROR_MESSAGE(), 900), 'FAIL');
END CATCH;

BEGIN TRY
---------------------------------------------------------------------------------------------------
-- 4. TRIGGERS (the only trigger in the EF history is TR_CustomerProfileChanges_AppendOnly, created by a PENDING migration)
---------------------------------------------------------------------------------------------------
INSERT #F (Section, CheckName, Obj, Owner, Expected, Actual, Status)
SELECT '4 Triggers', 'TR_CustomerProfileChanges_AppendOnly', N'CustomerProfileChanges', N'20261119081103_AddCustomerProfileAuditAndProperties',
       N'INSTEAD OF UPDATE, DELETE (after deploy)',
       ISNULL((SELECT TOP 1 CASE WHEN tr.is_instead_of_trigger = 1 THEN N'INSTEAD OF' ELSE N'AFTER' END + CASE WHEN tr.is_disabled = 1 THEN N' (disabled)' ELSE N'' END
               FROM sys.triggers tr WHERE tr.name = N'TR_CustomerProfileChanges_AppendOnly'), N'absent'),
       CASE WHEN EXISTS (SELECT 1 FROM #Applied WHERE MigrationId = N'20261119081103_AddCustomerProfileAuditAndProperties')
                 THEN CASE WHEN EXISTS (SELECT 1 FROM sys.triggers tr WHERE tr.name = N'TR_CustomerProfileChanges_AppendOnly' AND tr.is_instead_of_trigger = 1 AND tr.is_disabled = 0) THEN 'PASS' ELSE 'FAIL' END
            WHEN EXISTS (SELECT 1 FROM sys.triggers tr WHERE tr.name = N'TR_CustomerProfileChanges_AppendOnly') THEN 'DRIFT' ELSE 'PENDING' END;
INSERT #F (Section, CheckName, Obj, Expected, Actual, Status)
SELECT '4 Triggers', 'Other DML trigger present', tr.name, N'none expected', OBJECT_NAME(tr.parent_id), 'WARN'
FROM sys.triggers tr WHERE tr.parent_class = 1 AND tr.is_ms_shipped = 0 AND tr.name <> N'TR_CustomerProfileChanges_AppendOnly';
SET @Done = @Done + 1;
END TRY
BEGIN CATCH
    SET @Err = @Err + 1;
    INSERT #F (Section, CheckName, Obj, Expected, Actual, Status)
    VALUES ('0 Script error', 'Section 4 did not complete', N'line ' + CAST(ERROR_LINE() AS nvarchar(20)) + N', error ' + CAST(ERROR_NUMBER() AS nvarchar(20)), N'section completes', LEFT(ERROR_MESSAGE(), 900), 'FAIL');
END CATCH;

BEGIN TRY
---------------------------------------------------------------------------------------------------
-- 5. OBJECTS OUTSIDE EF MIGRATIONS: database/collections-receivables/*.sql (V001-V015, R001-R004)
--    Deployed by hand-run scripts, never by dotnet ef. Run order is in that folder's README.md.
---------------------------------------------------------------------------------------------------
INSERT #CollFn (Name) VALUES (N'fn_CollectionsTowerNumber'),(N'fn_CollectionsUnitFloor'),(N'fn_CollectionsUnitKey'),(N'fn_Collections_CrmUnitLinks'),(N'fn_Collections_TowerNames');
INSERT #CollPr (Name) VALUES (N'usp_CollectionsRcv_GetLines'),(N'usp_CollectionsRcv_GetStatus'),(N'usp_CollectionsRcv_GetTowers'),(N'usp_CollectionsRcv_GetUnit'),(N'usp_CollectionsRcv_GetUnitAccounts'),(N'usp_CollectionsRcv_GetUnits'),(N'usp_CollectionsRcv_NoteClientAbort'),(N'usp_CollectionsRcv_ReapDeadRuns'),(N'usp_CollectionsRcv_Refresh'),(N'usp_CollectionsRcv_SyncTowers'),(N'usp_Collections_GetCrmOwnerState'),(N'usp_Collections_GetCrmProjectMap'),(N'usp_Collections_GetCrmUnitLinks'),(N'usp_Collections_PublishCrmUnitOwners'),(N'usp_Collections_RecordCrmOwnerFailure'),(N'usp_Collections_RecordCrmProjectEvidence'),(N'usp_Collections_ResolveCrmOwnerCompanies'),(N'usp_Collections_ResolveCrmOwnerCompaniesCore'),(N'usp_Collections_StoreCrmUnitOwners');
INSERT #CollIx (TableName, IndexName) VALUES (N'CollectionsCrmUnitOwner',N'IX_CollectionsCrmUnitOwner_Unit'),(N'CollectionsCrmUnitOwner',N'IX_CollectionsCrmUnitOwner_UnitKey'),(N'CollectionsRcvAccountUnit',N'IX_CollectionsRcvAccountUnit_Code'),(N'CollectionsRcvLine',N'IX_CollectionsRcvLine_Due'),(N'CollectionsCrmProjectMapEvidence',N'UX_CollectionsCrmProjectMapEvidence_Obs'),(N'CollectionsCrmProjectMap',N'UX_CollectionsCrmProjectMap_Project_Code');
INSERT #LegPr  (Name) VALUES (N'usp_Collections_PublishReceivablesStaging'),(N'usp_Collections_RefreshReceivables'),(N'usp_Collections_GetReceivables'),(N'usp_Collections_GetCampaignUnits'),(N'usp_Collections_GetInstalmentUnitsPage'),(N'usp_Collections_GetInstalmentMonths');

INSERT #F (Section, CheckName, Obj, Expected, Actual, Status)
SELECT '5 Collections SQL', 'Table (not EF)', c.Name, N'exists', CASE WHEN t.object_id IS NULL THEN N'missing' ELSE N'exists' END, CASE WHEN t.object_id IS NULL THEN 'FAIL' ELSE 'PASS' END
FROM #CollTables c LEFT JOIN sys.tables t ON t.name = c.Name AND t.schema_id = SCHEMA_ID(N'dbo');
INSERT #F (Section, CheckName, Obj, Expected, Actual, Status)
SELECT '5 Collections SQL', 'Function', f.Name, N'exists (FN/IF/TF)', ISNULL(o.type_desc, N'missing'), CASE WHEN o.object_id IS NULL THEN 'FAIL' ELSE 'PASS' END
FROM #CollFn f LEFT JOIN sys.objects o ON o.name = f.Name AND o.schema_id = SCHEMA_ID(N'dbo') AND o.type IN ('FN', 'IF', 'TF');
INSERT #F (Section, CheckName, Obj, Expected, Actual, Status)
SELECT '5 Collections SQL', 'Stored procedure', p.Name, N'exists (called by the application)', ISNULL(o.type_desc, N'missing'), CASE WHEN o.object_id IS NULL THEN 'FAIL' ELSE 'PASS' END
FROM #CollPr p LEFT JOIN sys.objects o ON o.name = p.Name AND o.schema_id = SCHEMA_ID(N'dbo') AND o.type = 'P';
INSERT #F (Section, CheckName, Obj, Expected, Actual, Status)
SELECT '5 Collections SQL', 'Index (not EF)', x.TableName + N'.' + x.IndexName, N'exists', CASE WHEN i.index_id IS NULL THEN N'missing' ELSE N'exists' END, CASE WHEN i.index_id IS NULL THEN 'FAIL' ELSE 'PASS' END
FROM #CollIx x LEFT JOIN sys.indexes i ON i.name = x.IndexName AND i.object_id = OBJECT_ID(N'dbo.' + QUOTENAME(x.TableName));
INSERT #F (Section, CheckName, Obj, Expected, Actual, Status)
SELECT '5 Collections SQL', 'Legacy procedure (retired pipeline)', l.Name, N'either (M03 drop is optional)', CASE WHEN o.object_id IS NULL THEN N'absent' ELSE N'still present' END, 'INFO'
FROM #LegPr l LEFT JOIN sys.objects o ON o.name = l.Name AND o.schema_id = SCHEMA_ID(N'dbo') AND o.type = 'P';

-- CollectionsTowers lookup index: V001 creates EITHER the unique UX_ index (no duplicate towers) OR the non-unique IX_ index - never both
INSERT #F (Section, CheckName, Obj, Expected, Actual, Status)
SELECT '5 Collections SQL', 'Index (not EF) either-or (V001)', N'CollectionsTowers (CompanyId, TowerNumber)', N'UX_ or IX_CollectionsTowers_Company_TowerNumber',
       ISNULL(STUFF((SELECT N', ' + i.name FROM sys.indexes i WHERE i.object_id = OBJECT_ID(N'dbo.CollectionsTowers')
                     AND i.name IN (N'UX_CollectionsTowers_Company_TowerNumber', N'IX_CollectionsTowers_Company_TowerNumber') FOR XML PATH(N'')), 1, 2, N''), N'neither'),
       CASE WHEN EXISTS (SELECT 1 FROM sys.indexes i WHERE i.object_id = OBJECT_ID(N'dbo.CollectionsTowers') AND i.name IN (N'UX_CollectionsTowers_Company_TowerNumber', N'IX_CollectionsTowers_Company_TowerNumber')) THEN 'PASS' ELSE 'FAIL' END;

-- R004 version marker: usp_CollectionsRcv_GetUnits must have the @Floors parameter (the Floor filter; needs fn_CollectionsUnitFloor)
INSERT #F (Section, CheckName, Obj, Expected, Actual, Status)
SELECT '5 Collections SQL', 'GetUnits has @Floors parameter (R004)', N'usp_CollectionsRcv_GetUnits', N'@Floors present',
       CASE WHEN EXISTS (SELECT 1 FROM sys.parameters p WHERE p.object_id = OBJECT_ID(N'dbo.usp_CollectionsRcv_GetUnits') AND p.name = N'@Floors') THEN N'present' ELSE N'absent' END,
       CASE WHEN OBJECT_ID(N'dbo.usp_CollectionsRcv_GetUnits') IS NULL THEN 'INFO'
            WHEN EXISTS (SELECT 1 FROM sys.parameters p WHERE p.object_id = OBJECT_ID(N'dbo.usp_CollectionsRcv_GetUnits') AND p.name = N'@Floors') THEN 'PASS' ELSE 'FAIL' END;

-- modules that reference a missing local object (a script deployed out of order, e.g. R004 before V012)
INSERT #F (Section, CheckName, Obj, Expected, Actual, Status)
SELECT DISTINCT '5 Collections SQL', 'Unresolved reference in module', OBJECT_NAME(d.referencing_id), N'all references resolve (INFO = UPDATE/DELETE alias, not an object)',
       ISNULL(d.referenced_schema_name + N'.', N'') + d.referenced_entity_name,
       CASE WHEN d.referenced_schema_name IS NULL AND EXISTS (SELECT 1 FROM sys.sql_modules m
                 WHERE m.object_id = d.referencing_id
                   AND (REPLACE(REPLACE(REPLACE(m.definition, CHAR(13), N' '), CHAR(10), N' '), CHAR(9), N' ') LIKE N'%UPDATE ' + d.referenced_entity_name + N' %'
                     OR REPLACE(REPLACE(REPLACE(m.definition, CHAR(13), N' '), CHAR(10), N' '), CHAR(9), N' ') LIKE N'%DELETE ' + d.referenced_entity_name + N' %'))
                 THEN 'INFO'      -- the name is the alias target of UPDATE <alias> ... FROM / DELETE <alias> FROM, not a missing object
            WHEN d.referenced_entity_name LIKE N'%Collections%' THEN 'FAIL' ELSE 'WARN' END
FROM sys.sql_expression_dependencies d
JOIN sys.objects o ON o.object_id = d.referencing_id AND o.is_ms_shipped = 0 AND o.type IN ('P', 'FN', 'IF', 'TF', 'V', 'TR')
WHERE d.referenced_id IS NULL AND d.referenced_server_name IS NULL AND d.referenced_database_name IS NULL AND d.is_ambiguous = 0
  AND d.referenced_class = 1 AND d.referenced_entity_name NOT LIKE N'#%';

-- linked server used by usp_CollectionsRcv_Refresh (PACT reporting database)
INSERT #F (Section, CheckName, Obj, Expected, Actual, Status)
SELECT '5 Collections SQL', 'Linked server 10.10.10.94 (PACT)', N'sys.servers', N'configured',
       ISNULL((SELECT TOP 1 N'configured, data_access=' + CAST(is_data_access_enabled AS nvarchar(1)) FROM sys.servers WHERE name = N'10.10.10.94'), N'NOT configured'),
       CASE WHEN EXISTS (SELECT 1 FROM sys.servers WHERE name = N'10.10.10.94' AND is_data_access_enabled = 1) THEN 'PASS' ELSE 'WARN' END;
INSERT #F (Section, CheckName, Obj, Expected, Actual, Status)
VALUES ('5 Collections SQL', 'PACT remote procedures p4/p32AccountReceivablesV2', N'[10.10.10.94].[PACTRPT]', N'deployed from docs/Collections/pact-sql/deploy',
        N'NOT checked here (would query the remote PACT server) - verify with docs/Collections/pact-sql/deploy/20-smoke-and-compare-p4.sql / 21-...-p32.sql', 'INFO');
INSERT #F (Section, CheckName, Obj, Expected, Actual, Status)
SELECT '5 Collections SQL', 'Snapshot freshness', N'dbo.CollectionsRcvCompany', N'rows after the first refresh', CAST(SUM(p.rows) AS nvarchar(20)) + N' rows', 'INFO'
FROM sys.partitions p WHERE p.object_id = OBJECT_ID(N'dbo.CollectionsRcvCompany') AND p.index_id IN (0, 1);

-- Hangfire (created by Hangfire itself on first start, not by EF)
INSERT #F (Section, CheckName, Obj, Expected, Actual, Status)
SELECT '5 Collections SQL', 'Hangfire schema', N'HangFire', N'present when BackgroundJobs:Enabled = true',
       CASE WHEN SCHEMA_ID(N'HangFire') IS NULL THEN N'absent' ELSE N'present' END, 'INFO';
SET @Done = @Done + 1;
END TRY
BEGIN CATCH
    SET @Err = @Err + 1;
    INSERT #F (Section, CheckName, Obj, Expected, Actual, Status)
    VALUES ('0 Script error', 'Section 5 did not complete', N'line ' + CAST(ERROR_LINE() AS nvarchar(20)) + N', error ' + CAST(ERROR_NUMBER() AS nvarchar(20)), N'section completes', LEFT(ERROR_MESSAGE(), 900), 'FAIL');
END CATCH;

BEGIN TRY
---------------------------------------------------------------------------------------------------
-- 6. SEED / REFERENCE DATA  (each check is wrapped, a missing table never aborts the run)
---------------------------------------------------------------------------------------------------
-- 6a Channels (AddChannels migration + ChannelReferenceData seed): ids 1-11 with fixed codes
BEGIN TRY
  EXEC(N'
  INSERT #F (Section, CheckName, Obj, Expected, Actual, Status)
  SELECT ''6 Data/seed'', ''Channel row'', e.Code, CAST(e.Id AS nvarchar(5)) + N'' / '' + e.Code,
         ISNULL(CAST(c.ChannelId AS nvarchar(5)) + N'' / '' + c.Code + CASE WHEN c.IsActive = 1 THEN N'' active'' ELSE N'' inactive'' END, N''missing''),
         CASE WHEN c.ChannelId IS NULL THEN ''FAIL'' WHEN c.Code <> e.Code THEN ''WARN'' ELSE ''PASS'' END
  FROM (VALUES (1,N''PHONE''),(2,N''LEGACY_APP_OR_WEBSITE''),(3,N''LEGACY_WHATSAPP_OR_LIVE_CHAT''),(4,N''SOCIAL_DM''),(5,N''WALK_IN_KIOSK''),(6,N''WHATSAPP''),
               (7,N''LIVE_CHAT''),(8,N''WEBSITE''),(9,N''MOBILE_APP''),(10,N''INSTAGRAM''),(11,N''FACEBOOK'')) e(Id, Code)
  LEFT JOIN dbo.Channels c ON c.ChannelId = e.Id;');
END TRY
BEGIN CATCH
  INSERT #F (Section, CheckName, Obj, Actual, Status) VALUES ('6 Data/seed', 'Channels check could not run', N'dbo.Channels', LEFT(ERROR_MESSAGE(), 290), 'FAIL');
END CATCH;

-- 6b departments / General classification (EnsureDefaultGeneralClassification.sql, the app's last-resort fallback)
BEGIN TRY
  EXEC(N'
  INSERT #F (Section, CheckName, Obj, Expected, Actual, Status)
  SELECT ''6 Data/seed'', ''Active department has General category + request type + Published workflow'', d.Code,
         N''1 category, 1 request type, published workflow'',
         CAST(x.Cats AS nvarchar(5)) + N'' cat, '' + CAST(x.Rts AS nvarchar(5)) + N'' rt, '' + CAST(x.Pub AS nvarchar(5)) + N'' published'',
         CASE WHEN x.Cats = 1 AND x.Rts = 1 AND x.Pub >= 1 THEN ''PASS'' WHEN d.Code = N''CS'' THEN ''FAIL'' WHEN d.Code = N''COL'' THEN ''INFO'' ELSE ''WARN'' END
  FROM dbo.Departments d
  CROSS APPLY (SELECT
      (SELECT COUNT(*) FROM dbo.Categories c WHERE c.DepartmentId = d.DepartmentId AND c.IsActive = 1 AND LTRIM(RTRIM(c.Name)) = N''General'') AS Cats,
      (SELECT COUNT(*) FROM dbo.RequestTypes r WHERE r.DepartmentId = d.DepartmentId AND r.IsActive = 1 AND LTRIM(RTRIM(r.Name)) = N''General'') AS Rts,
      (SELECT COUNT(*) FROM dbo.RequestTypes r JOIN dbo.WorkflowTemplates w ON w.WorkflowId = r.WorkflowId AND w.Status = 2
        WHERE r.DepartmentId = d.DepartmentId AND r.IsActive = 1 AND LTRIM(RTRIM(r.Name)) = N''General'') AS Pub) x
  WHERE d.IsActive = 1;');
  IF NOT EXISTS (SELECT 1 FROM #F WHERE Section = '6 Data/seed' AND Obj = N'CS')
     INSERT #F (Section, CheckName, Obj, Expected, Actual, Status) VALUES ('6 Data/seed', 'Customer Services department (Code CS)', N'CS', N'active', N'missing/inactive', 'FAIL');
END TRY
BEGIN CATCH
  INSERT #F (Section, CheckName, Obj, Actual, Status) VALUES ('6 Data/seed', 'General-classification check could not run', N'Departments/Categories/RequestTypes', LEFT(ERROR_MESSAGE(), 290), 'FAIL');
END CATCH;

-- 6c Queueing routing data (ApplyQueueingTigerPropertiesMappings.sql - a DATA script, not a migration; company 4 = Tiger Properties)
BEGIN TRY
  EXEC(N'
  INSERT #F (Section, CheckName, Obj, Expected, Actual, Status)
  SELECT ''6 Data/seed'', ''Queueing ticket-type mapping (company 4)'', N''TicketType '' + e.TicketTypeId, N''-> RequestType '' + CAST(e.RequestTypeId AS nvarchar(10)) + N'' active'',
         ISNULL(N''-> RequestType '' + CAST(m.RequestTypeId AS nvarchar(10)) + CASE WHEN m.IsActive = 1 THEN N'' active'' ELSE N'' INACTIVE'' END, N''missing''),
         CASE WHEN m.QueueingTicketTypeMappingId IS NOT NULL AND m.RequestTypeId = e.RequestTypeId AND m.IsActive = 1 THEN ''PASS'' ELSE ''WARN'' END
  FROM (VALUES (N''53'',1),(N''54'',3),(N''55'',2),(N''56'',21),(N''57'',17),(N''58'',14),(N''59'',20),(N''60'',40)) e(TicketTypeId, RequestTypeId)
  LEFT JOIN dbo.QueueingTicketTypeMappings m ON m.CompanyId = N''4'' AND m.TicketTypeId = e.TicketTypeId;
  INSERT #F (Section, CheckName, Obj, Expected, Actual, Status)
  SELECT ''6 Data/seed'', ''Queueing default department (company 4)'', N''CompanyId 4'', N''DepartmentId 1 (CS), active'',
         ISNULL(N''DepartmentId '' + CAST(m.DepartmentId AS nvarchar(10)) + CASE WHEN m.IsActive = 1 THEN N'' active'' ELSE N'' INACTIVE'' END, N''missing''),
         CASE WHEN m.QueueingCompanyMappingId IS NOT NULL AND m.DepartmentId = 1 AND m.IsActive = 1 THEN ''PASS'' ELSE ''WARN'' END
  FROM (SELECT 1 AS x) o LEFT JOIN dbo.QueueingCompanyMappings m ON m.CompanyId = N''4'';
  INSERT #F (Section, CheckName, Obj, Expected, Actual, Status)
  SELECT ''6 Data/seed'', ''Mapped request type exists, active, department matches'', N''RequestType '' + CAST(e.RequestTypeId AS nvarchar(10)), N''dept '' + CAST(e.Dept AS nvarchar(5)) + N'', active, published workflow'',
         ISNULL(N''dept '' + CAST(r.DepartmentId AS nvarchar(5)) + CASE WHEN r.IsActive = 1 THEN N'', active'' ELSE N'', INACTIVE'' END + N'', published='' + CAST((SELECT COUNT(*) FROM dbo.WorkflowTemplates w WHERE w.WorkflowId = r.WorkflowId AND w.Status = 2) AS nvarchar(5)), N''missing''),
         CASE WHEN r.RequestTypeId IS NOT NULL AND r.IsActive = 1 AND r.DepartmentId = e.Dept AND EXISTS (SELECT 1 FROM dbo.WorkflowTemplates w WHERE w.WorkflowId = r.WorkflowId AND w.Status = 2) THEN ''PASS'' ELSE ''WARN'' END
  FROM (VALUES (1,1),(3,1),(2,1),(21,2),(17,1),(14,1),(20,3),(40,1)) e(RequestTypeId, Dept)
  LEFT JOIN dbo.RequestTypes r ON r.RequestTypeId = e.RequestTypeId;');
END TRY
BEGIN CATCH
  INSERT #F (Section, CheckName, Obj, Actual, Status) VALUES ('6 Data/seed', 'Queueing mapping check could not run', N'Queueing*Mappings', LEFT(ERROR_MESSAGE(), 290), 'FAIL');
END CATCH;

-- 6d context counts for the pending migrations
INSERT #F (Section, CheckName, Obj, Actual, Status)
SELECT '6 Data/seed', 'Row count (approx.)', t.name, CAST(SUM(p.rows) AS nvarchar(20)), 'INFO'
FROM sys.tables t JOIN sys.partitions p ON p.object_id = t.object_id AND p.index_id IN (0, 1)
WHERE t.name IN (N'CustomerProfileOverrides', N'CustomerProfileChanges', N'CustomerPropertyAssociations', N'TicketAssignmentHistory', N'TicketInteractions', N'Tickets', N'Employees', N'EmployeeReviews', N'EmployeeReviewDevices')
GROUP BY t.name;
BEGIN TRY
  EXEC(N'
  INSERT #F (Section, CheckName, Obj, Expected, Actual, Status)
  SELECT ''6 Data/seed'', ''CustomerProfileOverrides rows the pending migration back-fills with NEWID()'', N''CustomerProfileOverrides.CustomerProfileId'', N''(info)'', CAST(COUNT(*) AS nvarchar(20)), ''INFO''
  FROM dbo.CustomerProfileOverrides WHERE COL_LENGTH(N''dbo.CustomerProfileOverrides'', N''CustomerProfileId'') IS NULL;');
END TRY
BEGIN CATCH
  INSERT #F (Section, CheckName, Obj, Actual, Status) VALUES ('6 Data/seed', 'CustomerProfileOverrides count could not run', N'', LEFT(ERROR_MESSAGE(), 290), 'WARN');
END CATCH;
SET @Done = @Done + 1;
END TRY
BEGIN CATCH
    SET @Err = @Err + 1;
    INSERT #F (Section, CheckName, Obj, Expected, Actual, Status)
    VALUES ('0 Script error', 'Section 6 did not complete', N'line ' + CAST(ERROR_LINE() AS nvarchar(20)) + N', error ' + CAST(ERROR_NUMBER() AS nvarchar(20)), N'section completes', LEFT(ERROR_MESSAGE(), 900), 'FAIL');
END CATCH;

BEGIN TRY
---------------------------------------------------------------------------------------------------
-- 2b. PER-MIGRATION INTEGRITY: does each migration footprint (tables, columns, indexes, trigger) match its history row?
--     recorded + all objects present      = PASS
--     recorded + some/all objects missing = FAIL  (history says applied but the migration is incomplete / was rolled back by hand)
--     not recorded + no objects present   = PENDING
--     not recorded + all objects present  = DRIFT (applied by hand, history row missing: the script would fail)
--     not recorded + some objects present = DRIFT, PARTIALLY APPLIED (a failed / interrupted run): reconcile before deploying
---------------------------------------------------------------------------------------------------
INSERT #F (Section, CheckName, Obj, Owner, Expected, Actual, Status)
SELECT '2b Migration integrity', CASE WHEN x.Total = 0 THEN 'Migration (no schema objects)' WHEN x.Present < x.Total AND x.Present > 0 AND r.Rec = 0 THEN 'PARTIALLY APPLIED migration'
                                      WHEN x.Present < x.Total AND r.Rec = 1 THEN 'INCOMPLETE applied migration' ELSE 'Migration objects' END,
       e.MigrationId, e.MigrationId,
       CASE WHEN r.Rec = 1 THEN N'recorded; all ' + CAST(x.Total AS nvarchar(10)) + N' objects present' ELSE N'not recorded; 0 objects present (pending) ' END,
       CASE WHEN r.Rec = 1 THEN N'recorded; ' ELSE N'not recorded; ' END + CAST(x.Present AS nvarchar(10)) + N' of ' + CAST(x.Total AS nvarchar(10)) + N' objects present',
       CASE WHEN x.Total = 0 THEN CASE WHEN r.Rec = 1 THEN 'PASS' ELSE 'PENDING' END
            WHEN r.Rec = 1 THEN CASE WHEN x.Present = x.Total THEN 'PASS' ELSE 'FAIL' END
            ELSE CASE WHEN x.Present = 0 THEN 'PENDING' ELSE 'DRIFT' END END
FROM #ExpMig e
CROSS APPLY (SELECT CASE WHEN EXISTS (SELECT 1 FROM #Applied a WHERE a.MigrationId = e.MigrationId) THEN 1 ELSE 0 END AS Rec) r
CROSS APPLY (SELECT COUNT(*) AS Total, ISNULL(SUM(CASE WHEN f.Actual IN (N'missing', N'absent') THEN 0 ELSE 1 END), 0) AS Present
             FROM #F f WHERE f.Owner = e.MigrationId AND (f.Section LIKE '3 Schema/%' OR f.Section = '4 Triggers')) x;
SET @Done = @Done + 1;
END TRY
BEGIN CATCH
    SET @Err = @Err + 1;
    INSERT #F (Section, CheckName, Obj, Expected, Actual, Status)
    VALUES ('0 Script error', 'Section 2b did not complete', N'line ' + CAST(ERROR_LINE() AS nvarchar(20)) + N', error ' + CAST(ERROR_NUMBER() AS nvarchar(20)), N'section completes', LEFT(ERROR_MESSAGE(), 900), 'FAIL');
END CATCH;

BEGIN TRY
---------------------------------------------------------------------------------------------------
-- 7. NOT VERIFIABLE FROM THIS DATABASE  (listed so they are not forgotten)
---------------------------------------------------------------------------------------------------
INSERT #F (Section, CheckName, Obj, Expected, Actual, Status) VALUES
('7 Not in DB', 'Create_Handover_Users.sql', N'AspNetUsers / Employees / HO department', N'only if the Handover users are wanted in UAT', N'run the script in PREVIEW mode; this check does not guess e-mails', 'INFO'),
('7 Not in DB', 'ImportNewRequestTypes_UAT.sql', N'RequestTypes / Workflows', N'baseline already imported (UAT)', N'see section 6b: General request types; a full count comparison needs the workbook', 'INFO'),
('7 Not in DB', 'PROD-only scripts', N'Remediate_/Diagnose_/Backfill_/Execute_TigerCS_PROD_*', N'never run on UAT', N'PROD guard (server/database name) inside each script', 'INFO');
SET @Done = @Done + 1;
END TRY
BEGIN CATCH
    SET @Err = @Err + 1;
    INSERT #F (Section, CheckName, Obj, Expected, Actual, Status)
    VALUES ('0 Script error', 'Section 7 did not complete', N'line ' + CAST(ERROR_LINE() AS nvarchar(20)) + N', error ' + CAST(ERROR_NUMBER() AS nvarchar(20)), N'section completes', LEFT(ERROR_MESSAGE(), 900), 'FAIL');
END CATCH;

---------------------------------------------------------------------------------------------------
-- RESULTS
---------------------------------------------------------------------------------------------------
DECLARE @nFail int = (SELECT COUNT(*) FROM #F WHERE Status = 'FAIL');
DECLARE @nDrift int = (SELECT COUNT(*) FROM #F WHERE Status = 'DRIFT');
DECLARE @nPend int = (SELECT COUNT(*) FROM #F WHERE Status = 'PENDING' AND Section = '2b Migration integrity');
DECLARE @nPartial int = (SELECT COUNT(*) FROM #F WHERE Section = '2b Migration integrity' AND CheckName IN ('PARTIALLY APPLIED migration', 'INCOMPLETE applied migration'));
DECLARE @nWarn int = (SELECT COUNT(*) FROM #F WHERE Status = 'WARN');
DECLARE @Ready bit = CASE WHEN @Err = 0 AND @Done = 8 AND @nFail = 0 AND @nDrift = 0 THEN 1 ELSE 0 END;

-- RESULT 1: VERDICT
SELECT CASE WHEN @Err > 0 OR @Done < 8 THEN 'NOT READY: SCRIPT ERROR - the verification did not complete, results are unreliable (see section 0 Script error)'
            WHEN @Ready = 1 AND @nPend = 0 AND @nWarn = 0 THEN 'UP TO DATE: every migration is applied and every object verified'
            WHEN @Ready = 1 AND @nPend = 0 THEN 'UP TO DATE WITH WARNINGS: review WARN rows'
            WHEN @Ready = 1 AND @nWarn = 0 THEN 'READY TO DEPLOY: ' + CAST(@nPend AS varchar(10)) + ' pending migration(s), nothing else wrong'
            WHEN @Ready = 1 THEN 'READY TO DEPLOY WITH WARNINGS: ' + CAST(@nPend AS varchar(10)) + ' pending migration(s); review WARN rows'
            ELSE 'NOT READY: resolve FAIL / DRIFT rows first' END AS Verdict,
       @NApplied AS AppliedMigrations, (SELECT COUNT(*) FROM #ExpMig) AS MigrationsInMain, @nPend AS PendingMigrations,
       @nPartial AS PartialOrIncompleteMigrations, @Err AS ScriptErrors, @Done AS SectionsCompleted_of_8,
       (SELECT COUNT(*) FROM #F WHERE Status = 'PASS') AS [PASS], @nPend AS [PENDING_migrations], @nFail AS [FAIL], @nDrift AS [DRIFT], @nWarn AS [WARN],
       @LatestApplied AS LatestApplied;

-- RESULT 2: SUMMARY by section (PASS / PENDING / FAIL / DRIFT / WARN / INFO)
SELECT Section,
       SUM(CASE WHEN Status = 'PASS' THEN 1 ELSE 0 END)    AS [PASS],
       SUM(CASE WHEN Status = 'PENDING' THEN 1 ELSE 0 END) AS [PENDING],
       SUM(CASE WHEN Status = 'FAIL' THEN 1 ELSE 0 END)    AS [FAIL],
       SUM(CASE WHEN Status = 'DRIFT' THEN 1 ELSE 0 END)   AS [DRIFT],
       SUM(CASE WHEN Status = 'WARN' THEN 1 ELSE 0 END)    AS [WARN],
       SUM(CASE WHEN Status = 'INFO' THEN 1 ELSE 0 END)    AS [INFO]
FROM #F GROUP BY Section
UNION ALL
SELECT 'TOTAL',
       SUM(CASE WHEN Status = 'PASS' THEN 1 ELSE 0 END), SUM(CASE WHEN Status = 'PENDING' THEN 1 ELSE 0 END), SUM(CASE WHEN Status = 'FAIL' THEN 1 ELSE 0 END),
       SUM(CASE WHEN Status = 'DRIFT' THEN 1 ELSE 0 END), SUM(CASE WHEN Status = 'WARN' THEN 1 ELSE 0 END), SUM(CASE WHEN Status = 'INFO' THEN 1 ELSE 0 END)
FROM #F
ORDER BY 1;

-- RESULT 3: ACTION ITEMS (everything that is not PASS / INFO)
SELECT Section, CheckName, Obj, Owner, Expected, Actual, Status
FROM #F WHERE Status IN ('FAIL', 'DRIFT', 'WARN', 'PENDING')
ORDER BY CASE Status WHEN 'FAIL' THEN 1 WHEN 'DRIFT' THEN 2 WHEN 'WARN' THEN 3 ELSE 4 END, Section, Id;

-- RESULT 4: MIGRATION MAP (all migrations, one row each)
SELECT e.Ord, e.MigrationId, CASE WHEN a.MigrationId IS NULL THEN 'not recorded' ELSE 'recorded' END AS History, f.Actual AS ObjectState, f.Status
FROM #ExpMig e LEFT JOIN #Applied a ON a.MigrationId = e.MigrationId
LEFT JOIN #F f ON f.Section = '2b Migration integrity' AND f.Obj = e.MigrationId
ORDER BY e.Ord;

-- RESULT 5: DETAIL
SELECT Section, CheckName, Obj, Owner, Expected, Actual, Status FROM #F ORDER BY Id;
