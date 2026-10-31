using TigerCS.Application.Modules.CustomerVerification.CrmIntegration;
using TigerCS.Application.Modules.CustomerVerification.Dto;
using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.GenesysIntegration.Fakes;

namespace TigerCS.Tests.GenesysIntegration.Services;

/// <summary>
/// The core business rule of the Genesys phase: <b>any new customer inquiry
/// received through Genesys results in exactly one ticket</b> — one, never
/// zero, never two — and every channel reaches that through the same
/// ingestion flow.
///
/// <para>
/// These run the REAL intake, ticket-creation and customer-lookup services
/// (see <see cref="GenesysServiceFixture"/>), so a ticket created here is a
/// genuine TigerCS ticket with its ticket number, status history, SLA clock
/// and audit trail — not a stub standing in for one.
/// </para>
/// </summary>
public class GenesysInquiryIngestionAppServiceTests
{
    private static readonly Guid ServiceAccount = Guid.NewGuid();

    private static GenesysInquiryDto Inquiry(
        string conversationId,
        GenesysChannel channel = GenesysChannel.Phone,
        string? phone = "+971500000001",
        int? departmentId = null,
        string? departmentCode = null,
        string? queueId = null,
        string? customerName = null,
        string? towerName = null,
        string? unitNumber = null,
        string? subject = null) =>
        new(conversationId, channel,
            CustomerPhone: phone,
            CustomerName: customerName,
            QueueId: queueId,
            DepartmentId: departmentId,
            DepartmentCode: departmentCode,
            TowerName: towerName,
            UnitNumber: unitNumber,
            Subject: subject);

    // ---- 1. A new Genesys inquiry creates exactly one ticket ----

    [Fact]
    public async Task Ingest_NewInquiry_CreatesExactlyOneTicket_LinkedToTheConversation()
    {
        var f = new GenesysServiceFixture();
        var (department, category) = f.SeedGenesysDepartment("Customer Service", "CS");
        f.QueueMappings.Map("queue-cs", department.DepartmentId);

        var result = await f.Ingestion.IngestAsync(
            ServiceAccount, Inquiry("conv-1001", queueId: "queue-cs", subject: "Asking about handover"));

        Assert.Equal(GenesysIngestionOutcome.TicketCreated, result.Outcome);
        var ticket = Assert.Single(f.Tickets.All);
        Assert.Equal(ticket.TicketId, result.Ticket!.TicketId);
        Assert.StartsWith("TG-CS-", ticket.TicketNumber);
        Assert.Equal(department.DepartmentId, ticket.OriginatingDepartmentId);
        Assert.Equal("Asking about handover", ticket.RequestSummary);

        // Unclassified, and honestly so: the department is known, the request
        // is not. No category was invented — not even the department's own
        // (which exists, and is deliberately NOT used).
        Assert.Null(ticket.CategoryId);
        Assert.False(ticket.IsClassified);
        Assert.NotNull(category);

        // The permanent Ticket ↔ conversation link, on the originating interaction.
        var interaction = Assert.Single(f.Interactions.All);
        Assert.True(interaction.IsOriginatingInteraction);
        Assert.Equal(InteractionContextSource.Genesys, interaction.Source);
        Assert.Equal("conv-1001", interaction.GenesysConversationId);
        Assert.Equal(ticket.TicketId, interaction.TicketId);

        // It is a real ticket: the existing creation path ran in full.
        Assert.Equal(TicketStatus.Open, ticket.TicketStatus);

        // The SLA runs from creation on the default (Normal) priority's policy.
        Assert.Equal(SlaState.Running, ticket.SlaState);
        Assert.Single(f.Sla.SlaInstances.All);
        Assert.Contains(f.Audit.Written, w => w.Action == "Create" && w.EntityType == "Ticket");
        Assert.Contains(f.Audit.Written, w => w.Action == "GenesysInquiryIngested" && w.EntityId == "conv-1001");
    }

    // ---- 2. A duplicate ConversationId never creates a second ticket ----

    [Fact]
    public async Task Ingest_SameConversationTwice_ReturnsTheSameTicket_AndCreatesNoSecondTicket()
    {
        var f = new GenesysServiceFixture();
        var (department, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        f.QueueMappings.Map("queue-cs", department.DepartmentId);

        var first = await f.Ingestion.IngestAsync(ServiceAccount, Inquiry("conv-abc123", queueId: "queue-cs"));
        var retry = await f.Ingestion.IngestAsync(ServiceAccount, Inquiry("conv-abc123", queueId: "queue-cs"));
        var thirdDelivery = await f.Ingestion.IngestAsync(ServiceAccount, Inquiry("conv-abc123", queueId: "queue-cs"));

        Assert.Equal(GenesysIngestionOutcome.TicketCreated, first.Outcome);
        Assert.Equal(GenesysIngestionOutcome.AlreadyIngested, retry.Outcome);
        Assert.Equal(GenesysIngestionOutcome.AlreadyIngested, thirdDelivery.Outcome);

        // abc123 -> TCK-1001, and never abc123 -> TCK-1002.
        Assert.Single(f.Tickets.All);
        Assert.Single(f.Interactions.All);
        Assert.Equal(first.Ticket!.TicketId, retry.Ticket!.TicketId);
        Assert.Equal(first.Ticket.TicketId, thirdDelivery.Ticket!.TicketId);
        Assert.Equal(first.Ticket.TicketNumber, retry.Ticket.TicketNumber);
    }

    [Fact]
    public async Task Ingest_SameCustomerNewConversation_CreatesANewTicket_AndNeverTouchesTheEarlierOne()
    {
        // Pins the current behaviour for "the customer contacts us again after
        // an inactivity closure": a new conversation is a new ticket. The
        // earlier ticket is neither reopened nor altered here — reopening an
        // inactivity closure stays a human decision under the existing Reopen
        // permissions and window (POST /api/tickets/{id}/reopen).
        var f = new GenesysServiceFixture();
        var (department, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        f.QueueMappings.Map("queue-cs", department.DepartmentId);

        var first = await f.Ingestion.IngestAsync(ServiceAccount, Inquiry("conv-first", queueId: "queue-cs"));
        var statusBefore = first.Ticket!.TicketStatus;
        var second = await f.Ingestion.IngestAsync(ServiceAccount, Inquiry("conv-second", queueId: "queue-cs"));

        Assert.Equal(GenesysIngestionOutcome.TicketCreated, second.Outcome);
        Assert.Equal(2, f.Tickets.All.Count());
        Assert.NotEqual(first.Ticket.TicketId, second.Ticket!.TicketId);
        Assert.Equal(statusBefore.ToString(), f.Tickets.All.Single(t => t.TicketId == first.Ticket.TicketId).TicketStatus.ToString());
    }

    [Fact]
    public async Task Ingest_ConcurrentDuplicate_LosesTheDatabaseRace_AndStillYieldsOneTicket()
    {
        // The read-side check cannot protect against two deliveries that both
        // pass it; the unique index on GenesysConversationId is what does.
        // The fake interaction store mirrors that index (raising the same
        // DuplicateWriteException the real unit of work translates), so this
        // exercises the ingestion service's actual recovery path: answer with
        // the ticket that won the race.
        var f = new GenesysServiceFixture();
        var (department, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        f.QueueMappings.Map("queue-cs", department.DepartmentId);

        var winner = await f.Ingestion.IngestAsync(ServiceAccount, Inquiry("conv-race", queueId: "queue-cs"));

        // Simulate the loser: its read happened before the winner committed,
        // so it proceeds to creation and hits the unique index.
        var loserInteraction = f.InteractionFor("conv-race");
        Assert.NotNull(loserInteraction);

        var loser = await f.Ingestion.IngestAsync(ServiceAccount, Inquiry("conv-race", queueId: "queue-cs"));

        Assert.Equal(GenesysIngestionOutcome.AlreadyIngested, loser.Outcome);
        Assert.Equal(winner.Ticket!.TicketId, loser.Ticket!.TicketId);
        Assert.Single(f.Tickets.All);
    }

    // ---- 3 & 4. Phone: the flow starts at pickup ----

    [Fact]
    public async Task Ingest_CallPickedUp_CreatesExactlyOneTicket()
    {
        // A ringing call never reaches TigerCS at all — there is no event
        // vocabulary for Genesys to send and no endpoint to send it to.
        // Reaching ingestion IS the pickup.
        var f = new GenesysServiceFixture();
        var (department, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        f.QueueMappings.Map("queue-cs", department.DepartmentId);

        var answered = await f.Ingestion.IngestAsync(
            ServiceAccount, Inquiry("conv-call-9", queueId: "queue-cs"));

        Assert.Equal(GenesysIngestionOutcome.TicketCreated, answered.Outcome);
        Assert.Single(f.Tickets.All);
        Assert.Equal(WellKnownChannels.Phone, Assert.Single(f.Interactions.All).ChannelId);
    }

    // ---- 5, 6, 7. The website chat's choice is a DEPARTMENT ----

    [Theory]
    [InlineData("Customer Service", "CS")]
    [InlineData("Leasing", "LS")]
    [InlineData("Maintenance", "MNT")]
    public async Task Ingest_WebsiteChat_ExplicitSelection_RoutesToThatDepartment(string departmentName, string departmentCode)
    {
        var f = new GenesysServiceFixture();
        // Three real departments exist; only the selected one must be used.
        var customerService = f.SeedGenesysDepartment("Customer Service", "CS");
        var leasing = f.SeedGenesysDepartment("Leasing", "LS");
        var maintenance = f.SeedGenesysDepartment("Maintenance", "MNT");
        var expected = new[] { customerService, leasing, maintenance }
            .Single(d => d.Department.Code == departmentCode);

        var result = await f.Ingestion.IngestAsync(
            ServiceAccount,
            Inquiry($"conv-web-{departmentCode}", GenesysChannel.WebsiteChat,
                departmentId: expected.Department.DepartmentId,
                customerName: "Ahmed Ali", towerName: "Tiger Tower A", unitNumber: "1204"));

        Assert.Equal(GenesysIngestionOutcome.TicketCreated, result.Outcome);
        var ticket = Assert.Single(f.Tickets.All);

        // OriginatingDepartment AND CurrentDepartment are the selection.
        Assert.Equal(expected.Department.DepartmentId, ticket.OriginatingDepartmentId);
        Assert.Equal(expected.Department.DepartmentId, ticket.CurrentDepartmentId);
        Assert.Equal(departmentName, expected.Department.Name);

        // The selection is a DEPARTMENT and only a department: it produced no
        // category, even though this department has one.
        Assert.Null(ticket.CategoryId);

        // The chat form's tower/unit travel as the manual snapshot.
        Assert.Equal("Tiger Tower A", ticket.ManualProjectName);
        Assert.Equal("1204", ticket.ManualUnitNumber);
        Assert.Equal("Ahmed Ali", Assert.Single(f.Interactions.All).CustomerName);
    }

    [Fact]
    public async Task Ingest_WebsiteChat_ByDepartmentCode_RoutesToThatDepartment()
    {
        var f = new GenesysServiceFixture();
        f.SeedGenesysDepartment("Customer Service", "CS");
        var leasing = f.SeedGenesysDepartment("Leasing", "LS");

        var result = await f.Ingestion.IngestAsync(
            ServiceAccount,
            Inquiry("conv-web-code", GenesysChannel.WebsiteChat, departmentCode: "ls"));

        Assert.Equal(GenesysIngestionOutcome.TicketCreated, result.Outcome);
        Assert.Equal(leasing.Department.DepartmentId, Assert.Single(f.Tickets.All).OriginatingDepartmentId);
    }

    // ---- 8. The department selection is NOT a request type ----

    [Fact]
    public async Task Ingest_WebsiteChatDepartmentSelection_NeverInfersACategoryRequestTypeOrPriority()
    {
        var f = new GenesysServiceFixture();
        var (department, _) = f.SeedGenesysDepartment("Maintenance", "MNT");

        var result = await f.Ingestion.IngestAsync(
            ServiceAccount,
            Inquiry("conv-web-nort", GenesysChannel.WebsiteChat,
                departmentId: department.DepartmentId));

        Assert.Equal(GenesysIngestionOutcome.TicketCreated, result.Outcome);
        var ticket = Assert.Single(f.Tickets.All);

        // "Maintenance" is a department, not a request type and not a
        // category. Classification is the agent's later decision.
        Assert.Null(ticket.RequestTypeId);
        Assert.Null(ticket.WorkflowTemplateId);
        Assert.Null(ticket.CategoryId);

        // The priority is the configured default (Normal = Medium), which is
        // what lets the SLA run from creation; it is not inferred from the
        // department.
        Assert.Equal((byte)PriorityLevel.Medium, ticket.PriorityId);
        Assert.Equal(SlaState.Running, ticket.SlaState);
    }

    // ---- 9 & 10. Customer lookup: used, but never a gate ----

    [Fact]
    public async Task Ingest_MobileNumber_IsPassedThroughTheExistingCustomerLookup()
    {
        var f = new GenesysServiceFixture();
        var (department, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        f.CrmBuyers.Returns(CrmBuyerLookupResult.Success(
        [
            new CrmBuyerMatchDto(
                new CrmCustomerDto(9001, "Sami Nasser", null, "+971509990001", "sami@example.test"),
                [new CrmBuyerUnitDto(1, 4, "Contract", 101, "1506", 1, 2, 15, 10, "Nobles Tower", null, 1, "Buyer")])
        ]));

        var result = await f.Ingestion.IngestAsync(
            ServiceAccount,
            Inquiry("conv-lookup", phone: "+971509990001", departmentId: department.DepartmentId));

        Assert.Equal(GenesysIngestionOutcome.TicketCreated, result.Outcome);

        // The EXISTING lookup ran, with the inquiry's own mobile number —
        // Genesys never reaches CRM/PACT/Tasleeh itself.
        Assert.Equal("+971509990001", f.CrmBuyers.LastSearchedPhoneNumber);
        Assert.Equal(1, f.CrmBuyers.CallCount);

        // The number is preserved on the intake record and the interaction,
        // so customer history and any later re-lookup have it.
        Assert.Equal("+971509990001", Assert.Single(f.Interactions.All).CustomerPhone);
        Assert.Contains(f.Audit.Written, w => w.Action == "GenesysInquiryIngested");
    }

    [Fact]
    public async Task Ingest_CustomerNotFound_StillCreatesTheTicket()
    {
        var f = new GenesysServiceFixture();
        var (department, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        f.CrmBuyers.Returns(CrmBuyerLookupResult.NotFound());

        var result = await f.Ingestion.IngestAsync(
            ServiceAccount, Inquiry("conv-nomatch", phone: "+971500000404", departmentId: department.DepartmentId));

        Assert.Equal(GenesysIngestionOutcome.TicketCreated, result.Outcome);
        var ticket = Assert.Single(f.Tickets.All);

        // An unverified/manual customer inquiry — never auto-verified, and
        // never discarded.
        Assert.Equal(CrmVerificationStatus.Unverified, ticket.VerificationStatus);
        Assert.Null(ticket.CrmBuyerCustomerId);
    }

    [Fact]
    public async Task Ingest_CustomerLookupFails_TheInquiryStillBecomesATicket()
    {
        // "The fact that CRM/PACT lookup failed must NOT cause the Genesys
        // inquiry to disappear."
        var f = new GenesysServiceFixture();
        var (department, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        f.CrmBuyers.Returns(CrmBuyerLookupResult.Unavailable("CRM is down"));

        var result = await f.Ingestion.IngestAsync(
            ServiceAccount, Inquiry("conv-crmdown", phone: "+971500000001", departmentId: department.DepartmentId));

        Assert.Equal(GenesysIngestionOutcome.TicketCreated, result.Outcome);
        Assert.Single(f.Tickets.All);
        Assert.Equal("conv-crmdown", Assert.Single(f.Interactions.All).GenesysConversationId);
    }

    [Fact]
    public async Task Ingest_NoPhoneAtAll_StillCreatesTheTicket()
    {
        // A withheld caller id, or a social DM that carries no number: the
        // channel's RequiresPhone rule governs the agent wizard, and must
        // never discard a real inquiry.
        var f = new GenesysServiceFixture();
        var (department, _) = f.SeedGenesysDepartment("Customer Service", "CS");

        var result = await f.Ingestion.IngestAsync(
            ServiceAccount,
            Inquiry("conv-nophone", GenesysChannel.SocialMedia,
                phone: null, departmentId: department.DepartmentId));

        Assert.Equal(GenesysIngestionOutcome.TicketCreated, result.Outcome);
        Assert.Single(f.Tickets.All);
        Assert.Equal(string.Empty, Assert.Single(f.Interactions.All).CustomerPhone);
        // No lookup is attempted without a number to search with.
        Assert.Equal(0, f.CrmBuyers.CallCount);
    }

    // ---- Channels converge on ONE flow ----

    [Theory]
    [InlineData(GenesysChannel.Phone, WellKnownChannels.Phone)]
    [InlineData(GenesysChannel.WebsiteChat, WellKnownChannels.LiveChat)]
    [InlineData(GenesysChannel.WhatsApp, WellKnownChannels.WhatsApp)]
    [InlineData(GenesysChannel.SocialMedia, WellKnownChannels.SocialMediaDirectMessage)]
    [InlineData(GenesysChannel.Instagram, WellKnownChannels.Instagram)]
    [InlineData(GenesysChannel.Facebook, WellKnownChannels.Facebook)]
    public async Task Ingest_EveryChannel_UsesTheSameFlow_AndRecordsItsOwnOriginatingChannel(
        GenesysChannel channel, byte expectedChannelId)
    {
        var f = new GenesysServiceFixture();
        var (department, _) = f.SeedGenesysDepartment("Customer Service", "CS");

        var result = await f.Ingestion.IngestAsync(
            ServiceAccount, Inquiry($"conv-{channel}", channel, departmentId: department.DepartmentId));

        Assert.Equal(GenesysIngestionOutcome.TicketCreated, result.Outcome);
        var interaction = Assert.Single(f.Interactions.All);
        Assert.Equal(expectedChannelId, interaction.ChannelId);
        Assert.Equal(InteractionContextSource.Genesys, interaction.Source);
    }

    // ---- Queue → Department mapping is configuration, never code ----

    [Fact]
    public async Task Ingest_QueueMapping_ResolvesTheDepartment_ForNonWebsiteChannels()
    {
        var f = new GenesysServiceFixture();
        f.SeedGenesysDepartment("Customer Service", "CS");
        var leasing = f.SeedGenesysDepartment("Leasing", "LS");
        f.QueueMappings.Map("genesys-queue-leasing", leasing.Department.DepartmentId);

        var result = await f.Ingestion.IngestAsync(
            ServiceAccount,
            Inquiry("conv-wa-1", GenesysChannel.WhatsApp, queueId: "genesys-queue-leasing"));

        Assert.Equal(GenesysIngestionOutcome.TicketCreated, result.Outcome);
        Assert.Equal(leasing.Department.DepartmentId, Assert.Single(f.Tickets.All).OriginatingDepartmentId);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("code")]
    [InlineData("queue")]
    public async Task Ingest_RecordsWhichRoutingInputDecidedTheDepartment_OnTheAuditEntry(string via)
    {
        // A ticket that lands in an unexpected department (UAT: Genesys
        // chats filed under Finance) must be traceable to the exact input
        // that named it — the flow's explicit id or code, or the queue
        // mapping — because the code never falls back to a department.
        var f = new GenesysServiceFixture();
        f.SeedGenesysDepartment("Customer Service", "CS");
        var leasing = f.SeedGenesysDepartment("Leasing Customer Services", "LCS");
        f.QueueMappings.Map("TH_CS_Leasing_WM-queue-id", leasing.Department.DepartmentId);

        var inquiry = via switch
        {
            "id" => Inquiry("conv-src", GenesysChannel.WebsiteChat, departmentId: leasing.Department.DepartmentId),
            "code" => Inquiry("conv-src", GenesysChannel.WebsiteChat, departmentCode: "lcs"),
            _ => Inquiry("conv-src", GenesysChannel.WebsiteChat, queueId: "TH_CS_Leasing_WM-queue-id"),
        };

        var result = await f.Ingestion.IngestAsync(ServiceAccount, inquiry);

        Assert.Equal(GenesysIngestionOutcome.TicketCreated, result.Outcome);
        var audit = Assert.Single(f.Audit.Entries, a => a.Action == "GenesysInquiryIngested");
        var expectedSource = via switch
        {
            "id" => "DepartmentSource=ExplicitDepartmentId;ReceivedDepartmentId=" + leasing.Department.DepartmentId,
            "code" => "DepartmentSource=ExplicitDepartmentCode;ReceivedDepartmentId=(none);ReceivedDepartmentCode=lcs",
            _ => "DepartmentSource=QueueMapping;ReceivedDepartmentId=(none);ReceivedDepartmentCode=(none);QueueId=TH_CS_Leasing_WM-queue-id",
        };
        Assert.Contains(expectedSource, audit.AfterValue);
        Assert.Contains("DepartmentCode=LCS;", audit.AfterValue);
    }

    [Fact]
    public async Task Ingest_ExplicitSelectionWins_OverTheQueueMapping()
    {
        var f = new GenesysServiceFixture();
        var customerService = f.SeedGenesysDepartment("Customer Service", "CS");
        var leasing = f.SeedGenesysDepartment("Leasing", "LS");
        f.QueueMappings.Map("shared-queue", leasing.Department.DepartmentId);

        var result = await f.Ingestion.IngestAsync(
            ServiceAccount,
            Inquiry("conv-priority", GenesysChannel.WebsiteChat,
                departmentId: customerService.Department.DepartmentId, queueId: "shared-queue"));

        Assert.Equal(GenesysIngestionOutcome.TicketCreated, result.Outcome);
        Assert.Equal(customerService.Department.DepartmentId, Assert.Single(f.Tickets.All).OriginatingDepartmentId);
    }

    [Fact]
    public async Task Ingest_UnmappedQueue_IsReportedAsAConfigurationGap_NotRoutedToAFallback()
    {
        var f = new GenesysServiceFixture();
        f.SeedGenesysDepartment("Customer Service", "CS");

        var result = await f.Ingestion.IngestAsync(
            ServiceAccount, Inquiry("conv-unmapped", GenesysChannel.WhatsApp, queueId: "queue-nobody-configured"));

        Assert.Equal(GenesysIngestionOutcome.DepartmentNotResolved, result.Outcome);
        Assert.Empty(f.Tickets.All);
        Assert.Contains("queue-nobody-configured", result.Detail);
    }

    [Fact]
    public async Task Ingest_InactiveQueueMapping_DoesNotResolveADepartment()
    {
        var f = new GenesysServiceFixture();
        var (department, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        f.QueueMappings.Map("retired-queue", department.DepartmentId, isActive: false);

        var result = await f.Ingestion.IngestAsync(
            ServiceAccount, Inquiry("conv-retired", GenesysChannel.WhatsApp, queueId: "retired-queue"));

        Assert.Equal(GenesysIngestionOutcome.DepartmentNotResolved, result.Outcome);
        Assert.Empty(f.Tickets.All);
    }

    [Fact]
    public async Task Ingest_DepartmentThatWasNeverConfiguredForGenesys_StillProducesTheTicket()
    {
        // There is no per-department Genesys configuration any more, because
        // there is no category to configure: a department only has to exist
        // and be active. An inquiry can never be refused for want of a
        // category nobody has chosen.
        var f = new GenesysServiceFixture();
        var department = f.Departments.AddDepartment("Collections", "COL");

        var result = await f.Ingestion.IngestAsync(
            ServiceAccount, Inquiry("conv-nocat", departmentId: department.DepartmentId));

        Assert.Equal(GenesysIngestionOutcome.TicketCreated, result.Outcome);
        var ticket = Assert.Single(f.Tickets.All);
        Assert.Equal(department.DepartmentId, ticket.OriginatingDepartmentId);
        Assert.Null(ticket.CategoryId);
    }

    // ---- 16. The feature flag ----

    [Fact]
    public async Task Ingest_IntegrationDisabled_WritesNothing()
    {
        var f = new GenesysServiceFixture(enabled: false);
        var (department, _) = f.SeedGenesysDepartment("Customer Service", "CS");

        var result = await f.Ingestion.IngestAsync(
            ServiceAccount, Inquiry("conv-disabled", departmentId: department.DepartmentId));

        Assert.Equal(GenesysIngestionOutcome.IntegrationDisabled, result.Outcome);
        Assert.Empty(f.Tickets.All);
        Assert.Empty(f.Interactions.All);
        Assert.Empty(f.IntakeRecords.All);
    }

    [Fact]
    public async Task GenesysDisabled_NormalManualTicketCreationIsCompletelyUnaffected()
    {
        // The flag governs the Genesys ingestion services and nothing else:
        // the ordinary intake -> ticket path never consults it.
        var f = new GenesysServiceFixture(enabled: false);
        var (department, category) = f.SeedGenesysDepartment("Customer Service", "CS");

        var agentId = Guid.NewGuid();
        var intake = new IntakeRecord(
            WellKnownChannels.FaceToFaceKiosk, "+971500000077", department.DepartmentId,
            false, null, null, agentId, DateTime.UtcNow);
        await f.IntakeRecords.AddAsync(intake);

        var manual = await f.ManualTicketCreation.CreateAsync(
            agentId,
            new CreateTicketRequestDto(
                intake.IntakeRecordId, null, null, category.CategoryId, (byte)PriorityLevel.High, "Walk-in complaint"));

        Assert.Equal(TicketCreationOutcome.Success, manual.Outcome);
        var ticket = Assert.Single(f.Tickets.All);
        Assert.Equal("Walk-in complaint", ticket.RequestSummary);

        // A Face-to-Face interaction, with every Genesys field null.
        var interaction = Assert.Single(f.Interactions.All);
        Assert.Equal(InteractionContextSource.Ticketing, interaction.Source);
        Assert.Null(interaction.GenesysConversationId);
    }

    // ---- Input validation ----

    [Fact]
    public async Task Ingest_BlankConversationId_IsRefused()
    {
        var f = new GenesysServiceFixture();
        var (department, _) = f.SeedGenesysDepartment("Customer Service", "CS");

        var result = await f.Ingestion.IngestAsync(ServiceAccount, Inquiry("   ", departmentId: department.DepartmentId));

        // Without the conversation id nothing can be made idempotent, so the
        // inquiry is refused rather than half-processed.
        Assert.Equal(GenesysIngestionOutcome.ConversationIdRequired, result.Outcome);
        Assert.Empty(f.Tickets.All);
    }
}
