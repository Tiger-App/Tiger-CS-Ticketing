using Hangfire;
using Hangfire.SqlServer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Review;
using TigerCS.Application.Modules.GenesysIntegration.Services;
using TigerCS.Application.Modules.SlaAndEscalation.Abstractions;
using TigerCS.Application.Modules.Ticketing.Services;

namespace TigerCS.Infrastructure.BackgroundJobs;

/// <summary>
/// Registers the SLA background-job mechanism of ADR-0015 — Hangfire, backed
/// by the same SQL Server database (ADR-0003).
///
/// <para>
/// <b>Breach detection never depends on a connected client.</b> Both paths
/// are server-side background work against stored due timestamps: neither an
/// open browser nor a SignalR connection participates, and ADR-0016 confines
/// SignalR to state-change notification specifically so that it is not
/// load-bearing here.
/// </para>
/// </summary>
public static class BackgroundJobServiceCollectionExtensions
{
    public static IServiceCollection AddTigerCsBackgroundJobs(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<BackgroundJobOptions>(configuration.GetSection(BackgroundJobOptions.SectionName));

        var options = configuration.GetSection(BackgroundJobOptions.SectionName).Get<BackgroundJobOptions>()
            ?? new BackgroundJobOptions();

        services.Configure<OutboxDispatchOptions>(configuration.GetSection(OutboxDispatchOptions.SectionName));
        // Call Center unassigned-work reconciliation: its own hosted service, deliberately NOT part of Hangfire, so it runs (or not) regardless of
        // BackgroundJobs:Enabled. Off and dry-run by default.
        services.Configure<CallCenterReconciliationOptions>(configuration.GetSection(CallCenterReconciliationOptions.SectionName));
        services.AddSingleton<TigerCS.Application.Modules.Ticketing.Abstractions.ICallCenterReconciliationRunLock, CallCenterReconciliationRunLock>();
        services.AddScoped<CallCenterUnassignedWorkJob>();
        // One-time CS Agent backfill: manual only (System Administrator endpoint), never scheduled; live runs need CsAgentBackfill:Enabled (default false).
        services.Configure<CsAgentBackfillOptions>(configuration.GetSection(CsAgentBackfillOptions.SectionName));
        // Manual, admin-triggered only; a live run needs Enabled=true (default false). No hosted service runs it.
        services.Configure<DefaultClassificationBackfillOptions>(configuration.GetSection(DefaultClassificationBackfillOptions.SectionName));
        services.AddSingleton<CsAgentBackfillRunner>();
        services.AddHostedService<CallCenterUnassignedWorkHostedService>();
        // Recurring stale-PHONE assignment (approved rule: > 6h without activity, no end event needed): its own section, switch and interval. Off and dry-run by default.
        services.Configure<StalePhoneAssignmentOptions>(configuration.GetSection(StalePhoneAssignmentOptions.SectionName));
        services.AddScoped<StalePhoneAssignmentJob>();
        services.AddHostedService<StalePhoneAssignmentHostedService>();

        // Tells the Collections pages whether the scheduled snapshot refresh really exists in this process (informational; registered whether or not Hangfire runs).
        services.AddSingleton<TigerCS.Application.Modules.Collections.Abstractions.ISnapshotSchedule>(sp =>
            new SnapshotSchedule(options, sp.GetRequiredService<TigerCS.Application.Modules.Collections.ReceivablesSnapshotOptions>()));

        // Registered whether or not Hangfire runs, so a fired job resolves
        // the same way in every environment.
        services.AddScoped<SlaDeadlineCheckJob>();
        services.AddScoped<SlaSweepJob>();
        services.AddScoped<OutboxDispatchJob>();
        services.AddScoped<CollectionsReminderScheduleJob>();
        services.AddScoped<CollectionsReceivablesRefreshJob>();
        services.AddScoped<CollectionsReceivablesCompanyRetryJob>();
        services.AddScoped<CollectionsCrmOwnersRefreshJob>();
        services.AddScoped<CollectionsReviewRefreshJob>();
        services.AddScoped<CollectionsDispatchJob>();
        services.AddScoped<CollectionsSuppressionJob>();
        services.AddScoped<CollectionsAutoDispatchJob>();
        // Holds only the scope factory; it opens one scope per candidate itself.
        services.AddScoped<ChatbotInactivityCloseJob>();
        services.AddScoped<AiOnlyAutoCloseJob>();

        if (!options.Enabled)
        {
            // See NoOpSlaDeadlineScheduler's remarks: detection logic is
            // unaffected, only its timing.
            services.AddSingleton<ISlaDeadlineScheduler, NoOpSlaDeadlineScheduler>();
            // A person can still refresh the approval data (one bounded in-process worker); dispatching and the suppression sweep stay off with the jobs.
            services.AddSingleton<IReviewJobScheduler, InProcessReviewJobScheduler>();
            return services;
        }

        var connectionString = configuration.GetConnectionString("TigerCsDatabase")
            ?? throw new InvalidOperationException(
                "Connection string 'TigerCsDatabase' is required when BackgroundJobs:Enabled is true — Hangfire shares the "
                + "application database (ADR-0003/ADR-0015). See docs/DEV-SETUP.md.");

        services.AddHangfire(config => config
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UseSqlServerStorage(connectionString, new SqlServerStorageOptions
            {
                // Hangfire's own tables live in their own schema so they are
                // never confused with, or migrated alongside, the
                // application schema EF Core owns.
                SchemaName = "HangfireSla",
                PrepareSchemaIfNecessary = true,
                QueuePollInterval = TimeSpan.FromSeconds(15),
                DisableGlobalLocks = true
            }));

        services.AddHangfireServer();
        services.AddScoped<TigerCS.Application.Modules.Collections.Abstractions.IReceivablesRangeLoader, HangfireReceivablesRangeLoader>();
        services.AddSingleton<ISlaDeadlineScheduler, HangfireSlaDeadlineScheduler>();
        services.AddSingleton<IReviewJobScheduler, HangfireReviewJobScheduler>();

        return services;
    }

    /// <summary>
    /// Registers ADR-0013/ADR-0015's recurring Outbox dispatcher — the job
    /// that turns committed Outbox rows into outbound effects. Called after
    /// the host is built, for the same reason as the sweep below.
    ///
    /// <para>
    /// <b>Not registering it does not lose messages.</b> With
    /// <c>BackgroundJobs:Enabled</c> false (the test host, which has no SQL
    /// Server for Hangfire's own schema) Outbox rows still commit with their
    /// business transactions and stay <c>Pending</c> until something
    /// dispatches them — the switch governs when delivery happens, never
    /// whether the intent to deliver was recorded.
    /// </para>
    /// </summary>
    public static void UseTigerCsRecurringOutboxDispatch(
        this IServiceProvider services, BackgroundJobOptions backgroundJobOptions, OutboxDispatchOptions outboxOptions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(backgroundJobOptions);
        ArgumentNullException.ThrowIfNull(outboxOptions);

        if (!backgroundJobOptions.Enabled)
        {
            return;
        }

        // Clamped rather than trusted: a mistyped interval would silently
        // delay every customer-facing acknowledgement in the system, and an
        // interval of zero or negative would be rejected by Hangfire's cron
        // parser at startup rather than at a useful moment.
        var minutes = Math.Clamp(outboxOptions.PollIntervalMinutes, 1, 15);

        services.GetRequiredService<IRecurringJobManager>().AddOrUpdate<OutboxDispatchJob>(
            OutboxDispatchJob.RecurringJobId,
            job => job.RunAsync(CancellationToken.None),
            $"*/{minutes} * * * *");
    }

    /// <summary>
    /// Registers the recurring chatbot-inactivity closure. Runs every minute
    /// (Hangfire's finest recurring granularity), so a ticket is closed within
    /// about a minute of its customer-silence deadline passing. It reads the
    /// persisted timers, so a restart or deploy loses nothing: the next run
    /// closes whatever became due meanwhile. A no-op when
    /// <c>BackgroundJobs:Enabled</c> is false — the timers are still recorded
    /// and are honoured as soon as the job runs.
    /// </summary>
    public static void UseTigerCsRecurringChatbotInactivityClose(this IServiceProvider services, BackgroundJobOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        if (!options.Enabled)
        {
            return;
        }

        services.GetRequiredService<IRecurringJobManager>().AddOrUpdate<ChatbotInactivityCloseJob>(
            ChatbotInactivityCloseJob.RecurringJobId,
            job => job.RunAsync(CancellationToken.None),
            "* * * * *");
    }

    /// <summary>
    /// Registers the recurring AI-only closure (every 15 minutes) <b>only</b> when <c>AiOnlyAutoClose:Enabled</c> is true, and removes any existing
    /// registration otherwise — so the rule is never active in an environment until someone deliberately turns it on. A no-op when
    /// <c>BackgroundJobs:Enabled</c> is false.
    /// </summary>
    public static void UseTigerCsRecurringAiOnlyAutoClose(
        this IServiceProvider services, BackgroundJobOptions backgroundJobOptions, AiOnlyAutoCloseOptions aiOnlyOptions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(backgroundJobOptions);
        ArgumentNullException.ThrowIfNull(aiOnlyOptions);

        if (!backgroundJobOptions.Enabled)
        {
            return;
        }

        var manager = services.GetRequiredService<IRecurringJobManager>();
        if (!aiOnlyOptions.Enabled)
        {
            manager.RemoveIfExists(AiOnlyAutoCloseJob.RecurringJobId);
            return;
        }

        manager.AddOrUpdate<AiOnlyAutoCloseJob>(
            AiOnlyAutoCloseJob.RecurringJobId,
            job => job.RunAsync(CancellationToken.None),
            AiOnlyAutoCloseJob.Cron);
    }

    /// <summary>
    /// Registers SLA-Architecture.md §14's recurring safety sweep. Called
    /// after the host is built, because <see cref="IRecurringJobManager"/>
    /// needs a live storage connection.
    /// </summary>
    public static void UseTigerCsRecurringSlaSweep(this IServiceProvider services, BackgroundJobOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        if (!options.Enabled)
        {
            return;
        }

        // ADR-0015 / §14: "every 1–5 minutes". Clamped rather than trusted,
        // since a misconfigured interval would silently weaken the only
        // backstop behind the scheduled jobs.
        var minutes = Math.Clamp(options.SweepIntervalMinutes, 1, 5);

        services.GetRequiredService<IRecurringJobManager>().AddOrUpdate<SlaSweepJob>(
            SlaSweepJob.RecurringJobId,
            job => job.RunAsync(CancellationToken.None),
            $"*/{minutes} * * * *");
    }

    /// <summary>
    /// Registers the Collections reminder schedule only while TigerCS is the
    /// designated scheduler and the rules are confirmed, and removes it otherwise — so switching the
    /// setting off stops an already-registered schedule at the next start.
    /// </summary>
    public static void UseTigerCsRecurringCollectionsReminders(
        this IServiceProvider services, BackgroundJobOptions backgroundJobOptions, CollectionsOptions collectionsOptions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(backgroundJobOptions);
        ArgumentNullException.ThrowIfNull(collectionsOptions);

        if (!backgroundJobOptions.Enabled)
        {
            return;
        }

        var manager = services.GetRequiredService<IRecurringJobManager>();
        if (!collectionsOptions.IsTigerCsSchedulerActive)
        {
            manager.RemoveIfExists(CollectionsReminderScheduleJob.RecurringJobId);
            return;
        }

        manager.AddOrUpdate<CollectionsReminderScheduleJob>(
            CollectionsReminderScheduleJob.RecurringJobId,
            job => job.RunAsync(CancellationToken.None),
            collectionsOptions.ScheduleCron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.FindSystemTimeZoneById(collectionsOptions.TimeZoneId) });
    }

    /// <summary>
    /// Registers the recurring PACT receivables snapshot refresh: ONE Hangfire job per company (Dubai 4, Sharjah 32; SQL Server Agent is not used), evaluated in
    /// <see cref="ReceivablesSnapshotOptions.RefreshTimeZoneId"/> (default <c>0 */2 * * *</c> in Asia/Dubai = every two hours). The single recurring job of the previous pipeline is
    /// always removed. With <c>RefreshOnStartup</c> one run per company is triggered immediately so a fresh deployment does not wait for the first cron tick. A no-op when
    /// <c>BackgroundJobs:Enabled</c> is false: the snapshot then never refreshes and the pages show it as stale / not loaded (see docs/Collections/Receivables-Snapshot.md).
    /// </summary>
    public static void UseTigerCsRecurringCollectionsReceivablesRefresh(
        this IServiceProvider services, BackgroundJobOptions backgroundJobOptions, ReceivablesSnapshotOptions snapshotOptions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(backgroundJobOptions);
        ArgumentNullException.ThrowIfNull(snapshotOptions);

        if (!backgroundJobOptions.Enabled)
        {
            return;
        }

        var manager = services.GetRequiredService<IRecurringJobManager>();
        manager.RemoveIfExists(CollectionsReceivablesRefreshJob.LegacyRecurringJobId);   // the previous pipeline's schedule must not run any more
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(string.IsNullOrWhiteSpace(snapshotOptions.RefreshTimeZoneId) ? "Asia/Dubai" : snapshotOptions.RefreshTimeZoneId);
        var cron = string.IsNullOrWhiteSpace(snapshotOptions.RefreshCron) ? "0 */2 * * *" : snapshotOptions.RefreshCron;
        foreach (var company in new[] { 4, 32 })
        {
            var id = CollectionsReceivablesRefreshJob.RecurringJobId(company);
            if (!snapshotOptions.RefreshEnabled)
            {
                manager.RemoveIfExists(id);
                continue;
            }
            var companyId = company;
            manager.AddOrUpdate<CollectionsReceivablesRefreshJob>(id, job => job.RunAsync(companyId, CancellationToken.None), cron, new RecurringJobOptions { TimeZone = timeZone });
            if (snapshotOptions.RefreshOnStartup)
            {
                manager.Trigger(id);
            }
        }
    }

    /// <summary>Registers the recurring reload of the CRM owner feed while <c>
    /// </c> is true; removes it otherwise.</summary>
    public static void UseTigerCsRecurringCollectionsCrmOwnersRefresh(
        this IServiceProvider services, BackgroundJobOptions backgroundJobOptions, TigerCS.Application.Modules.Collections.CollectionsCrmOwnersOptions crmOptions)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (!backgroundJobOptions.Enabled) return;
        var manager = services.GetRequiredService<IRecurringJobManager>();
        if (!crmOptions.Enabled) { manager.RemoveIfExists(CollectionsCrmOwnersRefreshJob.RecurringJobId); return; }
        manager.AddOrUpdate<CollectionsCrmOwnersRefreshJob>(CollectionsCrmOwnersRefreshJob.RecurringJobId, job => job.RunAsync(CancellationToken.None),
            string.IsNullOrWhiteSpace(crmOptions.RefreshCron) ? "15 * * * *" : crmOptions.RefreshCron);
        if (crmOptions.RefreshOnStartup) manager.Trigger(CollectionsCrmOwnersRefreshJob.RecurringJobId);
    }

    /// <summary>
    /// Registers the paid-after-upload suppression sweep only while Genesys upload and its suppression are both enabled, and removes it otherwise.
    /// </summary>
    public static void UseTigerCsRecurringGenesysSuppression(
        this IServiceProvider services, BackgroundJobOptions backgroundJobOptions, GenesysOutboundOptions genesysOptions, string timeZoneId)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (!backgroundJobOptions.Enabled) return;

        var manager = services.GetRequiredService<IRecurringJobManager>();
        if (!genesysOptions.Enabled || !genesysOptions.SuppressionEnabled)
        {
            manager.RemoveIfExists(CollectionsSuppressionJob.RecurringJobId);
            return;
        }
        manager.AddOrUpdate<CollectionsSuppressionJob>(CollectionsSuppressionJob.RecurringJobId, job => job.RunAsync(CancellationToken.None),
            genesysOptions.SuppressionSweepCron, new RecurringJobOptions { TimeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId) });
    }

    /// <summary>
    /// Registers the Auto dispatcher only while <c>Collections:Campaigns:AutoSendingEnabled</c> AND the Genesys upload are both enabled, and removes it otherwise. With the default configuration
    /// (AutoSendingEnabled false) no automatic dispatch job exists at all.
    /// </summary>
    public static void UseTigerCsRecurringAutoDispatch(
        this IServiceProvider services, BackgroundJobOptions backgroundJobOptions, TigerCS.Application.Modules.Collections.CollectionsCampaignOptions campaignOptions,
        GenesysOutboundOptions genesysOptions, string timeZoneId)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (!backgroundJobOptions.Enabled) return;

        var manager = services.GetRequiredService<IRecurringJobManager>();
        if (!campaignOptions.AutoSendingEnabled || !genesysOptions.Enabled)
        {
            manager.RemoveIfExists(CollectionsAutoDispatchJob.RecurringJobId);
            return;
        }
        manager.AddOrUpdate<CollectionsAutoDispatchJob>(CollectionsAutoDispatchJob.RecurringJobId, job => job.RunAsync(CancellationToken.None),
            campaignOptions.AutoDispatchCron, new RecurringJobOptions { TimeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId) });
    }
}
