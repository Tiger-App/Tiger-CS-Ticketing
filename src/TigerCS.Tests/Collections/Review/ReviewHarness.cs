using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Review;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.Collections.Review;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Modules.Collections;
using TigerCS.Infrastructure.Persistence;
using TigerCS.Tests.Collections.Services;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using TigerCS.Tests.Notifications.Fakes;

namespace TigerCS.Tests.Collections.Review;

/// <summary>Scripted Genesys: records every upload exactly as the service sent it and answers from a queue of outcomes.</summary>
public sealed class FakeGenesysClient : IGenesysOutboundClient
{
    public sealed record Call(string ContactListId, IReadOnlyList<GenesysContactPayload> Contacts);
    private readonly object _gate = new();
    public List<Call> Calls { get; } = [];
    public Queue<GenesysUploadOutcome> Script { get; } = new();
    /// <summary>For the next Accepted upload only: return this many contact ids instead of one per contact (null entry = one per contact).</summary>
    public Queue<int?> IdCounts { get; } = new();
    public int Created;

    /// <summary>Every suppression (PUT callable=false) request, as the service sent it.</summary>
    public sealed record Suppression(string ContactListId, string ContactId, GenesysContactPayload Contact);
    public List<Suppression> Suppressions { get; } = [];
    public Queue<GenesysSuppressResult> SuppressScript { get; } = new();

    public Task<GenesysSuppressResult> SetNotCallableAsync(string contactListId, string contactId, GenesysContactPayload contact, CancellationToken ct)
    {
        lock (_gate)
        {
            Suppressions.Add(new(contactListId, contactId, contact));
            return Task.FromResult(SuppressScript.Count > 0 ? SuppressScript.Dequeue() : new GenesysSuppressResult(GenesysUploadOutcome.Accepted, 200, false, false, null));
        }
    }

    /// <summary>Column names per contact list id; a list that is not set has the six template columns. Set a list to a shorter array to test a list that does not fit.</summary>
    public Dictionary<string, string[]> ListColumns { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> ListChecks { get; } = [];
    public List<string> CampaignChecks { get; } = [];
    public Queue<GenesysUploadOutcome> ListCheckScript { get; } = new();

    public Task<GenesysContactListInfo> GetContactListAsync(string contactListId, CancellationToken ct)
    {
        lock (_gate)
        {
            ListChecks.Add(contactListId);
            var outcome = ListCheckScript.Count > 0 ? ListCheckScript.Dequeue() : GenesysUploadOutcome.Accepted;
            if (outcome != GenesysUploadOutcome.Accepted) return Task.FromResult(new GenesysContactListInfo(outcome, null, [], "Genesys could not be reached."));
            return Task.FromResult(new GenesysContactListInfo(outcome, 200, ListColumns.TryGetValue(contactListId, out var columns) ? columns : ["Phone", "CustomerName", "Email Address", "ReminderType", "AmountDue", "DueDate"], null));
        }
    }

    public Task<GenesysCampaignLookupResult> FindCampaignsByContactListAsync(string contactListId, CancellationToken ct)
    {
        lock (_gate)
        {
            CampaignChecks.Add(contactListId);
            return Task.FromResult(new GenesysCampaignLookupResult(GenesysUploadOutcome.Accepted, 200,
                Campaigns.Where(c => string.Equals(c.ContactListId, contactListId, StringComparison.OrdinalIgnoreCase)).ToList(), null));
        }
    }

    /// <summary>Campaigns that "exist" in the fake Genesys; <see cref="FindCampaignsByNameAsync"/> returns those whose name contains the searched text.</summary>
    public List<GenesysCampaignRef> Campaigns { get; } = [];
    public List<string> CampaignLookups { get; } = [];
    public List<(string Name, string ContactListId)> CampaignCreates { get; } = [];
    public Queue<GenesysUploadOutcome> LookupScript { get; } = new();
    public Queue<GenesysUploadOutcome> CreateScript { get; } = new();
    /// <summary>For the next Unknown create only: Genesys did make the campaign, but the answer was lost.</summary>
    public bool CreateUnknownStillCreates { get; set; }

    public Task<GenesysCampaignLookupResult> FindCampaignsByNameAsync(string name, CancellationToken ct)
    {
        lock (_gate)
        {
            CampaignLookups.Add(name);
            var outcome = LookupScript.Count > 0 ? LookupScript.Dequeue() : GenesysUploadOutcome.Accepted;
            return Task.FromResult(outcome == GenesysUploadOutcome.Accepted
                ? new GenesysCampaignLookupResult(outcome, 200, Campaigns.Where(c => c.Name.Contains(name, StringComparison.OrdinalIgnoreCase)).ToList(), null)
                : new GenesysCampaignLookupResult(outcome, outcome == GenesysUploadOutcome.Rejected ? 403 : null, [], "Genesys could not be reached."));
        }
    }

    /// <summary>Awaited before a create is applied: lets a test hold one dispatch inside "create" while another reaches its own lookup.</summary>
    public Func<Task>? BeforeCreate { get; set; }

    public async Task<GenesysCampaignCreateResult> CreateCampaignAsync(string name, string contactListId, GenesysCampaignOptions template, CancellationToken ct)
    {
        if (BeforeCreate is not null) await BeforeCreate();
        lock (_gate)
        {
            CampaignCreates.Add((name, contactListId));
            var outcome = CreateScript.Count > 0 ? CreateScript.Dequeue() : GenesysUploadOutcome.Accepted;
            if (outcome == GenesysUploadOutcome.Accepted || (outcome == GenesysUploadOutcome.Unknown && CreateUnknownStillCreates))
            {
                var made = new GenesysCampaignRef($"campaign-{Campaigns.Count + 1}", name, contactListId, "off");
                Campaigns.Add(made);
                if (outcome == GenesysUploadOutcome.Accepted) return new GenesysCampaignCreateResult(outcome, 200, made, null);
            }
            return new GenesysCampaignCreateResult(outcome, outcome == GenesysUploadOutcome.Rejected ? 400 : null, null, "Genesys did not confirm.");
        }
    }

    public Task<GenesysUploadResult> UploadContactsAsync(string contactListId, IReadOnlyList<GenesysContactPayload> contacts, CancellationToken ct)
    {
        lock (_gate)
        {
            Calls.Add(new(contactListId, contacts.ToList()));
            var outcome = Script.Count > 0 ? Script.Dequeue() : GenesysUploadOutcome.Accepted;
            return Task.FromResult(outcome switch
            {
                GenesysUploadOutcome.Accepted => new GenesysUploadResult(outcome, 200,
                    contacts.Take(IdCounts.Count > 0 ? IdCounts.Dequeue() ?? contacts.Count : contacts.Count).Select(_ => $"contact-{++Created}").ToList(), null),
                GenesysUploadOutcome.Rejected => new GenesysUploadResult(outcome, 400, [], "Genesys rejected the request (HTTP 400); nothing was created."),
                _ => new GenesysUploadResult(outcome, null, [], "The request timed out or the connection dropped before Genesys answered.")
            });
        }
    }
}

/// <summary>Scripted mail provider for the administrator report: records every message and can fail by recipient.</summary>
public sealed class FakeAdminEmailSender : TigerCS.Application.Modules.Notifications.Abstractions.IEmailSender
{
    public bool SendsRealMail { get; set; } = true;
    public List<TigerCS.Application.Modules.Notifications.Abstractions.EmailMessage> Sent { get; } = [];
    public Func<string, TigerCS.Application.Modules.Notifications.Abstractions.EmailSendResult>? Respond { get; set; }
    public Exception? Throw { get; set; }
    public Task<TigerCS.Application.Modules.Notifications.Abstractions.EmailSendResult> SendAsync(
        TigerCS.Application.Modules.Notifications.Abstractions.EmailMessage message, CancellationToken cancellationToken = default)
    {
        if (Throw is not null) throw Throw;
        var result = Respond?.Invoke(message.ToAddress) ?? TigerCS.Application.Modules.Notifications.Abstractions.EmailSendResult.Sent();
        if (result.Outcome == TigerCS.Application.Modules.Notifications.Abstractions.EmailSendOutcome.Sent) Sent.Add(message);
        return Task.FromResult(result);
    }
}

/// <summary>Test faults for the review-record writes: a failure before the batch is saved, or a lost answer AFTER the batch's transaction really committed.</summary>
public sealed class WriteFaults : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
{
    public int FailBeforeSave;
    public int FailAfterCommit;
    public bool Persistent;
    public int RecordSaves;
    public int FailProgress; public bool ProgressPersistent; public int ProgressAttempts;
    public int FailStart; public int StartAttempts;
    public int FailReads;
    public Func<Exception> Make { get; set; } = () => new TransientTestException();

    public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
        Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData, Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context!.ChangeTracker.Entries<TigerCS.Domain.Modules.Collections.Review.CollectionsReviewRecord>().Any(e => e.State == Microsoft.EntityFrameworkCore.EntityState.Added))
        {
            RecordSaves++;
            if (Persistent || FailBeforeSave > 0) { if (!Persistent) FailBeforeSave--; throw Make(); }
        }
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }
}

public sealed class CommitFaults(WriteFaults faults) : Microsoft.EntityFrameworkCore.Diagnostics.DbTransactionInterceptor
{
    public override Task TransactionCommittedAsync(System.Data.Common.DbTransaction transaction, Microsoft.EntityFrameworkCore.Diagnostics.TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        if (faults.FailAfterCommit > 0) { faults.FailAfterCommit--; throw faults.Make(); }
        return base.TransactionCommittedAsync(transaction, eventData, cancellationToken);
    }
}

public sealed class TransientTestException : Exception;

/// <summary>
/// Command-level faults. A failing WRITE is raised the way EF Core's non-retrying SQL Server strategy does: as an InvalidOperationException ("...likely due to a transient failure...")
/// around the provider exception; a failing READ is the raw provider exception.
/// </summary>
public sealed class CommandFaults(WriteFaults faults) : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
{
    public const string EfMessage = "An exception has been raised that is likely due to a transient failure. Consider enabling transient error resiliency by adding 'EnableRetryOnFailure' to the 'UseSqlServer' call.";

    public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> NonQueryExecutingAsync(System.Data.Common.DbCommand command,
        Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData, Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        var text = command.CommandText;
        if (text.Contains("UPDATE", StringComparison.Ordinal) && text.Contains("\"CollectionsReviewRuns\"", StringComparison.Ordinal))
        {
            if (text.Contains("StartedAtUtc", StringComparison.Ordinal) && !text.Contains("CompletedAtUtc", StringComparison.Ordinal))
            {
                faults.StartAttempts++;
                if (Take(ref faults.FailStart)) throw new InvalidOperationException(EfMessage, faults.Make());
            }
            else if (text.Contains("ProgressPercent", StringComparison.Ordinal) && !text.Contains("IsCurrent", StringComparison.Ordinal))
            {
                faults.ProgressAttempts++;
                if (faults.ProgressPersistent || Take(ref faults.FailProgress)) throw new InvalidOperationException(EfMessage, faults.Make());
            }
        }
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(System.Data.Common.DbCommand command,
        Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData, Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result, CancellationToken cancellationToken = default)
    {
        if (command.CommandText.Contains("FROM \"CollectionsReviewRuns\"", StringComparison.Ordinal) && Take(ref faults.FailReads)) throw faults.Make();
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    private static bool Take(ref int counter) { if (counter <= 0) return false; counter--; return true; }
}

public sealed class FakeJobScheduler : IReviewJobScheduler
{
    public bool Available { get; set; } = true;
    public bool IsAvailable => Available;
    /// <summary>Background jobs are off, but a refresh a person starts runs in this process (the in-process scheduler).</summary>
    public bool InProcessRefresh { get; set; }
    public bool CanRunRefresh => Available || InProcessRefresh;
    public bool RefreshRunsInProcess => InProcessRefresh;
    public Exception? EnqueueFailure { get; set; }
    public List<long> Refreshes { get; } = [];
    public List<long> Dispatches { get; } = [];
    public void EnqueueRefresh(long runId) { if (EnqueueFailure is not null) throw EnqueueFailure; Refreshes.Add(runId); }
    public void EnqueueDispatch(long dispatchId) => Dispatches.Add(dispatchId);
    public int Sweeps;
    public void EnqueueSuppressionSweep() => Sweeps++;
}

/// <summary>
/// The real EF store and services over a real relational database (SQLite), with scripted PACT and Genesys. In-memory by
/// default; <c>file: true</c> gives each scope its own connection so concurrent calls really race on the database.
/// </summary>
internal sealed class ReviewHarness : IDisposable
{
    private sealed class SqliteTigerCsDbContext(DbContextOptions<TigerCsDbContext> options) : TigerCsDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);
            builder.Entity<Ticket>().Property(t => t.RowVersion).HasDefaultValueSql("X'0000000000000000'");
            builder.Entity<TicketAgentHandoff>().Property(h => h.RowVersion).HasDefaultValueSql("X'0000000000000000'");
        }
    }

    /// <summary>A day on which Current Month (day 14) is scheduled, noon in Dubai.</summary>
    public static readonly DateTime Now = new(2026, 10, 14, 8, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection? _shared;
    private readonly string? _file;

    public CollectionsCampaignAppServiceTests.Source Source { get; } = new() { ReadAt = Now };
    public CollectionsOptions Options { get; } = new() { Enabled = true };
    public PactReceivablesOptions Sql { get; } = new() { Enabled = true };
    public CollectionsCampaignOptions Campaign { get; } = new() { FinancialSourceValidated = true, LegalNoticeExportEnabled = true };
    /// <summary>The automated reconciliation evidence the gate consults (null = sign-off only, as most tests want; set a history to test the per-company gate and held units).</summary>
    public TigerCS.Application.Modules.Collections.Reconciliation.ReconciliationHistory? Evidence { get; set; }
    public CollectionsReviewOptions Review { get; } = new();
    /// <summary>The dedicated Manual list id used by the tests (a made-up GUID; nothing is ever sent to Genesys).</summary>
    public const string ManualListId = "11111111-2222-4333-8444-555555555555";
    public GenesysOutboundOptions Genesys { get; } = NewGenesys();
    private static GenesysOutboundOptions NewGenesys()
    {
        var options = new GenesysOutboundOptions { Enabled = true, LiveCustomerDispatchEnabled = true, SuppressionEnabled = true, LeaseHeartbeatSeconds = 0.02 };
        options.ContactListIds["Manual"] = ManualListId;
        return options;
    }
    /// <summary>The shared CRM link service (null = CRM linking off: contacts are PACT's, as before).</summary>
    public TigerCS.Application.Modules.Collections.Services.CollectionsUnitLinkService? Links { get; set; }
    public FakeGenesysClient Client { get; } = new();
    public FakeJobScheduler Scheduler { get; } = new();
    public FakeAdminEmailSender Email { get; } = new();
    public WriteFaults Faults { get; } = new();
    public TigerCS.Infrastructure.Modules.Collections.ReviewWriteRetryPolicy RetryPolicy { get; set; } = new()
    { MaxAttempts = 3, Delay = _ => TimeSpan.Zero, ExtraTransient = e => e is TransientTestException };
    public FakeTimeProvider Time { get; } = new(Now);
    public CollectionsCaller Manager { get; } = new(Guid.NewGuid(), [Roles.CsManager], []);
    /// <summary>May read balances but not send.</summary>
    public CollectionsCaller Agent { get; } = new(Guid.NewGuid(), [Roles.CsAgent], []);
    /// <summary>No Collections grant at all.</summary>
    public CollectionsCaller Reporting { get; } = new(Guid.NewGuid(), [Roles.ReportingUser], []);

    public ReviewHarness(bool file = false)
    {
        if (file)
        {
            _file = Path.Combine(Path.GetTempPath(), $"review-{Guid.NewGuid():N}.db");
        }
        else
        {
            _shared = new SqliteConnection("DataSource=:memory:");
            _shared.Open();
        }
        using var context = CreateContext();
        context.Database.EnsureCreated();
    }

    public TigerCsDbContext CreateContext() => new SqliteTigerCsDbContext(
        (_shared is not null
            ? new DbContextOptionsBuilder<TigerCsDbContext>().UseSqlite(_shared)
            : new DbContextOptionsBuilder<TigerCsDbContext>().UseSqlite($"Data Source={_file};Pooling=False;Default Timeout=60"))
        .AddInterceptors(Faults, new CommitFaults(Faults), new CommandFaults(Faults)).Options);

    /// <summary>The export lock for tests that do not race: always free. The lock's own behaviour is tested with <see cref="GenesysExportRunLockTests"/>.</summary>
    public sealed class FreeExportLock : IGenesysExportLock
    {
        public Task<IAsyncDisposable?> TryAcquireAsync(TimeSpan wait, CancellationToken ct) => Task.FromResult<IAsyncDisposable?>(new NoLock());

        private sealed class NoLock : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    public sealed class Scope(TigerCsDbContext context, ReviewHarness h) : IDisposable
    {
        public IReviewStore Store { get; } = new ReviewStore(context, h.RetryPolicy);
        public ReviewQueryService Query { get; private set; } = null!;
        public ReviewRefreshService Refresh { get; private set; } = null!;
        public DispatchService Dispatch { get; private set; } = null!;
        public AutoDispatchService Auto { get; private set; } = null!;
        public GenesysExportService Export { get; private set; } = null!;
        public ManualReviewExportService ReviewExport { get; private set; } = null!;
        public SuppressionService Suppression { get; private set; } = null!;
        public DispatchAdminReportService AdminReport { get; private set; } = null!;
        public TigerCsDbContext Context => context;

        public Scope Wire(IGenesysOutboundClient? client = null)
        {
            var clock = new CollectionsClock(h.Options, h.Time);
            var auth = new CollectionsAuthorizationService(h.Options, new FakeDepartmentRepository());
            Query = new ReviewQueryService(h.Options, h.Review, auth, clock, Store, null, h.Genesys);
            Refresh = new ReviewRefreshService(h.Options, h.Campaign, h.Sql, h.Review, auth, clock, h.Source, Store, h.Scheduler, Query,
                NullLogger<ReviewRefreshService>.Instance, h.Links, h.Evidence);
            var balances = new CurrentBalanceReader(h.Sql, h.Source, Refresh, NullLogger<CurrentBalanceReader>.Instance);
            Dispatch = new DispatchService(h.Options, h.Genesys, auth, clock, Store, h.Scheduler, client ?? h.Client, Query, balances,
                NullLogger<DispatchService>.Instance);
            Export = new GenesysExportService(h.Options, h.Sql, h.Genesys, auth, clock, Query, client ?? h.Client, new TigerCS.Infrastructure.Audit.AuditEntryWriter(context, h.Time), new TigerCS.Infrastructure.Modules.Collections.CollectionsUnitOfWork(context), h.Review, new ReviewStore(context, h.RetryPolicy), new FreeExportLock(), NullLogger<GenesysExportService>.Instance);
            ReviewExport = new ManualReviewExportService(h.Options, h.Sql, auth, clock, Query, new TigerCS.Infrastructure.Audit.AuditEntryWriter(context, h.Time), new TigerCS.Infrastructure.Modules.Collections.CollectionsUnitOfWork(context), NullLogger<ManualReviewExportService>.Instance);
            Auto = new AutoDispatchService(h.Options, h.Campaign, clock, Store, Query, Refresh, Dispatch, NullLogger<AutoDispatchService>.Instance);
            AdminReport = new DispatchAdminReportService(h.Campaign, Store, h.Email, clock, auth, NullLogger<DispatchAdminReportService>.Instance);
            Suppression = new SuppressionService(h.Options, h.Genesys, clock, Store, client ?? h.Client, balances, NullLogger<SuppressionService>.Instance);
            return this;
        }

        public void Dispose() => context.Dispose();
    }

    public Scope NewScope(IGenesysOutboundClient? client = null) => new Scope(CreateContext(), this).Wire(client);

    /// <summary>Starts and runs a refresh job, the way Hangfire would, and returns the published run.</summary>
    public async Task<ReviewRunDto> RefreshAsync()
    {
        using var scope = NewScope();
        var started = await scope.Refresh.StartAsync(Manager, null, CancellationToken.None);
        Assert.True(started.IsSuccess, started.Detail);
        await scope.Refresh.RunAsync(started.Value!.RunId, CancellationToken.None);
        return (await scope.Query.GetRunAsync(Manager, started.Value.RunId, CancellationToken.None)).Value!;
    }

    /// <summary>A Ready-able V2 source row: remaining equals plan (Unpaid), valid UAE mobile, one unit per tenant.</summary>
    public static PactReceivableInstalment Row(int n, decimal amount = 500m, int day = 20, string status = "Installment", decimal? plan = null,
        int company = 4, string? mobile = null, string? name = null, string project = "TP140") => new(company, $"T{n:D4}", name ?? $"Customer {n}",
            mobile ?? $"97150{n:D7}", $"c{n}@example.test", 1000 + n, $"TP140-{n}", project, $"INV-{n}", "",
            new DateTime(2026, 10, day), amount, status, PlanAmount: plan ?? amount);

    public SelectionRequest All(ReviewFilter? filter = null) => new(filter ?? new ReviewFilter(), ReviewQueryService.ModeAllMatching);

    public async Task<(SelectionSummaryDto Summary, ConfirmDispatchRequest Confirm)> PrepareAsync(SelectionRequest? selection = null, string key = "k1")
    {
        using var scope = NewScope();
        var request = selection ?? All(new ReviewFilter(ReminderType: "CurrentMonth"));
        var summary = (await scope.Query.SummarizeAsync(Manager, request, CancellationToken.None)).Value!;
        return (summary, new ConfirmDispatchRequest(request, summary.Count, summary.Fingerprint, key, true));
    }

    public void Dispose()
    {
        _shared?.Dispose();
        SqliteConnection.ClearAllPools();
        if (_file is not null) try { File.Delete(_file); } catch (IOException) { }
    }
}
