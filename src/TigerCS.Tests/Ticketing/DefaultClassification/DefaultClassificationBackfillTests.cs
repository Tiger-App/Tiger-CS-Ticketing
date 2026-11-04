using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.ClassificationAndRouting;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Domain.Modules.WorkflowConfiguration;
using TigerCS.Tests.GenesysIntegration.Fakes;
using TigerCS.Tests.Ticketing.Fakes;

namespace TigerCS.Tests.Ticketing.DefaultClassification;

/// <summary>
/// The historical backfill: a read-only preview and dry run that identify tickets without a valid classification, and a separately enabled live
/// run that applies Customer Services / General only where necessary - classification only, never ownership, department, priority, SLA, status
/// or history, never Collections, never a valid classification.
/// </summary>
public class DefaultClassificationBackfillTests
{
    private static readonly DateTime Now = new(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc);

    private sealed class FakeBackfillRepository(FakeTicketRepository tickets) : IDefaultClassificationBackfillRepository
    {
        public Task<IReadOnlyList<Ticket>> ListPossiblyUnclassifiedAsync(long afterTicketId, int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Ticket>>(tickets.All.Where(t => t.TicketId > afterTicketId).OrderBy(t => t.TicketId).Take(limit).ToList());
    }

    private sealed class World
    {
        public required GenesysServiceFixture F { get; init; }
        public required Department Cs { get; init; }
        public required Department Leasing { get; init; }
        public required Department Collections { get; init; }
        public required Category General { get; init; }
        public required RequestType GeneralType { get; init; }
        public required Category LeasingCategory { get; init; }
        public required RequestType LeasingType { get; init; }
        public required DefaultClassificationBackfillService Backfill { get; init; }
        public Ticket Unclassified { get; set; } = null!;
        public Ticket Valid { get; set; } = null!;
        public Ticket CategoryOnly { get; set; } = null!;
        public Ticket RequestTypeOnly { get; set; } = null!;
        public Ticket Dangling { get; set; } = null!;
        public Ticket CollectionsTicket { get; set; } = null!;
        public Ticket AlreadyDefault { get; set; } = null!;
        public Ticket ClosedUnclassified { get; set; } = null!;
    }

    private static async Task<Ticket> AddAsync(World w, Ticket ticket) { await w.F.Tickets.AddAsync(ticket); return ticket; }

    private static async Task<World> BuildAsync()
    {
        var f = new GenesysServiceFixture();
        var cs = f.Departments.AddDepartment("Customer Services", "CS");
        var leasing = f.Departments.AddDepartment("Leasing Customer Services", "LCS");
        var collections = f.Departments.AddDepartment("Collections", "COL");
        var (_, general, generalType) = f.SeedDefaultClassification(cs);
        f.SeedGeneral(leasing);   // every active department has its own General
        var leasingCategory = f.Categories.Seed(leasing.DepartmentId, "Leasing Inquiry");
        var template = f.WorkflowTemplates.Add(TestWorkflows.PublishedStandard(workflowId: 400));
        var leasingType = f.RequestTypes.Add(new RequestType(
            leasing.DepartmentId, "Leasing Inquiry", template.WorkflowId, (byte)PriorityLevel.Medium, false, true, true, true));

        var w = new World
        {
            F = f, Cs = cs, Leasing = leasing, Collections = collections, General = general, GeneralType = generalType,
            LeasingCategory = leasingCategory, LeasingType = leasingType,
            Backfill = new DefaultClassificationBackfillService(new FakeBackfillRepository(f.Tickets), f.DefaultClassification)
        };

        w.Unclassified = await AddAsync(w, Ticket.CreateUnclassified("TG-CS-1", cs.DepartmentId, "no classification", Now, defaultPriorityId: 3));
        w.Unclassified.AssignTo(Guid.NewGuid());

        w.Valid = await AddAsync(w, Ticket.CreateUnverified("TG-LCS-2", leasing.DepartmentId, leasingCategory.CategoryId, 2, "valid", Now));
        w.Valid.ClassifyRequestType(leasingType.RequestTypeId);

        w.CategoryOnly = await AddAsync(w, Ticket.CreateUnverified("TG-LCS-3", leasing.DepartmentId, leasingCategory.CategoryId, 3, "category only", Now));

        w.RequestTypeOnly = await AddAsync(w, Ticket.CreateUnclassified("TG-CS-4", cs.DepartmentId, "type only", Now, defaultPriorityId: 3));
        w.RequestTypeOnly.ClassifyRequestType(leasingType.RequestTypeId);

        w.Dangling = await AddAsync(w, Ticket.CreateUnverified("TG-CS-5", cs.DepartmentId, 888888, 3, "dangling category", Now));
        w.Dangling.ClassifyRequestType(777777);

        w.CollectionsTicket = await AddAsync(w, Ticket.CreateUnclassified("TG-COL-6", collections.DepartmentId, "collections", Now, defaultPriorityId: 3));

        w.AlreadyDefault = await AddAsync(w, Ticket.CreateUnverified("TG-CS-7", cs.DepartmentId, general.CategoryId, 3, "default", Now));
        w.AlreadyDefault.ClassifyRequestType(generalType.RequestTypeId);

        w.ClosedUnclassified = await AddAsync(w, Ticket.CreateUnclassified("TG-CS-8", cs.DepartmentId, "old closed", Now, defaultPriorityId: 3));
        w.ClosedUnclassified.AssignTo(Guid.NewGuid());
        w.ClosedUnclassified.ChangeStatus(TicketStatus.InProgress);
        w.ClosedUnclassified.Resolve(ResolutionOutcome.Resolved, null);
        w.ClosedUnclassified.Close();
        return w;
    }

    private static DefaultClassificationBackfillOptions Options(bool enabled = false, bool includeCategoryOnly = false) =>
        new() { Enabled = enabled, IncludeCategoryOnly = includeCategoryOnly, DefaultBatchSize = 100, MaxBatchSize = 100 };

    private static string Snapshot(Ticket t) =>
        $"{t.CategoryId}|{t.RequestTypeId}|{t.WorkflowTemplateId}|{t.CurrentOwnerEmployeeId}|{t.CurrentDepartmentId}|{t.OriginatingDepartmentId}|{t.PriorityId}|{t.TicketStatus}|{t.SlaState}";

    // ---- Preview / dry run: read-only ----

    [Fact]
    public async Task Preview_IdentifiesEveryKindOfTicketWithoutAValidClassification_AndWritesNothing()
    {
        var w = await BuildAsync();
        var before = w.F.Tickets.All.ToDictionary(t => t.TicketId, Snapshot);
        var audits = w.F.Audit.Entries.Count;

        var preview = await w.Backfill.PreviewAsync(0, 100, Options());

        Assert.True(preview.ConfigurationAvailable);
        Assert.Equal(8, preview.Examined);
        Assert.Equal(4, preview.Candidates);   // unclassified, request-type-only, dangling and the closed unclassified ticket
        Assert.Equal(TicketClassificationState.NoClassification, Row(preview, w.Unclassified).State);
        Assert.Equal(TicketClassificationState.RequestTypeOnly, Row(preview, w.RequestTypeOnly).State);
        Assert.Equal(TicketClassificationState.InvalidReference, Row(preview, w.Dangling).State);
        Assert.Equal(DefaultClassificationBackfillClass.AlreadyValid, Row(preview, w.Valid).Class);
        Assert.Equal(DefaultClassificationBackfillClass.AlreadyDefault, Row(preview, w.AlreadyDefault).Class);
        Assert.Equal(DefaultClassificationBackfillClass.CategoryOnlyKept, Row(preview, w.CategoryOnly).Class);
        Assert.Equal(DefaultClassificationBackfillClass.ExcludedCollections, Row(preview, w.CollectionsTicket).Class);
        Assert.Equal(DefaultClassificationBackfillClass.Candidate, Row(preview, w.ClosedUnclassified).Class);
        Assert.Equal(1, preview.ByStatus["Closed"]);
        // Nothing moved.
        Assert.Equal(before, w.F.Tickets.All.ToDictionary(t => t.TicketId, Snapshot));
        Assert.Equal(audits, w.F.Audit.Entries.Count);
    }

    private static DefaultClassificationBackfillRow Row(DefaultClassificationBackfillPreview p, Ticket t) => p.Rows.Single(r => r.TicketId == t.TicketId);

    [Fact]
    public async Task ADryRun_ReportsWhatWouldBeDone_AndChangesNothing()
    {
        var w = await BuildAsync();
        var before = w.F.Tickets.All.ToDictionary(t => t.TicketId, Snapshot);

        var run = await w.Backfill.RunAsync(0, 100, dryRun: true, Options(enabled: false));

        Assert.Equal(DefaultClassificationBackfillRunStatus.Completed, run.Status);
        Assert.True(run.DryRun);
        Assert.Equal(4, run.WouldApply);
        Assert.Equal(0, run.Applied);
        Assert.Equal(before, w.F.Tickets.All.ToDictionary(t => t.TicketId, Snapshot));
        Assert.DoesNotContain(w.F.Audit.Entries, e => e.Action == "ApplyDefaultClassification");
    }

    [Fact]
    public async Task ALiveRun_IsRefusedUnlessExplicitlyEnabled()
    {
        var w = await BuildAsync();
        var before = w.F.Tickets.All.ToDictionary(t => t.TicketId, Snapshot);

        var run = await w.Backfill.RunAsync(0, 100, dryRun: false, Options(enabled: false));

        Assert.Equal(DefaultClassificationBackfillRunStatus.Disabled, run.Status);
        Assert.Equal(before, w.F.Tickets.All.ToDictionary(t => t.TicketId, Snapshot));
    }

    [Fact]
    public async Task WithoutTheConfiguration_NeitherPreviewNorRunAppliesAnything()
    {
        var f = new GenesysServiceFixture();
        f.Departments.AddDepartment("Customer Services", "CS");
        var leasing = f.Departments.AddDepartment("Leasing Customer Services", "LCS");
        await f.Tickets.AddAsync(Ticket.CreateUnclassified("TG-LCS-1", leasing.DepartmentId, "x", Now, defaultPriorityId: 3));
        var service = new DefaultClassificationBackfillService(new FakeBackfillRepository(f.Tickets), f.DefaultClassification);

        var preview = await service.PreviewAsync(0, 10, Options());
        var run = await service.RunAsync(0, 10, dryRun: false, Options(enabled: true));

        Assert.False(preview.ConfigurationAvailable);
        Assert.Equal(DefaultClassificationBackfillRunStatus.ConfigurationMissing, run.Status);
        Assert.Null(f.Tickets.All.Single().CategoryId);
    }

    // ---- Live run (separately enabled) ----

    [Fact]
    public async Task ALiveRun_AppliesGeneralOnlyWhereNecessary_AndNothingElseChanges()
    {
        var w = await BuildAsync();
        var snapshots = w.F.Tickets.All.ToDictionary(t => t.TicketId, t => (Ticket: t, Before: Snapshot(t)));
        var ownerOfUnclassified = w.Unclassified.CurrentOwnerEmployeeId;

        var run = await w.Backfill.RunAsync(0, 100, dryRun: false, Options(enabled: true));

        Assert.Equal(4, run.Applied);
        foreach (var t in new[] { w.Unclassified, w.RequestTypeOnly, w.Dangling, w.ClosedUnclassified })
        {
            Assert.Equal(w.General.CategoryId, t.CategoryId);
            Assert.Equal(w.GeneralType.RequestTypeId, t.RequestTypeId);
            Assert.NotNull(t.WorkflowTemplateId);
        }

        // Ownership, department, priority, status and SLA are exactly as they were.
        Assert.Equal(ownerOfUnclassified, w.Unclassified.CurrentOwnerEmployeeId);
        foreach (var (ticket, before) in snapshots.Values)
        {
            var after = Snapshot(ticket);
            Assert.Equal(before.Split('|').Skip(3), after.Split('|').Skip(3));   // everything after the three classification fields
        }

        Assert.Equal(TicketStatus.Closed, w.ClosedUnclassified.TicketStatus);   // a closed ticket gets the label, not a reopening
        // Left alone: valid, category-only (unless approved), already default, Collections.
        Assert.Equal(w.LeasingCategory.CategoryId, w.Valid.CategoryId);
        Assert.Equal(w.LeasingType.RequestTypeId, w.Valid.RequestTypeId);
        Assert.Equal(w.LeasingCategory.CategoryId, w.CategoryOnly.CategoryId);
        Assert.Null(w.CategoryOnly.RequestTypeId);
        Assert.Null(w.CollectionsTicket.CategoryId);
        Assert.Null(w.CollectionsTicket.RequestTypeId);
        Assert.Equal(4, w.F.Audit.Entries.Count(e => e.Action == "ApplyDefaultClassification" && e.AfterValue!.Contains("Historical backfill", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ASecondLiveRun_ChangesNothing()
    {
        var w = await BuildAsync();
        await w.Backfill.RunAsync(0, 100, dryRun: false, Options(enabled: true));
        var after = w.F.Tickets.All.ToDictionary(t => t.TicketId, Snapshot);
        var audits = w.F.Audit.Entries.Count;

        var second = await w.Backfill.RunAsync(0, 100, dryRun: false, Options(enabled: true));

        Assert.Equal(0, second.Applied);
        Assert.Equal(after, w.F.Tickets.All.ToDictionary(t => t.TicketId, Snapshot));
        Assert.Equal(audits, w.F.Audit.Entries.Count);
    }

    [Fact]
    public async Task CategoryOnlyTickets_AreAppliedOnlyWhenTheApprovedRunIncludesThem()
    {
        var w = await BuildAsync();

        var run = await w.Backfill.RunAsync(0, 100, dryRun: false, Options(enabled: true, includeCategoryOnly: true));

        Assert.Equal(5, run.Applied);
        var leasingGeneral = (await w.F.Categories.ListAsync(true, w.Leasing.DepartmentId)).Single(c => c.Name == "General");
        Assert.Equal(leasingGeneral.CategoryId, w.CategoryOnly.CategoryId);   // General within the ticket's own department
        Assert.NotNull(w.CategoryOnly.RequestTypeId);
    }

    [Fact]
    public async Task ABatch_IsBoundedAndResumable()
    {
        var w = await BuildAsync();

        var first = await w.Backfill.RunAsync(0, 3, dryRun: true, Options());
        var second = await w.Backfill.RunAsync(first.ResumeAfterTicketId, 100, dryRun: true, Options());

        Assert.Equal(3, first.Examined);
        Assert.False(first.BacklogExhausted);
        Assert.Equal(5, second.Examined);
        Assert.True(second.BacklogExhausted);
    }

    [Fact]
    public async Task ATicketInADepartmentWithoutGeneral_IsReportedAsAConfigurationError_NeverGivenCustomerServicesGeneral()
    {
        var w = await BuildAsync();
        var handover = w.F.Departments.AddDepartment("Handover", "HO");   // active, no General of its own
        var stranded = await AddAsync(w, Ticket.CreateUnclassified("TG-HO-9", handover.DepartmentId, "no general here", Now, defaultPriorityId: 3));

        var preview = await w.Backfill.PreviewAsync(0, 100, Options());
        var run = await w.Backfill.RunAsync(0, 100, dryRun: false, Options(enabled: true));

        Assert.Equal(DefaultClassificationBackfillClass.DepartmentGeneralMissing, Row(preview, stranded).Class);
        Assert.Equal(1, preview.Exclusions["DepartmentGeneralMissing"]);
        Assert.Null(stranded.CategoryId);
        Assert.Null(stranded.RequestTypeId);
        Assert.Equal(handover.DepartmentId, stranded.CurrentDepartmentId);
        Assert.Equal(4, run.Applied);   // the others are still done
    }

    // ---- The classifier is the single source of the decision ----

    [Theory]
    [InlineData(TicketClassificationState.Valid, false, false, DefaultClassificationBackfillClass.AlreadyValid)]
    [InlineData(TicketClassificationState.NoClassification, false, false, DefaultClassificationBackfillClass.Candidate)]
    [InlineData(TicketClassificationState.Inconsistent, false, false, DefaultClassificationBackfillClass.Candidate)]
    [InlineData(TicketClassificationState.CategoryOnly, false, false, DefaultClassificationBackfillClass.CategoryOnlyKept)]
    [InlineData(TicketClassificationState.NoClassification, true, false, DefaultClassificationBackfillClass.ExcludedCollections)]
    [InlineData(TicketClassificationState.Valid, false, true, DefaultClassificationBackfillClass.AlreadyDefault)]
    public void TheClassifier_DecidesEachState(TicketClassificationState state, bool collections, bool isDefault, DefaultClassificationBackfillClass expected) =>
        Assert.Equal(expected, DefaultClassificationBackfillClassifier.Decide(state, collections, isDefault, new DefaultClassificationBackfillOptions()));
}
