using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.GenesysIntegration.Fakes;

namespace TigerCS.Tests.GenesysIntegration.Services;

/// <summary>
/// Ticket 225: a <c>HumanAgent</c> transcript line carried <c>senderId = callcenter5@tigergroup.ae</c> (an e-mail) although the agent's
/// <c>AspNetUsers.GenesysUserId</c> is a GUID, so every line resolved to nobody, no handler was recorded, and the agent never counted as
/// having handled the ticket. The contract id is the GUID and is always tried first; an e-mail-form id is accepted only as a strict
/// fallback that names exactly one active, unlocked, non-service CS Agent. Names are never matched. Explicit ownership is never overwritten.
/// </summary>
public class GenesysHumanAgentSenderIdentityTests
{
    private static readonly Guid ServiceAccount = Guid.NewGuid();
    private const string AgentGuid = "5b0c1d7e-3f2a-4a8b-9c61-7e4d2f9a1b05";
    private const string AgentEmail = "callcenter5@tigergroup.ae";
    private const string AgentName = "Esraa Alfadil Alnour Ahmed";

    private sealed class World
    {
        public required GenesysServiceFixture F { get; init; }
        public required Department Cs { get; init; }
        public required Guid Esraa { get; init; }
        public required Guid Shahd { get; init; }
        public Ticket Ticket(long id) => F.Tickets.All.Single(t => t.TicketId == id);
    }

    private static World NewWorld(Action<GenesysServiceFixture>? configure = null, bool fallback = true)
    {
        var f = new GenesysServiceFixture(callCenterFirst: true, strictAccounts: true);
        f.Options.AllowEmailSenderIdFallback = fallback;
        var (cs, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        var esraa = f.AddAgent(AgentGuid, AgentName);
        f.AgentMappings.SetAccountEmail(esraa, AgentEmail);
        var shahd = f.AddAgent("g-shahd", "Shahd Mirza");
        f.AgentMappings.SetAccountEmail(shahd, "callcenter7@tigergroup.ae");
        configure?.Invoke(f);
        return new World { F = f, Cs = cs, Esraa = esraa, Shahd = shahd };
    }

    private static async Task<long> NewChatWithHumanHandoffAsync(World w, string conversationId)
    {
        var created = await w.F.Ingestion.IngestAsync(
            ServiceAccount,
            new GenesysInquiryDto(conversationId, GenesysChannel.WebsiteChat, CustomerPhone: "+971500000001", DepartmentId: w.Cs.DepartmentId));
        Assert.Equal(GenesysIngestionOutcome.TicketCreated, created.Outcome);
        await w.F.AgentHandoff.RequestAsync(
            ServiceAccount, new GenesysHandoffRequestDto(conversationId, AgentAvailable: false, Mode: "HumanTakeover"));
        return created.Ticket!.TicketId;
    }

    private static Task<GenesysTicketUpdateResult> HumanSaysAsync(World w, long ticketId, string conversationId, string? senderId, string externalId = "m-1") =>
        w.F.TicketUpdate.UpdateAsync(
            ServiceAccount, ticketId,
            new GenesysTicketUpdateDto(conversationId, Transcript:
            [
                new GenesysTranscriptMessageDto("HumanAgent", DateTime.UtcNow, "Hello, how can I help?", AgentName, senderId, externalId)
            ]));

    /// <summary>Makes <paramref name="owner"/> the explicit (human-actor) owner, as Take Ownership / a supervisor does.</summary>
    private static async Task MakeExplicitOwnerAsync(World w, long ticketId, Guid owner)
    {
        var ticket = w.Ticket(ticketId);
        foreach (var current in w.F.TicketAssignments.Added.Where(a => a.TicketId == ticketId && a.IsCurrent))
        {
            current.MarkSuperseded();
        }

        ticket.AssignTo(owner);
        await w.F.TicketAssignments.AddAsync(new TicketAssignment(ticketId, owner, ticket.CurrentDepartmentId, DateTime.UtcNow, owner));
    }

    /// <summary>The line gave no usable identity: nobody was recorded as handler and nobody was given the ticket BECAUSE of it (a Round Robin provisional owner may exist, which is a different, pre-existing rule).</summary>
    private static void AssertIdentityRejected(World w, long ticketId)
    {
        Assert.Empty(HandlersOf(w, ticketId));
        Assert.DoesNotContain(w.F.Audit.Entries, e => e.Action == "AssignOnAnswer" && e.EntityId == ticketId.ToString());
    }

    private static IEnumerable<TicketInteractionHandler> HandlersOf(World w, long ticketId) =>
        w.F.Handlers.All.Where(h => h.TicketId == ticketId);

    // ---------------------------------------------------------------------------------------------------------------------------
    // Resolution: GUID first, strict e-mail fallback second
    // ---------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task GuidSenderId_ResolvesThroughGenesysUserId()
    {
        var w = NewWorld();

        var result = await w.F.AgentResolution.ResolveSenderIdAsync(AgentGuid);

        Assert.True(result.IsResolved);
        Assert.Equal(w.Esraa, result.Agent!.UserId);
        Assert.Equal(AgentGuid, result.Agent.GenesysUserId);
    }

    [Theory]
    [InlineData("callcenter5@tigergroup.ae")]
    [InlineData("  CallCenter5@TigerGroup.AE ")]
    public async Task EmailSenderId_ResolvesToTheUniqueActiveCsAgent_AndRecordsTheirGuid(string senderId)
    {
        var w = NewWorld();

        var result = await w.F.AgentResolution.ResolveSenderIdAsync(senderId);

        Assert.True(result.IsResolved);
        Assert.Equal(w.Esraa, result.Agent!.UserId);
        Assert.Equal(AgentGuid, result.Agent.GenesysUserId);   // the handler row carries the contract id, not the e-mail
    }

    [Fact]
    public async Task EmailMatchesTheRecordedGenesysEmailToo()
    {
        var w = NewWorld(f => f.AgentMappings.Unmap("g-shahd"));
        var other = w.F.MapAgent("g-other", genesysEmail: "genesys.only@tigergroup.ae", displayName: "Other", roles: Roles.CsAgent);
        w.F.Accounts!.Add(other, "other.user", Roles.CsAgent);

        var result = await w.F.AgentResolution.ResolveSenderIdAsync("genesys.only@tigergroup.ae");

        Assert.True(result.IsResolved);
        Assert.Equal(other, result.Agent!.UserId);
    }

    [Fact]
    public async Task GuidThatIsMapped_NeverFallsThroughToAnEmailLookup()
    {
        // A mapped-but-deactivated GUID is final: it is not "rescued" by some other account carrying a look-alike e-mail.
        var w = NewWorld(f => f.AgentMappings.Deactivate(AgentGuid));

        var result = await w.F.AgentResolution.ResolveSenderIdAsync(AgentGuid);

        Assert.Equal(GenesysAgentResolutionOutcome.Inactive, result.Outcome);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task MissingSenderId_IsInvalid(string? senderId)
    {
        var w = NewWorld();
        Assert.Equal(GenesysAgentResolutionOutcome.Invalid, (await w.F.AgentResolution.ResolveSenderIdAsync(senderId)).Outcome);
    }

    [Theory]
    [InlineData("nobody@tigergroup.ae")]                                   // well-formed, but no account carries it
    [InlineData("callcenter5")]                                            // local part only
    [InlineData("callcenter5@")]                                           // malformed
    [InlineData("@tigergroup.ae")]                                         // malformed
    [InlineData("callcenter5@tigergroup")]                                 // no dotted host
    [InlineData("Esraa Alfadil Alnour Ahmed")]                             // a DISPLAY NAME is never an identity
    [InlineData("Esraa <callcenter5@tigergroup.ae>")]                      // display-name form is not a bare address
    [InlineData("callcenter5@tigergroup.ae;callcenter7@tigergroup.ae")]    // a list is not an identity
    [InlineData("callcenter5@tigergroup.ae callcenter7@tigergroup.ae")]
    [InlineData("callcenter5@tigergroup.ae@evil.example")]
    [InlineData("callcenter")]                                             // partial address
    [InlineData("tigergroup.ae")]
    public async Task InvalidOrUnknownSenderId_ResolvesToNobody(string senderId)
    {
        var w = NewWorld();

        var result = await w.F.AgentResolution.ResolveSenderIdAsync(senderId);

        Assert.False(result.IsResolved);
        Assert.Null(result.Agent);
    }

    [Fact]
    public async Task OverlongSenderId_IsInvalid()
    {
        var w = NewWorld();
        var result = await w.F.AgentResolution.ResolveSenderIdAsync(new string('a', 60) + "@tigergroup.ae");
        Assert.Equal(GenesysAgentResolutionOutcome.Invalid, result.Outcome);
    }

    [Fact]
    public async Task DisplayNameOfAMappedAgent_IsNeverMatched()
    {
        var w = NewWorld();
        Assert.False((await w.F.AgentResolution.ResolveSenderIdAsync(AgentName)).IsResolved);
        Assert.False((await w.F.AgentResolution.ResolveSenderIdAsync("Shahd Mirza")).IsResolved);
    }

    [Fact]
    public async Task DuplicateEmail_IsAmbiguous_AndResolvesToNeitherUser()
    {
        var w = NewWorld(f => { });
        w.F.AgentMappings.SetAccountEmail(w.Shahd, AgentEmail);   // two accounts now carry callcenter5@...

        var result = await w.F.AgentResolution.ResolveSenderIdAsync(AgentEmail);

        Assert.Equal(GenesysAgentResolutionOutcome.Ambiguous, result.Outcome);
        Assert.Null(result.Agent);
    }

    [Fact]
    public async Task DuplicateEmail_EvenWhenOneAccountIsInactive_IsStillAmbiguous()
    {
        var w = NewWorld();
        w.F.AgentMappings.SetAccountEmail(w.Shahd, AgentEmail);
        w.F.AgentMappings.Deactivate("g-shahd");

        Assert.Equal(GenesysAgentResolutionOutcome.Ambiguous, (await w.F.AgentResolution.ResolveSenderIdAsync(AgentEmail)).Outcome);
    }

    [Fact]
    public async Task InactiveAgent_ByEmail_IsRejected()
    {
        var w = NewWorld(f => f.AgentMappings.Deactivate(AgentGuid));
        Assert.Equal(GenesysAgentResolutionOutcome.Inactive, (await w.F.AgentResolution.ResolveSenderIdAsync(AgentEmail)).Outcome);
    }

    [Fact]
    public async Task LockedOutAgent_ByEmail_IsRejected()
    {
        var w = NewWorld();
        w.F.Accounts!.LockOut(w.Esraa);
        Assert.Equal(GenesysAgentResolutionOutcome.LockedOut, (await w.F.AgentResolution.ResolveSenderIdAsync(AgentEmail)).Outcome);
    }

    [Fact]
    public async Task ServiceAccount_ByEmail_IsRejected()
    {
        var w = NewWorld();
        w.F.ServiceIdentities!.Add(w.Esraa);
        Assert.Equal(GenesysAgentResolutionOutcome.ServiceAccount, (await w.F.AgentResolution.ResolveSenderIdAsync(AgentEmail)).Outcome);
    }

    [Theory]
    [InlineData(Roles.DepartmentEmployee)]
    [InlineData(Roles.DepartmentHead)]
    public async Task NonCsAgent_ByEmail_IsRejected(string role)
    {
        var w = NewWorld();
        var other = w.F.AddAgent("g-dept", "Dept Person", roles: role);
        w.F.AgentMappings.SetAccountEmail(other, "dept.person@tigergroup.ae");

        Assert.Equal(GenesysAgentResolutionOutcome.NotCsAgent, (await w.F.AgentResolution.ResolveSenderIdAsync("dept.person@tigergroup.ae")).Outcome);
    }

    [Fact]
    public async Task FallbackSwitchedOff_EmailIsNotResolved_ButGuidStillIs()
    {
        var w = NewWorld(fallback: false);

        Assert.False((await w.F.AgentResolution.ResolveSenderIdAsync(AgentEmail)).IsResolved);
        Assert.True((await w.F.AgentResolution.ResolveSenderIdAsync(AgentGuid)).IsResolved);
    }

    [Fact]
    public async Task EmailOnlyAccountWithoutAGenesysId_IsRecordedUnderItsEmail()
    {
        var w = NewWorld();
        var lone = Guid.NewGuid();
        w.F.AgentMappings.MapWithoutGenesysId(lone, "lone.agent@tigergroup.ae", "Lone Agent");
        w.F.UserRoles.Add(lone, Roles.CsAgent);
        w.F.Accounts!.Add(lone, "lone.agent", Roles.CsAgent);

        var result = await w.F.AgentResolution.ResolveSenderIdAsync("lone.agent@tigergroup.ae");

        Assert.True(result.IsResolved);
        Assert.Equal(lone, result.Agent!.UserId);
        Assert.Equal("lone.agent@tigergroup.ae", result.Agent.GenesysUserId);
    }

    // ---------------------------------------------------------------------------------------------------------------------------
    // End to end: the Ticket 225 shape - HumanAgent lines through the update facade
    // ---------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Ticket225Shape_EmailSenderId_RecordsTheAgentAsHandler_AndTheyOwnTheUnownedTicket()
    {
        var w = NewWorld();
        var ticketId = await NewChatWithHumanHandoffAsync(w, "conv-225");

        var result = await HumanSaysAsync(w, ticketId, "conv-225", AgentEmail);
        Assert.Equal(GenesysTicketUpdateOutcome.Applied, result.Outcome);

        var handler = Assert.Single(HandlersOf(w, ticketId));
        Assert.Equal(w.Esraa, handler.HandledByUserId);
        Assert.Equal(AgentGuid, handler.GenesysAgentUserId);
        Assert.Equal(InteractionHandlerSource.Transcript, handler.Source);
        Assert.Equal(w.Esraa, w.Ticket(ticketId).CurrentOwnerEmployeeId);
    }

    [Fact]
    public async Task GuidSenderId_RecordsTheHandlerFromTheTranscript()
    {
        var w = NewWorld();
        var ticketId = await NewChatWithHumanHandoffAsync(w, "conv-guid");

        await HumanSaysAsync(w, ticketId, "conv-guid", AgentGuid);

        var handler = Assert.Single(HandlersOf(w, ticketId));
        Assert.Equal(w.Esraa, handler.HandledByUserId);
        Assert.Equal(InteractionHandlerSource.Transcript, handler.Source);
    }

    [Fact]
    public async Task ExplicitOwner_IsNotOverwritten_ButTheRecognisedAgentStillBecomesAHandler()
    {
        var w = NewWorld();
        var ticketId = await NewChatWithHumanHandoffAsync(w, "conv-shahd");
        await MakeExplicitOwnerAsync(w, ticketId, w.Shahd);   // Shahd took ownership explicitly

        await HumanSaysAsync(w, ticketId, "conv-shahd", AgentEmail);

        var ticket = w.Ticket(ticketId);
        Assert.Equal(w.Shahd, ticket.CurrentOwnerEmployeeId);                                   // untouched
        var current = Assert.Single(w.F.TicketAssignments.Added, a => a.TicketId == ticketId && a.IsCurrent);
        Assert.Equal(w.Shahd, current.AssignedEmployeeId);
        Assert.DoesNotContain(w.F.Audit.Entries, e => e.Action == "AssignOnAnswer" && e.EntityId == ticketId.ToString());

        var handler = Assert.Single(HandlersOf(w, ticketId));                                   // evidence recorded, ownership not
        Assert.Equal(w.Esraa, handler.HandledByUserId);
    }

    [Fact]
    public async Task MissingSenderId_RecordsNoHandler_AndAssignsNobody()
    {
        var w = NewWorld();
        var ticketId = await NewChatWithHumanHandoffAsync(w, "conv-missing");

        await HumanSaysAsync(w, ticketId, "conv-missing", senderId: null);

        AssertIdentityRejected(w, ticketId);
    }

    [Fact]
    public async Task UnknownEmail_RecordsNoHandler_AndDoesNotGuess()
    {
        var w = NewWorld();
        var ticketId = await NewChatWithHumanHandoffAsync(w, "conv-unknown");

        await HumanSaysAsync(w, ticketId, "conv-unknown", "stranger@tigergroup.ae");

        AssertIdentityRejected(w, ticketId);
    }

    [Fact]
    public async Task DuplicateEmail_RecordsNoHandler_AndOwnsNothing()
    {
        var w = NewWorld();
        w.F.AgentMappings.SetAccountEmail(w.Shahd, AgentEmail);
        var ticketId = await NewChatWithHumanHandoffAsync(w, "conv-dup");

        await HumanSaysAsync(w, ticketId, "conv-dup", AgentEmail);

        AssertIdentityRejected(w, ticketId);
    }

    [Fact]
    public async Task LockedAgent_ByEmail_RecordsNoHandler_AndDoesNotOwn()
    {
        var w = NewWorld();
        w.F.Accounts!.LockOut(w.Esraa);
        var ticketId = await NewChatWithHumanHandoffAsync(w, "conv-locked");

        await HumanSaysAsync(w, ticketId, "conv-locked", AgentEmail);

        AssertIdentityRejected(w, ticketId);
    }

    [Fact]
    public async Task RedeliveringTheSameLine_RecordsTheHandlerOnce()
    {
        var w = NewWorld();
        var ticketId = await NewChatWithHumanHandoffAsync(w, "conv-redeliver");

        await HumanSaysAsync(w, ticketId, "conv-redeliver", AgentEmail, "m-1");
        await HumanSaysAsync(w, ticketId, "conv-redeliver", AgentEmail, "m-1");
        await HumanSaysAsync(w, ticketId, "conv-redeliver", AgentGuid, "m-2");   // same person by GUID later: still one handler row

        Assert.Single(HandlersOf(w, ticketId));
    }
}
