using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using TigerCS.Domain.Audit;
using TigerCS.Domain.Infrastructure;
using TigerCS.Domain.Modules.ClassificationAndRouting;
using TigerCS.Domain.Modules.Collections;
using TigerCS.Domain.Modules.Collections.Review;
using TigerCS.Domain.Modules.CustomerReviews;
using TigerCS.Domain.Modules.CustomerVerification;
using TigerCS.Domain.Modules.GenesysIntegration;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Notifications;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Domain.Modules.WorkflowConfiguration;
using TigerCS.Infrastructure.Audit;
using TigerCS.Infrastructure.Identity;
using TigerCS.Infrastructure.Modules.ClassificationAndRouting.Configurations;
using TigerCS.Infrastructure.Modules.Collections;
using TigerCS.Infrastructure.Modules.CustomerReviews;
using TigerCS.Infrastructure.Modules.CustomerVerification.Configurations;
using TigerCS.Infrastructure.Modules.GenesysIntegration.Configurations;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Configurations;
using TigerCS.Infrastructure.Modules.Notifications.Configurations;
using TigerCS.Infrastructure.Modules.SlaAndEscalation.Configurations;
using TigerCS.Infrastructure.Modules.Ticketing.Configurations;
using TigerCS.Infrastructure.Modules.WorkflowConfiguration.Configurations;
using TigerCS.Infrastructure.Persistence.Configurations;

namespace TigerCS.Infrastructure.Persistence;

/// <summary>
/// Identity and Access, Customer Verification, Ticketing, and — added by the
/// SLA and Escalation increment — <c>SlaPolicies</c>, <c>TicketSlaInstances</c>,
/// <c>TicketEscalations</c>, the <c>BusinessCalendars</c>/
/// <c>BusinessCalendarWorkingDays</c>/<c>Holidays</c> reference data
/// (ADR-0010), and <c>IdempotencyRecords</c> (ADR-0014).
///
/// <para>
/// The Notifications increment adds <c>OutboxMessages</c> (ADR-0013) and
/// <c>Notifications</c> (§2.21), completing the §2.23 pair whose idempotency
/// half already existed.
/// </para>
///
/// <para>
/// <c>TicketSlaPausePeriods</c> (ISSUE-018 SLA pause/resume) and
/// <c>PriorityDowngradeRequests</c> (ISSUE-023 Department-Head approval) are
/// both mapped. Genesys and attachments arrive with their own increments.
/// </para>
/// </summary>
public class TigerCsDbContext(DbContextOptions<TigerCsDbContext> options)
    : IdentityDbContext<ApplicationUser, ApplicationRole, Guid>(options)
{
    public DbSet<Employee> Employees => Set<Employee>();

    public DbSet<Department> Departments => Set<Department>();

    public DbSet<UserDepartmentAssignment> UserDepartmentAssignments => Set<UserDepartmentAssignment>();

    public DbSet<UnitReference> UnitReferences => Set<UnitReference>();

    public DbSet<ContactReference> ContactReferences => Set<ContactReference>();

    public DbSet<VerificationSession> VerificationSessions => Set<VerificationSession>();
    public DbSet<CrmDocumentDeliveryRequest> CrmDocumentDeliveryRequests => Set<CrmDocumentDeliveryRequest>();
    public DbSet<CustomerOtpChallenge> CustomerOtpChallenges => Set<CustomerOtpChallenge>();

    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    public DbSet<Priority> Priorities => Set<Priority>();

    public DbSet<Category> Categories => Set<Category>();

    public DbSet<Channel> Channels => Set<Channel>();

    public DbSet<IntakeRecord> IntakeRecords => Set<IntakeRecord>();

    public DbSet<DepartmentCustomerLookupSource> DepartmentCustomerLookupSources => Set<DepartmentCustomerLookupSource>();

    public DbSet<Ticket> Tickets => Set<Ticket>();

    public DbSet<TicketRequesterSnapshot> TicketRequesterSnapshots => Set<TicketRequesterSnapshot>();

    public DbSet<TicketStatusHistory> TicketStatusHistoryEntries => Set<TicketStatusHistory>();

    public DbSet<TicketAssignment> TicketAssignments => Set<TicketAssignment>();

    public DbSet<DepartmentAssignmentRotation> DepartmentAssignmentRotations => Set<DepartmentAssignmentRotation>();

    public DbSet<TicketResolution> TicketResolutions => Set<TicketResolution>();

    public DbSet<TicketNote> TicketNotes => Set<TicketNote>();

    public DbSet<SlaPolicy> SlaPolicies => Set<SlaPolicy>();

    public DbSet<BusinessCalendar> BusinessCalendars => Set<BusinessCalendar>();

    public DbSet<BusinessCalendarWorkingDay> BusinessCalendarWorkingDays => Set<BusinessCalendarWorkingDay>();

    public DbSet<Holiday> Holidays => Set<Holiday>();

    public DbSet<TicketSlaInstance> TicketSlaInstances => Set<TicketSlaInstance>();

    public DbSet<TicketSlaPausePeriod> TicketSlaPausePeriods => Set<TicketSlaPausePeriod>();

    public DbSet<TicketEscalation> TicketEscalations => Set<TicketEscalation>();

    public DbSet<PriorityDowngradeRequest> PriorityDowngradeRequests => Set<PriorityDowngradeRequest>();

    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public DbSet<Notification> Notifications => Set<Notification>();

    public DbSet<TicketReviewInvitation> TicketReviewInvitations => Set<TicketReviewInvitation>();

    public DbSet<TicketReview> TicketReviews => Set<TicketReview>();

    public DbSet<Workflow> Workflows => Set<Workflow>();

    public DbSet<WorkflowTemplate> WorkflowTemplates => Set<WorkflowTemplate>();

    public DbSet<WorkflowStepTransition> WorkflowStepTransitions => Set<WorkflowStepTransition>();

    public DbSet<WorkflowTemplateStep> WorkflowTemplateSteps => Set<WorkflowTemplateStep>();

    public DbSet<RequestType> RequestTypes => Set<RequestType>();

    public DbSet<RequestTypeSlaPolicy> RequestTypeSlaPolicies => Set<RequestTypeSlaPolicy>();

    public DbSet<DepartmentWorkflowSettings> DepartmentWorkflowSettings => Set<DepartmentWorkflowSettings>();

    public DbSet<RequestTypeAssignmentRule> RequestTypeAssignmentRules => Set<RequestTypeAssignmentRule>();

    public DbSet<RequestTypeAssignmentRuleMember> RequestTypeAssignmentRuleMembers => Set<RequestTypeAssignmentRuleMember>();

    public DbSet<TicketPendingRecord> TicketPendingRecords => Set<TicketPendingRecord>();

    public DbSet<TicketInteraction> TicketInteractions => Set<TicketInteraction>();

    public DbSet<TicketInteractionMessage> TicketInteractionMessages => Set<TicketInteractionMessage>();

    public DbSet<TicketInteractionHandler> TicketInteractionHandlers => Set<TicketInteractionHandler>();

    public DbSet<TicketAgentHandoff> TicketAgentHandoffs => Set<TicketAgentHandoff>();

    public DbSet<GenesysQueueMapping> GenesysQueueMappings => Set<GenesysQueueMapping>();

    public DbSet<GenesysScreenPopLaunch> GenesysScreenPopLaunches => Set<GenesysScreenPopLaunch>();

    public DbSet<CollectionsReminder> CollectionsReminders => Set<CollectionsReminder>();

    public DbSet<CollectionsReminderChannel> CollectionsReminderChannels => Set<CollectionsReminderChannel>();

    public DbSet<CollectionsReminderEvent> CollectionsReminderEvents => Set<CollectionsReminderEvent>();

    public DbSet<CollectionsReviewRun> CollectionsReviewRuns => Set<CollectionsReviewRun>();

    public DbSet<CollectionsReviewRecord> CollectionsReviewRecords => Set<CollectionsReviewRecord>();

    public DbSet<CollectionsDispatch> CollectionsDispatches => Set<CollectionsDispatch>();

    public DbSet<CollectionsDispatchItem> CollectionsDispatchItems => Set<CollectionsDispatchItem>();

    public DbSet<CollectionsGenesysBatch> CollectionsGenesysBatches => Set<CollectionsGenesysBatch>();

    public DbSet<TicketApproval> TicketApprovals => Set<TicketApproval>();

    public DbSet<TicketWorkflowEvent> TicketWorkflowEvents => Set<TicketWorkflowEvent>();

    public DbSet<RequestTypeApprovalRequirement> RequestTypeApprovalRequirements => Set<RequestTypeApprovalRequirement>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfiguration(new ApplicationUserConfiguration());
        builder.ApplyConfiguration(new ApplicationRoleConfiguration());
        builder.ApplyConfiguration(new EmployeeConfiguration());
        builder.ApplyConfiguration(new DepartmentConfiguration());
        builder.ApplyConfiguration(new UserDepartmentAssignmentConfiguration());

        builder.ApplyConfiguration(new UnitReferenceConfiguration());
        builder.ApplyConfiguration(new ContactReferenceConfiguration());
        builder.ApplyConfiguration(new VerificationSessionConfiguration());
        builder.ApplyConfiguration(new CrmDocumentDeliveryRequestConfiguration());
        builder.ApplyConfiguration(new CustomerOtpChallengeConfiguration());

        builder.ApplyConfiguration(new AuditEntryConfiguration());

        builder.ApplyConfiguration(new PriorityConfiguration());
        builder.ApplyConfiguration(new CategoryConfiguration());
        builder.ApplyConfiguration(new ChannelConfiguration());
        builder.ApplyConfiguration(new IntakeRecordConfiguration());
        builder.ApplyConfiguration(new DepartmentCustomerLookupSourceConfiguration());
        builder.ApplyConfiguration(new TicketConfiguration());
        builder.ApplyConfiguration(new TicketRequesterSnapshotConfiguration());
        builder.ApplyConfiguration(new TicketStatusHistoryConfiguration());
        builder.ApplyConfiguration(new TicketAssignmentConfiguration());
        builder.ApplyConfiguration(new DepartmentAssignmentRotationConfiguration());
        builder.ApplyConfiguration(new TicketResolutionConfiguration());
        builder.ApplyConfiguration(new TicketNoteConfiguration());

        builder.ApplyConfiguration(new SlaPolicyConfiguration());
        builder.ApplyConfiguration(new BusinessCalendarConfiguration());
        builder.ApplyConfiguration(new BusinessCalendarWorkingDayConfiguration());
        builder.ApplyConfiguration(new HolidayConfiguration());
        builder.ApplyConfiguration(new TicketSlaInstanceConfiguration());
        builder.ApplyConfiguration(new TicketSlaPausePeriodConfiguration());
        builder.ApplyConfiguration(new TicketEscalationConfiguration());
        builder.ApplyConfiguration(new PriorityDowngradeRequestConfiguration());
        builder.ApplyConfiguration(new IdempotencyRecordConfiguration());
        builder.ApplyConfiguration(new OutboxMessageConfiguration());
        builder.ApplyConfiguration(new NotificationConfiguration());
        builder.ApplyConfiguration(new TicketReviewInvitationConfiguration());
        builder.ApplyConfiguration(new TicketReviewConfiguration());

        builder.ApplyConfiguration(new WorkflowConfiguration());
        builder.ApplyConfiguration(new WorkflowTemplateConfiguration());
        builder.ApplyConfiguration(new WorkflowStepTransitionConfiguration());
        builder.ApplyConfiguration(new WorkflowTemplateStepConfiguration());
        builder.ApplyConfiguration(new RequestTypeConfiguration());
        builder.ApplyConfiguration(new RequestTypeSlaPolicyConfiguration());
        builder.ApplyConfiguration(new DepartmentWorkflowSettingsConfiguration());
        builder.ApplyConfiguration(new RequestTypeAssignmentRuleConfiguration());
        builder.ApplyConfiguration(new RequestTypeAssignmentRuleMemberConfiguration());
        builder.ApplyConfiguration(new TicketPendingRecordConfiguration());
        builder.ApplyConfiguration(new TicketInteractionConfiguration());
        builder.ApplyConfiguration(new TicketInteractionHandlerConfiguration());
        builder.ApplyConfiguration(new TicketInteractionMessageConfiguration());
        builder.ApplyConfiguration(new TicketAgentHandoffConfiguration());
        builder.ApplyConfiguration(new GenesysQueueMappingConfiguration());
        builder.ApplyConfiguration(new GenesysScreenPopLaunchConfiguration());

        builder.ApplyConfiguration(new CollectionsReminderConfiguration());
        builder.ApplyConfiguration(new CollectionsReminderChannelConfiguration());
        builder.ApplyConfiguration(new CollectionsReminderEventConfiguration());
        builder.ApplyConfiguration(new CollectionsReviewRunConfiguration());
        builder.ApplyConfiguration(new CollectionsReviewRecordConfiguration());
        builder.ApplyConfiguration(new CollectionsDispatchConfiguration());
        builder.ApplyConfiguration(new CollectionsDispatchItemConfiguration());
        builder.ApplyConfiguration(new CollectionsGenesysBatchConfiguration());
        builder.ApplyConfiguration(new TicketApprovalConfiguration());
        builder.ApplyConfiguration(new TicketWorkflowEventConfiguration());
        builder.ApplyConfiguration(new RequestTypeApprovalRequirementConfiguration());

        // Supplemental — see this configuration's own remarks for why it is
        // not folded into VerificationSessionConfiguration (a
        // CustomerVerification-module file this increment does not modify).
        builder.ApplyConfiguration(new VerificationSessionConsumptionConcurrencyConfiguration());
    }
}
