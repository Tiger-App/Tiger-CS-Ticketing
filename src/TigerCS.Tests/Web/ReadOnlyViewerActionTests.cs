// TigerCS.Web is referenced under an alias - see TigerCS.Tests.csproj.
extern alias TigerCsWeb;

using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCsWeb::TigerCS.Web.Models;

namespace TigerCS.Tests.Web;

/// <summary>
/// UI mirror of the agreed read-only rule: Chairman/CEO and Reporting User are
/// offered no write control on a ticket, even as the (impossible but
/// defensive) current owner or as a member of the ticket's department. The
/// API refuses the same actions; these helpers only keep the controls hidden.
/// </summary>
public class ReadOnlyViewerActionTests
{
    private const int TicketDepartmentId = 7;
    private static readonly Guid Viewer = Guid.NewGuid();

    private static TicketActionContext Context(string role, bool owner, bool member) =>
        new([role], Viewer, owner ? Viewer : Guid.NewGuid(), TicketDepartmentId, member ? [TicketDepartmentId] : []);

    [Theory]
    [InlineData(Roles.ChairmanCeo, false, false)]
    [InlineData(Roles.ChairmanCeo, true, true)]
    [InlineData(Roles.ChairmanCeo, false, true)]
    [InlineData(Roles.ReportingUser, false, false)]
    [InlineData(Roles.ReportingUser, true, true)]
    public void ReadOnlyViewer_IsOfferedNoWriteControl(string role, bool owner, bool member)
    {
        var context = Context(role, owner, member);
        IReadOnlyCollection<string> roles = [role];

        Assert.False(TicketActions.CanAssign(context));
        Assert.False(TicketActions.CanAssignAcrossDepartments(roles));
        Assert.False(TicketActions.CanChangeStatus(context));
        Assert.False(TicketActions.CanResolve(context));
        Assert.False(TicketActions.CanEscalate(context));
        Assert.False(TicketActions.CanRaiseManualFlag(context));
        Assert.False(TicketActions.CanEscalateToLevel4(context));
        Assert.False(TicketActions.CanClose(roles));
        Assert.False(TicketActions.CanReopen(roles));
        Assert.False(TicketActions.CanTransfer(context));
        Assert.False(TicketActions.CanAddNote(roles));
    }

    [Theory]
    [InlineData(Roles.CsAgent)]
    [InlineData(Roles.DepartmentEmployee)]
    [InlineData(Roles.CsManager)]
    [InlineData(Roles.SystemAdministrator)]
    public void WorkingRoles_AreOfferedTheNoteComposer(string role) =>
        Assert.True(TicketActions.CanAddNote([role]));

    [Fact]
    public void ARoleCombination_IsReadOnlyOnlyWhenEveryRoleIs()
    {
        Assert.True(Roles.IsReadOnlyCaller([Roles.ChairmanCeo]));
        Assert.True(Roles.IsReadOnlyCaller([Roles.ChairmanCeo, Roles.ReportingUser]));
        Assert.False(Roles.IsReadOnlyCaller([Roles.ChairmanCeo, Roles.CsManager]));
        Assert.False(Roles.IsReadOnlyCaller([]));

        // Chairman + CS Manager keeps the CS Manager's controls.
        Assert.True(TicketActions.CanAssignAcrossDepartments([Roles.ChairmanCeo, Roles.CsManager]));
        Assert.True(TicketActions.CanAddNote([Roles.ChairmanCeo, Roles.CsManager]));
    }
}
