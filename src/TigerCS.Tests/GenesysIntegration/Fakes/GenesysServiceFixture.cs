using Microsoft.Extensions.Logging.Abstractions;
using TigerCS.Application.Modules.CustomerVerification.Services;
using TigerCS.Application.Modules.GenesysIntegration;
using TigerCS.Application.Modules.GenesysIntegration.Services;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.ClassificationAndRouting;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Domain.Modules.WorkflowConfiguration;
using TigerCS.Tests.Administration.Services;
using TigerCS.Tests.CustomerVerification.Fakes;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using TigerCS.Tests.Notifications.Fakes;
using TigerCS.Tests.SlaAndEscalation.Fakes;
using TigerCS.Tests.Ticketing.Fakes;

namespace TigerCS.Tests.GenesysIntegration.Fakes;

/// <summary>
/// Wires the Genesys ingestion and conversation-end services over the REAL
/// intake, ticket-creation and customer-lookup application services — only
/// the repositories and gateways are doubles.
///
/// <para>
/// That is deliberate, and is what these tests are for: the phase's central
/// claim is that Genesys reuses the existing ticket flow rather than
/// reimplementing it, so a fixture that stubbed
/// <c>TicketCreationAppService</c> would assert nothing about the claim. A
/// Genesys ticket here goes through the same ticket-number generation,
/// status history, SLA clock, auto-assignment, outbox and audit path an
/// agent-created ticket does.
/// </para>
/// </summary>
public sealed class GenesysServiceFixture
{
    public GenesysOptions Options { get; }
    public GenesysInquiryIngestionAppService Ingestion { get; }
    public GenesysConversationEndAppService ConversationEnd { get; }
    public TicketInteractionQueryAppService InteractionQuery { get; }

    /// <summary>
    /// The ordinary, non-Genesys ticket-creation service — the very same
    /// instance the Genesys ingestion composes. Exposed so a test can prove
    /// that manual/Face-to-Face creation still works with the Genesys feature
    /// flag off, through the real path rather than a parallel one.
    /// </summary>
    public TicketCreationAppService ManualTicketCreation { get; }

    /// <summary>The Unclassified → Classified transition an agent performs after reading the inquiry.</summary>
    public TicketClassificationAppService Classification { get; }

    /// <summary>
    /// The approved default classification (Customer Services / General), resolved from this fixture's departments, categories, request types
    /// and workflows. Until <see cref="SeedDefaultClassification"/> runs it is "not configured" and every service keeps its previous behaviour.
    /// </summary>
    public DefaultClassificationService DefaultClassification { get; }

    public FakeChannelRepository Channels { get; } = new FakeChannelRepository().SeedWellKnown();
    public FakeDepartmentRepository Departments { get; } = new();
    public FakeCategoryRepository Categories { get; } = new();
    public FakeTicketRepository Tickets { get; } = new();
    public FakeIntakeRecordRepository IntakeRecords { get; } = new();
    public FakeTicketInteractionRepository Interactions { get; } = new();
    public FakeGenesysQueueMappingRepository QueueMappings { get; } = new();
    public FakeGenesysConversationRepository Conversations { get; }
    public FakeAuditEntryWriter Audit { get; } = new();
    public FakeTicketingUnitOfWork UnitOfWork { get; } = new();
    public FakeCrmBuyerLookupGateway CrmBuyers { get; } = new();
    public FakePactCustomerLookupGateway Pact { get; } = new();
    public FakeTasleehGateway Tasleeh { get; } = new();
    public FakeUserDepartmentAssignmentRepository DepartmentAssignments { get; } = new() { EveryUserIsResponsible = true };
    public FakeTicketAssignmentRepository TicketAssignments { get; } = new();

    // Workflow configuration shared by every service below, so a test that
    // seeds a request type, its published workflow, its assignment rule or
    // its department settings is seen by creation, classification and routing alike.
    public FakeRequestTypeRepository RequestTypes { get; } = new();
    public FakeWorkflowTemplateRepository WorkflowTemplates { get; } = new();
    public FakeRequestTypeAssignmentRuleRepository AssignmentRules { get; } = new();
    public FakeDepartmentWorkflowSettingsRepository DepartmentSettings { get; } = new();

    /// <summary>Applies a Genesys-supplied request type to a ticket (ingestion and PATCH both go through it).</summary>
    public GenesysRequestTypeClassificationAppService RequestTypeClassification { get; }

    /// <summary>The ticket's typed event log — where the handoff story is recorded for Ticket Activity.</summary>
    public FakeTicketWorkflowEventRepository WorkflowEvents { get; } = new();
    public GenesysAgentHandlingHistory HandlingHistory { get; }
    public FakeTicketStatusHistoryRepository StatusHistory { get; } = new();
    public FakeTicketApprovalRepository Approvals { get; } = new();

    /// <summary>The SLA services ticket creation runs through — exposed so a test can assert that an unclassified inquiry opened no period at all.</summary>
    public SlaServiceFixture Sla { get; }

    /// <summary>Pending human work, on any channel. Mirrors both of the table's filtered unique indexes.</summary>
    public FakeTicketAgentHandoffRepository Handoffs { get; } = new();

    /// <summary>Genesys reporting that an interaction needs a human, and who took it.</summary>
    public GenesysAgentHandoffAppService AgentHandoff { get; }

    /// <summary>The agent-facing work list and its actions.</summary>
    public AgentHandoffAppService PendingWork { get; }

    /// <summary>Genesys contract #2 — the call-pickup customer lookup.</summary>
    public GenesysCustomerLookupAppService CustomerLookup { get; }

    /// <summary>Genesys contract #3 — the one update facade.</summary>
    public GenesysTicketUpdateAppService TicketUpdate { get; }

    /// <summary>The Genesys agent → Ticketing user mapping (AspNetUsers.GenesysUserId), mirroring the filtered unique index.</summary>
    public FakeGenesysAgentMappingRepository AgentMappings { get; } = new();

    /// <summary>Roles of the mapped Ticketing users, as the resolver reports them.</summary>
    public FakeUserRoleReader UserRoles { get; } = new();

    /// <summary>Genesys User ID → Ticketing user resolution.</summary>
    public GenesysAgentResolutionAppService AgentResolution { get; }

    /// <summary>The strict agent-action entry point (agent-context endpoint).</summary>
    public GenesysAgentContextAppService AgentContext { get; }

    /// <summary>Every user recorded as having handled a Genesys interaction — the same instance the ticket repository's CS Agent visibility reads.</summary>
    public FakeTicketInteractionHandlerRepository Handlers => Tickets.Handlers;

    /// <summary>Records each connected agent as a handler of the interaction (used by ingestion and the update facade).</summary>
    public GenesysInteractionHandlerRecorder HandlerRecorder { get; }

    /// <summary>
    /// The ticket queries exactly as production wires them: a CS Agent sees only what is assigned to them or what they handled. (The
    /// fixture's other services use a permissive handler repository so tests about other rules can use a CS Agent as an arbitrary actor.)
    /// </summary>
    public TicketQueryAppService StrictTicketQuery { get; }

    /// <summary>The agent work list and claim exactly as production wires them (strict visibility), unlike <see cref="PendingWork"/>.</summary>
    public AgentHandoffAppService StrictPendingWork { get; }

    /// <summary>Controllable clock shared by the update facade, the lifecycle service and the inactivity closer — advance it to cross the timeout.</summary>
    public FakeTimeProvider Clock { get; } = new(DateTime.UtcNow);

    /// <summary>The chatbot customer-inactivity closer (the background job's whole body).</summary>
    public ChatbotInactivityCloseAppService InactivityClose { get; }

    /// <summary>The REAL lifecycle service the closer calls — over the same ticket, status-history, audit and Outbox fakes.</summary>
    public TicketLifecycleAppService Lifecycle { get; }

    /// <summary>The Call Center department, when the fixture was built with <c>callCenterFirst</c> (production wiring: Round Robin + department lookup).</summary>
    public Department? CallCenter { get; private set; }

    public FakeAssignmentRotationRepository Rotation { get; } = new();

    /// <summary>Internal notes - written by people only, so any note on a ticket is human work.</summary>
    public FakeTicketNoteRepository TicketNotes { get; } = new();

    /// <summary>
    /// Identity accounts of the Call Center agents, present only when the fixture was built with <c>strictAccounts</c>: then the assignment
    /// service checks lockout / account existence (and roles) exactly as production does, so a test can lock an agent out. Every agent a
    /// test wants to be assignable must then be added through <see cref="AddAgent"/>.
    /// </summary>
    public FakeUserAccountManager? Accounts { get; }

    /// <summary>Service / integration identities (never assignable), present only with <c>strictAccounts</c>.</summary>
    public MutableServiceIdentities? ServiceIdentities { get; }

    /// <summary>The "who answered owns the ticket" service the update facade and the handoff use.</summary>
    public GenesysAnsweringAgentAssignmentService AnsweringAgents { get; }

    /// <summary>The assignment engine (Round Robin + direct assignment to the answering agent), as production composes it for Genesys.</summary>
    public TicketAutoAssignmentService AutoAssignment { get; }

    /// <summary>Service identities a test can add to; the production registry is configuration, this is its stand-in.</summary>
    public sealed class MutableServiceIdentities : TigerCS.Application.Authorization.IServiceIdentityRegistry
    {
        private readonly HashSet<Guid> _ids = [];
        public MutableServiceIdentities Add(Guid id) { _ids.Add(id); return this; }
        public bool IsServiceIdentity(Guid employeeId) => _ids.Contains(employeeId);
        public IReadOnlyCollection<Guid> ServiceIdentityIds => _ids;
    }

    public FakeTicketResolutionRepository Resolutions { get; } = new();
    public FakeTicketPendingRecordRepository PendingRecords { get; } = new();
    public FakeOutboxWriter Outbox { get; } = new();

    /// <param name="autoRaiseHandoffOnConversationEnd">
    /// The rollback lever <c>Genesys:AutoRaiseHandoffOnConversationEnd</c> (production default: false — ending a conversation is not a
    /// request for a human). Tests of the legacy safety net switch it on.
    /// </param>
    public GenesysServiceFixture(
        bool enabled = true, int? inactivityTimeoutMinutes = null, bool autoRaiseHandoffOnConversationEnd = false, bool callCenterFirst = false,
        bool strictAccounts = false, bool assignOnAnswer = true)
    {
        Options = new GenesysOptions { Enabled = enabled, AutoRaiseHandoffOnConversationEnd = autoRaiseHandoffOnConversationEnd, AssignOnAnswer = assignOnAnswer };
        if (inactivityTimeoutMinutes is { } minutes)
        {
            Options.CustomerInactivityTimeoutMinutes = minutes;
            Options.AwaitingReplyTimeoutMinutes = minutes;
        }

        Conversations = new FakeGenesysConversationRepository(Interactions);
        Handoffs.Tickets = Tickets;
        Handoffs.Interactions = Interactions;

        var outbox = Outbox;
        UnitOfWork.OutboxWriter = outbox;
        var sla = new SlaServiceFixture(Tickets, statusHistory: StatusHistory, audit: Audit, unitOfWork: UnitOfWork);
        Sla = sla;

        var intakeRecordAppService = new IntakeRecordAppService(
            IntakeRecords, Departments, Channels, UnitOfWork, Audit, TimeProvider.System);

        if (callCenterFirst)
        {
            CallCenter = Departments.AddDepartment("Call Center", "CC");
        }

        if (strictAccounts)
        {
            Accounts = new FakeUserAccountManager();
            ServiceIdentities = new MutableServiceIdentities();
        }

        var autoAssignment = callCenterFirst
            ? new TicketAutoAssignmentService(
                AssignmentRules, DepartmentSettings, DepartmentAssignments, TicketAssignments, Audit, Rotation, Departments,
                roleReader: Accounts, accountManager: Accounts, serviceIdentities: ServiceIdentities, workflowEventRepository: WorkflowEvents,
                ticketNoteRepository: TicketNotes)
            : new TicketAutoAssignmentService(
                AssignmentRules, DepartmentSettings, DepartmentAssignments, TicketAssignments, Audit);
        AutoAssignment = autoAssignment;

        DefaultClassification = new DefaultClassificationService(
            Departments, Categories, RequestTypes, WorkflowTemplates, Tickets, UnitOfWork, Audit);

        var ticketCreationAppService = new TicketCreationAppService(
            IntakeRecords, new FakeUnitReferenceRepository(), new FakeContactReferenceRepository(),
            Categories, new FakePriorityRepository(), Departments, Tickets,
            new FakeTicketRequesterSnapshotRepository(), StatusHistory, UnitOfWork, Audit, outbox,
            sla.DueDates, Clock, RequestTypes, Interactions,
            autoAssignment,
            WorkflowTemplates, serviceIdentities: null, workflowEventRepository: WorkflowEvents,
            defaultClassification: DefaultClassification);
        ManualTicketCreation = ticketCreationAppService;

        // The same customer lookup the New Ticket wizard uses — composed
        // exactly as CustomerSearchAppServiceTests composes it.
        var customerLookup = new CustomerLookupAppService(
            IntakeRecords, new FakeDepartmentCustomerLookupSourceRepository(), new FakeCrmCustomerLookupGateway(),
            Pact, Tasleeh,
            new CrmUnitLookupAppService(
                new FakeCrmGateway(), new FakeUnitReferenceRepository(), new FakeContactReferenceRepository(),
                new FakeCustomerVerificationUnitOfWork(), TimeProvider.System),
            TimeProvider.System);
        var customerSearch = new CustomerSearchAppService(
            new CrmBuyerLookupAppService(CrmBuyers, NullLogger<CrmBuyerLookupAppService>.Instance), customerLookup);

        // Contract #2: the call-pickup lookup, composed over exactly the same
        // customer search the ingestion path uses — no second CRM path.
        CustomerLookup = new GenesysCustomerLookupAppService(
            Options, customerSearch, IntakeRecords, Tickets);

        AgentResolution = new GenesysAgentResolutionAppService(AgentMappings, UserRoles, DepartmentAssignments, ServiceIdentities, options: Options, accountManager: Accounts);

        HandlingHistory = new GenesysAgentHandlingHistory(WorkflowEvents, AgentResolution, Clock);

        AnsweringAgents = new GenesysAnsweringAgentAssignmentService(AgentResolution, autoAssignment, Handoffs, UnitOfWork, Clock);

        AgentHandoff = new GenesysAgentHandoffAppService(
            Options, Conversations, Handoffs, WorkflowEvents, Tickets, UnitOfWork, Audit, TimeProvider.System,
            callCenterFirst ? Departments : null, callCenterFirst ? TicketAssignments : null, callCenterFirst ? autoAssignment : null,
            answeringAgents: AnsweringAgents);

        RequestTypeClassification = new GenesysRequestTypeClassificationAppService(
            Tickets, Approvals, Conversations, Handoffs,
            new TicketRequestTypeRoutingService(
                RequestTypes, Categories, WorkflowTemplates, Departments, autoAssignment, sla.DueDates, StatusHistory, Audit, WorkflowEvents),
            new GenesysDefaultPriorityResolver(Options, new FakePriorityRepository()),
            AgentHandoff, UnitOfWork, TimeProvider.System);

        HandlerRecorder = new GenesysInteractionHandlerRecorder(AgentResolution, Tickets.Handlers, UnitOfWork, Audit, Clock);

        Ingestion = new GenesysInquiryIngestionAppService(
            Options, Conversations, QueueMappings, Departments, Channels,
            Tickets, intakeRecordAppService, ticketCreationAppService, customerSearch, Audit, UnitOfWork,
            AgentResolution,
            new GenesysDefaultPriorityResolver(Options, new FakePriorityRepository()),
            RequestTypeClassification,
            HandlingHistory,
            HandlerRecorder,
            DefaultClassification);

        StrictTicketQuery = new TicketQueryAppService(
            Tickets, DepartmentAssignments, new FakeTicketResolutionRepository(),
            new FakeTicketStatusHistoryRepository(), ReopenPolicy.Default, TimeProvider.System, handlerRepository: Tickets.Handlers);

        Classification = new TicketClassificationAppService(
            Tickets, Categories, new FakePriorityRepository(), RequestTypes,
            WorkflowTemplates, DepartmentAssignments, StatusHistory, UnitOfWork, Audit,
            sla.DueDates, TimeProvider.System, handlerRepository: FakeTicketInteractionHandlerRepository.Permissive(),
            departmentRepository: Departments, autoAssignmentService: autoAssignment, workflowEventRepository: WorkflowEvents,
            defaultClassification: DefaultClassification);

        ConversationEnd = new GenesysConversationEndAppService(
            Options, Conversations, Tickets, Handoffs, WorkflowEvents, sla.FirstHumanResponse,
            UnitOfWork, Audit, TimeProvider.System);

        var ticketQuery = new TicketQueryAppService(
            Tickets, DepartmentAssignments, new FakeTicketResolutionRepository(),
            new FakeTicketStatusHistoryRepository(), ReopenPolicy.Default, TimeProvider.System, handlerRepository: FakeTicketInteractionHandlerRepository.Permissive());

        InteractionQuery = new TicketInteractionQueryAppService(
            Tickets, Interactions, Conversations, Channels, Handoffs, ticketQuery);

        PendingWork = new AgentHandoffAppService(
            Handoffs, ticketQuery, Tickets, TicketAssignments, StatusHistory, WorkflowEvents,
            DepartmentAssignments, UnitOfWork, Audit, TimeProvider.System);

        StrictPendingWork = new AgentHandoffAppService(
            Handoffs, StrictTicketQuery, Tickets, TicketAssignments, StatusHistory, WorkflowEvents,
            DepartmentAssignments, UnitOfWork, Audit, TimeProvider.System);

        var requestTypes = RequestTypes;
        var workflowTemplates = WorkflowTemplates;
        var departmentSettings = DepartmentSettings;
        Lifecycle = new TicketLifecycleAppService(
            Tickets, Resolutions, StatusHistory, DepartmentAssignments, UnitOfWork, Audit, sla.BreachProcessor, Clock,
            PendingRecords, requestTypes, workflowTemplates, outbox, Departments, departmentSettings, WorkflowEvents,
            new TicketAutoAssignmentService(
                AssignmentRules, departmentSettings, DepartmentAssignments, TicketAssignments, Audit),
            sla.DueDates,
            new ReopenEligibilityService(StatusHistory, requestTypes, workflowTemplates, ReopenPolicy.Default), handlerRepository: FakeTicketInteractionHandlerRepository.Permissive(), ticketAssignmentRepository: TicketAssignments);

        // Contract #3: the one update facade over the two services above.
        TicketUpdate = new GenesysTicketUpdateAppService(
            Options, Conversations, Tickets, Departments, Handoffs, UnitOfWork, ConversationEnd, AgentHandoff, AgentResolution, Audit,
            WorkflowEvents, Clock, RequestTypeClassification, Lifecycle, HandlingHistory, HandlerRecorder,
            autoAssignmentService: callCenterFirst ? autoAssignment : null,
            answeringAgentAssignment: AnsweringAgents,
            ticketAssignmentRepository: TicketAssignments,
            ticketNoteRepository: TicketNotes,
            defaultClassification: DefaultClassification);

        InactivityClose = new ChatbotInactivityCloseAppService(
            Options, Conversations, Tickets, Handoffs, Departments, Lifecycle, UnitOfWork, Clock, Audit);

        AgentContext = new GenesysAgentContextAppService(
            Options, AgentResolution, Conversations, Tickets, UnitOfWork, Audit, HandlingHistory);
    }

    /// <summary>
    /// A department that can receive Genesys inquiries, plus one real
    /// category of that department for the classification step.
    ///
    /// <para>
    /// The category is <b>not</b> configuration the integration reads — the
    /// integration has no category configuration at all any more. It exists
    /// here only so a test can classify the ticket afterwards, exactly as an
    /// agent would.
    /// </para>
    /// </summary>
    public (Department Department, Category Category) SeedGenesysDepartment(string name, string code)
    {
        var department = Departments.AddDepartment(name, code);
        var category = Categories.Seed(department.DepartmentId, $"{name} Enquiry");
        return (department, category);
    }

    /// <summary>
    /// Configures the approved default classification the way the idempotent importer does: the Customer Services department (CS) with ONE
    /// General category, ONE General request type and a Published workflow, and no assignment rule. Returns what was created.
    /// </summary>
    public (Department Department, Category Category, RequestType RequestType) SeedDefaultClassification(Department? department = null)
    {
        department ??= Departments.ListAsync(activeOnly: false).GetAwaiter().GetResult().FirstOrDefault(d => d.Code == "CS") ?? Departments.AddDepartment("Customer Services", "CS");
        var category = Categories.Seed(department.DepartmentId, "General");
        var template = WorkflowTemplates.Add(TestWorkflows.PublishedStandard(workflowId: 9100));
        var requestType = RequestTypes.Add(new RequestType(
            department.DepartmentId, "General", template.WorkflowId, (byte)PriorityLevel.Medium,
            allowAgentPriorityChange: false, allowPendingCustomer: true, allowPendingInternal: true, allowReopen: true));
        return (department, category, requestType);
    }

    private int _nextGeneralWorkflowId = 9200;

    /// <summary>The department's OWN General category and General request type (published workflow, no assignment rule) - what the setup script creates for every active department.</summary>
    public (Category Category, RequestType RequestType) SeedGeneral(Department department)
    {
        var category = Categories.Seed(department.DepartmentId, "General");
        var template = WorkflowTemplates.Add(TestWorkflows.PublishedStandard(workflowId: _nextGeneralWorkflowId++));
        var requestType = RequestTypes.Add(new RequestType(
            department.DepartmentId, "General", template.WorkflowId, (byte)PriorityLevel.Medium,
            allowAgentPriorityChange: false, allowPendingCustomer: true, allowPendingInternal: true, allowReopen: true));
        return (category, requestType);
    }

    /// <summary>
    /// Maps a Genesys User ID to a Ticketing user for a test — the same
    /// administrative act UAT performs with the documented UPDATE statement.
    /// Returns the Ticketing user id.
    /// </summary>
    public Guid MapAgent(string genesysUserId, string? genesysEmail = null, bool isActive = true, string displayName = "Mapped Agent", params string[] roles)
    {
        var userId = Guid.NewGuid();
        AgentMappings.Map(userId, genesysUserId, genesysEmail, displayName, isActive);
        if (roles.Length > 0)
        {
            UserRoles.Add(userId, roles);
        }

        return userId;
    }

    /// <summary>
    /// A person who works in the Call Center: a Ticketing user, mapped to a Genesys User ID, a member of the Call Center, with an Identity
    /// account (when the fixture is strict) and the CS Agent role. Returns the Ticketing user id.
    /// </summary>
    public Guid AddAgent(string genesysUserId, string displayName = "Call Center Agent", bool isActive = true, bool lockedOut = false, bool inCallCenter = true, params string[] roles)
    {
        roles = roles.Length == 0 ? [Roles.CsAgent] : roles;
        var userId = MapAgent(genesysUserId, genesysEmail: null, isActive, displayName, roles);
        if (inCallCenter)
        {
            DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(userId, CallCenter!.DepartmentId, isPrimary: true, DateTime.UtcNow, assignedByEmployeeId: null));
        }

        if (Accounts is not null)
        {
            Accounts.Add(userId, displayName.Replace(' ', '.').ToLowerInvariant() + "." + userId.ToString("N")[..6], roles);
            if (lockedOut)
            {
                Accounts.LockOut(userId);
            }
        }

        return userId;
    }

    /// <summary>The originating interaction recorded for one Genesys conversation, or null.</summary>
    public TicketInteraction? InteractionFor(string conversationId) =>
        Interactions.All.FirstOrDefault(i => i.GenesysConversationId == conversationId);
}
