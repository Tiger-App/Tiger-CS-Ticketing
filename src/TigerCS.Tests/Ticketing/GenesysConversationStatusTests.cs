using TigerCS.Application.Modules.Ticketing.Services;
using Xunit;

namespace TigerCS.Tests.Ticketing;

/// <summary>The derived Genesys conversation status: separate from the ticket status and the owner, never assigns anyone.</summary>
public sealed class GenesysConversationStatusTests
{
    [Theory]
    [InlineData("Ai", GenesysConversationStatus.InProgress)]                       // AI live: in progress, the AI Agent keeps the ticket
    [InlineData("HumanFollowUp", GenesysConversationStatus.AwaitingGenesysUpdate)]  // a person is needed, none confirmed: awaiting, never "Unassigned"
    [InlineData("WaitingForHuman", GenesysConversationStatus.AwaitingGenesysUpdate)]
    [InlineData("Human", GenesysConversationStatus.Assigned)]
    [InlineData(null, GenesysConversationStatus.InProgress)]
    public void OpenConversation_IsJudgedByTheHandlingState(string? handling, string expected) =>
        Assert.Equal(expected, GenesysConversationStatus.Derive(isEnded: false, humanConfirmed: false, handling));

    [Theory]
    [InlineData("Ai")]
    [InlineData("HumanFollowUp")]
    [InlineData("WaitingForHuman")]
    [InlineData("Human")]
    public void AnEndedConversation_IsEnded_WhateverElse(string handling)
    {
        Assert.Equal(GenesysConversationStatus.Ended, GenesysConversationStatus.Derive(true, false, handling));
        Assert.Equal(GenesysConversationStatus.Ended, GenesysConversationStatus.Derive(true, true, handling));
    }

    [Theory]
    [InlineData("Ai")]
    [InlineData("HumanFollowUp")]
    [InlineData("WaitingForHuman")]
    public void AConfirmedHuman_Wins_OverAnyWaitingState(string handling) =>
        Assert.Equal(GenesysConversationStatus.Assigned, GenesysConversationStatus.Derive(false, humanConfirmed: true, handling));

    [Fact]
    public void Labels_AreCompactAndNeverSayUnassigned()
    {
        foreach (var s in new[] { GenesysConversationStatus.AwaitingGenesysUpdate, GenesysConversationStatus.InProgress, GenesysConversationStatus.Assigned, GenesysConversationStatus.Ended })
        {
            Assert.DoesNotContain("Unassigned", GenesysConversationStatus.Label(s));
        }

        Assert.Equal("—", GenesysConversationStatus.Label(null));
    }
}
