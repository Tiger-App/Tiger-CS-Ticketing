/*
================================================================================
 Verify_TigerCS_PROD_PendingMigrations_ReadOnly.sql          (READ-ONLY - SELECT only, nothing is written)
 Database : TigerCS_Ticketing_Prod

 Which EF migrations of this release (43 in the repository at commit-time; regenerate if the release changes) are NOT yet recorded in
 dbo.__EFMigrationsHistory, and are the tables the historical backfill needs present?
 Run BEFORE deploying. Send the three result sets back; nothing here changes data or schema.

   1. Pending migrations   - repository migrations missing from the history table (applied, in order, by TigerCS-all-migrations.idempotent.sql).
   2. Unknown to this release - history rows this release does not contain (a NEWER or DIFFERENT build was deployed: stop and investigate).
   3. Backfill prerequisites - tables / columns the backfill script requires.
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
    (N'20261105094644_AddCustomerContactDetails');

SELECT r.MigrationId AS PendingMigration FROM @Repo r
WHERE NOT EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory h WHERE h.MigrationId = r.MigrationId) ORDER BY r.MigrationId;

SELECT h.MigrationId AS RecordedInProductionButUnknownToThisRelease, h.ProductVersion FROM dbo.__EFMigrationsHistory h
WHERE NOT EXISTS (SELECT 1 FROM @Repo r WHERE r.MigrationId = h.MigrationId) ORDER BY h.MigrationId;

SELECT v.Item, CASE WHEN v.Present = 1 THEN 'PRESENT' ELSE 'MISSING (deploy the migrations first)' END AS Status FROM (VALUES
    (N'dbo.DepartmentAssignmentRotations', CASE WHEN OBJECT_ID(N'dbo.DepartmentAssignmentRotations', N'U') IS NOT NULL THEN 1 ELSE 0 END),
    (N'dbo.TicketInteractionHandlers',     CASE WHEN OBJECT_ID(N'dbo.TicketInteractionHandlers', N'U') IS NOT NULL THEN 1 ELSE 0 END),
    (N'dbo.UserPermissionOverrides',       CASE WHEN OBJECT_ID(N'dbo.UserPermissionOverrides', N'U') IS NOT NULL THEN 1 ELSE 0 END),
    (N'dbo.CollectionsTowerAssignments (new in this release)', CASE WHEN OBJECT_ID(N'dbo.CollectionsTowerAssignments', N'U') IS NOT NULL THEN 1 ELSE 0 END),
    (N'dbo.CustomerContactDetails (read by BOTH the Customers directory and the Customer Profile)', CASE WHEN OBJECT_ID(N'dbo.CustomerContactDetails', N'U') IS NOT NULL THEN 1 ELSE 0 END),
    (N'dbo.Tickets.WorkflowTemplateId',    CASE WHEN COL_LENGTH(N'dbo.Tickets', N'WorkflowTemplateId') IS NOT NULL THEN 1 ELSE 0 END),
    (N'dbo.Tickets.CreatedByEmployeeId',   CASE WHEN COL_LENGTH(N'dbo.Tickets', N'CreatedByEmployeeId') IS NOT NULL THEN 1 ELSE 0 END),
    (N'dbo.TicketWorkflowEvents.TransferReasonCode', CASE WHEN COL_LENGTH(N'dbo.TicketWorkflowEvents', N'TransferReasonCode') IS NOT NULL THEN 1 ELSE 0 END),
    (N'Customer Services (CS) General category + request type', CASE WHEN EXISTS (SELECT 1 FROM dbo.Departments d JOIN dbo.Categories c ON c.DepartmentId = d.DepartmentId AND c.Name = N'General'
        JOIN dbo.RequestTypes r ON r.DepartmentId = d.DepartmentId AND r.Name = N'General' WHERE d.Code = N'CS') THEN 1 ELSE 0 END)
) v(Item, Present);
