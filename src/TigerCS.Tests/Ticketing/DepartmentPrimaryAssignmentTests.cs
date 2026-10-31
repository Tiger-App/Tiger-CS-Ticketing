extern alias TigerCsWeb;

using System.Runtime.CompilerServices;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCsWeb::TigerCS.Web.Models;

namespace TigerCS.Tests.Ticketing;

/// <summary>
/// The approved operational model: the responsible DEPARTMENT is the primary
/// assignment and the employee only the secondary one. A ticket therefore
/// always has an accountable destination — a null
/// <c>CurrentOwnerEmployeeId</c> means "no specific employee", never "no
/// owner at all", and the UI must say so.
/// </summary>
public class DepartmentPrimaryAssignmentTests
{
    private const int FacilityManagementId = 7;
    private static readonly DateTime Now = new(2026, 9, 4, 9, 0, 0, DateTimeKind.Utc);

    private static Ticket NewTicket(int departmentId = FacilityManagementId) =>
        Ticket.CreateUnverified(
            "TG-FM-20260904-0001", departmentId, categoryId: 5,
            (byte)PriorityLevel.Medium, "AC not cooling", Now);

    private static string SourceFile(string relativeToSrc, [CallerFilePath] string testFilePath = "")
    {
        var srcDir = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFilePath)!, "..", ".."));
        return Path.Combine(srcDir, relativeToSrc);
    }

    // ---- A ticket with a department but no employee is queued, not ownerless ----

    [Fact]
    public void TicketWithDepartmentAndNoEmployee_IsShownAsTheDepartmentQueue()
    {
        var ticket = NewTicket();

        // The domain state the whole rule rests on: a responsible department
        // always exists; only the employee is absent.
        Assert.Equal(FacilityManagementId, ticket.CurrentDepartmentId);
        Assert.Null(ticket.CurrentOwnerEmployeeId);

        Assert.Equal(
            "Facility Management Queue (Unassigned)",
            TicketDisplay.AssignedToLabel(
                ticket.CurrentOwnerEmployeeId, ownerName: null,
                ticket.CurrentDepartmentId, "Facility Management"));

        Assert.Equal(
            "Facility Management",
            TicketDisplay.AssignedDepartmentLabel(ticket.CurrentDepartmentId, "Facility Management"));
    }

    [Fact]
    public void AssignedToLabel_NeverProducesUnassigned_EvenWithoutAResolvedDepartmentName()
    {
        // Worst case: the department name could not be resolved. The label
        // still names a queue — degraded to neutral wording, never "Unassigned"
        // and never a raw department id.
        var label = TicketDisplay.AssignedToLabel(
            currentOwnerEmployeeId: null, ownerName: null,
            currentDepartmentId: FacilityManagementId, departmentName: null);

        Assert.Equal("Department queue", label);
        Assert.DoesNotContain("Unassigned", label, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Employee assignment is secondary: it never moves the department ----

    [Fact]
    public void AssigningAnEmployee_DoesNotChangeTheResponsibleDepartment()
    {
        var ticket = NewTicket();
        var ahmed = Guid.NewGuid();

        ticket.AssignTo(ahmed);

        Assert.Equal(FacilityManagementId, ticket.CurrentDepartmentId);
        Assert.Equal(ahmed, ticket.CurrentOwnerEmployeeId);

        // Assigned Department is unchanged; only Assigned To moved from the
        // queue to the named employee.
        Assert.Equal(
            "Facility Management",
            TicketDisplay.AssignedDepartmentLabel(ticket.CurrentDepartmentId, "Facility Management"));
        Assert.Equal(
            "Ahmed",
            TicketDisplay.AssignedToLabel(ticket.CurrentOwnerEmployeeId, "Ahmed", ticket.CurrentDepartmentId, "Facility Management"));
    }

    [Fact]
    public void TransferringDepartment_MovesTheDepartmentAndReturnsTheTicketToTheNewQueue()
    {
        const int DepartmentC = 9;
        var ticket = NewTicket();
        ticket.AssignTo(Guid.NewGuid());

        ticket.TransferToDepartment(DepartmentC);

        // The department moves first and the previous department's employee
        // cannot stay accountable — the ticket lands in department C's queue.
        Assert.Equal(DepartmentC, ticket.CurrentDepartmentId);
        Assert.Null(ticket.CurrentOwnerEmployeeId);
        Assert.Equal(
            "Collections Queue (Unassigned)",
            TicketDisplay.AssignedToLabel(ticket.CurrentOwnerEmployeeId, ownerName: null, ticket.CurrentDepartmentId, "Collections"));

        // OriginatingDepartmentId stays write-once — transfer moves only the
        // current responsible department.
        Assert.Equal(FacilityManagementId, ticket.OriginatingDepartmentId);
    }

    // ---- The UI never shows a misleading ownerless state ----

    [Theory]
    [InlineData("TigerCS.Web/Pages/TicketDetails.cshtml")]
    [InlineData("TigerCS.Web/Pages/Shared/_TicketRow.cshtml")]
    [InlineData("TigerCS.Web/Pages/Dashboard.cshtml")]
    [InlineData("TigerCS.Web/Pages/Tickets.cshtml")]
    [InlineData("TigerCS.Web/Pages/Shared/_TicketListView.cshtml")]
    public void TicketViews_NeverRenderOwnerUnassigned(string relativePath)
    {
        var html = File.ReadAllText(SourceFile(relativePath.Replace('/', Path.DirectorySeparatorChar)));

        // "Unassigned" would claim the ticket has no responsible owner, which
        // is never true: it is always at least its department's queue.
        Assert.DoesNotContain("Unassigned", html, StringComparison.Ordinal);

        // The user-facing label is "Assigned To", never the old "Owner".
        Assert.DoesNotContain(">Owner<", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<strong>Owner:</strong>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void TicketDetailsAndQueue_ShowBothAssignedDepartmentAndAssignedTo()
    {
        var details = File.ReadAllText(SourceFile(Path.Combine("TigerCS.Web", "Pages", "TicketDetails.cshtml")));
        Assert.Contains("Assigned Department", details, StringComparison.Ordinal);
        Assert.Contains("Assigned To", details, StringComparison.Ordinal);

        var queue = File.ReadAllText(SourceFile(Path.Combine("TigerCS.Web", "Pages", "Shared", "_TicketListView.cshtml")));
        Assert.Contains("<th>Assigned Department</th>", queue, StringComparison.Ordinal);
        Assert.Contains("<th>Assigned To</th>", queue, StringComparison.Ordinal);
    }
}
