using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TigerCS.Application.Modules.CustomerVerification.CrmIntegration;
using TigerCS.Application.Modules.CustomerVerification.Dto;
using TigerCS.Domain.Modules.ClassificationAndRouting;
using TigerCS.Domain.Modules.CustomerVerification;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Application.Modules.SlaAndEscalation.Services;
using TigerCS.Domain.Audit;
using TigerCS.Infrastructure.Identity;
using TigerCS.Application.Modules.Notifications.Services;
using TigerCS.Domain.Infrastructure;
using TigerCS.Domain.Modules.Notifications;
using TigerCS.Infrastructure.Modules.SlaAndEscalation.Seed;
using TigerCS.Infrastructure.Modules.Ticketing.Seed;
using TigerCS.Integrations.Modules.EmailIntegration;
using TigerCS.Infrastructure.Persistence;
using TigerCS.Tests.CustomerVerification.Fakes;

namespace TigerCS.Tests.IdentityAndAccess.Integration;

/// <summary>
/// Hosts the real Api in-process for 401/403 smoke tests, with the SQL
/// Server-backed DbContext swapped for a per-instance EF Core InMemory
/// database — no real SQL Server is needed to run this test class.
/// </summary>
public sealed class TigerCsApiFactory : WebApplicationFactory<Program>
{
    public readonly string DatabaseName = Guid.NewGuid().ToString();

    /// <summary>
    /// Extra configuration applied on top of this factory's test defaults,
    /// set at construction (before the host is built). Lets an end-to-end
    /// test re-point a real integration at a local stub — e.g.
    /// <c>Pact:Provider=Http</c> plus a stub PACT server's
    /// <c>PactApi:BaseUrl</c>/<c>PactApi:ApiKey</c>
    /// (<c>PactHttpLookupEndToEndTests</c>) — without touching any committed
    /// appsettings file.
    /// </summary>
    public Dictionary<string, string?> ExtraConfiguration { get; init; } = [];

    /// <summary>
    /// The <c>Crm:Provider</c> this host runs on. "Mock" (the default) pins
    /// MockCrmGateway's fixture data, which every unit/contact-lookup and
    /// customer-search test is written against. A test proving that the real
    /// environments' standard "Http" never serves fixture data sets "Http"
    /// (CrmHttpProviderEndpointsTests).
    /// </summary>
    public string CrmProvider { get; init; } = "Mock";

    /// <summary>Extra test-only service registrations, applied last (e.g. a fixed clock for one module).</summary>
    public Action<IServiceCollection>? ExtraServices { get; init; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Explicit, deterministic environment name — distinct from "Development"
        // (so DevSeedData's own auto-seeding doesn't also run and race with this
        // factory's manual seeding) and from "Production" (Program.cs now
        // refuses to start at all in Production, per review item 8).
        builder.UseEnvironment("Testing");

        // ADR-0015's Hangfire server provisions its own SQL Server schema on
        // startup, which this InMemory-backed host has no database for.
        //
        // Set through UseSetting rather than ConfigureAppConfiguration
        // because the background-job registration reads this value while
        // services are being registered, and ConfigureAppConfiguration's
        // sources are not merged into builder.Configuration until the host is
        // built — the same eager-versus-lazy ordering AddTigerCsInfrastructure
        // already documents for its own connection-string read. UseSetting
        // writes into builder.Configuration immediately, exactly as
        // UseEnvironment above does.
        //
        // Breach detection itself stays fully wired:
        // SlaBreachDetectionAppService is registered and exercised unchanged,
        // and the scheduler becomes NoOpSlaDeadlineScheduler. This governs job
        // execution, never job logic.
        builder.UseSetting("BackgroundJobs:Enabled", "false");

        // "Http" is the standard Crm:Provider for every real environment
        // (appsettings.json), where the two generic CRM ports fail closed
        // until Tiger CRM publishes their endpoints
        // (UnimplementedCrmHttpGateway). This host must not depend on that
        // deployment default: every test that touches unit/contact lookup or
        // customer search is written against MockCrmGateway's fixture, so
        // "Mock" is pinned explicitly here (CrmProvider) — the same
        // test-double swap as the ICrmBuyerLookupGateway replacement below
        // and the InMemory DbContext. UseSetting for the same eager-read
        // reason as above.
        builder.UseSetting("Crm:Provider", CrmProvider);

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:TigerCsDatabase"] = "Server=(unused-for-tests);Database=(unused-for-tests);",
                ["Jwt:Issuer"] = "TigerCS.Tests",
                ["Jwt:Audience"] = "TigerCS.Tests.Client",
                ["Jwt:SigningKey"] = "test-only-signing-key-at-least-32-characters-long-1234567890",
                ["Jwt:ExpirationMinutes"] = "60",
                // The customer email pipeline runs end-to-end against the
                // in-memory RecordingEmailSender: enabled, but nothing ever
                // leaves the process (EmailSenderSafety allows Recording in
                // the "Testing" environment).
                ["EmailNotifications:Enabled"] = "true",
                // The shipped default is test mode (all customer mail redirected to TestEmail). The integration host keeps the pre-existing
                // customer behaviour so the lifecycle tests keep proving it; tests of Test mode override this through
                // ExtraConfiguration. Public base URL: where the review links in the emails point.
                ["CustomerNotifications:IsTestMode"] = "false",
                ["CustomerNotifications:PublicBaseUrl"] = "https://tickets.example.com",
                ["EmailNotifications:Provider"] = "Recording",
                // Deterministic, offline test defaults regardless of what
                // the deployable appsettings.json ships (production now sets
                // Pact:Provider=Http with a real internal PACT address —
                // unreachable from CI, where it would turn every Pact
                // lookup into a 15s timeout reported as Failed). Tests that
                // exercise the real HTTP path (PactHttpLookupEndToEndTests)
                // override these via ExtraConfiguration, which is applied
                // after and therefore wins.
                // The shipped appsettings.json selects "Http" for both Crm and
                // Pact in every real environment. The integration host pins
                // both to Mock by default (Crm through CrmProvider, so one test
                // class can opt into "Http") — a test must never depend on
                // which adapter the deployed config names.
                ["Crm:Provider"] = CrmProvider,
                ["Pact:Provider"] = "Mock",
                // Isolation from any deployable or developer appsettings: the verification-test redirection, the customer test mailbox/phone and the SMTP
                // credentials are blank here, so a mailbox or number committed to a config file can never become the recipient a test observes (CRM documents,
                // OTP and notification mail go to the CRM / fixture address the test set up, and only into RecordingEmailSender). The tests that cover the
                // redirection itself switch it on through ExtraConfiguration, which is applied after and wins.
                ["VerificationTesting:Enabled"] = "false",
                ["VerificationTesting:EmailOverride"] = "",
                ["VerificationTesting:SmsOverride"] = "",
                ["CustomerNotifications:TestEmail"] = "",
                ["CustomerNotifications:TestPhone"] = "",
                ["EmailNotifications:Password"] = "",
                ["PactApi:BaseUrl"] = "",
                ["PactApi:ApiKey"] = "",
                // The Genesys integration ships switched OFF (appsettings.json)
                // and is switched on here so the endpoint tests exercise the
                // real ingestion path rather than the disabled short-circuit.
                // A test that needs it off again overrides this through
                // ExtraConfiguration, which is applied after and therefore
                // wins — see GenesysDisabledEndpointsTests.
                ["Genesys:Enabled"] = "true"
            });

            if (ExtraConfiguration.Count > 0)
            {
                config.AddInMemoryCollection(ExtraConfiguration);
            }
        });

        builder.ConfigureServices(services =>
        {
            // Program.cs registers TigerCsDbContext against SqlServer. Swapping in
            // the InMemory provider for tests requires removing every descriptor
            // that registration added — EF Core refuses to build its internal
            // service provider if two database providers are both registered,
            // so trimming only the DbContextOptions<T> entry is not enough.
            var efCoreDescriptors = services
                .Where(d => (d.ServiceType.FullName ?? string.Empty).Contains("EntityFrameworkCore", StringComparison.Ordinal)
                    || (d.ServiceType == typeof(TigerCsDbContext))
                    || (d.ServiceType == typeof(DbContextOptions<TigerCsDbContext>))
                    || (d.ServiceType == typeof(DbContextOptions)))
                .ToList();
            foreach (var descriptor in efCoreDescriptors)
            {
                services.Remove(descriptor);
            }

            // The production run lock is a SQL Server application lock; this host has no SQL Server (the connection string is a placeholder), so the
            // admin backfill/reconciliation endpoints would fail to connect. Swap in a lock that keeps only the in-process single-flight gate.
            services.RemoveAll<TigerCS.Application.Modules.Ticketing.Abstractions.ICallCenterReconciliationRunLock>();
            services.AddSingleton<TigerCS.Application.Modules.Ticketing.Abstractions.ICallCenterReconciliationRunLock, InProcessRunLock>();

            services.AddDbContext<TigerCsDbContext>(options => options
                .UseInMemoryDatabase(DatabaseName)
                // The InMemory provider does not support real transactions
                // (TicketingUnitOfWork.BeginTransactionAsync, added by the
                // senior review's atomicity fix — item 11) and logs
                // TransactionIgnoredWarning, which EF Core escalates to an
                // exception by default. Real SQL Server (Program.cs's own
                // registration, untouched here) does support transactions
                // and never raises this warning — this suppression is
                // test-only, matching the same InMemory-vs-real-SQL-Server
                // testing split already established for unique-index
                // enforcement elsewhere in this codebase (see
                // CustomerVerificationUnitOfWork's remarks).
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));

            // CrmBuyerHttpGateway (Program.cs -> AddTigerCsIntegrations) is a
            // real HTTP client with no test/mock provider switch of its own
            // (unlike ICrmGateway's MockCrmGateway) — this host has no real
            // CRM to call, so it's swapped for a deterministic in-memory
            // double here, the same way the SQL Server DbContext above is
            // swapped for the InMemory provider. Production wiring
            // (IntegrationsServiceCollectionExtensions) is untouched.
            services.RemoveAll<ICrmBuyerLookupGateway>();
            // Registered as a singleton (rather than one fresh instance per
            // scope, as the fixture predates this) so a test can hold onto
            // the same instance across separate requests/scopes and assert
            // its CallCount never rises while loading Customer History —
            // proof that history never issues a live CRM call.
            services.AddSingleton(_ => new FakeCrmBuyerLookupGateway().Returns(
                CrmBuyerLookupResult.Success(
                [
                    new CrmBuyerMatchDto(
                        new CrmCustomerDto(9001, "Test Buyer", null, "+971500000900", "buyer@example.test"),
                        [
                            new CrmBuyerUnitDto(
                                LeadId: 9100, LeadStatus: 8, LeadStatusName: "Sold", UnitId: 9200,
                                UnitNumber: "1204", UnitStatus: 3, UnitType: 2, FloorNumber: 12, ProjectId: 79,
                                ProjectName: "Tiger Tower", ProjectArabicName: null, CustomerType: 1,
                                CustomerTypeName: "Buyer")
                        ])
                ])));
            services.AddScoped<ICrmBuyerLookupGateway>(sp => sp.GetRequiredService<FakeCrmBuyerLookupGateway>());

            ExtraServices?.Invoke(services);
        });
    }

    /// <summary>Seeds roles + one employee with the given role, returning (username, password, employeeId).</summary>
    public async Task<(string Username, string Password, Guid EmployeeId)> SeedEmployeeAsync(string roleName)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
        await db.Database.EnsureCreatedAsync();

        // The channel catalogue the intake endpoint validates against —
        // the same reference rows the AddChannels migration inserts.
        await ChannelReferenceData.SeedAsync(db);

        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        foreach (var roleToSeed in Roles.All)
        {
            if (!await roleManager.RoleExistsAsync(roleToSeed))
            {
                await roleManager.CreateAsync(new ApplicationRole(roleToSeed, roleToSeed));
            }
        }

        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var username = $"user-{Guid.NewGuid():N}";
        const string password = "Test-Password-1!";
        var user = new ApplicationUser { UserName = username, Email = username };
        var createResult = await userManager.CreateAsync(user, password);
        if (!createResult.Succeeded)
        {
            throw new InvalidOperationException(string.Join("; ", createResult.Errors.Select(e => e.Description)));
        }

        await userManager.AddToRoleAsync(user, roleName);

        db.Employees.Add(new Employee(user.Id, "Test Employee", isGeynessStaff: false, DateTime.UtcNow));
        await db.SaveChangesAsync();

        return (username, password, user.Id);
    }

    /// <summary>Gives an existing test employee a further role (a person may hold several).</summary>
    public async Task AddRoleAsync(Guid employeeId, string roleName)
    {
        using var scope = Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await userManager.FindByIdAsync(employeeId.ToString())
            ?? throw new InvalidOperationException("No such test employee.");
        await userManager.AddToRoleAsync(user, roleName);
    }

    /// <summary>
    /// Records a confirmed, <b>agent-asserted</b> verification session (method Otp, but without the server-recorded proof the OTP
    /// service attaches) for <paramref name="employeeId"/> and the CRM unit — the shape a forged or merely asserted session has.
    /// It must never release private data. Test setup.
    /// </summary>
    public async Task<Guid> SeedConfirmedVerificationSessionAsync(
        Guid employeeId, string crmUnitId, VerificationMethod method = VerificationMethod.Otp)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
        var now = DateTime.UtcNow;
        var unit = new UnitReference(crmUnitId, "1204", "Tiger Tower", null, null, now);
        db.Add(unit);
        await db.SaveChangesAsync();
        var contact = new ContactReference("c-" + Guid.NewGuid().ToString("N")[..8], unit.UnitReferenceId, "Test Buyer", null, ContactType.Owner, null, now);
        db.Add(contact);
        await db.SaveChangesAsync();
        var session = new VerificationSession(
            Guid.NewGuid(), employeeId, unit.UnitReferenceId, contact.ContactReferenceId, "1204", null, null, null, null, null,
            now, now.AddMinutes(30), null);
        session.Confirm(now, method);
        db.Add(session);
        await db.SaveChangesAsync();
        return session.VerificationSessionId;
    }

    /// <summary>
    /// Maps a Ticketing user to a Genesys agent — the same administrative act
    /// UAT performs directly on <c>AspNetUsers</c> (see
    /// docs/architecture/Genesys-API-Contracts.md), done here through the
    /// Identity store. Test setup, not the thing under test: no endpoint
    /// creates or edits this mapping.
    /// </summary>
    public async Task MapGenesysAgentAsync(Guid employeeId, string genesysUserId, string? genesysEmail = null)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
        var user = await db.Users.FirstAsync(u => u.Id == employeeId);
        user.GenesysUserId = genesysUserId;
        user.GenesysEmail = genesysEmail;
        await db.SaveChangesAsync();
    }

    /// <summary>How many Ticketing users carry this Genesys User ID — the proof that an unmapped agent never provisioned one.</summary>
    public async Task<int> CountUsersMappedToAsync(string genesysUserId)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
        return await db.Users.CountAsync(u => u.GenesysUserId == genesysUserId);
    }

    /// <summary>Deactivates an employee directly — the existing retirement path (never a delete).</summary>
    public async Task DeactivateEmployeeAsync(Guid employeeId)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
        var employee = await db.Employees.FirstAsync(e => e.EmployeeId == employeeId);
        employee.Deactivate(DateTime.UtcNow);
        await db.SaveChangesAsync();
    }

    /// <summary>Reads the interaction recorded for one Genesys conversation, for assertions on stored ownership rather than on the API projection.</summary>
    public async Task<TicketInteraction?> GetInteractionByConversationAsync(string conversationId)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
        return await db.TicketInteractions.AsNoTracking().FirstOrDefaultAsync(i => i.GenesysConversationId == conversationId);
    }

    /// <summary>Reads a ticket's stored owner, so a test can prove that recording an interaction's handler never moved the ticket's assignment.</summary>
    public async Task<Guid?> GetTicketCurrentOwnerAsync(long ticketId)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
        return (await db.Tickets.AsNoTracking().FirstAsync(t => t.TicketId == ticketId)).CurrentOwnerEmployeeId;
    }

    /// <summary>Creates a department directly (bypassing the app services — this is test setup, not the thing under test).</summary>
    public async Task<int> CreateDepartmentAsync(string name, string code)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
        var department = new Department(name, code);
        db.Departments.Add(department);
        await db.SaveChangesAsync();
        return department.DepartmentId;
    }

    /// <summary>Adds a NON-primary department membership (a user in several departments).</summary>
    public async Task AssignDepartmentAsync(Guid employeeId, int departmentId)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
        db.UserDepartmentAssignments.Add(new UserDepartmentAssignment(
            employeeId, departmentId, isPrimary: false, DateTime.UtcNow, assignedByEmployeeId: null));
        await db.SaveChangesAsync();
    }

    /// <summary>Assigns an employee to a department (primary), replacing any existing primary assignment.</summary>
    public async Task AssignPrimaryDepartmentAsync(Guid employeeId, int departmentId)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();

        var existingPrimary = await db.UserDepartmentAssignments
            .FirstOrDefaultAsync(a => a.EmployeeId == employeeId && a.IsPrimary);
        existingPrimary?.ClearPrimary();

        db.UserDepartmentAssignments.Add(new UserDepartmentAssignment(
            employeeId, departmentId, isPrimary: true, DateTime.UtcNow, assignedByEmployeeId: null));
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Creates a request type in the given department (with its own workflow
    /// template) and, when an approval type is given, an active approval
    /// requirement for it targeting the given department — direct test
    /// setup for the phase-3 approval flow, bypassing the app services.
    /// </summary>
    /// <summary>
    /// <paramref name="approvalTargetRoleName"/> configures the requirement as
    /// role-targeted instead of department-targeted, and suppresses the
    /// Waiting-for-Approval step: ReopenApproval is a request raised against a
    /// finished ticket, not a stage in the ticket's forward workflow.
    /// </summary>
    public async Task<int> CreateRequestTypeAsync(
        string name, int departmentId, TigerCS.Domain.Modules.WorkflowConfiguration.ApprovalType? requiredApproval = null,
        int? approvalTargetDepartmentId = null, string? approvalTargetRoleName = null, bool allowReopen = true)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();

        // A logical workflow with one Published version — the version every
        // ticket of this request type pins at creation.
        var code = Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        var workflow = new TigerCS.Domain.Modules.WorkflowConfiguration.Workflow(code, "Test Approval Workflow", null, DateTime.UtcNow);
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync();

        var version = new TigerCS.Domain.Modules.WorkflowConfiguration.WorkflowTemplate(
            workflow.WorkflowId, 1, code, "Test Approval Template", null,
            allowsPendingCustomer: true, allowsPendingInternal: true, requiresApproval: requiredApproval is not null,
            DateTime.UtcNow, createdByEmployeeId: null);
        version.AppendStep("Ticket Created", TigerCS.Domain.Modules.WorkflowConfiguration.WorkflowStepKind.Created);
        version.AppendStep("Assigned", TigerCS.Domain.Modules.WorkflowConfiguration.WorkflowStepKind.Assigned);
        if (requiredApproval is { } approvalStepType && approvalTargetRoleName is null)
        {
            version.AppendStep("Waiting for Approval", TigerCS.Domain.Modules.WorkflowConfiguration.WorkflowStepKind.WaitingForApproval,
                approvalType: approvalStepType);
        }

        version.AppendStep("In Progress", TigerCS.Domain.Modules.WorkflowConfiguration.WorkflowStepKind.InProgress);
        version.AppendStep("Pending Customer", TigerCS.Domain.Modules.WorkflowConfiguration.WorkflowStepKind.PendingCustomer, isOptional: true);
        version.AppendStep("Pending Internal / Third Party", TigerCS.Domain.Modules.WorkflowConfiguration.WorkflowStepKind.PendingInternal, isOptional: true);
        version.AppendStep("Resolved", TigerCS.Domain.Modules.WorkflowConfiguration.WorkflowStepKind.Resolved);
        version.AppendStep("Closed", TigerCS.Domain.Modules.WorkflowConfiguration.WorkflowStepKind.Closed);
        version.Publish(DateTime.UtcNow, publishedByEmployeeId: null);
        db.WorkflowTemplates.Add(version);
        await db.SaveChangesAsync();

        var requestType = new TigerCS.Domain.Modules.WorkflowConfiguration.RequestType(
            departmentId, name, workflow.WorkflowId,
            (byte)TigerCS.Domain.Modules.SlaAndEscalation.PriorityLevel.Medium,
            allowAgentPriorityChange: false, allowPendingCustomer: true, allowPendingInternal: true, allowReopen);
        db.RequestTypes.Add(requestType);
        await db.SaveChangesAsync();

        if (requiredApproval is { } approvalType)
        {
            db.RequestTypeApprovalRequirements.Add(
                approvalTargetRoleName is { } roleName
                    ? TigerCS.Domain.Modules.WorkflowConfiguration.RequestTypeApprovalRequirement.ForRole(
                        requestType.RequestTypeId, approvalType, roleName)
                    : TigerCS.Domain.Modules.WorkflowConfiguration.RequestTypeApprovalRequirement.ForDepartment(
                        requestType.RequestTypeId, approvalType, approvalTargetDepartmentId ?? departmentId));
            await db.SaveChangesAsync();
        }

        return requestType.RequestTypeId;
    }

    /// <summary>Creates a category routed to the given department (bypassing the app services — test setup, not the thing under test).</summary>
    public async Task<int> CreateCategoryAsync(string name, int departmentId, bool isActive = true)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
        var category = new Category(name, departmentId, isActive: isActive);
        db.Categories.Add(category);
        await db.SaveChangesAsync();
        return category.CategoryId;
    }

    /// <summary>Configures a Department's customer-lookup source(s) directly (bypassing the app services — test setup, not the thing under test).</summary>
    public async Task SeedDepartmentCustomerLookupSourceAsync(int departmentId, params CustomerLookupSource[] sources)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
        foreach (var source in sources)
        {
            db.DepartmentCustomerLookupSources.Add(new DepartmentCustomerLookupSource(departmentId, source));
        }

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Seeds the fixed MVP priority set and its SLA reference data
    /// (idempotent), needed since this factory does not run DevSeedData.
    ///
    /// <para>
    /// The SLA policies and business calendar come from
    /// <see cref="SlaReferenceData"/> — the same source a real deployment
    /// seeds from — rather than being restated here, so an endpoint test can
    /// never pass against target values that differ from the approved ones.
    /// </para>
    /// </summary>
    public async Task SeedPrioritiesAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();

        if (!await db.Priorities.AnyAsync())
        {
            foreach (var level in Enum.GetValues<PriorityLevel>())
            {
                db.Priorities.Add(new Priority((byte)level, level.ToString(), (byte)level));
            }

            await db.SaveChangesAsync();
        }

        await SlaReferenceData.SeedAsync(db);
    }

    /// <summary>Reads a ticket's current SLA period directly, for assertions that need the stored due dates rather than the API projection.</summary>
    public async Task<TicketSlaInstance?> GetCurrentSlaInstanceAsync(long ticketId)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
        return await db.TicketSlaInstances.FirstOrDefaultAsync(i => i.TicketId == ticketId && i.PeriodEndAtUtc == null);
    }

    /// <summary>Rewrites a ticket's current SLA due timestamps, so a test can put a deadline in the past without waiting for one.</summary>
    public async Task ForceSlaDueDatesAsync(long ticketId, DateTime firstResponseDueAtUtc, DateTime resolutionDueAtUtc)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();

        var instance = await db.TicketSlaInstances.FirstAsync(i => i.TicketId == ticketId && i.PeriodEndAtUtc == null);

        // Test setup reaching past the domain's own write-once surface on
        // purpose: TicketSlaInstance exposes no due-date setter, which is
        // exactly the property under test everywhere else.
        db.Entry(instance).Property(nameof(TicketSlaInstance.FirstResponseDueAtUtc)).CurrentValue = firstResponseDueAtUtc;
        db.Entry(instance).Property(nameof(TicketSlaInstance.ResolutionDueAtUtc)).CurrentValue = resolutionDueAtUtc;

        await db.SaveChangesAsync();
    }

    /// <summary>Every escalation recorded against a ticket, newest first.</summary>
    public async Task<IReadOnlyList<TicketEscalation>> GetEscalationsAsync(long ticketId)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
        return await db.TicketEscalations
            .Where(e => e.TicketId == ticketId)
            .OrderByDescending(e => e.TicketEscalationId)
            .ToListAsync();
    }

    /// <summary>Audit entries recorded against a ticket, for the audit assertions.</summary>
    public async Task<IReadOnlyList<AuditEntry>> GetAuditEntriesAsync(string entityId)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
        return await db.AuditEntries.Where(a => a.EntityId == entityId).ToListAsync();
    }

    /// <summary>Runs a breach check exactly as a fired scheduled job or a sweep tick would, without a scheduler present.</summary>
    public async Task<SlaBreachProcessingOutcome> RunDeadlineCheckAsync(long ticketId, SlaDeadlineType deadlineType)
    {
        using var scope = Services.CreateScope();
        var detection = scope.ServiceProvider.GetRequiredService<SlaBreachDetectionAppService>();
        return await detection.CheckDeadlineAsync(ticketId, deadlineType);
    }

    /// <summary>Runs SLA-Architecture.md §14's safety sweep against the whole host.</summary>
    public async Task<SlaSweepResult> RunSlaSweepAsync()
    {
        using var scope = Services.CreateScope();
        var detection = scope.ServiceProvider.GetRequiredService<SlaBreachDetectionAppService>();
        return await detection.SweepAsync();
    }

    /// <summary>
    /// Runs one pass of ADR-0013's Outbox dispatcher exactly as the recurring
    /// Hangfire job would, without a scheduler present.
    ///
    /// <para>
    /// <c>BackgroundJobs:Enabled</c> is false in this host (no SQL Server for
    /// Hangfire's own schema), which governs job <i>execution</i> only — the
    /// dispatcher, its handlers and the email adapter are all registered and
    /// exercised here unchanged.
    /// </para>
    /// </summary>
    public async Task<OutboxDispatchResult> RunOutboxDispatchAsync()
    {
        using var scope = Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<OutboxDispatcher>().DispatchPendingAsync();
    }

    /// <summary>The development email adapter, so a test can assert what would actually have been delivered.</summary>
    public RecordingEmailSender EmailSender => Services.GetRequiredService<RecordingEmailSender>();

    /// <summary>The single, shared CRM Buyer Lookup double for this host — its <c>CallCount</c> proves whether a live CRM call actually happened.</summary>
    public FakeCrmBuyerLookupGateway CrmBuyerLookupGateway => Services.GetRequiredService<FakeCrmBuyerLookupGateway>();

    public async Task<IReadOnlyList<OutboxMessage>> GetOutboxMessagesAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
        return await db.OutboxMessages.OrderBy(m => m.OccurredAtUtc).ToListAsync();
    }

    public async Task<IReadOnlyList<Notification>> GetNotificationsAsync(long ticketId)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
        return await db.Notifications.Where(n => n.TicketId == ticketId).ToListAsync();
    }

    public async Task<IReadOnlyList<IdempotencyRecord>> GetIdempotencyRecordsAsync(string scopeName)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
        return await db.IdempotencyRecords.Where(r => r.Scope == scopeName).ToListAsync();
    }

    /// <summary>
    /// Records <paramref name="userId"/> as having handled the ticket's (first) interaction — what a Genesys connect event does in
    /// production — and records them as the ticket's responsible CS Agent, so they can follow a ticket a department employee works. Tests whose
    /// subject is something other than visibility use it to say "this agent took the call".
    /// </summary>
    public async Task RecordHandlerAsync(long ticketId, Guid userId)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
        var interactionId = await db.TicketInteractions.Where(i => i.TicketId == ticketId)
            .OrderBy(i => i.TicketInteractionId).Select(i => i.TicketInteractionId).FirstAsync();
        db.TicketInteractionHandlers.Add(TicketInteractionHandler.Create(
            interactionId, ticketId, userId, "test-genesys-" + userId.ToString("N")[..8],
            InteractionHandlerSource.Routing, connectedAtUtc: null, DateTime.UtcNow));

        // Handling alone grants no access (a CS Agent sees only tickets assigned to them, or that a department employee holds and they are
        // responsible for). So the test actor also becomes the ticket's responsible CS Agent the legitimate way: a superseded assignment row on
        // its history. The ticket row itself (and so its RowVersion and owner) is left untouched; use AssignOwnerAsync to make them the owner.
        var ticketDepartmentId = await db.Tickets.Where(t => t.TicketId == ticketId).Select(t => t.CurrentDepartmentId).FirstAsync();
        var history = new TicketAssignment(ticketId, userId, ticketDepartmentId, DateTime.UtcNow, assigningActorEmployeeId: null);
        history.MarkSuperseded();
        db.TicketAssignments.Add(history);

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Makes <paramref name="userId"/> the ticket's current owner (as an unassigned ticket's first assignment): the CS Agent a ticket is
    /// assigned to. This changes the ticket's RowVersion - re-read it before a request that carries one.
    /// </summary>
    public async Task AssignOwnerAsync(long ticketId, Guid userId)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
        var ticket = await db.Tickets.FirstAsync(t => t.TicketId == ticketId);
        ticket.AssignTo(userId);
        (await db.TicketAssignments.FirstOrDefaultAsync(a => a.TicketId == ticketId && a.IsCurrent))?.MarkSuperseded();
        db.TicketAssignments.Add(new TicketAssignment(ticketId, userId, ticket.CurrentDepartmentId, DateTime.UtcNow, assigningActorEmployeeId: null));
        await db.SaveChangesAsync();
    }

    /// <summary>Same, for the user a logged-in test client authenticates as (read from its bearer token).</summary>
    public Task RecordHandlerAsync(long ticketId, HttpClient authenticatedClient) =>
        RecordHandlerAsync(ticketId, UserIdOf(authenticatedClient));

    /// <summary>The employee id inside a test client's bearer token.</summary>
    public static Guid UserIdOf(HttpClient authenticatedClient)
    {
        var token = authenticatedClient.DefaultRequestHeaders.Authorization?.Parameter
            ?? throw new InvalidOperationException("The client is not authenticated.");
        var jwt = new Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler().ReadJsonWebToken(token);
        var id = jwt.Claims.First(c => c.Type is System.Security.Claims.ClaimTypes.NameIdentifier or "nameid" or "sub").Value;
        return Guid.Parse(id);
    }

    public async Task<Ticket?> GetTicketAsync(long ticketId)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
        return await db.Tickets.AsNoTracking().FirstOrDefaultAsync(t => t.TicketId == ticketId);
    }

    /// <summary>Audit entries recorded against an Outbox message id, for the notification-audit assertions.</summary>
    public async Task<IReadOnlyList<AuditEntry>> GetNotificationAuditAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
        return await db.AuditEntries.Where(a => a.Action.StartsWith("Notification")).ToListAsync();
    }
}

/// <summary>Test-host run lock: the in-process gate only (no SQL Server application lock).</summary>
internal sealed class InProcessRunLock : TigerCS.Application.Modules.Ticketing.Abstractions.ICallCenterReconciliationRunLock
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IAsyncDisposable?> TryAcquireAsync(CancellationToken cancellationToken = default) =>
        await _gate.WaitAsync(0, cancellationToken) ? new Release(_gate) : null;

    private sealed class Release(SemaphoreSlim gate) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() { gate.Release(); return ValueTask.CompletedTask; }
    }
}
