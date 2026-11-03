using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.BackgroundJobs;
using Env = TigerCS.Tests.Ticketing.Services.CallCenterUnassignedWorkReconcilerTests.Env;

namespace TigerCS.Tests.Ticketing.Services;

/// <summary>The pass around the reconciler: defaults, MaxAssignmentsPerRun, no overlap, and the eligible / skipped / assigned / failed log lines.</summary>
public class CallCenterUnassignedWorkJobTests
{
    private sealed class Monitor(CallCenterReconciliationOptions value) : IOptionsMonitor<CallCenterReconciliationOptions>
    {
        public CallCenterReconciliationOptions CurrentValue { get; } = value;
        public CallCenterReconciliationOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<CallCenterReconciliationOptions, string?> listener) => null;
    }

    private sealed class Capture<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add((logLevel, formatter(state, exception) + (exception is null ? string.Empty : " | " + exception.Message)));
    }

    private static (CallCenterUnassignedWorkJob Job, Capture<CallCenterUnassignedWorkJob> Log, CallCenterReconciliationRunLock Lock) Build(Env env, CallCenterReconciliationOptions options)
    {
        var services = new ServiceCollection().AddSingleton(env.Reconciler).BuildServiceProvider();
        var log = new Capture<CallCenterUnassignedWorkJob>();
        var runLock = new CallCenterReconciliationRunLock(new ConfigurationBuilder().Build());
        return (new CallCenterUnassignedWorkJob(services.GetRequiredService<IServiceScopeFactory>(), new Monitor(options), runLock, log), log, runLock);
    }

    [Fact]
    public void ShippedDefaults_AreDisabled_DryRun_CsAgentsOnly()
    {
        var defaults = new CallCenterReconciliationOptions();
        Assert.False(defaults.Enabled);
        Assert.True(defaults.DryRun);
        Assert.True(defaults.CsAgentRoleOnly);

        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "appsettings.json"));
        var section = new ConfigurationBuilder().AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json))).Build().GetSection("CallCenterReconciliation").Get<CallCenterReconciliationOptions>()!;
        Assert.False(section.Enabled);
        Assert.True(section.DryRun);
    }

    [Fact]
    public async Task Disabled_DoesNothing_AndLogsNothing()
    {
        var env = new Env();
        env.AddCallCenterAgents(1);
        var ticket = await env.AddQueuedTicketAsync();
        var (job, log, _) = Build(env, new CallCenterReconciliationOptions { Enabled = false, DryRun = false });

        await job.RunAsync(CancellationToken.None);

        Assert.Null(ticket.CurrentOwnerEmployeeId);
        Assert.Empty(log.Lines);
    }

    [Fact]
    public async Task DryRun_WritesNothing_AndLogsEligibleTicketsAndSummary()
    {
        var env = new Env();
        env.AddCallCenterAgents(1);
        var ticket = await env.AddQueuedTicketAsync();
        var (job, log, _) = Build(env, new CallCenterReconciliationOptions { Enabled = true, DryRun = true });

        await job.RunAsync(CancellationToken.None);

        Assert.Null(ticket.CurrentOwnerEmployeeId);
        Assert.Empty(env.Audit.Entries);
        Assert.Contains(log.Lines, l => l.Message.Contains("Reconciliation eligible") && l.Message.Contains(ticket.TicketNumber));
        Assert.Contains(log.Lines, l => l.Message.StartsWith("DRY RUN: would reconcile"));
        Assert.Contains(log.Lines, l => l.Message.Contains("finished (DRY RUN - nothing written)") && l.Message.Contains("would-assign 1") && l.Message.Contains("assigned 0"));
    }

    [Fact]
    public async Task Live_AssignsAndLogs_AndSummarisesSkippedByClass()
    {
        var env = new Env();
        var agents = env.AddCallCenterAgents(1);
        var human = await env.AddQueuedTicketAsync();
        var ai = await env.AddQueuedTicketAsync(e => e with { HasGenesysInteraction = true, HasLiveGenesysInteraction = true, LastActivityUtc = new DateTime(2026, 11, 3, 8, 59, 0, DateTimeKind.Utc) });
        var (job, log, _) = Build(env, new CallCenterReconciliationOptions { Enabled = true, DryRun = false });

        await job.RunAsync(CancellationToken.None);

        Assert.Equal(agents[0], human.CurrentOwnerEmployeeId);
        Assert.Null(ai.CurrentOwnerEmployeeId);   // active AI preserved
        Assert.Contains(log.Lines, l => l.Message.Contains("ASSIGNED ticket") && l.Message.Contains(human.TicketNumber));
        Assert.Contains(log.Lines, l => l.Message.Contains("assigned 1") && l.Message.Contains("ActiveAiConversation=1"));
    }

    [Fact]
    public async Task MaxAssignmentsPerRun_IsHonoured_TheRestWaitForTheNextRun()
    {
        var env = new Env();
        env.AddCallCenterAgents(2);
        var tickets = new List<Ticket>();
        for (var i = 0; i < 5; i++) tickets.Add(await env.AddQueuedTicketAsync());
        var (job, log, _) = Build(env, new CallCenterReconciliationOptions { Enabled = true, DryRun = false, MaxAssignmentsPerRun = 2 });

        await job.RunAsync(CancellationToken.None);

        Assert.Equal(2, tickets.Count(t => t.CurrentOwnerEmployeeId is not null));
        Assert.Contains(log.Lines, l => l.Message.Contains("MaxAssignmentsPerRun reached True"));

        // The real evidence query lists only unowned tickets; mirror that for the stub.
        foreach (var t in tickets) env.Evidence.Rows[t.TicketId] = env.Evidence.Rows[t.TicketId] with { OwnerEmployeeId = t.CurrentOwnerEmployeeId };
        await job.RunAsync(CancellationToken.None);
        Assert.Equal(4, tickets.Count(t => t.CurrentOwnerEmployeeId is not null));   // owned ones are never re-dealt; two more are
    }

    [Fact]
    public async Task OverlappingRun_IsSkipped_AndTouchesNothing()
    {
        var env = new Env();
        env.AddCallCenterAgents(1);
        var ticket = await env.AddQueuedTicketAsync();
        var (job, log, runLock) = Build(env, new CallCenterReconciliationOptions { Enabled = true, DryRun = false });

        await using (var held = await runLock.TryAcquireAsync())
        {
            Assert.NotNull(held);
            Assert.Null(await runLock.TryAcquireAsync());   // a second acquirer is refused, never queued
            await job.RunAsync(CancellationToken.None);
            Assert.Null(ticket.CurrentOwnerEmployeeId);
            Assert.Contains(log.Lines, l => l.Level == LogLevel.Warning && l.Message.Contains("another run is in progress"));
        }

        await job.RunAsync(CancellationToken.None);   // lock released: the next run proceeds
        Assert.NotNull(ticket.CurrentOwnerEmployeeId);
    }

    [Fact]
    public async Task ConcurrentRuns_NeverDoubleAssign()
    {
        var env = new Env();
        env.AddCallCenterAgents(3);
        var tickets = new List<Ticket>();
        for (var i = 0; i < 6; i++) tickets.Add(await env.AddQueuedTicketAsync());
        var (job, _, _) = Build(env, new CallCenterReconciliationOptions { Enabled = true, DryRun = false });

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => job.RunAsync(CancellationToken.None))));

        Assert.All(tickets, t => Assert.NotNull(t.CurrentOwnerEmployeeId));
        Assert.Equal(6, env.Assignments.Added.Count(a => a.IsCurrent));   // exactly one current assignment per ticket
    }

    [Fact]
    public async Task NoEligibleAgent_AndFailures_AreLoggedAsWarningsAndErrors()
    {
        var env = new Env();   // no agents at all
        var ticket = await env.AddQueuedTicketAsync();
        var (job, log, _) = Build(env, new CallCenterReconciliationOptions { Enabled = true, DryRun = false });

        await job.RunAsync(CancellationToken.None);
        Assert.Contains(log.Lines, l => l.Level == LogLevel.Warning && l.Message.Contains("no eligible Call Center CS Agent") && l.Message.Contains(ticket.TicketNumber));

        var failing = new Env();
        failing.AddCallCenterAgents(1);
        var t2 = await failing.AddQueuedTicketAsync();
        failing.UnitOfWork.ThrowConcurrencyConflictOnCall = 1;   // an unexpected (non lost-race) exception
        var (job2, log2, _) = Build(failing, new CallCenterReconciliationOptions { Enabled = true, DryRun = false });
        await job2.RunAsync(CancellationToken.None);
        Assert.Contains(log2.Lines, l => l.Level == LogLevel.Error && l.Message.Contains("FAILED") && l.Message.Contains(t2.TicketNumber));
        Assert.Contains(log2.Lines, l => l.Message.Contains("failed 1"));
    }
}
