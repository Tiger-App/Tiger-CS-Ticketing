using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Tests.GenesysIntegration.Fakes;

namespace TigerCS.Tests.GenesysIntegration.Services;

/// <summary>
/// The Create Ticket contract's optional fields: only <c>conversationId</c>
/// and <c>channel</c> are required, every Genesys-supplied context field may
/// be absent, and an absent field is stored as absent — never replaced by a
/// fake or default value. Department resolution stays
/// departmentId → departmentCode → mapped queueId, with no default department.
/// </summary>
public class GenesysCreateTicketOptionalFieldsTests
{
    private static readonly Guid ServiceAccount = Guid.NewGuid();

    private static string NewConversation() => "conv-" + Guid.NewGuid().ToString("N");

    // ---- Optional fields ----

    [Fact]
    public async Task WithoutQueueId_WhenDepartmentIdIsSupplied_CreatesTheTicketInThatDepartment()
    {
        var f = new GenesysServiceFixture();
        var (department, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        var conversationId = NewConversation();

        var result = await f.Ingestion.IngestAsync(
            ServiceAccount, new GenesysInquiryDto(conversationId, GenesysChannel.WebsiteChat, DepartmentId: department.DepartmentId));

        Assert.Equal(GenesysIngestionOutcome.TicketCreated, result.Outcome);
        Assert.Equal(department.DepartmentId, Assert.Single(f.Tickets.All).OriginatingDepartmentId);
        Assert.Null(f.InteractionFor(conversationId)!.GenesysQueueId);
    }

    [Fact]
    public async Task WithoutQueueName_CreatesTheTicket_AndStoresNoQueueName()
    {
        var f = new GenesysServiceFixture();
        var (department, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        f.QueueMappings.Map("genesys-queue-cs", department.DepartmentId);
        var conversationId = NewConversation();

        var result = await f.Ingestion.IngestAsync(
            ServiceAccount, new GenesysInquiryDto(conversationId, GenesysChannel.Phone, QueueId: "genesys-queue-cs"));

        Assert.Equal(GenesysIngestionOutcome.TicketCreated, result.Outcome);
        var interaction = f.InteractionFor(conversationId)!;
        Assert.Equal("genesys-queue-cs", interaction.GenesysQueueId);
        Assert.Null(interaction.GenesysQueueName);
    }

    [Fact]
    public async Task WithoutTowerOrUnit_CreatesTheTicket_WithNoProjectOrUnitSnapshot()
    {
        var f = new GenesysServiceFixture();
        var (department, _) = f.SeedGenesysDepartment("Customer Service", "CS");

        var result = await f.Ingestion.IngestAsync(
            ServiceAccount, new GenesysInquiryDto(NewConversation(), GenesysChannel.WebsiteChat, DepartmentId: department.DepartmentId));

        Assert.Equal(GenesysIngestionOutcome.TicketCreated, result.Outcome);
        var ticket = Assert.Single(f.Tickets.All);
        Assert.Null(ticket.ManualProjectName);
        Assert.Null(ticket.ManualUnitNumber);
        Assert.Null(Assert.Single(f.IntakeRecords.All).RawUnitNumberEntered);
    }

    [Theory]
    [InlineData(GenesysChannel.Phone, "Phone call received via Genesys")]
    [InlineData(GenesysChannel.WebsiteChat, "Website chat started via Genesys")]
    [InlineData(GenesysChannel.WhatsApp, "WhatsApp conversation started via Genesys")]
    [InlineData(GenesysChannel.SocialMedia, "Social media conversation started via Genesys")]
    [InlineData(GenesysChannel.Instagram, "Instagram conversation started via Genesys")]
    [InlineData(GenesysChannel.Facebook, "Facebook Messenger conversation started via Genesys")]
    public async Task WithoutSubject_CreatesTheTicket_WithTheFactualChannelSummary(GenesysChannel channel, string expectedSummary)
    {
        var f = new GenesysServiceFixture();
        var (department, _) = f.SeedGenesysDepartment("Customer Service", "CS");

        var result = await f.Ingestion.IngestAsync(
            ServiceAccount, new GenesysInquiryDto(NewConversation(), channel, DepartmentId: department.DepartmentId));

        Assert.Equal(GenesysIngestionOutcome.TicketCreated, result.Outcome);
        Assert.Equal(expectedSummary, Assert.Single(f.Tickets.All).RequestSummary);
    }

    [Fact]
    public async Task OnlyTheRequiredFieldsAndADepartment_WritesNoFakeOrDefaultValues()
    {
        var f = new GenesysServiceFixture();
        var (department, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        var conversationId = NewConversation();

        var result = await f.Ingestion.IngestAsync(
            ServiceAccount,
            new GenesysInquiryDto(conversationId, GenesysChannel.Phone, CustomerPhone: null, DepartmentId: department.DepartmentId));

        Assert.Equal(GenesysIngestionOutcome.TicketCreated, result.Outcome);

        var interaction = f.InteractionFor(conversationId)!;
        Assert.Null(interaction.GenesysQueueId);
        Assert.Null(interaction.GenesysQueueName);
        Assert.Null(interaction.GenesysAgentId);
        Assert.Null(interaction.GenesysAgentName);
        Assert.Null(interaction.GenesysAgentUserId);
        Assert.Null(interaction.HandledByUserId);
        Assert.Null(interaction.CalledNumber);
        Assert.Null(interaction.Direction);
        Assert.Null(interaction.InteractionStartedAtUtc);
        Assert.Null(interaction.CustomerName);
        Assert.Null(interaction.CustomerEmail);
        Assert.Equal(string.Empty, interaction.CustomerPhone);

        var ticket = Assert.Single(f.Tickets.All);
        Assert.Null(ticket.ManualProjectName);
        Assert.Null(ticket.ManualUnitNumber);
        Assert.Null(ticket.CategoryId);
        Assert.Null(ticket.RequestTypeId);
        // The one value written for the caller: the configured default (Normal) priority.
        Assert.Equal((byte)PriorityLevel.Medium, ticket.PriorityId);
    }

    [Fact]
    public async Task TheDocumentedFullPayload_StoresEverySuppliedValueVerbatim()
    {
        var f = new GenesysServiceFixture();
        var (department, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        var startedAt = new DateTime(2026, 9, 10, 9, 31, 0, DateTimeKind.Utc);

        var result = await f.Ingestion.IngestAsync(ServiceAccount, new GenesysInquiryDto(
            "8f2c1e40-3d2a-4b1c-9e77-1a2b3c4d5e6f", GenesysChannel.WebsiteChat,
            Direction: "Inbound",
            CustomerPhone: "+971501234567",
            CustomerName: "Ahmed Ali",
            CustomerEmail: "ahmed@example.com",
            QueueId: "genesys-queue-cs",
            QueueName: "Customer Service",
            AgentId: "ga-7",
            AgentName: "Layla",
            StartedAtUtc: startedAt,
            DepartmentId: department.DepartmentId,
            TowerName: "Tiger Tower A",
            UnitNumber: "1204",
            Subject: "NOC for resale"));

        Assert.Equal(GenesysIngestionOutcome.TicketCreated, result.Outcome);
        var ticket = Assert.Single(f.Tickets.All);
        Assert.Equal("NOC for resale", ticket.RequestSummary);
        Assert.Equal("Tiger Tower A", ticket.ManualProjectName);
        Assert.Equal("1204", ticket.ManualUnitNumber);
        Assert.Equal(department.DepartmentId, ticket.OriginatingDepartmentId);

        var interaction = f.InteractionFor("8f2c1e40-3d2a-4b1c-9e77-1a2b3c4d5e6f")!;
        Assert.Equal("genesys-queue-cs", interaction.GenesysQueueId);
        Assert.Equal("Customer Service", interaction.GenesysQueueName);
        Assert.Equal("ga-7", interaction.GenesysAgentId);
        Assert.Equal("Layla", interaction.GenesysAgentName);
        Assert.Equal("Inbound", interaction.Direction);
        Assert.Equal(startedAt, interaction.InteractionStartedAtUtc);
        Assert.Equal("Ahmed Ali", interaction.CustomerName);
        Assert.Equal("ahmed@example.com", interaction.CustomerEmail);
        Assert.Equal("+971501234567", interaction.CustomerPhone);
    }

    // ---- Department resolution: departmentId → departmentCode → mapped queueId ----

    [Fact]
    public async Task DepartmentCode_ResolvesTheDepartment_WithoutAQueue()
    {
        var f = new GenesysServiceFixture();
        var (department, _) = f.SeedGenesysDepartment("Leasing", "LEASE");

        var result = await f.Ingestion.IngestAsync(
            ServiceAccount, new GenesysInquiryDto(NewConversation(), GenesysChannel.WebsiteChat, DepartmentCode: "lease"));

        Assert.Equal(GenesysIngestionOutcome.TicketCreated, result.Outcome);
        Assert.Equal(department.DepartmentId, Assert.Single(f.Tickets.All).OriginatingDepartmentId);
    }

    [Fact]
    public async Task QueueMapping_ResolvesTheDepartment_WhenNoExplicitDepartmentIsSupplied()
    {
        var f = new GenesysServiceFixture();
        var (department, _) = f.SeedGenesysDepartment("Maintenance", "MNT");
        f.QueueMappings.Map("genesys-queue-mnt", department.DepartmentId);

        var result = await f.Ingestion.IngestAsync(
            ServiceAccount, new GenesysInquiryDto(NewConversation(), GenesysChannel.Phone, QueueId: "genesys-queue-mnt"));

        Assert.Equal(GenesysIngestionOutcome.TicketCreated, result.Outcome);
        Assert.Equal(department.DepartmentId, Assert.Single(f.Tickets.All).OriginatingDepartmentId);
    }

    [Fact]
    public async Task ExplicitDepartmentId_WinsOverTheQueueMapping()
    {
        var f = new GenesysServiceFixture();
        var (chosen, _) = f.SeedGenesysDepartment("Leasing", "LEASE");
        var (mapped, _) = f.SeedGenesysDepartment("Maintenance", "MNT");
        f.QueueMappings.Map("genesys-queue-mnt", mapped.DepartmentId);

        await f.Ingestion.IngestAsync(ServiceAccount, new GenesysInquiryDto(
            NewConversation(), GenesysChannel.WebsiteChat, QueueId: "genesys-queue-mnt", DepartmentId: chosen.DepartmentId));

        Assert.Equal(chosen.DepartmentId, Assert.Single(f.Tickets.All).OriginatingDepartmentId);
    }

    [Fact]
    public async Task InactiveDepartmentId_IsRefused_NamingThatDepartment_AndDoesNotFallBackToTheQueue()
    {
        var f = new GenesysServiceFixture();
        var (inactive, _) = f.SeedGenesysDepartment("Retired", "OLD");
        inactive.Deactivate();
        var (mapped, _) = f.SeedGenesysDepartment("Maintenance", "MNT");
        f.QueueMappings.Map("genesys-queue-mnt", mapped.DepartmentId);

        var result = await f.Ingestion.IngestAsync(ServiceAccount, new GenesysInquiryDto(
            NewConversation(), GenesysChannel.WebsiteChat, QueueId: "genesys-queue-mnt", DepartmentId: inactive.DepartmentId));

        Assert.Equal(GenesysIngestionOutcome.DepartmentNotResolved, result.Outcome);
        Assert.Equal($"Department {inactive.DepartmentId} does not exist or is inactive.", result.Detail);
        Assert.Empty(f.Tickets.All);
    }

    [Fact]
    public async Task UnknownDepartmentId_IsRefused_NamingThatDepartment()
    {
        var f = new GenesysServiceFixture();

        var result = await f.Ingestion.IngestAsync(
            ServiceAccount, new GenesysInquiryDto(NewConversation(), GenesysChannel.WebsiteChat, DepartmentId: 987654));

        Assert.Equal(GenesysIngestionOutcome.DepartmentNotResolved, result.Outcome);
        Assert.Equal("Department 987654 does not exist or is inactive.", result.Detail);
    }

    [Fact]
    public async Task UnknownDepartmentCode_IsRefused_NamingThatCode()
    {
        var f = new GenesysServiceFixture();

        var result = await f.Ingestion.IngestAsync(
            ServiceAccount, new GenesysInquiryDto(NewConversation(), GenesysChannel.WebsiteChat, DepartmentCode: "NOPE"));

        Assert.Equal(GenesysIngestionOutcome.DepartmentNotResolved, result.Outcome);
        Assert.Equal("No active department has the code 'NOPE'.", result.Detail);
    }

    [Fact]
    public async Task UnmappedQueue_IsRefused_NamingTheQueue()
    {
        var f = new GenesysServiceFixture();
        f.SeedGenesysDepartment("Customer Service", "CS");

        var result = await f.Ingestion.IngestAsync(
            ServiceAccount, new GenesysInquiryDto(NewConversation(), GenesysChannel.Phone, QueueId: "genesys-queue-unknown"));

        Assert.Equal(GenesysIngestionOutcome.DepartmentNotResolved, result.Outcome);
        Assert.Equal("Genesys queue 'genesys-queue-unknown' has no active department mapping.", result.Detail);
        Assert.Empty(f.Tickets.All);
    }

    [Fact]
    public async Task NoDepartmentAndNoQueue_IsRefused_AndNoDefaultDepartmentIsUsed()
    {
        var f = new GenesysServiceFixture();
        f.SeedGenesysDepartment("Customer Service", "CS");

        var result = await f.Ingestion.IngestAsync(
            ServiceAccount, new GenesysInquiryDto(NewConversation(), GenesysChannel.Phone, CustomerPhone: "+971501234567"));

        Assert.Equal(GenesysIngestionOutcome.DepartmentNotResolved, result.Outcome);
        Assert.Equal("The inquiry named no department and carried no queue id.", result.Detail);
        Assert.Empty(f.Tickets.All);
    }
}
