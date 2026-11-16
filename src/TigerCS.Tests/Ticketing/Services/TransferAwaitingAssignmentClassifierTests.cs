using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Tests.Ticketing.Services;

/// <summary>
/// A department transfer that found nobody eligible leaves the ticket Unassigned in the destination. The unassigned-work classifier must (a) hand such a
/// ticket to its OWN department's Round Robin once someone is eligible and (b) never route it to the Call Center - while leaving every other classification
/// exactly as it was.
/// </summary>
public class TransferAwaitingAssignmentClassifierTests
{
    private const int CallCenter = 1;
    private const int Facilities = 7;
    private static readonly DateTime Now = new(2026, 11, 16, 9, 0, 0, DateTimeKind.Utc);

    private static UnassignedTicketEvidence Evidence(
        int department, bool awaiting = false, bool leftCallCenter = false, bool hasGenesys = false, bool releasedByPerson = false, bool pendingApproval = false,
        TicketStatus status = TicketStatus.Open, Guid? owner = null) =>
        new(1, "TG-1", status, owner, department, OriginatingDepartmentId: 5, Now.AddDays(-2), PendingApproval: pendingApproval,
            HasGenesysInteraction: hasGenesys, HasLiveGenesysInteraction: false, RequiresHumanFollowUp: false, OpenHandoff: false, HumanEvidence: false,
            AnsweringEmployeeId: null, AnsweringGenesysUserId: null, PhonePickupByUnmappedAgent: false, UnmappedGenesysAgentId: null,
            LastActivityUtc: Now.AddDays(-2), LeftCallCenter: leftCallCenter, ReleasedByPerson: releasedByPerson, AwaitingDepartmentAssignment: awaiting);

    private static UnassignedTicketClassification Classify(UnassignedTicketEvidence e) => UnassignedTicketClassifier.Classify(e, CallCenter, Now);

    [Fact]
    public void ATransferredUnassignedTicketInADepartment_IsDealtByThatDepartment_NotTheCallCenter()
    {
        var result = Classify(Evidence(Facilities, awaiting: true));

        Assert.Equal(UnassignedTicketClass.TransferredAwaitingDepartmentAssignment, result.Class);
        Assert.Equal(UnassignedTicketAction.RoundRobinDepartment, result.Action);
    }

    [Fact]
    public void ATicketThatLeftTheCallCenterAndIsAwaitingItsDepartment_IsDealtByTheDepartment_NotLeftAlone()
    {
        var result = Classify(Evidence(Facilities, awaiting: true, leftCallCenter: true));

        Assert.Equal(UnassignedTicketAction.RoundRobinDepartment, result.Action);
    }

    [Fact]
    public void ABackFromTheCallCenterTransfer_IsNotTheDepartmentRule_BecauseTheCallCenterKeepsItsOwnRules()
    {
        var result = Classify(Evidence(CallCenter, awaiting: true));

        Assert.NotEqual(UnassignedTicketAction.RoundRobinDepartment, result.Action);
        Assert.NotEqual(UnassignedTicketClass.TransferredAwaitingDepartmentAssignment, result.Class);
    }

    [Fact]
    public void WithoutTheFlag_NothingChanges_AnUnrelatedUnownedTicketKeepsItsExistingClassification()
    {
        // Exactly the pre-existing outcomes: a ticket that left the Call Center is left alone; a manually created one in a department is routed to the Call Center.
        Assert.Equal(UnassignedTicketClass.TransferredOutOfCallCenter, Classify(Evidence(Facilities, leftCallCenter: true)).Class);
        Assert.Equal(UnassignedTicketAction.None, Classify(Evidence(Facilities, leftCallCenter: true)).Action);
        Assert.Equal(UnassignedTicketAction.RoundRobinCallCenter, Classify(Evidence(Facilities)).Action);
    }

    [Theory]
    [InlineData(true, false)]    // pending approval: the approval decides first
    [InlineData(false, true)]    // owned: never redistributed
    public void ApprovalsAndOwners_StillWinOverTheFlag(bool pendingApproval, bool owned)
    {
        var result = Classify(Evidence(Facilities, awaiting: true, pendingApproval: pendingApproval, owner: owned ? Guid.NewGuid() : null));

        Assert.Equal(UnassignedTicketAction.None, result.Action);
    }

    [Theory]
    [InlineData(TicketStatus.InProgress)]
    [InlineData(TicketStatus.Resolved)]
    [InlineData(TicketStatus.Closed)]
    public void OnlyOpenTicketsAreDealt(TicketStatus status)
    {
        Assert.Equal(UnassignedTicketAction.None, Classify(Evidence(Facilities, awaiting: true, status: status)).Action);
    }
}
