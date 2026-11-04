using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace TigerCS.Tests.IdentityAndAccess.Integration;

/// <summary>
/// Keeps requirement 6 of the authorization correction ("every currently
/// protected API endpoint/action") honest over time.
///
/// <para>
/// <c>SystemAdministratorEndpointAuthorizationTests</c> proves the override
/// endpoint by endpoint, but a hand-written list of endpoints silently stops
/// meaning "every" the moment someone adds a controller action. This test
/// reads the host's real <see cref="EndpointDataSource"/> — the routes
/// ASP.NET Core actually mapped — and fails if the protected surface differs
/// from the inventory below. A new endpoint is then a failing test with a
/// message naming it, not an untested authorization gap.
/// </para>
/// </summary>
public class ProtectedEndpointInventoryTests : IClassFixture<TigerCsApiFactory>
{
    private readonly TigerCsApiFactory _factory;

    public ProtectedEndpointInventoryTests(TigerCsApiFactory factory) => _factory = factory;

    /// <summary>
    /// Every protected endpoint, each with the test that proves a System
    /// Administrator JWT is authorized for it. Anonymous endpoints
    /// (<c>GET /health</c>, <c>POST /api/auth/login</c>) are excluded by the
    /// <c>[AllowAnonymous]</c> filter below, not by this list.
    /// </summary>
    private static readonly Dictionary<string, string> CoveredByTest = new()
    {
        ["POST /api/auth/logout"] = nameof(SystemAdministratorEndpointAuthorizationTests.Logout_Returns204),
        ["POST /api/auth/change-password"] = nameof(PasswordManagementEndpointsTests.ChangePassword_Success_Returns204_RejectsTheCallersOwnTokenAfterwards_AndAFreshLoginWorks),
        ["GET /api/users/me"] = nameof(SystemAdministratorEndpointAuthorizationTests.GetOwnProfile_Returns200),
        ["GET /api/users/assignable"] = nameof(SystemAdministratorEndpointAuthorizationTests.ListAssignableUsers_Returns200),
        ["PATCH /api/users/{employeeId:guid}/activation"] = nameof(SystemAdministratorEndpointAuthorizationTests.SetUserActivation_Returns200),
        ["GET /api/roles"] = nameof(SystemAdministratorEndpointAuthorizationTests.GetRoleCatalog_Returns200),
        ["GET /api/departments"] = nameof(SystemAdministratorEndpointAuthorizationTests.ListDepartments_Returns200),
        ["GET /api/departments/{departmentId:int}/users"] = nameof(SystemAdministratorEndpointAuthorizationTests.ListDepartmentUsers_Returns200),
        ["GET /api/categories"] = nameof(SystemAdministratorEndpointAuthorizationTests.GetCategories_Returns200),
        ["GET /api/crm/units/{crmUnitId}"] = nameof(SystemAdministratorEndpointAuthorizationTests.GetCrmUnit_Returns200),
        ["GET /api/crm/units/search"] = nameof(SystemAdministratorEndpointAuthorizationTests.SearchCrmUnits_Returns200),
        ["GET /api/crm/units/{crmUnitId}/contacts"] = nameof(SystemAdministratorEndpointAuthorizationTests.GetCrmUnitContacts_Returns200),
        ["GET /api/crm/buyers"] = nameof(SystemAdministratorEndpointAuthorizationTests.GetBuyerByPhone_Returns200),
        ["POST /api/verification-sessions"] = nameof(SystemAdministratorEndpointAuthorizationTests.CreateVerificationSession_Returns201),
        ["GET /api/verification-sessions/{verificationSessionId:guid}"] = nameof(SystemAdministratorEndpointAuthorizationTests.GetVerificationSession_Returns200),
        ["POST /api/intake-records"] = nameof(SystemAdministratorEndpointAuthorizationTests.CreateIntakeRecord_WithSystemAdministratorJwt_Returns201),
        ["GET /api/intake-records/{intakeRecordId:long}/customer-lookup"] = nameof(SystemAdministratorEndpointAuthorizationTests.SearchCustomerLookup_Returns200),
        ["POST /api/tickets"] = nameof(SystemAdministratorEndpointAuthorizationTests.CreateTicket_WithCustomerMatch_Returns201),
        ["GET /api/tickets"] = nameof(SystemAdministratorEndpointAuthorizationTests.GetTicketQueue_Returns200),
        ["GET /api/tickets/{ticketId:long}"] = nameof(SystemAdministratorEndpointAuthorizationTests.GetTicketDetail_ForATicketInADepartmentTheAdministratorDoesNotBelongTo_Returns200),
        ["POST /api/tickets/{ticketId:long}/assignment"] = nameof(SystemAdministratorEndpointAuthorizationTests.AssignTicket_Returns200),
        ["GET /api/admin/cs-agent-backfill/preview"] = nameof(SystemAdministratorEndpointAuthorizationTests.CsAgentBackfill_PreviewAndDryRun_Return200_AndWriteNothing),
        ["POST /api/admin/cs-agent-backfill/run"] = nameof(SystemAdministratorEndpointAuthorizationTests.CsAgentBackfill_PreviewAndDryRun_Return200_AndWriteNothing),
        ["GET /api/admin/default-classification-backfill/preview"] = nameof(SystemAdministratorEndpointAuthorizationTests.DefaultClassificationBackfill_PreviewAndDryRun_Return200_AndALiveRunIsRefusedUnlessEnabled),
        ["POST /api/admin/default-classification-backfill/run"] = nameof(SystemAdministratorEndpointAuthorizationTests.DefaultClassificationBackfill_PreviewAndDryRun_Return200_AndALiveRunIsRefusedUnlessEnabled),
        ["POST /api/tickets/{ticketId:long}/assignment/retry"] = nameof(SystemAdministratorEndpointAuthorizationTests.RetryCsAgentAssignment_Returns200_AndIsIdempotentForAnAlreadyAssignedTicket),
        ["POST /api/tickets/{ticketId:long}/ownership"] = nameof(SystemAdministratorEndpointAuthorizationTests.TakeOwnership_IsRefused_BecauseTheCallerMustThemselvesBeACallCenterAgent_AndTheTicketIsUntouched),
        ["POST /api/tickets/{ticketId:long}/transfer"] = nameof(SystemAdministratorEndpointAuthorizationTests.TransferTicket_Returns200),
        ["POST /api/tickets/{ticketId:long}/status"] = nameof(SystemAdministratorEndpointAuthorizationTests.ChangeTicketStatus_Returns200),
        ["POST /api/tickets/{ticketId:long}/resolution"] = nameof(SystemAdministratorEndpointAuthorizationTests.ResolveAndCloseTicket_BothReturn200),
        ["POST /api/tickets/{ticketId:long}/close"] = nameof(SystemAdministratorEndpointAuthorizationTests.ResolveAndCloseTicket_BothReturn200),
        ["POST /api/tickets/{ticketId:long}/classification"] = nameof(SystemAdministratorEndpointAuthorizationTests.GenesysEndpoints_AuthorizedThroughTheOverride),
        ["POST /api/tickets/{ticketId:long}/reopen"] = nameof(SystemAdministratorEndpointAuthorizationTests.ReopenTicket_Returns200),
        ["GET /api/tickets/{ticketId:long}/history"] = nameof(SystemAdministratorEndpointAuthorizationTests.GetTicketLifecycleHistory_Returns200),
        ["POST /api/tickets/{ticketId:long}/reconciliation"] = nameof(SystemAdministratorEndpointAuthorizationTests.ReconcileUnverifiedTicket_Returns200),
        ["GET /api/tickets/{ticketId:long}/approvals"] = nameof(SystemAdministratorEndpointAuthorizationTests.ApprovalWorkflowEndpoints_AuthorizedThroughTheOverride),
        ["POST /api/tickets/{ticketId:long}/approvals"] = nameof(SystemAdministratorEndpointAuthorizationTests.ApprovalWorkflowEndpoints_AuthorizedThroughTheOverride),
        ["POST /api/tickets/{ticketId:long}/approvals/{approvalId:long}/decision"] = nameof(SystemAdministratorEndpointAuthorizationTests.ApprovalWorkflowEndpoints_AuthorizedThroughTheOverride),
        ["POST /api/tickets/{ticketId:long}/approvals/{approvalId:long}/cancellation"] = nameof(SystemAdministratorEndpointAuthorizationTests.ApprovalWorkflowEndpoints_AuthorizedThroughTheOverride),
        ["POST /api/tickets/{ticketId:long}/workflow-events"] = nameof(SystemAdministratorEndpointAuthorizationTests.ApprovalWorkflowEndpoints_AuthorizedThroughTheOverride),
        ["POST /api/tickets/{ticketId:long}/notes"] = nameof(SystemAdministratorEndpointAuthorizationTests.AddAndListTicketNotes_Return201And200),
        ["GET /api/tickets/{ticketId:long}/notes"] = nameof(SystemAdministratorEndpointAuthorizationTests.AddAndListTicketNotes_Return201And200),
        ["GET /api/tickets/{ticketId:long}/customer-history"] = nameof(SystemAdministratorEndpointAuthorizationTests.GetTicketCustomerHistory_Returns200),
        ["GET /api/tickets/{ticketId:long}/customer-profile"] = nameof(SystemAdministratorEndpointAuthorizationTests.GetTicketCustomerProfile_Returns200),

        // Genesys integration phase 1 — the inbound boundary and the ticket's
        // conversation-history read.
        // The three Genesys contracts.
        ["POST /api/genesys/tickets"] = nameof(SystemAdministratorEndpointAuthorizationTests.GenesysEndpoints_AuthorizedThroughTheOverride),
        ["PATCH /api/genesys/tickets/{ticketId:long}"] = nameof(SystemAdministratorEndpointAuthorizationTests.GenesysEndpoints_AuthorizedThroughTheOverride),
        ["POST /api/genesys/documents/send-copy"] = nameof(SystemAdministratorEndpointAuthorizationTests.GenesysDocumentSendCopy_AuthorizedThroughTheOverride),
        ["POST /api/genesys/verification/buyer-lookup"] = nameof(SystemAdministratorEndpointAuthorizationTests.GenesysBuyerVerification_AuthorizedThroughTheOverride),
        ["POST /api/genesys/verification/otp/send"] = nameof(SystemAdministratorEndpointAuthorizationTests.GenesysBuyerVerification_AuthorizedThroughTheOverride),
        ["POST /api/genesys/verification/otp/resend"] = nameof(SystemAdministratorEndpointAuthorizationTests.GenesysBuyerVerification_AuthorizedThroughTheOverride),
        ["POST /api/genesys/verification/otp/verify"] = nameof(SystemAdministratorEndpointAuthorizationTests.GenesysBuyerVerification_AuthorizedThroughTheOverride),
        ["GET /api/genesys/customers/lookup"] = nameof(SystemAdministratorEndpointAuthorizationTests.GenesysCustomerLookup_AuthorizedThroughTheOverride),
        ["POST /api/genesys/customers/unit-details"] = nameof(SystemAdministratorEndpointAuthorizationTests.GenesysCustomerUnitDetails_AuthorizedThroughTheOverride),
        // Genesys agent identity mapping — the strict agent-action endpoint.
        ["POST /api/genesys/agent-context"] = nameof(GenesysIntegration.Integration.GenesysAgentMappingEndpointsTests.MappedAgent_ResolvesToTheTicketingUser_AndRecordsInteractionOwnership),
        // Genesys Secure Screen Pop — issuing a launch URL (redeeming it is anonymous, below).
        ["POST /api/genesys/screen-pop"] = nameof(GenesysIntegration.Integration.GenesysScreenPopEndpointsTests.Issue_ReturnsAOneHourLaunchUrl_AndStoresOnlyTheHash),
        ["GET /api/collections/customer-lookup/payment-summary"] = nameof(Collections.Edsm.EdsmPaymentSummaryApiTests.LookupFinancialsAndHistory_AreAuthorizedWithoutAnyTicket_ThroughTheRealHost),
        ["GET /api/customers/lookup/ticket-history"] = nameof(Collections.Edsm.EdsmPaymentSummaryApiTests.LookupFinancialsAndHistory_AreAuthorizedWithoutAnyTicket_ThroughTheRealHost),
        // Collections — the explicit financial permissions, with the System Administrator override, on both prefixes.
        ["GET /api/collections/receivables/customers"] = nameof(Collections.Integration.PactReceivablesEndpointTests.SystemAdministratorCanReadThroughCentralOverride_WithNoCustomerTicket),
        ["GET /api/collections/receivables/instalments"] = nameof(Collections.Integration.PactReceivablesEndpointTests.InstalmentsEndpoint_IsAuthorizedForFinancialReaders_AndPassesEveryFilterToTheService),
        ["GET /api/collections/receivables/towers"] = nameof(Collections.Integration.PactReceivablesEndpointTests.TowersEndpoint_IsAuthorizedForFinancialReaders_AndDeniedToReportingUsers),
        ["POST /api/collections/receivables/coverage/load"] = nameof(Collections.Integration.PactReceivablesEndpointTests.CoverageLoadEndpoint_StartsABackgroundLoad_OnlyForAuthorizedCallers_AndOnlyWhenNeeded),
        ["GET /api/collections/receivables/snapshot/status"] = nameof(Collections.Integration.ReconciliationEndpointTests.SnapshotStatusEndpoint_IsAuthorizedForFinancialReaders_AndReadsOnlyTheStatus),
        ["GET /api/collections/reconciliation"] = nameof(Collections.Integration.ReconciliationEndpointTests.ReconciliationEndpoints_AreAuthorized_ReadingStartsNothing_AndARunNeedsAnExplicitPost),
        ["GET /api/collections/reconciliation/runs/{runId:guid}"] = nameof(Collections.Integration.ReconciliationEndpointTests.ReconciliationEndpoints_AreAuthorized_ReadingStartsNothing_AndARunNeedsAnExplicitPost),
        ["POST /api/collections/reconciliation/run"] = nameof(Collections.Integration.ReconciliationEndpointTests.ReconciliationEndpoints_AreAuthorized_ReadingStartsNothing_AndARunNeedsAnExplicitPost),
        ["GET /api/collections/tower-assignments"] = nameof(Collections.Integration.CollectionsTowerAssignmentEndpointTests.TowerAssignmentEndpoints_AreAuthenticated_OpenToTheSystemAdministrator_AndRefuseAnEmployeeOutsideCollections),
        ["GET /api/collections/tower-assignments/history"] = nameof(Collections.Integration.CollectionsTowerAssignmentEndpointTests.TowerAssignmentEndpoints_AreAuthenticated_OpenToTheSystemAdministrator_AndRefuseAnEmployeeOutsideCollections),
        ["POST /api/collections/tower-assignments/activate"] = nameof(Collections.Integration.CollectionsTowerAssignmentEndpointTests.TowerAssignmentEndpoints_AreAuthenticated_OpenToTheSystemAdministrator_AndRefuseAnEmployeeOutsideCollections),
        ["POST /api/collections/tower-assignments/copy-previous"] = nameof(Collections.Integration.CollectionsTowerAssignmentEndpointTests.TowerAssignmentEndpoints_AreAuthenticated_OpenToTheSystemAdministrator_AndRefuseAnEmployeeOutsideCollections),
        ["PUT /api/collections/tower-assignments/employees/{employeeId:guid}"] = nameof(Collections.Integration.CollectionsTowerAssignmentEndpointTests.TowerAssignmentEndpoints_AreAuthenticated_OpenToTheSystemAdministrator_AndRefuseAnEmployeeOutsideCollections),
        ["GET /api/tickets/{ticketId:long}/assignment-history"] = nameof(Collections.Integration.CollectionsTowerAssignmentEndpointTests.TicketAssignmentHistoryEndpoint_RequiresALogin_AndIsReachableByTheSystemAdministrator),
        ["GET /api/collections/campaigns/preview"] = nameof(Collections.Integration.CollectionsCampaignEndpointsTests.SystemAdministratorCanPreviewAndExportReview_ThroughCentralOverride),
        ["GET /api/collections/campaigns/export"] = nameof(Collections.Integration.CollectionsCampaignEndpointsTests.SystemAdministratorCanPreviewAndExportReview_ThroughCentralOverride),
        ["GET /api/collections/units/payment-summary"] = nameof(Collections.Integration.CollectionsUnitsEndpointsTests.SystemAdministrator_IsAuthorized_OnTheUnitSummaryAndCrmStatus_ThroughCentralOverride),
        ["GET /api/collections/leasing/payment-summary"] = nameof(Collections.Integration.CollectionsUnitsEndpointsTests.SystemAdministrator_IsAuthorized_OnTheUnitSummaryAndCrmStatus_ThroughCentralOverride),
        ["GET /api/collections/customers/lookup"] = nameof(Collections.Integration.CollectionsUnitsEndpointsTests.SystemAdministrator_IsAuthorized_OnTheUnitSummaryAndCrmStatus_ThroughCentralOverride),
        ["GET /api/collections/customers/crm/{crmCustomerId:int}/units"] = nameof(Collections.Integration.CollectionsUnitsEndpointsTests.SystemAdministrator_IsAuthorized_OnTheUnitSummaryAndCrmStatus_ThroughCentralOverride),
        ["GET /api/collections/crm-owners/status"] = nameof(Collections.Integration.CollectionsUnitsEndpointsTests.SystemAdministrator_IsAuthorized_OnTheUnitSummaryAndCrmStatus_ThroughCentralOverride),
        ["GET /api/collections/review/runs/current"] = nameof(Collections.Integration.CollectionsReviewEndpointsTests.SystemAdministrator_IsAuthorizedOnEveryReviewRoute_ThroughTheOverride),
        ["GET /api/collections/review/runs/{runId:long}"] = nameof(Collections.Integration.CollectionsReviewEndpointsTests.SystemAdministrator_IsAuthorizedOnEveryReviewRoute_ThroughTheOverride),
        ["POST /api/collections/review/dispatches/{dispatchId:guid}/cancel"] = nameof(Collections.Integration.CollectionsReviewEndpointsTests.SystemAdministrator_IsAuthorizedOnEveryReviewRoute_ThroughTheOverride),
        ["POST /api/collections/review/dispatches/{dispatchId:guid}/admin-email/retry"] = nameof(Collections.Integration.CollectionsReviewEndpointsTests.SystemAdministrator_IsAuthorizedOnEveryReviewRoute_ThroughTheOverride),
        ["GET /api/collections/review/overlaps"] = nameof(Collections.Integration.CollectionsReviewEndpointsTests.SystemAdministrator_IsAuthorizedOnEveryReviewRoute_ThroughTheOverride),
        ["GET /api/collections/review/dispatches/{dispatchId:guid}/contacts"] = nameof(Collections.Integration.CollectionsReviewEndpointsTests.SystemAdministrator_IsAuthorizedOnEveryReviewRoute_ThroughTheOverride),
        ["POST /api/collections/review/suppression/sweep"] = nameof(Collections.Integration.CollectionsReviewEndpointsTests.SystemAdministrator_IsAuthorizedOnEveryReviewRoute_ThroughTheOverride),
        ["POST /api/collections/review/refresh"] = nameof(Collections.Integration.CollectionsReviewEndpointsTests.SystemAdministrator_IsAuthorizedOnEveryReviewRoute_ThroughTheOverride),
        ["GET /api/collections/review/records"] = nameof(Collections.Integration.CollectionsReviewEndpointsTests.SystemAdministrator_IsAuthorizedOnEveryReviewRoute_ThroughTheOverride),
        ["GET /api/collections/review/reasons"] = nameof(Collections.Integration.CollectionsReviewEndpointsTests.SystemAdministrator_IsAuthorizedOnEveryReviewRoute_ThroughTheOverride),
        ["POST /api/collections/review/selection/summary"] = nameof(Collections.Integration.CollectionsReviewEndpointsTests.SystemAdministrator_IsAuthorizedOnEveryReviewRoute_ThroughTheOverride),
        ["POST /api/collections/review/dispatches"] = nameof(Collections.Integration.CollectionsReviewEndpointsTests.SystemAdministrator_IsAuthorizedOnEveryReviewRoute_ThroughTheOverride),
        ["GET /api/collections/review/dispatches"] = nameof(Collections.Integration.CollectionsReviewEndpointsTests.SystemAdministrator_IsAuthorizedOnEveryReviewRoute_ThroughTheOverride),
        ["GET /api/collections/review/dispatches/{dispatchId:guid}"] = nameof(Collections.Integration.CollectionsReviewEndpointsTests.SystemAdministrator_IsAuthorizedOnEveryReviewRoute_ThroughTheOverride),
        ["POST /api/collections/review/dispatches/{dispatchId:guid}/batches/{batchId:long}/reconcile"] = nameof(Collections.Integration.CollectionsReviewEndpointsTests.SystemAdministrator_IsAuthorizedOnEveryReviewRoute_ThroughTheOverride),
        ["GET /api/genesys/collections/customers/{crmCustomerId}/outstanding"] = nameof(Collections.Integration.CollectionsEndpointsTests.SystemAdministrator_IsAuthorizedOnEveryCollectionsRoute_ThroughTheOverride),
        ["GET /api/genesys/collections/customers/{crmCustomerId}/payments"] = nameof(Collections.Integration.CollectionsEndpointsTests.SystemAdministrator_IsAuthorizedOnEveryCollectionsRoute_ThroughTheOverride),
        ["GET /api/genesys/collections/reminders/candidates"] = nameof(Collections.Integration.CollectionsEndpointsTests.SystemAdministrator_IsAuthorizedOnEveryCollectionsRoute_ThroughTheOverride),
        ["POST /api/genesys/collections/reminders"] = nameof(Collections.Integration.CollectionsEndpointsTests.SystemAdministrator_IsAuthorizedOnEveryCollectionsRoute_ThroughTheOverride),
        ["POST /api/genesys/collections/reminders/{reminderId}/outcomes"] = nameof(Collections.Integration.CollectionsEndpointsTests.SystemAdministrator_IsAuthorizedOnEveryCollectionsRoute_ThroughTheOverride),
        ["GET /api/genesys/collections/customers/{crmCustomerId}/reminders"] = nameof(Collections.Integration.CollectionsEndpointsTests.SystemAdministrator_IsAuthorizedOnEveryCollectionsRoute_ThroughTheOverride),
        ["GET /api/collections/customers/{crmCustomerId}/outstanding"] = nameof(Collections.Integration.CollectionsEndpointsTests.SystemAdministrator_IsAuthorizedOnEveryCollectionsRoute_ThroughTheOverride),
        ["GET /api/collections/customers/{crmCustomerId}/payments"] = nameof(Collections.Integration.CollectionsEndpointsTests.SystemAdministrator_IsAuthorizedOnEveryCollectionsRoute_ThroughTheOverride),
        ["GET /api/collections/reminders/candidates"] = nameof(Collections.Integration.CollectionsEndpointsTests.SystemAdministrator_IsAuthorizedOnEveryCollectionsRoute_ThroughTheOverride),
        ["POST /api/collections/reminders"] = nameof(Collections.Integration.CollectionsEndpointsTests.SystemAdministrator_IsAuthorizedOnEveryCollectionsRoute_ThroughTheOverride),
        ["POST /api/collections/reminders/{reminderId}/outcomes"] = nameof(Collections.Integration.CollectionsEndpointsTests.SystemAdministrator_IsAuthorizedOnEveryCollectionsRoute_ThroughTheOverride),
        ["GET /api/collections/customers/{crmCustomerId}/reminders"] = nameof(Collections.Integration.CollectionsEndpointsTests.SystemAdministrator_IsAuthorizedOnEveryCollectionsRoute_ThroughTheOverride),
        ["GET /api/collections/customers/by-key/{customerKey}/payment-summary"] = nameof(Collections.Integration.CollectionsEndpointsTests.SystemAdministrator_IsAuthorizedOnEveryCollectionsRoute_ThroughTheOverride),
        ["GET /api/collections/customers/by-key/{customerKey}/payment-transactions"] = nameof(Collections.Integration.CollectionsEndpointsTests.SystemAdministrator_IsAuthorizedOnEveryCollectionsRoute_ThroughTheOverride),
        ["GET /api/genesys/collections/customers/by-key/{customerKey}/payment-summary"] = nameof(Collections.Integration.CollectionsEndpointsTests.SystemAdministrator_IsAuthorizedOnEveryCollectionsRoute_ThroughTheOverride),
        ["GET /api/genesys/collections/customers/by-key/{customerKey}/payment-transactions"] = nameof(Collections.Integration.CollectionsEndpointsTests.SystemAdministrator_IsAuthorizedOnEveryCollectionsRoute_ThroughTheOverride),
        ["GET /api/pending-customer-interactions"] = nameof(SystemAdministratorEndpointAuthorizationTests.PendingCustomerInteractions_AuthorizedThroughTheOverride),
        ["POST /api/pending-customer-interactions/{handoffId:long}/start"] = nameof(SystemAdministratorEndpointAuthorizationTests.PendingCustomerInteractions_AuthorizedThroughTheOverride),
        ["POST /api/pending-customer-interactions/{handoffId:long}/complete"] = nameof(SystemAdministratorEndpointAuthorizationTests.PendingCustomerInteractions_AuthorizedThroughTheOverride),
        ["POST /api/pending-customer-interactions/{handoffId:long}/cancel"] = nameof(SystemAdministratorEndpointAuthorizationTests.PendingCustomerInteractions_AuthorizedThroughTheOverride),
        ["GET /api/tickets/{ticketId:long}/interactions"] = nameof(SystemAdministratorEndpointAuthorizationTests.GetTicketInteractions_Returns200),
        ["GET /api/tickets/{ticketId:long}/activity-timeline"] = nameof(SystemAdministratorEndpointAuthorizationTests.GetTicketActivityTimeline_Returns200),
        ["GET /api/tickets/{ticketId:long}/reviews"] = nameof(SystemAdministratorEndpointAuthorizationTests.GetTicketReviews_Returns200_WithNoTokens),
        ["GET /api/admin/sla/configuration"] = nameof(Administration.Integration.AdministrationEndpointsTests.Sla_Configuration_IsReadableByTheAdministrator),
        ["GET /api/admin/genesys/queue-mappings"] = nameof(Administration.Integration.AdministrationEndpointsTests.GenesysRouting_QueueMappings_ThroughTheRealHost),
        ["POST /api/admin/genesys/queue-mappings"] = nameof(Administration.Integration.AdministrationEndpointsTests.GenesysRouting_QueueMappings_ThroughTheRealHost),
        ["PUT /api/admin/genesys/queue-mappings/{genesysQueueMappingId:int}"] = nameof(Administration.Integration.AdministrationEndpointsTests.GenesysRouting_QueueMappings_ThroughTheRealHost),


        ["GET /api/customers/crm/{crmCustomerId:int}/ticket-history"] = nameof(SystemAdministratorEndpointAuthorizationTests.GetCrmCustomerTicketHistory_Returns200),
        ["GET /api/customers/external/{source}/{externalCustomerId}/ticket-history"] = nameof(SystemAdministratorEndpointAuthorizationTests.GetExternalCustomerHistory_Returns200),
        ["GET /api/customers/search"] = nameof(SystemAdministratorEndpointAuthorizationTests.SearchCustomers_Returns200),
        ["GET /api/customers"] = nameof(SystemAdministratorEndpointAuthorizationTests.ListCustomers_Returns200),
        ["GET /api/customers/profile/{customerKey}"] = nameof(SystemAdministratorEndpointAuthorizationTests.GetCustomerProfileByKey_Returns200),
        ["GET /api/customers/{customerKey}/documents"] = nameof(CrmDocuments.CustomerDocumentsEndpointTests.SystemAdministrator_IsAuthorizedOnEveryDocumentsRoute_ThroughTheOverride),
        ["GET /api/customers/{customerKey}/documents/{crmLeadId:int}/{documentType}/{recordId}/file"] = nameof(CrmDocuments.CustomerDocumentsEndpointTests.SystemAdministrator_IsAuthorizedOnEveryDocumentsRoute_ThroughTheOverride),
        ["POST /api/customers/{customerKey}/documents/send"] = nameof(CrmDocuments.CustomerDocumentsEndpointTests.SystemAdministrator_IsAuthorizedOnEveryDocumentsRoute_ThroughTheOverride),
        ["GET /api/dashboard"] = nameof(SystemAdministratorEndpointAuthorizationTests.GetDashboard_Returns200),
        ["GET /api/dashboard/overview"] = nameof(SystemAdministratorEndpointAuthorizationTests.GetDashboardOverview_Returns200),

        // Reports (CS Manager tier) — covered by TeamPerformanceEndpointsTests
        // (CS Manager / GM / Chairman succeed, CS Agent and the department
        // roles are refused, the administrator passes through the override).
        ["GET /api/reports/team-performance"] = nameof(Reporting.Integration.TeamPerformanceEndpointsTests.SystemAdministrator_IsAuthorizedOnBothEndpoints_ThroughTheOverride),
        ["GET /api/reports/team-performance/records"] = nameof(Reporting.Integration.TeamPerformanceEndpointsTests.SystemAdministrator_IsAuthorizedOnBothEndpoints_ThroughTheOverride),

        // SLA and Escalation. Automatic Level 2 escalation on breach has no
        // row here because it has no endpoint: MVP-API-Contracts.md §5.7
        // makes it system-triggered, raised inside the background-job
        // transaction that records the breach.
        ["GET /api/tickets/{ticketId:long}/sla"] = nameof(SystemAdministratorEndpointAuthorizationTests.GetTicketSla_Returns200),
        ["POST /api/tickets/{ticketId:long}/sla/first-response"] = nameof(SystemAdministratorEndpointAuthorizationTests.RecordFirstResponse_Returns200),
        ["POST /api/tickets/{ticketId:long}/escalations"] = nameof(SystemAdministratorEndpointAuthorizationTests.EscalateTicketAndListEscalations_Return201And200),
        ["GET /api/tickets/{ticketId:long}/escalations"] = nameof(SystemAdministratorEndpointAuthorizationTests.EscalateTicketAndListEscalations_Return201And200),
        ["POST /api/tickets/{ticketId:long}/sla/priority-downgrade-requests"] = nameof(SystemAdministratorEndpointAuthorizationTests.PriorityDowngradeEndpoints_AuthorizedThroughTheOverride),
        ["GET /api/tickets/{ticketId:long}/sla/priority-downgrade-requests"] = nameof(SystemAdministratorEndpointAuthorizationTests.PriorityDowngradeEndpoints_AuthorizedThroughTheOverride),
        ["GET /api/priority-downgrade-requests/pending"] = nameof(SystemAdministratorEndpointAuthorizationTests.PriorityDowngradeEndpoints_AuthorizedThroughTheOverride),
        ["POST /api/priority-downgrade-requests/{requestId:long}/approve"] = nameof(SystemAdministratorEndpointAuthorizationTests.PriorityDowngradeEndpoints_AuthorizedThroughTheOverride),
        ["POST /api/priority-downgrade-requests/{requestId:long}/reject"] = nameof(SystemAdministratorEndpointAuthorizationTests.PriorityDowngradeEndpoints_AuthorizedThroughTheOverride),

        // Administration / Workflow Designer phase — covered by
        // AdministrationEndpointsTests (System Administrator succeeds) and
        // AdministrationAuthorizationTests (every other role is refused).
        ["GET /api/request-types"] = nameof(Administration.Integration.AdministrationEndpointsTests.RequestTypeDirectory_FiltersByDepartment_ForAnyStaff),
        ["GET /api/admin/users"] = nameof(Administration.Integration.AdministrationEndpointsTests.Users_CreateListGetEditRolesMembershipAndDeactivate),
        ["GET /api/admin/users/{employeeId:guid}"] = nameof(Administration.Integration.AdministrationEndpointsTests.Users_CreateListGetEditRolesMembershipAndDeactivate),
        ["POST /api/admin/users"] = nameof(Administration.Integration.AdministrationEndpointsTests.Users_CreateListGetEditRolesMembershipAndDeactivate),
        ["PUT /api/admin/users/{employeeId:guid}/profile"] = nameof(Administration.Integration.AdministrationEndpointsTests.Users_CreateListGetEditRolesMembershipAndDeactivate),
        ["PATCH /api/admin/users/{employeeId:guid}/activation"] = nameof(Administration.Integration.AdministrationEndpointsTests.Users_CreateListGetEditRolesMembershipAndDeactivate),
        ["PUT /api/admin/users/{employeeId:guid}/roles"] = nameof(Administration.Integration.AdministrationEndpointsTests.Users_CreateListGetEditRolesMembershipAndDeactivate),
        ["POST /api/admin/users/{employeeId:guid}/departments"] = nameof(Administration.Integration.AdministrationEndpointsTests.Users_CreateListGetEditRolesMembershipAndDeactivate),
        ["DELETE /api/admin/users/{employeeId:guid}/departments/{departmentId:int}"] = nameof(Administration.Integration.AdministrationEndpointsTests.Users_CreateListGetEditRolesMembershipAndDeactivate),
        ["POST /api/admin/users/{employeeId:guid}/password"] = nameof(PasswordManagementEndpointsTests.AdminReset_GoodPassword_Returns204_InvalidatesEveryEarlierToken_LiftsLockout_AndAuditsWithoutTheSecret),
        ["POST /api/admin/users/{employeeId:guid}/unlock"] = nameof(PasswordManagementEndpointsTests.AdminUnlock_ClearsLockoutEndAndFailedCount_LetsTheUserSignInAgain_AndAudits),
        ["GET /api/admin/users/{employeeId:guid}/permissions"] = nameof(TigerCS.Tests.Permissions.PermissionEnforcementTests.Permission_management_is_refused_to_every_non_administrator_and_to_anonymous_callers),
        ["PUT /api/admin/users/{employeeId:guid}/permissions"] = nameof(TigerCS.Tests.Permissions.PermissionEnforcementTests.Saving_permissions_through_the_api_takes_effect_on_the_next_request_and_is_audited),
        ["GET /api/admin/users/password-policy"] = nameof(PasswordManagementEndpointsTests.PasswordPolicyEndpoint_ReturnsTheConfiguredIdentityOptions_ToAnAdministratorOnly),
        ["GET /api/channels"] = nameof(SystemAdministratorEndpointAuthorizationTests.ListChannels_Returns200),
        ["GET /api/admin/channels"] = nameof(Administration.Integration.AdministrationEndpointsTests.Channels_ListAddEditActivationAndDuplicateCode_ThroughTheRealHost),
        ["GET /api/admin/channels/{channelId:int:range(1,255)}"] = nameof(Administration.Integration.AdministrationEndpointsTests.Channels_ListAddEditActivationAndDuplicateCode_ThroughTheRealHost),
        ["POST /api/admin/channels"] = nameof(Administration.Integration.AdministrationEndpointsTests.Channels_ListAddEditActivationAndDuplicateCode_ThroughTheRealHost),
        ["PUT /api/admin/channels/{channelId:int:range(1,255)}"] = nameof(Administration.Integration.AdministrationEndpointsTests.Channels_ListAddEditActivationAndDuplicateCode_ThroughTheRealHost),
        ["PATCH /api/admin/channels/{channelId:int:range(1,255)}/activation"] = nameof(Administration.Integration.AdministrationEndpointsTests.Channels_ListAddEditActivationAndDuplicateCode_ThroughTheRealHost),
        ["GET /api/admin/departments"] = nameof(Administration.Integration.AdministrationEndpointsTests.Departments_CreateListGetEditMembersAndDeactivate),
        ["GET /api/admin/departments/{departmentId:int}"] = nameof(Administration.Integration.AdministrationEndpointsTests.Departments_CreateListGetEditMembersAndDeactivate),
        ["POST /api/admin/departments"] = nameof(Administration.Integration.AdministrationEndpointsTests.Departments_CreateListGetEditMembersAndDeactivate),
        ["PUT /api/admin/departments/{departmentId:int}"] = nameof(Administration.Integration.AdministrationEndpointsTests.Departments_CreateListGetEditMembersAndDeactivate),
        ["PATCH /api/admin/departments/{departmentId:int}/activation"] = nameof(Administration.Integration.AdministrationEndpointsTests.Departments_CreateListGetEditMembersAndDeactivate),
        ["POST /api/admin/departments/{departmentId:int}/members"] = nameof(Administration.Integration.AdministrationEndpointsTests.Departments_CreateListGetEditMembersAndDeactivate),
        ["DELETE /api/admin/departments/{departmentId:int}/members/{employeeId:guid}"] = nameof(Administration.Integration.AdministrationEndpointsTests.Departments_CreateListGetEditMembersAndDeactivate),
        ["GET /api/admin/request-types"] = nameof(Administration.Integration.AdministrationEndpointsTests.RequestTypes_CreateListGetEditConfigureAndDeactivate),
        ["GET /api/admin/request-types/{requestTypeId:int}"] = nameof(Administration.Integration.AdministrationEndpointsTests.RequestTypes_CreateListGetEditConfigureAndDeactivate),
        ["POST /api/admin/request-types"] = nameof(Administration.Integration.AdministrationEndpointsTests.RequestTypes_CreateListGetEditConfigureAndDeactivate),
        ["PUT /api/admin/request-types/{requestTypeId:int}"] = nameof(Administration.Integration.AdministrationEndpointsTests.RequestTypes_CreateListGetEditConfigureAndDeactivate),
        ["PATCH /api/admin/request-types/{requestTypeId:int}/activation"] = nameof(Administration.Integration.AdministrationEndpointsTests.RequestTypes_CreateListGetEditConfigureAndDeactivate),
        ["PUT /api/admin/request-types/{requestTypeId:int}/assignment-rule"] = nameof(Administration.Integration.AdministrationEndpointsTests.RequestTypes_CreateListGetEditConfigureAndDeactivate),
        ["PUT /api/admin/request-types/{requestTypeId:int}/approval-requirements/{approvalType}"] = nameof(Administration.Integration.AdministrationEndpointsTests.RequestTypes_CreateListGetEditConfigureAndDeactivate),
        ["PUT /api/admin/request-types/{requestTypeId:int}/sla-policies/{priorityId}"] = nameof(Administration.Integration.AdministrationEndpointsTests.RequestTypes_CreateListGetEditConfigureAndDeactivate),
        ["GET /api/admin/workflows/catalog"] = nameof(Administration.Integration.AdministrationEndpointsTests.Workflows_DesignPublishVersionAndPinTickets),
        ["GET /api/admin/workflows"] = nameof(Administration.Integration.AdministrationEndpointsTests.Workflows_DesignPublishVersionAndPinTickets),
        ["GET /api/admin/workflows/{workflowId:int}"] = nameof(Administration.Integration.AdministrationEndpointsTests.Workflows_DesignPublishVersionAndPinTickets),
        ["POST /api/admin/workflows"] = nameof(Administration.Integration.AdministrationEndpointsTests.Workflows_DesignPublishVersionAndPinTickets),
        ["PUT /api/admin/workflows/{workflowId:int}"] = nameof(Administration.Integration.AdministrationEndpointsTests.Workflows_DesignPublishVersionAndPinTickets),
        ["PATCH /api/admin/workflows/{workflowId:int}/activation"] = nameof(Administration.Integration.AdministrationEndpointsTests.Workflows_DesignPublishVersionAndPinTickets),
        ["POST /api/admin/workflows/{workflowId:int}/versions"] = nameof(Administration.Integration.AdministrationEndpointsTests.Workflows_DesignPublishVersionAndPinTickets),
        ["GET /api/admin/workflows/versions/{versionId:int}"] = nameof(Administration.Integration.AdministrationEndpointsTests.Workflows_DesignPublishVersionAndPinTickets),
        ["PUT /api/admin/workflows/versions/{versionId:int}/settings"] = nameof(Administration.Integration.AdministrationEndpointsTests.Workflows_DesignPublishVersionAndPinTickets),
        ["POST /api/admin/workflows/versions/{versionId:int}/steps"] = nameof(Administration.Integration.AdministrationEndpointsTests.Workflows_DesignPublishVersionAndPinTickets),
        ["PUT /api/admin/workflows/versions/{versionId:int}/steps/{stepId:int}"] = nameof(Administration.Integration.AdministrationEndpointsTests.Workflows_DesignPublishVersionAndPinTickets),
        ["DELETE /api/admin/workflows/versions/{versionId:int}/steps/{stepId:int}"] = nameof(Administration.Integration.AdministrationEndpointsTests.Workflows_DesignPublishVersionAndPinTickets),
        ["POST /api/admin/workflows/versions/{versionId:int}/steps/{stepId:int}/move"] = nameof(Administration.Integration.AdministrationEndpointsTests.Workflows_DesignPublishVersionAndPinTickets),
        ["PUT /api/admin/workflows/versions/{versionId:int}/steps/{stepId:int}/transitions"] = nameof(Administration.Integration.AdministrationEndpointsTests.Workflows_DesignPublishVersionAndPinTickets),
        ["POST /api/admin/workflows/versions/{versionId:int}/publish"] = nameof(Administration.Integration.AdministrationEndpointsTests.Workflows_DesignPublishVersionAndPinTickets),
        ["DELETE /api/admin/workflows/versions/{versionId:int}"] = nameof(Administration.Integration.AdministrationEndpointsTests.Workflows_DesignPublishVersionAndPinTickets)
    };

    private static IReadOnlyCollection<string> ProtectedEndpointsOf(EndpointDataSource endpoints) =>
        endpoints.Endpoints
            .OfType<RouteEndpoint>()
            // Anonymous by explicit opt-out: GET /health and POST
            // /api/auth/login, the only two Security-Architecture.md §5
            // allows.
            .Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is null)
            .SelectMany(e =>
            {
                var methods = e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["*"];
                return methods.Select(m => $"{m} /{e.RoutePattern.RawText?.TrimStart('/')}");
            })
            .Distinct()
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

    [Fact]
    public void EveryProtectedEndpointHasASystemAdministratorAuthorizationTest()
    {
        using var scope = _factory.Services.CreateScope();
        var endpoints = scope.ServiceProvider.GetRequiredService<EndpointDataSource>();

        var actual = ProtectedEndpointsOf(endpoints);

        var untested = actual.Where(e => !CoveredByTest.ContainsKey(e)).ToList();
        Assert.True(
            untested.Count == 0,
            "These protected endpoints have no System Administrator authorization test (requirement 6). "
            + "Add one to SystemAdministratorEndpointAuthorizationTests and list it in CoveredByTest: "
            + string.Join(", ", untested));

        var stale = CoveredByTest.Keys.Where(e => !actual.Contains(e)).ToList();
        Assert.True(
            stale.Count == 0,
            "These entries in CoveredByTest no longer match a mapped endpoint — the route changed or was removed: "
            + string.Join(", ", stale));
    }

    [Fact]
    public void TheOnlyAnonymousBusinessEndpointsAreHealthAndLogin()
    {
        // Security-Architecture.md §5: "no anonymous endpoint exists except
        // the health-check surface" (plus login itself, which cannot require
        // a token it issues). Pinned here because the override's safety
        // argument assumes every other endpoint sits behind the authenticated
        // fallback policy — an endpoint that opted out of authorization
        // entirely would not be covered by an authorization override at all.
        //
        // The two OpenAPI document routes are anonymous by design and are
        // not business endpoints: they are mapped only in
        // OpenApiDocumentation.EnabledEnvironments (Development/Testing —
        // this factory runs as "Testing") and never exist in Production, per
        // MapTigerCsSwagger. Listed explicitly rather than filtered out by a
        // pattern, so a third anonymous route would still fail this test.
        //
        // /api/auth/screen-pop/redeem is anonymous for the same reason login
        // is: it is where a credential is exchanged for a session. The
        // credential is a one-time, one-hour Screen Pop token issued only to
        // the authenticated Genesys service account, and the session it
        // yields is the mapped user's ordinary one (GenesysScreenPopEndpointsTests).
        //
        // /api/public/ticket-reviews/{lookup,submit} are anonymous because they serve the customer satisfaction review
        // page, opened from an email link by a customer with no login. The credential is a random 256-bit single-ticket
        // token (only its hash is stored, it expires, and one review is accepted per ticket); lookup is read-only and
        // submit is the only write (PublicTicketReviewEndpointsTests, TicketReviewFlowTests).
        using var scope = _factory.Services.CreateScope();
        var endpoints = scope.ServiceProvider.GetRequiredService<EndpointDataSource>();

        var anonymous = endpoints.Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            .Select(e => $"/{e.RoutePattern.RawText?.TrimStart('/')}")
            .Distinct()
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            [
                "/api/auth/login",
                "/api/auth/screen-pop/redeem",
                "/api/public/ticket-reviews/lookup",
                "/api/public/ticket-reviews/submit",
                "/health",
                "/openapi/{documentName}.json",
                "/swagger/{documentName}/swagger.json"
            ],
            anonymous);
    }
}
