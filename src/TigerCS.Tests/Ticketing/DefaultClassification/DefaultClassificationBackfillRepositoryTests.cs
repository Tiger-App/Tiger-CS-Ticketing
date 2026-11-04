using Microsoft.EntityFrameworkCore;
using TigerCS.Domain.Modules.ClassificationAndRouting;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Modules.Ticketing.Repositories;
using TigerCS.Tests.WorkflowConfiguration;

namespace TigerCS.Tests.Ticketing.DefaultClassification;

/// <summary>The EF query behind the backfill returns exactly the tickets that might lack a valid classification - read-only, resumable, ordered.</summary>
public class DefaultClassificationBackfillRepositoryTests
{
    private static readonly DateTime Now = new(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task ReturnsMissingPartialAndDanglingTickets_InOrder_AndSkipsValidOnes()
    {
        await using var db = await WorkflowConfigurationTestDb.CreateSeededContextAsync();
        var cs = await db.Departments.SingleAsync(d => d.Code == "CS");
        var category = new Category("Handover", cs.DepartmentId);
        db.Categories.Add(category);
        var requestType = await db.RequestTypes.FirstAsync(r => r.DepartmentId == cs.DepartmentId);
        await db.SaveChangesAsync();

        var none = Ticket.CreateUnclassified("TG-CS-1", cs.DepartmentId, "none", Now, defaultPriorityId: 3);
        var valid = Ticket.CreateUnverified("TG-CS-2", cs.DepartmentId, category.CategoryId, 3, "valid", Now);
        valid.ClassifyRequestType(requestType.RequestTypeId);
        var categoryOnly = Ticket.CreateUnverified("TG-CS-3", cs.DepartmentId, category.CategoryId, 3, "category only", Now);
        var dangling = Ticket.CreateUnverified("TG-CS-4", cs.DepartmentId, 99999, 3, "dangling", Now);
        dangling.ClassifyRequestType(requestType.RequestTypeId);
        db.Tickets.AddRange(none, valid, categoryOnly, dangling);
        await db.SaveChangesAsync();

        var repository = new DefaultClassificationBackfillRepository(db);

        var all = await repository.ListPossiblyUnclassifiedAsync(0, 10);
        var resumed = await repository.ListPossiblyUnclassifiedAsync(none.TicketId, 10);
        var limited = await repository.ListPossiblyUnclassifiedAsync(0, 1);

        Assert.Equal([none.TicketId, categoryOnly.TicketId, dangling.TicketId], all.Select(t => t.TicketId));
        Assert.DoesNotContain(all, t => t.TicketId == valid.TicketId);
        Assert.Equal([categoryOnly.TicketId, dangling.TicketId], resumed.Select(t => t.TicketId));
        Assert.Single(limited);
    }
}
