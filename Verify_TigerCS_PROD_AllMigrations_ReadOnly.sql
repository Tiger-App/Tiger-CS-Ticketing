/*
================================================================================
 Verify_TigerCS_PROD_AllMigrations_ReadOnly.sql   (READ-ONLY - SELECT only, nothing is written)
 Database : TigerCS_Ticketing_Prod

 Run BEFORE and AFTER every deployment. Send the three result sets back; nothing here changes data or schema.

   1. Pending      - repository migrations NOT recorded in dbo.__EFMigrationsHistory (what still has to be deployed).
   2. Unknown      - history rows this release does not contain (a NEWER or DIFFERENT build was deployed: stop and investigate).
   3. Schema check - the tables / columns the newest migrations create, and whether each really exists. A migration can be recorded
                     as applied while its object is missing (or the reverse); this is what catches the missing-table incident.

 The migration list below is checked against the real migrations by a test (EveryMigrationIsListedInTheProductionVerifyScript), so it
 cannot go stale: adding a migration without updating this script fails the build.
================================================================================
*/
SET NOCOUNT ON;
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;
IF DB_NAME() <> N'TigerCS_Ticketing_Prod' THROW 50900, 'ABORTED: connect to TigerCS_Ticketing_Prod.', 1;

DECLARE @Repo TABLE (MigrationId nvarchar(150) PRIMARY KEY);
INSERT @Repo (MigrationId) VALUES
    (N'20260819072347_InitialIdentityAndAccess'),
    (N'20260820072044_AddCustomerVerification'),
    (N'20260820140000_AddTicketing'),
    (N'20260821090000_AddTicketOperations'),
    (N'20260821130000_AddSlaAndEscalation'),
    (N'20260822090000_AddNotificationsAndOutbox'),
    (N'20260825053743_AddIntakeRecordPhoneNumber'),
    (N'20260825061341_AddDepartmentCustomerLookupSource'),
    (N'20260825090047_AddDepartmentCustomerLookupSources'),
    (N'20260827090146_AddCrmBuyerLookupToTickets'),
    (N'20260901103309_AddExternalCustomerVerificationToTickets'),
    (N'20260903164723_AddWorkflowConfiguration'),
    (N'20260904075902_AddWorkflowAutomation'),
    (N'20260904082241_ReplaceInteractionContextWithTicketInteractions'),
    (N'20260904091143_AddApprovalWorkflow'),
    (N'20260908054944_AddWorkflowVersioning'),
    (N'20260909084839_AddChannels'),
    (N'20260910080219_AddGenesysIntegration'),
    (N'20260911105154_AddGenesysAgentMappingAndInteractionOwnership'),
    (N'20260912110119_AddExternalCustomerSnapshotToTickets'),
    (N'20260921133507_AddAgentHandoffTriggerAndConcurrency'),
    (N'20260928132618_AddGenesysScreenPopLaunches'),
    (N'20261005072511_AddCollectionsReminders'),
    (N'20261007093644_AddChatbotInactivityAndCrmDocumentCopies'),
    (N'20261007103153_AddResolutionClosedForCustomerInactivity'),
    (N'20261007180009_AddCustomerOtpVerification'),
    (N'20261008184722_AddSlaPauseAndPriorityDowngrade'),
    (N'20261009065134_AddCollectionsReviewAndGenesysDispatch'),
    (N'20261009074521_AddDispatchSuppressionAndVoiceEligibility'),
    (N'20261009102002_AddSmsChannelToCustomerOtp'),
    (N'20261012124810_AddAgentDeskDocumentDelivery'),
    (N'20261020090000_AddGenesysCampaignToDispatchBatch'),
    (N'20261020105026_AddLastCustomerInteractionToTicketInteractions'),
    (N'20261021090000_AddTowerToReviewRecords'),
    (N'20261021120000_AddAdminEmailToCollectionsDispatch'),
    (N'20261022081031_AddCustomerSatisfactionReviews'),
    (N'20261022090000_AddGenesysServiceRequestToTicketInteractions'),
    (N'20261030061255_AddTicketInteractionHandlers'),
    (N'20261030062642_AddTicketTransferTracking'),
    (N'20261030073857_AddTicketCreatedBy'),
    (N'20261031094748_AddDepartmentAssignmentRotation'),
    (N'20261102065459_AddUserPermissionOverrides'),
    (N'20261104130835_AddCollectionsTowerAssignments'),
    (N'20261105094644_AddCustomerContactDetails'),
    (N'20261106044011_AddDirectSendInstalments'),
    (N'20261109074858_AddCollectionsGenesysDeliveryStats'),
    (N'20261109120816_AddCollectionsExceptionInclusions'),
    (N'20261109123253_AddManualGenesysDispatchItemFields'),
    (N'20261110070152_AddUserTransferDepartments'),
    (N'20261111073632_AddTicketOriginalHumanAgent'),
    (N'20261111132429_AddOriginalAgentWorkflowCutoff'),
    (N'20261112065853_AddGenesysAssignmentHistorySource'),
    (N'20261112071745_AddTicketAssignmentSupersededAt'),
    (N'20261117101035_AddQueueingIntegration'),
    (N'20261118120000_AddCustomerProfileOverrides'),
    (N'20261118124858_AddTicketAiAgentOwner'),
    (N'20261119081103_AddCustomerProfileAuditAndProperties'),
    (N'20261119120249_AddAssignmentHistoryPreviousDepartment'),
    (N'20261120074059_AddInteractionEndReceivedAt'),
    (N'20261124071552_AddEmployeeReviews');

-- 1. Pending: in the repository, not applied in production
SELECT r.MigrationId AS PendingMigration FROM @Repo r
WHERE NOT EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory h WHERE h.MigrationId = r.MigrationId)
ORDER BY r.MigrationId;

-- 2. Unknown: applied in production, not in this repository (stop and investigate if any rows)
SELECT h.MigrationId AS UnknownToThisRelease FROM dbo.__EFMigrationsHistory h
WHERE NOT EXISTS (SELECT 1 FROM @Repo r WHERE r.MigrationId = h.MigrationId) ORDER BY h.MigrationId;

-- 3. Schema objects created by the newest migrations (Present = 0 means MISSING; list the 0 rows first)
SELECT x.Migration, x.Object,
       CASE WHEN x.Col IS NULL THEN CASE WHEN OBJECT_ID(N'dbo.' + x.Tbl, N'U') IS NOT NULL THEN 1 ELSE 0 END
            ELSE CASE WHEN COL_LENGTH(N'dbo.' + x.Tbl, x.Col) IS NOT NULL THEN 1 ELSE 0 END END AS Present
FROM (VALUES
    (N'AddCollectionsTowerAssignments',        N'table CollectionsTowerAssignments',        N'CollectionsTowerAssignments', NULL),
    (N'AddCollectionsTowerAssignments',        N'table TicketAssignmentHistory',            N'TicketAssignmentHistory', NULL),
    (N'AddCollectionsTowerAssignments',        N'table TicketCollectionsUnits',             N'TicketCollectionsUnits', NULL),
    (N'AddCustomerContactDetails',             N'table CustomerContactDetails',             N'CustomerContactDetails', NULL),
    (N'AddDirectSendInstalments',              N'table CollectionsDispatches',              N'CollectionsDispatches', NULL),
    (N'AddCollectionsGenesysDeliveryStats',    N'table CollectionsGenesysSyncStates',       N'CollectionsGenesysSyncStates', NULL),
    (N'AddCollectionsExceptionInclusions',     N'table CollectionsExceptionInclusions',     N'CollectionsExceptionInclusions', NULL),
    (N'AddUserTransferDepartments',            N'table UserTransferDepartments',            N'UserTransferDepartments', NULL),
    (N'AddQueueingIntegration',                N'table QueueingEvents',                     N'QueueingEvents', NULL),
    (N'AddQueueingIntegration',                N'table QueueingTicketLinks',                N'QueueingTicketLinks', NULL),
    (N'AddQueueingIntegration',                N'table QueueingCompanyMappings',            N'QueueingCompanyMappings', NULL),
    (N'AddQueueingIntegration',                N'table QueueingTicketTypeMappings',         N'QueueingTicketTypeMappings', NULL),
    (N'AddQueueingIntegration',                N'table QueueingUserMappings',               N'QueueingUserMappings', NULL),
    (N'AddCustomerProfileOverrides',           N'table CustomerProfileOverrides',           N'CustomerProfileOverrides', NULL),
    (N'AddTicketAiAgentOwner',                 N'column Tickets.AiAgentOwnerEmployeeId',    N'Tickets', N'AiAgentOwnerEmployeeId'),
    (N'AddCustomerProfileAuditAndProperties',  N'table CustomerProfileChanges',             N'CustomerProfileChanges', NULL),
    (N'AddCustomerProfileAuditAndProperties',  N'table CustomerPropertyAssociations',       N'CustomerPropertyAssociations', NULL),
    (N'AddCustomerProfileAuditAndProperties',  N'column CustomerProfileOverrides.CustomerProfileId', N'CustomerProfileOverrides', N'CustomerProfileId'),
    (N'AddCustomerProfileAuditAndProperties',  N'column CustomerProfileOverrides.FullName',  N'CustomerProfileOverrides', N'FullName'),
    (N'AddCustomerProfileAuditAndProperties',  N'column CustomerProfileOverrides.Email',     N'CustomerProfileOverrides', N'Email'),
    (N'AddCustomerProfileAuditAndProperties',  N'column CustomerProfileOverrides.LandlineNumber', N'CustomerProfileOverrides', N'LandlineNumber'),
    (N'AddCustomerProfileAuditAndProperties',  N'column CustomerProfileOverrides.AdditionalPhones', N'CustomerProfileOverrides', N'AdditionalPhones'),
    (N'AddCustomerProfileAuditAndProperties',  N'column CustomerProfileOverrides.Nationality', N'CustomerProfileOverrides', N'Nationality'),
    (N'AddCustomerProfileAuditAndProperties',  N'column CustomerProfileOverrides.Country',   N'CustomerProfileOverrides', N'Country'),
    (N'AddCustomerProfileAuditAndProperties',  N'column CustomerProfileOverrides.Emirate',   N'CustomerProfileOverrides', N'Emirate'),
    (N'AddCustomerProfileAuditAndProperties',  N'column CustomerProfileOverrides.Location',  N'CustomerProfileOverrides', N'Location'),
    (N'AddCustomerProfileAuditAndProperties',  N'column CustomerProfileOverrides.PreferredLanguage', N'CustomerProfileOverrides', N'PreferredLanguage'),
    (N'AddAssignmentHistoryPreviousDepartment', N'column TicketAssignmentHistory.PreviousDepartmentId', N'TicketAssignmentHistory', N'PreviousDepartmentId'),
    (N'AddInteractionEndReceivedAt', N'column TicketInteractions.EndReceivedAtUtc', N'TicketInteractions', N'EndReceivedAtUtc'),
    (N'AddEmployeeReviews',        N'table EmployeeReviewDevices',              N'EmployeeReviewDevices', NULL),
    (N'AddEmployeeReviews',        N'table EmployeeReviews',                    N'EmployeeReviews', NULL)
) x(Migration, Object, Tbl, Col)
ORDER BY Present, x.Migration;

-- 4. The append-only trigger on the change history (expect 1 row, is_disabled = 0)
SELECT t.name AS TriggerName, t.is_disabled FROM sys.triggers t
WHERE t.name = N'TR_CustomerProfileChanges_AppendOnly' AND t.parent_id = OBJECT_ID(N'dbo.CustomerProfileChanges');
