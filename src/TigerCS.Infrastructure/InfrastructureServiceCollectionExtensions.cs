using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using TigerCS.Application.Abstractions;
using TigerCS.Application.Authorization;
using TigerCS.Application.Modules.Administration.Services;
using TigerCS.Application.Modules.ClassificationAndRouting.Services;
using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Review;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Application.Modules.CrmDocuments;
using TigerCS.Application.Modules.CrmDocuments.Abstractions;
using TigerCS.Application.Modules.CrmDocuments.Services;
using TigerCS.Application.Modules.CustomerReviews.Abstractions;
using TigerCS.Application.Modules.CustomerReviews.Services;
using TigerCS.Application.Modules.CustomerVerification.Abstractions;
using TigerCS.Application.Modules.CustomerVerification.Services;
using TigerCS.Application.Modules.GenesysIntegration;
using TigerCS.Application.Modules.GenesysIntegration.Abstractions;
using TigerCS.Application.Modules.GenesysIntegration.Services;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Application.Modules.IdentityAndAccess.Services;
using TigerCS.Application.Modules.SlaAndEscalation.Abstractions;
using TigerCS.Application.Modules.Notifications;
using TigerCS.Application.Modules.Reporting.Abstractions;
using TigerCS.Application.Modules.Reporting.Services;
using TigerCS.Application.Modules.Notifications.Abstractions;
using TigerCS.Application.Modules.Notifications.Services;
using TigerCS.Application.Modules.SlaAndEscalation.Services;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Application.Modules.WorkflowConfiguration.Abstractions;
using TigerCS.Application.Modules.WorkflowConfiguration.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Infrastructure.Audit;
using TigerCS.Infrastructure.BackgroundJobs;
using TigerCS.Infrastructure.Identity;
using TigerCS.Infrastructure.Modules.Collections;
using TigerCS.Infrastructure.Modules.CustomerReviews;
using TigerCS.Infrastructure.Modules.CustomerVerification.Repositories;
using TigerCS.Infrastructure.Modules.GenesysIntegration.Repositories;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Authorization;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Repositories;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Services;
using TigerCS.Infrastructure.Modules.Reporting.Repositories;
using TigerCS.Infrastructure.Modules.Notifications.Repositories;
using TigerCS.Infrastructure.Modules.SlaAndEscalation.Repositories;
using TigerCS.Infrastructure.Modules.Ticketing.Repositories;
using TigerCS.Infrastructure.Modules.WorkflowConfiguration.Repositories;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddTigerCsInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        // Resolved lazily (IConfiguration from DI, at first DbContext creation)
        // rather than read from the `configuration` parameter eagerly here —
        // an eager read would run before test-host configuration overrides
        // (e.g. WebApplicationFactory) are merged in, same reasoning as the
        // JWT options below.
        services.AddDbContext<TigerCsDbContext>((serviceProvider, options) =>
        {
            var connectionString = serviceProvider.GetRequiredService<IConfiguration>().GetConnectionString("TigerCsDatabase")
                ?? throw new InvalidOperationException(
                    "Connection string 'TigerCsDatabase' is not configured. See docs/DEV-SETUP.md.");
            options.UseSqlServer(connectionString);
        });

        services.AddIdentityCore<ApplicationUser>(options =>
            {
                // Security-Architecture.md §1/§13: [ASSUMPTION] — exact policy not
                // yet specified by management. These are the pilot defaults, one
                // notch above Identity's own bare defaults — NOT hardcoded final
                // values: the Configure<IdentityOptions> call below layers any
                // "Identity:Password:*"/"Identity:Lockout:*" configuration on top,
                // so a real deployment can override them without a code change.
                options.Password.RequiredLength = 8;
                options.Password.RequireDigit = true;
                options.Password.RequireLowercase = true;
                options.Password.RequireUppercase = true;
                options.Password.RequireNonAlphanumeric = true;
                options.Password.RequiredUniqueChars = 4;

                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                options.Lockout.AllowedForNewUsers = true;

                options.User.RequireUniqueEmail = false;
            })
            .AddRoles<ApplicationRole>()
            .AddEntityFrameworkStores<TigerCsDbContext>()
            .AddSignInManager();

        // Optional override of the pilot defaults above via configuration
        // ("Identity:Password:RequiredLength", "Identity:Lockout:MaxFailedAccessAttempts",
        // etc.) — only keys actually present in configuration change anything;
        // this and the validation below run on Configure<IdentityOptions>'s IOptions
        // pipeline, so it applies to every options access, not only at startup.
        services.Configure<IdentityOptions>(configuration.GetSection("Identity"));

        services.AddOptions<IdentityOptions>()
            .Validate(o => o.Password.RequiredLength >= 8,
                "Identity:Password:RequiredLength must be at least 8 (Security-Architecture.md §1 pilot floor).")
            .Validate(o => o.Lockout.MaxFailedAccessAttempts is > 0 and <= 10,
                "Identity:Lockout:MaxFailedAccessAttempts must be between 1 and 10.")
            .Validate(o => o.Lockout.DefaultLockoutTimeSpan >= TimeSpan.FromMinutes(1),
                "Identity:Lockout:DefaultLockoutTimeSpan must be at least 1 minute.")
            .ValidateOnStart();

        services.AddHttpContextAccessor();
        services.AddScoped<IClaimsTransformation, DepartmentClaimsTransformation>();

        services.Configure<JwtOptions>(configuration.GetSection(JwtOptions.SectionName));
        services.AddSingleton(TimeProvider.System);

        services.AddScoped<IEmployeeRepository, EmployeeRepository>();
        services.AddScoped<IDepartmentRepository, DepartmentRepository>();
        services.AddScoped<IUserDepartmentAssignmentRepository, UserDepartmentAssignmentRepository>();
        services.AddScoped<IIdentityUnitOfWork, IdentityUnitOfWork>();
        services.AddScoped<IUserRoleReader, UserRoleReader>();
        services.AddScoped<IUserAccountManager, UserAccountManager>();
        services.AddScoped<IRoleCatalogReader, RoleCatalogReader>();
        services.AddScoped<IIdentityAuthenticator, IdentityAuthenticator>();
        services.AddScoped<ITokenService, JwtTokenService>();
        services.AddScoped<IEmployeeDirectory, EmployeeDirectory>();
        services.AddScoped<ICurrentUserContext, CurrentUserContext>();

        services.AddScoped<IAuthorizationHandler, ActiveEmployeeHandler>();
        services.AddScoped<IAuthorizationHandler, DepartmentScopedHandler>();

        // The central System Administrator authorization override (ADR-0024).
        // Registered once, here, rather than referenced by any policy or
        // controller: ASP.NET Core runs every registered IAuthorizationHandler
        // against every authorization evaluation, so this one registration is
        // what makes the override apply to the whole policy catalog — and to
        // policies added after it. Singleton because the handler holds no
        // state and touches no scoped service; the other two are scoped
        // because they inject scoped repositories.
        services.AddSingleton<IAuthorizationHandler, SystemAdministratorOverrideHandler>();

        services.Configure<ServiceIdentityOptions>(configuration.GetSection(ServiceIdentityOptions.SectionName));
        services.AddSingleton<IServiceIdentityRegistry, ServiceIdentityRegistry>();
        services.AddScoped<IAuthorizationHandler, ServiceIdentityRestrictionHandler>();
        services.AddSingleton<IAuthorizationHandler, ReadOnlyCallerWriteGuardHandler>();

        services.AddScoped<AuthenticationAppService>();
        services.AddScoped<UserProfileAppService>();
        services.AddScoped<DepartmentUserAppService>();
        services.AddScoped<AssignableUserAppService>();
        services.AddScoped<DepartmentDirectoryAppService>();
        services.AddScoped<RoleCatalogAppService>();
        services.AddScoped<UserActivationAppService>();
        services.AddScoped<DepartmentAssignmentService>();

        services.AddScoped<IAuditEntryWriter, AuditEntryWriter>();

        services.AddScoped<IUnitReferenceRepository, UnitReferenceRepository>();
        services.AddScoped<IContactReferenceRepository, ContactReferenceRepository>();
        services.AddScoped<IVerificationSessionRepository, VerificationSessionRepository>();
        services.AddScoped<ICustomerVerificationUnitOfWork, CustomerVerificationUnitOfWork>();

        // Chatbot "send me a copy of my document" — identity from the existing
        // verification session, records from the CRM document gateway
        // (registered by AddTigerCsIntegrations; fails closed until Tiger CRM
        // publishes a document endpoint), delivery by the email channel
        // sender. CrmDocuments:Enabled gates the endpoint.
        services.Configure<CrmDocumentOptions>(configuration.GetSection(CrmDocumentOptions.SectionName));
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<CrmDocumentOptions>>().Value);
        // Local/UAT-only redirection of OTP + document messages to test inboxes; off by default, refused elsewhere.
        services.Configure<VerificationTestingOptions>(configuration.GetSection(VerificationTestingOptions.SectionName));
        services.AddSingleton(sp => new VerificationTestingPolicy(
            sp.GetRequiredService<IOptions<VerificationTestingOptions>>().Value,
            sp.GetRequiredService<Microsoft.Extensions.Hosting.IHostEnvironment>().EnvironmentName));
        services.AddScoped<ICrmDocumentDeliveryRepository, CrmDocumentDeliveryRepository>();
        services.AddScoped<DocumentDeliveryTicketService>();
        services.AddScoped<IDocumentDeliveryTicketHandler>(sp => sp.GetRequiredService<DocumentDeliveryTicketService>());
        services.AddScoped<CrmDocumentCopyAppService>();
        services.AddScoped<CustomerDocumentsAppService>();
        services.AddScoped<ICustomerOtpChallengeRepository, CustomerOtpChallengeRepository>();
        services.AddSingleton<IOtpCodeGenerator, RandomOtpCodeGenerator>();
        services.AddScoped<CrmBuyerVerificationCache>();
        services.AddScoped<CustomerOtpAppService>();
        services.AddScoped<CrmUnitLookupAppService>();
        services.AddScoped<CrmBuyerLookupAppService>();
        services.AddScoped<VerificationSessionAppService>();

        services.AddScoped<ICategoryRepository, CategoryRepository>();
        services.AddScoped<CategoryCatalogAppService>();
        services.AddScoped<IPriorityRepository, PriorityRepository>();
        services.AddScoped<IChannelRepository, ChannelRepository>();
        services.AddScoped<ChannelDirectoryAppService>();
        services.AddScoped<IIntakeRecordRepository, IntakeRecordRepository>();
        services.AddScoped<IDepartmentCustomerLookupSourceRepository, DepartmentCustomerLookupSourceRepository>();
        services.AddScoped<ITicketRepository, TicketRepository>();
        services.AddScoped<IDashboardQueryRepository, DashboardQueryRepository>();
        services.AddScoped<ITicketRequesterSnapshotRepository, TicketRequesterSnapshotRepository>();
        services.AddScoped<ITicketStatusHistoryRepository, TicketStatusHistoryRepository>();
        services.AddScoped<ITicketAssignmentRepository, TicketAssignmentRepository>();
        services.AddScoped<IAssignmentRotationRepository, AssignmentRotationRepository>();
        services.AddScoped<ITicketResolutionRepository, TicketResolutionRepository>();
        services.AddScoped<ITicketNoteRepository, TicketNoteRepository>();
        services.AddScoped<ITicketingUnitOfWork, TicketingUnitOfWork>();
        services.AddScoped<IntakeRecordAppService>();
        services.AddScoped<CustomerLookupAppService>();
        services.AddScoped<TicketCreationAppService>();
        services.AddScoped<TicketQueryAppService>();
        services.AddScoped<TicketAssignmentAppService>();
        services.AddScoped<ReopenEligibilityService>();
        services.AddScoped<TicketLifecycleAppService>();
        services.AddScoped<TicketNoteAppService>();
        services.AddScoped<TicketReconciliationAppService>();
        services.AddScoped<CustomerHistoryAppService>();
        services.AddScoped<CustomerProfileAppService>();
        services.AddScoped<ICustomerDirectoryRepository, CustomerDirectoryRepository>();
        services.AddScoped<CustomerDirectoryAppService>();

        // Customer Workspace phase: standalone customer search (composes the
        // existing CRM Buyer + PACT/Tasleeh lookups), the Dashboard
        // aggregate, and ISSUE-011's reopen window — 7 days unless
        // configuration overrides it (Ticketing:ReopenWindowDays), validated
        // here so a nonsensical value fails at startup, not mid-request.
        services.AddScoped<CustomerSearchAppService>();
        services.AddScoped<DashboardAppService>();

        // Reports (CS Manager): the Team Performance report — a read-only
        // aggregate over Identity role membership and the ticket history
        // tables; the Api's CsManagerOrGeneralManager policy guards it.
        services.AddScoped<ITeamPerformanceQueryRepository, TeamPerformanceQueryRepository>();
        services.AddScoped<TeamPerformanceAppService>();
        var reopenWindowDays = configuration.GetValue("Ticketing:ReopenWindowDays", ReopenPolicy.DefaultWindowDays);
        if (reopenWindowDays < 1)
        {
            throw new InvalidOperationException(
                $"Ticketing:ReopenWindowDays must be at least 1 (got {reopenWindowDays}).");
        }

        services.AddSingleton(new ReopenPolicy(reopenWindowDays));

        // SLA and Escalation (ADR-0009/0010/0011/0014/0015). No policy,
        // controller or role list here names the System Administrator role:
        // every service below routes its resource-scoped decisions through
        // AuthorizationGate and every policy through
        // SystemAdministratorOverrideHandler, so ADR-0024's central override
        // covers this whole module without a line of override-specific code
        // — which is the structural property requirement 4 of that
        // correction asked for.
        services.AddScoped<ISlaPolicyRepository, SlaPolicyRepository>();
        services.AddScoped<IBusinessCalendarRepository, BusinessCalendarRepository>();
        services.AddScoped<ITicketSlaInstanceRepository, TicketSlaInstanceRepository>();
        services.AddScoped<ITicketSlaPausePeriodRepository, TicketSlaPausePeriodRepository>();
        services.AddScoped<SlaPauseService>();
        services.AddScoped<ITicketEscalationRepository, TicketEscalationRepository>();
        services.AddScoped<IIdempotencyRecordStore, IdempotencyRecordStore>();
        services.AddScoped<SlaDueDateService>();
        services.AddScoped<SlaBreachProcessor>();

        // The one writer of Ticket.FirstHumanResponseAtUtc, shared by the
        // API-facing first-response endpoint and the Genesys transcript path
        // (ISSUE-019 — one measurement, one mechanism).
        services.AddScoped<FirstHumanResponseRecorder>();
        services.AddScoped<SlaBreachDetectionAppService>();
        services.AddScoped<SlaQueryAppService>();
        services.AddScoped<SlaFirstResponseAppService>();

        // Priority-downgrade approval (ISSUE-023 Option B, section 5.6).
        // PriorityDowngradeOptions is bound in the Api host (Program.cs).
        services.AddScoped<IPriorityDowngradeRequestRepository, PriorityDowngradeRequestRepository>();
        services.AddScoped<PriorityDowngradeAppService>();
        services.AddScoped<TicketEscalationAppService>();

        // Workflow/SLA Configuration (phase 1) — the Department → Request
        // Type → Workflow Template → SLA configuration layer. Read-only at
        // this phase; configuration is seeded/database-driven.
        services.AddScoped<IWorkflowRepository, WorkflowRepository>();
        services.AddScoped<IWorkflowTemplateRepository, WorkflowTemplateRepository>();
        services.AddScoped<IWorkflowConfigurationUnitOfWork, WorkflowConfigurationUnitOfWork>();
        services.AddScoped<IRequestTypeRepository, RequestTypeRepository>();
        services.AddScoped<IRequestTypeSlaPolicyRepository, RequestTypeSlaPolicyRepository>();
        services.AddScoped<IDepartmentWorkflowSettingsRepository, DepartmentWorkflowSettingsRepository>();
        services.AddScoped<WorkflowConfigurationQueryService>();

        // Workflow/Automation (phase 2) — assignment rules, structured
        // pending, and the interaction-context record behind the Genesys
        // boundary (no Genesys API client exists yet — only the context
        // Ticketing persists).
        services.AddScoped<IRequestTypeAssignmentRuleRepository, RequestTypeAssignmentRuleRepository>();
        services.AddScoped<ITicketPendingRecordRepository, TicketPendingRecordRepository>();
        services.AddScoped<ITicketInteractionRepository, TicketInteractionRepository>();
        services.AddScoped<ITicketInteractionHandlerRepository, TicketInteractionHandlerRepository>();
        services.AddScoped<ITicketAgentHandoffRepository, TicketAgentHandoffRepository>();
        services.AddScoped<TicketAutoAssignmentService>();
        services.AddScoped<TicketRequestTypeRoutingService>();

        // Workflow/Automation (phase 3) — approvals, approval requirements,
        // and the typed workflow event store phase 4's SLA triggers read.
        services.AddScoped<ITicketApprovalRepository, TicketApprovalRepository>();
        services.AddScoped<ITicketWorkflowEventRepository, TicketWorkflowEventRepository>();
        services.AddScoped<IRequestTypeApprovalRequirementRepository, RequestTypeApprovalRequirementRepository>();
        services.AddScoped<TicketApprovalAppService>();

        // Genesys integration (phase 1) — the normalized inquiry boundary.
        // Every Genesys channel converges on ONE ingestion flow that reuses
        // the intake, ticket-creation and customer-lookup services already
        // registered above; no Genesys API client exists, because no Genesys
        // endpoint, credential or payload schema is confirmed yet.
        //
        // The feature flag is bound here, in the composition root, and read
        // only by the two Genesys app services: with Genesys:Enabled false
        // (the default) they refuse to do anything and every existing
        // manual/Face-to-Face flow is completely unaffected — nothing in the
        // normal ticketing path consults it.
        // Bound through the options pipeline and resolved lazily — never read
        // eagerly from the `configuration` parameter here, for the same
        // ordering reason the JWT options and the connection string document
        // above: an eager read happens before test-host configuration
        // overrides (WebApplicationFactory) are merged in, so the flag would
        // silently keep its deployed value under test.
        services.Configure<GenesysOptions>(configuration.GetSection(GenesysOptions.SectionName));
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<GenesysOptions>>().Value);

        services.AddScoped<IGenesysQueueMappingRepository, GenesysQueueMappingRepository>();
        services.AddScoped<IGenesysConversationRepository, GenesysConversationRepository>();
        // Genesys agent identity mapping — AspNetUsers.GenesysUserId → Ticketing
        // user, and the interaction-ownership record it enables. Read-only
        // over Identity's store; nothing provisions a user.
        services.AddScoped<IGenesysAgentMappingRepository, GenesysAgentMappingRepository>();
        services.AddScoped<GenesysAgentResolutionAppService>();
        services.AddScoped<GenesysAgentHandlingHistory>();
        services.AddScoped<GenesysInteractionHandlerRecorder>();
        services.AddScoped<GenesysAgentContextAppService>();
        // Secure Screen Pop — one-time, one-hour launch tokens (hash stored
        // only) that sign the mapped agent into TigerCS Web.
        services.AddScoped<IGenesysScreenPopLaunchStore, GenesysScreenPopLaunchStore>();
        services.AddScoped<GenesysScreenPopAppService>();
        services.AddScoped<GenesysDefaultPriorityResolver>();
        services.AddScoped<GenesysRequestTypeClassificationAppService>();
        services.AddScoped<GenesysInquiryIngestionAppService>();
        services.AddScoped<GenesysConversationEndAppService>();
        services.AddScoped<GenesysAgentHandoffAppService>();
        services.AddScoped<GenesysTicketUpdateAppService>();
        services.AddScoped<ChatbotInactivityCloseAppService>();
        services.AddScoped<GenesysCustomerLookupAppService>();
        services.AddScoped<GenesysVerifiedBuyerResolver>();
        services.AddScoped<GenesysCustomerUnitDetailsAppService>();

        // Collections — balances read from the authoritative financial source
        // (ICollectionsFinancialSource, registered by AddTigerCsIntegrations),
        // reminders and their delivery events persisted here, customer
        // responses turned into tickets through the Genesys ingestion above.
        // Collections:Enabled gates every entry point; nothing sends by default.
        services.Configure<CollectionsOptions>(configuration.GetSection(CollectionsOptions.SectionName));
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<CollectionsOptions>>().Value);
        services.AddScoped<ICollectionsReminderRepository, CollectionsReminderRepository>();
        services.AddScoped<ICollectionsUnitOfWork, CollectionsUnitOfWork>();
        services.AddScoped<CollectionsAuthorizationService>();
        services.AddScoped<CollectionsClock>();
        services.Configure<PactReceivablesOptions>(configuration.GetSection(PactReceivablesOptions.SectionName));
        services.AddScoped(sp => sp.GetRequiredService<IOptions<PactReceivablesOptions>>().Value);
        // Pages read the LOCAL snapshot (dbo.CollectionsRcvLine), refreshed from PACT every two hours by the per-company Hangfire jobs; pages never call PACT.
        services.Configure<ReceivablesSnapshotOptions>(configuration.GetSection(ReceivablesSnapshotOptions.SectionName));
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<ReceivablesSnapshotOptions>>().Value);
        services.AddScoped<RcvReceivablesSource>();
        services.AddScoped<IPactReceivablesSource>(sp => sp.GetRequiredService<RcvReceivablesSource>());
        services.AddScoped<ICollectionsTowerCatalog, SqlCollectionsTowerCatalog>();
        // CRM owners of each unit (bulk, local): loaded in the background, joined in SQL / read in bulk. Disabled until CRM publishes GetUnitOwners.
        services.Configure<CollectionsCrmOwnersOptions>(configuration.GetSection(CollectionsCrmOwnersOptions.SectionName));
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<CollectionsCrmOwnersOptions>>().Value);
        services.AddScoped<ICollectionsCrmOwnerStore, SqlCollectionsCrmOwnerStore>();
        services.AddScoped<CollectionsCrmOwnersRefreshService>();
        services.AddScoped<CollectionsUnitLinkService>();
        services.AddScoped<ICollectionsCrmProjectMap, SqlCollectionsCrmProjectMap>();
        services.AddScoped<ICollectionsCrmProjectEvidenceStore, SqlCollectionsCrmProjectEvidenceStore>();
        services.AddScoped<PactContractDiscovery>();
        services.AddScoped<CollectionsUnitStatementService>();
        services.AddScoped<CustomerUnitLinkService>();
        services.AddScoped<CollectionsUnitPaymentSummaryAppService>();
        services.AddScoped<CollectionsLeasingSummaryAppService>();
        services.AddScoped<IReceivablesRefresher, RcvReceivablesRefresher>();
        // Hangfire replaces this in AddTigerCsBackgroundJobs when BackgroundJobs:Enabled is true.
        services.TryAddScoped<IReceivablesRangeLoader, InProcessReceivablesRangeLoader>();
        services.AddScoped<PactReceivableCustomersAppService>();
        services.AddScoped<PactInstalmentsAppService>();
        services.Configure<CollectionsCampaignOptions>(configuration.GetSection(CollectionsCampaignOptions.SectionName));
        services.AddScoped(sp => sp.GetRequiredService<IOptions<CollectionsCampaignOptions>>().Value);
        // Reconciliation of the snapshot with a fresh read of the same PACT procedure (on request only). The history is a singleton: it is also the single-flight guard and the gate's evidence.
        services.AddSingleton<TigerCS.Application.Modules.Collections.Reconciliation.ReconciliationHistory>();
        services.AddSingleton<TigerCS.Application.Modules.Collections.Reconciliation.IFinancialReconciliationEvidence>(sp => sp.GetRequiredService<TigerCS.Application.Modules.Collections.Reconciliation.ReconciliationHistory>());
        services.AddScoped<PactSqlReceivablesSource>();
        services.AddScoped<TigerCS.Application.Modules.Collections.Reconciliation.IReconciliationSource, PactDirectReconciliationSource>();
        services.AddSingleton<TigerCS.Application.Modules.Collections.Reconciliation.IReconciliationLauncher, InProcessReconciliationLauncher>();
        services.AddScoped<TigerCS.Application.Modules.Collections.Reconciliation.FinancialReconciliationService>();
        services.AddScoped<CollectionsCampaignAppService>();
        // Review and approval workflow: stored review data, explicit refresh/dispatch jobs, Genesys outbound upload.
        services.Configure<CollectionsReviewOptions>(configuration.GetSection(CollectionsReviewOptions.SectionName));
        services.AddScoped(sp => sp.GetRequiredService<IOptions<CollectionsReviewOptions>>().Value);
        services.AddScoped<IReviewStore, ReviewStore>();
        services.AddScoped<ReviewQueryService>();
        services.AddScoped<ReviewRefreshService>();
        services.AddScoped<CurrentBalanceReader>();
        services.AddScoped<DispatchService>();
        services.AddScoped<DispatchAdminReportService>();
        services.AddScoped<SuppressionService>();
        services.AddScoped<CollectionsAccountQueryAppService>();
        services.AddScoped<ICollectionsCustomerProfiles, CustomerDirectoryCollectionsProfiles>();
        services.AddScoped(sp => sp.GetRequiredService<IOptions<CollectionsEdsmOptions>>().Value);
        services.AddSingleton(sp => new PactAccountMappingCache(sp.GetService<TimeProvider>() ?? TimeProvider.System));
        services.AddScoped<CollectionsNextPaymentService>();
        services.AddScoped<CollectionsPaymentSummaryAppService>();
        services.AddScoped<CollectionsReminderAppService>();
        services.AddScoped<CollectionsReminderOutcomeAppService>();
        services.AddScoped<IOutboxEventHandler, CollectionsReminderDispatchHandler>();
        services.AddScoped<IOutboxEventHandler, CollectionsResponseTicketHandler>();
        // Email is the only reminder channel with an approved provider (the
        // existing EmailNotifications sender). SMS has none and fails closed;
        // the voice bot is dialled by Genesys, not by TigerCS.
        services.AddScoped<IReminderDeliveryProvider, EmailReminderDeliveryProvider>();
        services.AddScoped<TicketInteractionQueryAppService>();
        services.AddScoped<TigerCS.Application.Modules.CustomerReviews.Services.TicketReviewQueryAppService>();
        services.AddScoped<TicketClassificationAppService>();
        services.AddScoped<AgentHandoffAppService>();

        // Administration / Workflow Designer phase — SystemAdministrator-only
        // endpoints compose these over the existing services above.
        services.AddScoped<AdminUserAppService>();
        services.AddScoped<AdminDepartmentAppService>();
        services.AddScoped<AdminRequestTypeAppService>();
        services.AddScoped<AdminWorkflowAppService>();
        services.AddScoped<AdminChannelAppService>();
        services.AddScoped<AdminGenesysRoutingAppService>();

        // Notifications and the transactional Outbox (ADR-0013/ADR-0014,
        // MVP-Data-Dictionary.md §2.21/§2.23).
        //
        // IOutboxWriter is registered here, in the Infrastructure module that
        // Module-Design.md says owns OutboxMessage — Ticketing consumes the
        // port and never references the Notifications module, so the
        // prohibited-dependency rule holds in both directions.
        services.AddScoped<IOutboxWriter, OutboxWriter>();
        services.AddScoped<IOutboxMessageRepository, OutboxMessageRepository>();
        services.AddScoped<INotificationRepository, NotificationRepository>();
        services.AddScoped<INotificationsUnitOfWork, NotificationsUnitOfWork>();

        // Every IOutboxEventHandler registered against this interface is
        // discovered by the dispatcher and matched on its own EventType, so a
        // later consumer (SLA breach alerts, Genesys event processing) is one
        // registration with no dispatcher change.
        services.AddScoped<CustomerContactResolver>();
        services.AddScoped<CustomerTicketSubjectResolver>();

        // Customer satisfaction reviews: secure review links in Resolved/Closed emails and the public page's server logic.
        services.AddScoped<ITicketReviewRepository, TicketReviewRepository>();
        services.AddScoped<TicketReviewInvitationService>();
        services.AddScoped<TicketReviewAppService>();
        services.AddScoped<IOutboxEventHandler, TicketAcknowledgementHandler>();
        services.AddScoped<IOutboxEventHandler, TicketResolvedNotificationHandler>();
        services.AddScoped<IOutboxEventHandler, TicketClosedNotificationHandler>();
        services.AddScoped<IOutboxEventHandler, TicketReopenedNotificationHandler>();

        services.AddScoped(sp => sp
            .GetRequiredService<IOptions<OutboxDispatchOptions>>().Value.ToPolicy());
        services.AddScoped<OutboxDispatcher>();

        services.AddTigerCsBackgroundJobs(configuration);

        return services;
    }

    public static AuthorizationOptions AddTigerCsAuthorizationPolicies(this AuthorizationOptions options)
    {
        AuthorizationPolicyBuilder Base() =>
            new AuthorizationPolicyBuilder().RequireAuthenticatedUser().AddRequirements(
                new ActiveEmployeeRequirement(),
                // Confines configured service identities (the Genesys integration account) to the integration surface.
                new ServiceIdentityRestrictionRequirement(),
                // Chairman/CEO and Reporting User are read-only: no mutating method unless the endpoint documents otherwise.
                new ReadOnlyCallerWriteGuardRequirement());

        options.DefaultPolicy = Base().Build();

        // Security-Architecture.md §5: "no anonymous endpoint exists except
        // the health-check surface" — every endpoint requires authentication
        // unless it explicitly opts out with [AllowAnonymous].
        options.FallbackPolicy = Base().Build();

        options.AddPolicy(PolicyNames.AuthenticatedStaff, Base().Build());

        options.AddPolicy(PolicyNames.DepartmentScoped, Base()
            .AddRequirements(new DepartmentScopedRequirement(
            [
                // Security-Architecture.md §3, verbatim: "CS-layer roles (Geyness
                // Agent, Supervisor, CS Manager) are scoped differently — across
                // all departments." These three (CS Agent/CS Supervisor/CS
                // Manager per this increment's role-naming decision) are the only
                // roles that citation names as cross-department. General
                // Manager/Chairman-CEO/System Administrator are included too,
                // on the separate basis of Solution-Analysis.md §4.1's
                // permission matrix, which gives each of them "View: All
                // tickets" — a broader read grant than §3's Close/Reopen-
                // specific carve-out. Flagged for confirmation before a real
                // ticket endpoint consumes this policy: whether GM/Chairman/
                // SysAdmin's cross-department reach should extend beyond View.
                Roles.CsAgent, Roles.CsSupervisor, Roles.CsManager,
                Roles.GeneralManager, Roles.ChairmanCeo, Roles.SystemAdministrator
            ]))
            .Build());

        options.AddPolicy(PolicyNames.SupervisorOrAbove, Base()
            .RequireRole(Roles.CsSupervisor, Roles.CsManager, Roles.GeneralManager)
            .Build());

        options.AddPolicy(PolicyNames.DepartmentHeadOrAbove, Base()
            .RequireRole(Roles.DepartmentHead, Roles.CsManager, Roles.GeneralManager)
            .Build());

        // Chairman/CEO is read-only by agreed decision: no operational policy admits it.
        options.AddPolicy(PolicyNames.CsManagerOrGeneralManager, Base()
            .RequireRole(Roles.CsManager, Roles.GeneralManager)
            .Build());

        // Read access to reports (Team Performance): the management tier plus
        // the read-only executive. A read policy only; nothing on it mutates.
        options.AddPolicy(PolicyNames.ReportsRead, Base()
            .RequireRole(Roles.CsManager, Roles.GeneralManager, Roles.ChairmanCeo)
            .Build());

        options.AddPolicy(PolicyNames.SystemAdministrator, Base()
            .RequireRole(Roles.SystemAdministrator)
            .Build());

        // CS Agent/CS Supervisor only — see PolicyNames.CustomerVerification's own
        // remarks for the Solution-Analysis.md §4.1 vs. MVP-API-Contracts.md
        // §0 conflict this resolves, confirmed rather than guessed.
        options.AddPolicy(PolicyNames.CustomerVerification, Base()
            .RequireRole(Roles.CsAgent, Roles.CsSupervisor)
            .Build());

        return options;
    }
}
