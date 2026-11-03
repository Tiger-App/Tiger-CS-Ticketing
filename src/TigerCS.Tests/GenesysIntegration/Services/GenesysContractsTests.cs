using TigerCS.Application.Modules.CustomerVerification.CrmIntegration;
using TigerCS.Application.Modules.CustomerVerification.Dto;
using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.GenesysIntegration.Fakes;

namespace TigerCS.Tests.GenesysIntegration.Services;

/// <summary>
/// The three externally consumable Genesys contracts, as Genesys asked for
/// them:
///
/// <list type="number">
/// <item>Create Ticket, one endpoint for every channel.</item>
/// <item>Customer/mobile lookup on call pickup.</item>
/// <item>Update Ticket.</item>
/// </list>
///
/// <para>
/// These run the REAL services behind those contracts, so what they prove is
/// what a Genesys integrator would actually observe.
/// </para>
/// </summary>
public class GenesysContractsTests
{
    private static readonly Guid ServiceAccount = Guid.NewGuid();

    private static GenesysInquiryDto Inquiry(
        string conversationId, GenesysChannel channel, int departmentId, string? phone = "+971500000001") =>
        new(conversationId, channel,
            CustomerPhone: phone, CustomerName: "Ahmed Ali", DepartmentId: departmentId);

    // ---- Contract 2: customer lookup on call pickup ----

    [Fact]
    public async Task Lookup_WithNoCustomerAnywhere_AnswersCleanlyRatherThanFailing()
    {
        var f = new GenesysServiceFixture();
        f.CrmBuyers.Returns(CrmBuyerLookupResult.NotFound());

        var result = await f.CustomerLookup.LookUpAsync("+971500000009");

        Assert.NotNull(result);
        // The whole point: "nobody" is an answer, not an error — the Create
        // Ticket call that follows must still work.
        Assert.False(result!.Found);
        Assert.Equal("NotFound", result.CrmStatus);
        Assert.Empty(result.CrmBuyers);
        Assert.Empty(result.Tickets);
        Assert.Equal(0, result.OpenTicketCount);
        Assert.Equal("+971500000009", result.PhoneNumber);
    }

    [Fact]
    public async Task Lookup_WithACrmMatch_ReturnsTheCustomerAndTheirUnits()
    {
        var f = new GenesysServiceFixture();
        f.CrmBuyers.Returns(CrmBuyerLookupResult.Success(
        [
            new CrmBuyerMatchDto(
                new CrmCustomerDto(4001, "Ahmed Al-Farsi", null, "+971500000001", "ahmed@example.test"),
                [new CrmBuyerUnitDto(1, 4, "Contract", 101, "1204", 1, 2, 15, 10, "Tiger Tower A", null, 1, "Buyer")])
        ]));

        var result = await f.CustomerLookup.LookUpAsync("+971500000001");

        Assert.True(result!.Found);
        Assert.Equal("Found", result.CrmStatus);
        var match = Assert.Single(result.CrmBuyers);
        Assert.Equal(4001, match.Customer.CustomerId);
        // Units, not just a boolean — what the agent needs on pickup.
        Assert.Equal("1204", Assert.Single(match.Units).UnitNumber);
    }

    [Fact]
    public async Task Lookup_WhenCrmIsDown_ReportsFailedWithoutThrowing_AndNeverGatesAnything()
    {
        var f = new GenesysServiceFixture();
        f.CrmBuyers.Returns(CrmBuyerLookupResult.Unavailable("CRM unreachable"));

        var result = await f.CustomerLookup.LookUpAsync("+971500000001");

        Assert.False(result!.Found);
        Assert.Equal("Failed", result.CrmStatus);

        // And creating the ticket afterwards still works — the rule the whole
        // integration hangs on.
        var (department, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        var created = await f.Ingestion.IngestAsync(
            ServiceAccount, Inquiry("conv-crm-down", GenesysChannel.Phone, department.DepartmentId));
        Assert.Equal(GenesysIngestionOutcome.TicketCreated, created.Outcome);
    }

    [Fact]
    public async Task Lookup_ReturnsTheCallersExistingTickets_OpenOnesFirst()
    {
        var f = new GenesysServiceFixture();
        var (department, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        const string phone = "+971500000077";

        var first = await f.Ingestion.IngestAsync(
            ServiceAccount, Inquiry("conv-hist-1", GenesysChannel.Phone, department.DepartmentId, phone));
        var second = await f.Ingestion.IngestAsync(
            ServiceAccount, Inquiry("conv-hist-2", GenesysChannel.WhatsApp, department.DepartmentId, phone));

        var result = await f.CustomerLookup.LookUpAsync(phone);

        Assert.Equal(2, result!.Tickets.Count);
        Assert.Equal(2, result.OpenTicketCount);
        Assert.All(result.Tickets, t => Assert.True(t.IsOpen));
        Assert.Contains(result.Tickets, t => t.TicketId == first.Ticket!.TicketId);
        Assert.Contains(result.Tickets, t => t.TicketId == second.Ticket!.TicketId);
    }

    [Fact]
    public async Task Lookup_IsRefusedWhileTheIntegrationIsDisabled()
    {
        var f = new GenesysServiceFixture(enabled: false);

        Assert.Null(await f.CustomerLookup.LookUpAsync("+971500000001"));
    }

    // ---- Contract 3: the one update facade ----

    private static async Task<(GenesysServiceFixture Fixture, long TicketId)> CreatedTicketAsync(
        string conversationId = "conv-update", GenesysChannel channel = GenesysChannel.WebsiteChat)
    {
        var f = new GenesysServiceFixture();
        var (department, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        var created = await f.Ingestion.IngestAsync(
            ServiceAccount, Inquiry(conversationId, channel, department.DepartmentId));
        Assert.Equal(GenesysIngestionOutcome.TicketCreated, created.Outcome);
        return (f, created.Ticket!.TicketId);
    }

    [Fact]
    public async Task Update_EndsTheConversationAndStoresTheTranscript_WithoutClosingTheTicket()
    {
        var (f, ticketId) = await CreatedTicketAsync();

        var result = await f.TicketUpdate.UpdateAsync(
            ServiceAccount, ticketId,
            new GenesysTicketUpdateDto(
                "conv-update",
                AgentId: "ga-7", AgentName: "Layla",
                Ended: new GenesysConversationEndUpdateDto(
                    DateTime.UtcNow, "AgentDisconnect",
                    [
                        new GenesysTranscriptMessageDto("Customer", DateTime.UtcNow.AddMinutes(-2), "Any update?"),
                        new GenesysTranscriptMessageDto("HumanAgent", DateTime.UtcNow.AddMinutes(-1), "Checking now.")
                    ])));

        Assert.Equal(GenesysTicketUpdateOutcome.Applied, result.Outcome);
        Assert.True(result.ConversationEnded);
        Assert.Equal(2, result.TranscriptMessageCount);

        // The load-bearing rule, echoed on the response itself.
        Assert.Equal(nameof(TicketStatus.Open), result.TicketStatus);
        Assert.Equal(TicketStatus.Open, Assert.Single(f.Tickets.All).TicketStatus);
        Assert.Null(Assert.Single(f.Tickets.All).ResolutionOutcome);

        // The agent context landed on the interaction.
        Assert.Equal("ga-7", Assert.Single(f.Interactions.All).GenesysAgentId);
    }

    [Fact]
    public async Task Update_ResendingTheSameTranscript_StoresNothingTwice()
    {
        var (f, ticketId) = await CreatedTicketAsync();
        var sentAt = new DateTime(2026, 9, 10, 9, 33, 0, DateTimeKind.Utc);

        // A real retry resends the SAME messages — same ids, same timestamps.
        GenesysTicketUpdateDto Update() => new(
            "conv-update",
            Ended: new GenesysConversationEndUpdateDto(
                sentAt.AddMinutes(5), "CustomerDisconnect",
                [
                    new GenesysTranscriptMessageDto("Customer", sentAt, "I want to sell my apartment.", ExternalMessageId: "m-1"),
                    new GenesysTranscriptMessageDto("VirtualAgent", sentAt.AddSeconds(20), "An NOC for resale?", ExternalMessageId: "m-2")
                ]));

        var first = await f.TicketUpdate.UpdateAsync(ServiceAccount, ticketId, Update());
        var replay = await f.TicketUpdate.UpdateAsync(ServiceAccount, ticketId, Update());
        var thirdTime = await f.TicketUpdate.UpdateAsync(ServiceAccount, ticketId, Update());

        Assert.Equal(GenesysTicketUpdateOutcome.Applied, first.Outcome);
        Assert.Equal(GenesysTicketUpdateOutcome.Applied, replay.Outcome);
        Assert.Equal(2, first.TranscriptMessageCount);
        Assert.Equal(2, replay.TranscriptMessageCount);
        Assert.Equal(2, thirdTime.TranscriptMessageCount);
        Assert.Equal(2, f.Conversations.AllMessages.Count);
    }

    [Fact]
    public async Task Update_ResendingWithoutMessageIds_StillStoresNothingTwice()
    {
        // No messageId supplied. Sender + timestamp + body identify the line
        // instead — two genuinely distinct messages matching all three would
        // be the same person saying the same thing at the same instant.
        var (f, ticketId) = await CreatedTicketAsync();
        var sentAt = new DateTime(2026, 9, 10, 9, 33, 0, DateTimeKind.Utc);

        GenesysTicketUpdateDto Update() => new(
            "conv-update",
            Ended: new GenesysConversationEndUpdateDto(
                sentAt.AddMinutes(5), "CustomerDisconnect",
                [new GenesysTranscriptMessageDto("Customer", sentAt, "Hello?")]));

        await f.TicketUpdate.UpdateAsync(ServiceAccount, ticketId, Update());
        var replay = await f.TicketUpdate.UpdateAsync(ServiceAccount, ticketId, Update());

        Assert.Equal(1, replay.TranscriptMessageCount);
        Assert.Single(f.Conversations.AllMessages);
    }

    [Fact]
    public async Task Update_AFullerRetryAfterATruncatedDelivery_CompletesTheTranscript()
    {
        // The reason a redelivered end is not simply skipped: if the first
        // delivery was cut short, refusing the retry outright would leave the
        // conversation permanently incomplete — and the COMPLETE transcript is
        // the requirement.
        var (f, ticketId) = await CreatedTicketAsync();
        var sentAt = new DateTime(2026, 9, 10, 9, 33, 0, DateTimeKind.Utc);
        var endedAt = sentAt.AddMinutes(5);

        var truncated = await f.TicketUpdate.UpdateAsync(
            ServiceAccount, ticketId,
            new GenesysTicketUpdateDto(
                "conv-update",
                Ended: new GenesysConversationEndUpdateDto(
                    endedAt, "CustomerDisconnect",
                    [new GenesysTranscriptMessageDto("Customer", sentAt, "I want to sell my apartment.", ExternalMessageId: "m-1")])));
        Assert.Equal(1, truncated.TranscriptMessageCount);

        var full = await f.TicketUpdate.UpdateAsync(
            ServiceAccount, ticketId,
            new GenesysTicketUpdateDto(
                "conv-update",
                Ended: new GenesysConversationEndUpdateDto(
                    // A later end time is IGNORED — the recorded end is
                    // write-once — but the missing messages still land.
                    endedAt.AddHours(1), "SomethingElse",
                    [
                        new GenesysTranscriptMessageDto("Customer", sentAt, "I want to sell my apartment.", ExternalMessageId: "m-1"),
                        new GenesysTranscriptMessageDto("VirtualAgent", sentAt.AddSeconds(20), "An NOC for resale?", ExternalMessageId: "m-2"),
                        new GenesysTranscriptMessageDto("Customer", sentAt.AddSeconds(45), "Yes.", ExternalMessageId: "m-3")
                    ])));

        Assert.Equal(3, full.TranscriptMessageCount);
        Assert.Equal(3, f.Conversations.AllMessages.Count);

        var interaction = Assert.Single(f.Interactions.All);
        Assert.Equal(endedAt, interaction.EndedAtUtc);
        Assert.Equal("CustomerDisconnect", interaction.EndReason);
    }

    [Fact]
    public async Task Update_PreservesEverySenderType_InChronologicalOrder()
    {
        var (f, ticketId) = await CreatedTicketAsync();
        var sentAt = new DateTime(2026, 9, 10, 9, 33, 0, DateTimeKind.Utc);

        await f.TicketUpdate.UpdateAsync(
            ServiceAccount, ticketId,
            new GenesysTicketUpdateDto(
                "conv-update",
                Ended: new GenesysConversationEndUpdateDto(
                    sentAt.AddMinutes(5), "CustomerDisconnect",
                    [
                        new GenesysTranscriptMessageDto("System", sentAt, "Conversation started.", ExternalMessageId: "m-0"),
                        new GenesysTranscriptMessageDto("Customer", sentAt.AddSeconds(10), "I want to sell my apartment.", ExternalMessageId: "m-1"),
                        new GenesysTranscriptMessageDto("VirtualAgent", sentAt.AddSeconds(20), "An NOC for resale?", ExternalMessageId: "m-2"),
                        new GenesysTranscriptMessageDto("Customer", sentAt.AddSeconds(45), "Yes.", ExternalMessageId: "m-3"),
                        new GenesysTranscriptMessageDto("HumanAgent", sentAt.AddSeconds(90), "I can help with that.", "Layla", "ga-7", "m-4")
                    ])));

        var viewer = Guid.NewGuid();
        f.Tickets.All.Single(t => t.TicketId == ticketId).AssignTo(viewer); // a CS Agent sees only a ticket assigned to them
        var history = await f.InteractionQuery.GetForTicketAsync(viewer, [Roles.CsAgent], ticketId);
        var interaction = Assert.Single(history.Response!.Interactions);

        // All four sender types survive, in the order they were delivered.
        Assert.Equal(
            ["System", "Customer", "VirtualAgent", "Customer", "HumanAgent"],
            interaction.Messages.Select(m => m.Sender).ToArray());
        Assert.Equal([1, 2, 3, 4, 5], interaction.Messages.Select(m => m.Sequence).ToArray());
        Assert.Equal("Layla", interaction.Messages[4].SenderName);
        Assert.Equal("I want to sell my apartment.", interaction.Messages[1].Body);

        // And the conversation is closed while the ticket is not.
        Assert.Equal("Ended", interaction.Status);
        Assert.Equal("CustomerDisconnect", interaction.EndReason);
        Assert.Equal(TicketStatus.Open, Assert.Single(f.Tickets.All).TicketStatus);
    }

    [Fact]
    public async Task Update_CarriesHumanHandoffState_AndReportsItOnEveryLaterUpdate()
    {
        var (f, ticketId) = await CreatedTicketAsync();

        var handoff = await f.TicketUpdate.UpdateAsync(
            ServiceAccount, ticketId,
            new GenesysTicketUpdateDto(
                "conv-update",
                Handoff: new GenesysHandoffUpdateDto(
                    Required: true, Mode: "ContinueChat", Reason: "Bot could not answer")));

        Assert.Equal(GenesysTicketUpdateOutcome.Applied, handoff.Outcome);
        Assert.Equal(nameof(AgentHandoffStatus.WaitingForAgent), handoff.HandoffStatus);
        var workItemId = handoff.TicketAgentHandoffId;

        // The customer disconnects. The live session is over; the work is not
        // — and the response says so without being asked about handoff.
        var ended = await f.TicketUpdate.UpdateAsync(
            ServiceAccount, ticketId,
            new GenesysTicketUpdateDto(
                "conv-update", Ended: new GenesysConversationEndUpdateDto(DateTime.UtcNow, "CustomerDisconnect")));

        Assert.True(ended.ConversationEnded);
        Assert.Equal(nameof(AgentHandoffStatus.WaitingForAgent), ended.HandoffStatus);
        Assert.Equal(workItemId, ended.TicketAgentHandoffId);

        // And a redelivered handoff is still the same one work item.
        var replay = await f.TicketUpdate.UpdateAsync(
            ServiceAccount, ticketId,
            new GenesysTicketUpdateDto("conv-update", Handoff: new GenesysHandoffUpdateDto(Required: true)));
        Assert.Equal(workItemId, replay.TicketAgentHandoffId);
        Assert.Single(f.Handoffs.All);
    }

    [Fact]
    public async Task Update_RecordsWhichAgentTookThePendingWork()
    {
        var (f, ticketId) = await CreatedTicketAsync();
        await f.TicketUpdate.UpdateAsync(
            ServiceAccount, ticketId,
            new GenesysTicketUpdateDto("conv-update", Handoff: new GenesysHandoffUpdateDto(Required: true)));

        var assigned = await f.TicketUpdate.UpdateAsync(
            ServiceAccount, ticketId,
            new GenesysTicketUpdateDto(
                "conv-update", AgentName: "Noor",
                Handoff: new GenesysHandoffUpdateDto(AssignedAgentId: "ga-9")));

        Assert.Equal(GenesysTicketUpdateOutcome.Applied, assigned.Outcome);
        Assert.Equal(nameof(AgentHandoffStatus.Assigned), assigned.HandoffStatus);
        Assert.Equal("ga-9", Assert.Single(f.Handoffs.All).GenesysAgentId);
    }

    [Fact]
    public async Task Update_ThroughTheWrongTicket_IsRefused_RatherThanAppliedToTheWrongOne()
    {
        var (f, ticketId) = await CreatedTicketAsync("conv-a", GenesysChannel.Phone);
        var (department, _) = f.SeedGenesysDepartment("Leasing", "LSE");
        var other = await f.Ingestion.IngestAsync(
            ServiceAccount, Inquiry("conv-b", GenesysChannel.WhatsApp, department.DepartmentId));

        // conv-a belongs to the first ticket; addressing it through the
        // second one is a caller mistake, and guessing which they meant would
        // attach somebody else's transcript.
        var result = await f.TicketUpdate.UpdateAsync(
            ServiceAccount, other.Ticket!.TicketId,
            new GenesysTicketUpdateDto(
                "conv-a", Ended: new GenesysConversationEndUpdateDto(DateTime.UtcNow, "AgentDisconnect")));

        Assert.Equal(GenesysTicketUpdateOutcome.ConversationTicketMismatch, result.Outcome);
        Assert.All(f.Interactions.All, i => Assert.False(i.IsEnded));
        Assert.NotEqual(0, ticketId);
    }

    [Fact]
    public async Task Update_RefusesAMalformedTranscript_BeforeWritingAnyOfIt()
    {
        var (f, ticketId) = await CreatedTicketAsync();

        var result = await f.TicketUpdate.UpdateAsync(
            ServiceAccount, ticketId,
            new GenesysTicketUpdateDto(
                "conv-update",
                Ended: new GenesysConversationEndUpdateDto(
                    DateTime.UtcNow, "AgentDisconnect",
                    [
                        new GenesysTranscriptMessageDto("Customer", DateTime.UtcNow, "This one is fine."),
                        new GenesysTranscriptMessageDto("Martian", DateTime.UtcNow, "This one is not.")
                    ])));

        Assert.Equal(GenesysTicketUpdateOutcome.InvalidTranscript, result.Outcome);
        // Nothing half-stored: not even the valid first message.
        Assert.False(Assert.Single(f.Interactions.All).IsEnded);
    }

    [Theory]
    [InlineData("conv-never-created", GenesysTicketUpdateOutcome.ConversationNotFound)]
    [InlineData("", GenesysTicketUpdateOutcome.ConversationIdRequired)]
    public async Task Update_RefusesAnUnusableConversationId(string conversationId, GenesysTicketUpdateOutcome expected)
    {
        var (f, ticketId) = await CreatedTicketAsync();

        var result = await f.TicketUpdate.UpdateAsync(
            ServiceAccount, ticketId, new GenesysTicketUpdateDto(conversationId));

        Assert.Equal(expected, result.Outcome);
    }

    [Fact]
    public async Task Update_RefusesAnUnrecognizedHandoffMode()
    {
        var (f, ticketId) = await CreatedTicketAsync();

        var result = await f.TicketUpdate.UpdateAsync(
            ServiceAccount, ticketId,
            new GenesysTicketUpdateDto(
                "conv-update", Handoff: new GenesysHandoffUpdateDto(Required: true, Mode: "SCHEDULED_CALLBACK_V2")));

        Assert.Equal(GenesysTicketUpdateOutcome.InvalidHandoffMode, result.Outcome);
        Assert.Empty(f.Handoffs.All);
    }

    [Fact]
    public async Task Update_IsRefusedWhileTheIntegrationIsDisabled()
    {
        var f = new GenesysServiceFixture(enabled: false);

        var result = await f.TicketUpdate.UpdateAsync(
            ServiceAccount, ticketId: 1, new GenesysTicketUpdateDto("conv-x"));

        Assert.Equal(GenesysTicketUpdateOutcome.IntegrationDisabled, result.Outcome);
    }

    [Fact]
    public async Task Update_CannotReachTheTicketsBusinessState()
    {
        // The contract has no field for category, request type, priority,
        // status, owner, department, resolution or closure — so an update
        // that ends a conversation and hands off to a human still leaves
        // every one of them exactly as ticket creation left it.
        var (f, ticketId) = await CreatedTicketAsync();
        var before = Assert.Single(f.Tickets.All);

        await f.TicketUpdate.UpdateAsync(
            ServiceAccount, ticketId,
            new GenesysTicketUpdateDto(
                "conv-update",
                AgentId: "ga-7",
                Ended: new GenesysConversationEndUpdateDto(DateTime.UtcNow, "AgentDisconnect"),
                Handoff: new GenesysHandoffUpdateDto(Required: true, Mode: "ContinueChat")));

        var after = Assert.Single(f.Tickets.All);
        Assert.Equal(TicketStatus.Open, after.TicketStatus);
        Assert.Null(after.CategoryId);
        // The default priority is the only business state a new Genesys ticket
        // carries; the update contract moved nothing from it.
        Assert.Equal(before.PriorityId, after.PriorityId);
        Assert.Null(after.RequestTypeId);
        Assert.Null(after.CurrentOwnerEmployeeId);
        Assert.Null(after.ResolutionOutcome);
        Assert.Equal(before.SlaState, after.SlaState);
        Assert.Equal(before.CurrentDepartmentId, after.CurrentDepartmentId);
    }
}
