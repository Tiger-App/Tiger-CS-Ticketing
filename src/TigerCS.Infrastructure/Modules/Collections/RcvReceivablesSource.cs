using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.Collections;

namespace TigerCS.Infrastructure.Modules.Collections;

/// <summary>
/// Reads receivables from the LOCAL snapshot (<c>dbo.CollectionsRcvLine</c>, procedures <c>dbo.usp_CollectionsRcv_*</c>). It never calls PACT, so a page view is independent of PACT latency.
/// Missing data is a status, never an empty list: a company that was never loaded reports <c>HasSnapshot = false</c>; a stale or failed one is served with its status so the caller can
/// warn and the export gate can refuse. The snapshot always covers the whole window 2000-2099 (<see cref="ReceivablesSnapshotOptions.SourceFromDate"/>..), so there are no coverage gaps.
/// </summary>
public sealed class RcvReceivablesSource(
    IConfiguration configuration, ReceivablesSnapshotOptions options, PactReceivablesOptions pactOptions,
    TimeProvider timeProvider, ILogger<RcvReceivablesSource> logger, ISnapshotSchedule? schedule = null) : IPactReceivablesSource, IPactInstalmentUnitSource, IUnitReceivablesSource, ISnapshotStatusSource
{
    public Task<PactReceivablesSnapshot> ReadAsync(DateOnly throughDate, CancellationToken cancellationToken) =>
        ReadAsync(new PactReceivablesRequest(null, throughDate), cancellationToken);

    /// <summary>
    /// Payment rows of a due-date window [FromDate, ThroughDate] (Campaigns, Review, the legacy customers API): <c>dbo.usp_CollectionsRcv_GetLines</c>. It is a plain window read: it applies NO
    /// Due / Overdue classification of its own (<see cref="PactReceivablesRequest.Class"/> and <see cref="PactReceivablesRequest.AsOfDate"/> are ignored). Every caller classifies the rows itself -
    /// Campaigns by the Dubai DAY (Due = today, Overdue = before today); the legacy <c>/api/collections/receivables</c> list keeps its documented EDSM month rule inside its own service.
    /// </summary>
    public async Task<PactReceivablesSnapshot> ReadAsync(PactReceivablesRequest request, CancellationToken cancellationToken)
    {
        if (pactOptions.ApplyLegacyExclusions)
            throw new PactReceivablesSourceException("The legacy exclusions are not supported by the local receivables snapshot; disable them.");
        if (request.CompanyId is not (null or 4 or 32))
            throw new PactReceivablesSourceException("The PACT report company is not supported.");
        var from = request.FromDate ?? DateOnly.FromDateTime(pactOptions.StartDate);
        if (from > request.ThroughDate)
            throw new PactReceivablesSourceException("The receivables start date is after its end date.");

        var companies = new List<SnapshotCompanyRaw>();
        var rows = new List<PactReceivableInstalment>();
        var limit = Math.Clamp(pactOptions.MaxSourceRows, 1, 1_000_000);
        // dbo.CollectionsTowers (the tower picker and filter), read before the lines so that each row can carry its tower's local id: the procedure returns the tower NUMBER and name only, while
        // the review records (the Manual list) filter by TowerId. Without the id a Manual tower filter matched nobody, although the preview, which filters inside the procedure, listed the tower.
        var towers = new Dictionary<(int Company, string Number), int>();
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            towers = await ReadTowerIdsAsync(connection, cancellationToken);
            await using var command = Procedure(connection, "dbo.usp_CollectionsRcv_GetLines");
            command.Parameters.Add("@CompanyId", SqlDbType.Int).Value = (object?)request.CompanyId ?? DBNull.Value;
            command.Parameters.Add("@TowerId", SqlDbType.Int).Value = (object?)request.TowerId ?? DBNull.Value;
            command.Parameters.Add("@FromDate", SqlDbType.Date).Value = from.ToDateTime(TimeOnly.MinValue);
            command.Parameters.Add("@ToDate", SqlDbType.Date).Value = request.ThroughDate.ToDateTime(TimeOnly.MinValue);
            command.Parameters.Add(new SqlParameter("@MinAmount", SqlDbType.Decimal) { Precision = 19, Scale = 4, Value = request.MinAmount });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await ReadScopeAsync(reader, cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) companies.Add(ReadCompany(reader));
            await reader.NextResultAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var due = DateOnly.FromDateTime(reader.GetDateTime(reader.GetOrdinal("DueDate")));
                rows.Add(new PactReceivableInstalment(
                    reader.GetInt32(reader.GetOrdinal("CompanyId")), reader.GetString(reader.GetOrdinal("TenantId")),
                    reader.GetString(reader.GetOrdinal("FullName")), reader.GetString(reader.GetOrdinal("Mobile")), reader.GetString(reader.GetOrdinal("Email")),
                    (int)reader.GetInt64(reader.GetOrdinal("UnitId")), reader.GetString(reader.GetOrdinal("UnitCode")), reader.GetString(reader.GetOrdinal("ProjectCode")),
                    reader.GetString(reader.GetOrdinal("VoucherNumber")), reader.GetString(reader.GetOrdinal("ChequeNumber")),
                    due.ToDateTime(TimeOnly.MinValue), reader.GetDecimal(reader.GetOrdinal("Amount")), Text(reader, "SourceStatus") ?? "",
                    Text(reader, "TowerNumber"), null, Text(reader, "TowerName")));
                if (rows.Count > limit)
                    throw new PactReceivablesSourceException("The receivables snapshot exceeded the configured row limit; no partial list was returned.");
            }
        }
        catch (SqlException ex)
        {
            logger.LogWarning("Receivables snapshot read failed (SQL error {SqlNumber}).", ex.Number);
            throw new PactReceivablesSourceException("The local receivables snapshot could not be read.");
        }

        var snapshot = ReceivablesSnapshotComposer.Compose(from, request.ThroughDate, companies, [], ResolveTowerIds(rows, towers), timeProvider.GetUtcNow().UtcDateTime, options.MaxAgeMinutes, MaxRunMinutes);
        snapshot = snapshot with { Snapshot = WithSchedule(snapshot.Snapshot!) };
        logger.LogInformation("Receivables snapshot read: {Rows} rows, {Loaded}/{Scope} companies loaded, oldest success {ReadAt:O}, window {From:yyyy-MM-dd}..{To:yyyy-MM-dd}.",
            rows.Count, snapshot.Snapshot!.Companies.Count(c => c.HasSnapshot), snapshot.Snapshot.Companies.Count, snapshot.ReadAtUtc, from, request.ThroughDate);
        return snapshot;
    }

    /// <summary>
    /// Receivables page: one round trip to <c>dbo.usp_CollectionsRcv_GetUnits</c>. Payments are filtered (window, Due / Overdue), summed per apartment, filtered by Minimum Total, counted,
    /// ordered (latest due date DESC) and paged in SQL; the payments of exactly the listed apartments come back with them.
    /// <see cref="PactInstalmentsRequest.AsOf"/> is TODAY (Dubai); <see cref="PactInstalmentsRequest.MinTotal"/> is the apartment-level minimum (strictly greater).
    /// </summary>
    public async Task<PactInstalmentUnitsPage> ReadInstalmentUnitsAsync(PactInstalmentsRequest request, CancellationToken cancellationToken)
    {
        if (request.CompanyId is not (null or 4 or 32))
            throw new PactReceivablesSourceException("The PACT report company is not supported.");
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var companies = new List<SnapshotCompanyRaw>();
        var heads = new List<(int Company, string Tenant, long UnitId, string UnitCode, string Name, string? Tower, string? TowerName, int Count, decimal Total, DateOnly Latest,
            int? CrmCustomers, int? CrmCustomerId, string? CrmName, string? CrmPhone, string? CrmEmail, bool CrmCompanyAmbiguous)>();
        var byUnit = new Dictionary<(int, string, long, string), List<PactInstalmentRowDto>>();
        var totals = new PactInstalmentTotalsDto(0, 0, 0, 0, 0, 0, 0, 0, 0);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = Procedure(connection, "dbo.usp_CollectionsRcv_GetUnits");
            command.Parameters.Add("@Today", SqlDbType.Date).Value = request.AsOf.ToDateTime(TimeOnly.MinValue);
            command.Parameters.Add("@CompanyId", SqlDbType.Int).Value = (object?)request.CompanyId ?? DBNull.Value;
            command.Parameters.Add("@TowerId", SqlDbType.Int).Value = (object?)request.TowerId ?? DBNull.Value;
            // Several towers travel as ONE comma-separated parameter (OR in the procedure, before aggregation and paging). Only sent when used, so one tower or all towers never depend on it.
            if (request.TowerIds is { Count: > 0 } towerIds)
                command.Parameters.Add("@TowerIds", SqlDbType.NVarChar, -1).Value = string.Join(',', towerIds.Select(id => id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            command.Parameters.Add("@FromDate", SqlDbType.Date).Value = request.From.ToDateTime(TimeOnly.MinValue);
            command.Parameters.Add("@ToDate", SqlDbType.Date).Value = request.To.ToDateTime(TimeOnly.MinValue);
            command.Parameters.Add("@Status", SqlDbType.VarChar, 10).Value = request.Status ?? "all";
            command.Parameters.Add("@Search", SqlDbType.NVarChar, 200).Value = (object?)request.Search ?? DBNull.Value;
            command.Parameters.Add("@PhoneDigits", SqlDbType.NVarChar, 200).Value = (object?)request.PhoneDigits ?? DBNull.Value;
            command.Parameters.Add(new SqlParameter("@MinTotal", SqlDbType.Decimal) { Precision = 19, Scale = 4, Value = (object?)request.MinTotal ?? DBNull.Value });
            command.Parameters.Add("@PageNumber", SqlDbType.Int).Value = request.Page;
            command.Parameters.Add("@PageSize", SqlDbType.Int).Value = request.PageSize;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            await ReadScopeAsync(reader, cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) companies.Add(ReadCompany(reader));
            await reader.NextResultAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
                totals = new PactInstalmentTotalsDto(reader.GetInt32(reader.GetOrdinal("LineCount")), reader.GetDecimal(reader.GetOrdinal("Total")),
                    reader.GetInt32(reader.GetOrdinal("OverdueLineCount")), reader.GetDecimal(reader.GetOrdinal("OverdueTotal")),
                    reader.GetInt32(reader.GetOrdinal("DueLineCount")), reader.GetDecimal(reader.GetOrdinal("DueTotal")), 0, 0m, 0, reader.GetInt32(reader.GetOrdinal("UnitCount")));
            await reader.NextResultAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                heads.Add((reader.GetInt32(reader.GetOrdinal("CompanyId")), reader.GetString(reader.GetOrdinal("TenantId")), reader.GetInt64(reader.GetOrdinal("UnitId")),
                    reader.GetString(reader.GetOrdinal("UnitCode")), reader.GetString(reader.GetOrdinal("FullName")), Text(reader, "TowerNumber"), Text(reader, "TowerName"),
                    reader.GetInt32(reader.GetOrdinal("InstalmentCount")), reader.GetDecimal(reader.GetOrdinal("Total")),
                    DateOnly.FromDateTime(reader.GetDateTime(reader.GetOrdinal("LatestDueDate"))),
                    NullableInt(reader, "CrmCustomers"), NullableInt(reader, "CrmCustomerId"), Text(reader, "CrmName"), Text(reader, "CrmPhone"), Text(reader, "CrmEmail"),
                    NullableInt(reader, "CrmCompanyAmbiguous") == 1));
            await reader.NextResultAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var company = reader.GetInt32(reader.GetOrdinal("CompanyId"));
                var key = (company, reader.GetString(reader.GetOrdinal("TenantId")), reader.GetInt64(reader.GetOrdinal("UnitId")), reader.GetString(reader.GetOrdinal("UnitCode")));
                if (!byUnit.TryGetValue(key, out var list)) byUnit[key] = list = [];
                // PACT returns only the remaining amount: the original and paid amounts are unknown, never estimated. The payment status follows the remaining amount (0 = paid, more = unpaid,
                // contradicted or invalid = Unknown): the same rule as the campaign records and the customers API.
                var remaining = reader.GetDecimal(reader.GetOrdinal("RemainingAmount"));
                var sourceStatus = Text(reader, "SourceStatus") ?? "";
                list.Add(new PactInstalmentRowDto(company, ReceivablesSnapshotStatusBuilder.CompanyName(company), null, null, (int)key.Item3, key.Item4, key.Item2, "",
                    reader.GetString(reader.GetOrdinal("Mobile")), reader.GetString(reader.GetOrdinal("Email")),
                    reader.GetString(reader.GetOrdinal("VoucherNumber")), reader.GetString(reader.GetOrdinal("ChequeNumber")),
                    DateOnly.FromDateTime(reader.GetDateTime(reader.GetOrdinal("DueDate"))), null, null, remaining,
                    CollectionsPaymentFilters.Classify(remaining, null, null, sourceStatus, false), reader.GetString(reader.GetOrdinal("Classification")), sourceStatus));
            }
        }
        catch (SqlException ex)
        {
            logger.LogWarning("Unit page read failed (SQL error {SqlNumber}).", ex.Number);
            throw new PactReceivablesSourceException("The local receivables snapshot could not be read.");
        }
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var status = WithSchedule(ReceivablesSnapshotComposer.BuildStatus(request.From, request.To, companies, [], now, options.MaxAgeMinutes, MaxRunMinutes));
        var units = heads.Select(h => new PactInstalmentUnitDto(h.Company, ReceivablesSnapshotStatusBuilder.CompanyName(h.Company), h.Tower, h.TowerName, (int)h.UnitId, h.UnitCode,
            h.Tenant, h.Name, h.Count, h.Total, h.Latest,
            byUnit.TryGetValue((h.Company, h.Tenant, h.UnitId, h.UnitCode), out var rows) ? rows.Select(r => r with { TowerNumber = h.Tower, TowerName = h.TowerName, CustomerName = h.Name }).ToList() : [],
            h.CrmCustomers, h.CrmCustomerId, h.CrmName, h.CrmPhone, h.CrmEmail, h.CrmCompanyAmbiguous)).ToList();
        var sqlMs = timer.Elapsed.TotalMilliseconds;
        logger.LogInformation("Unit page read: page {Page}, {Units} units / {Total} payments in {SqlMs:F0} ms (window {From:yyyy-MM-dd}..{To:yyyy-MM-dd}, status {Status}, min total {Min}).",
            request.Page, totals.UnitCount, totals.Count, sqlMs, request.From, request.To, request.Status ?? "all", request.MinTotal);
        return new PactInstalmentUnitsPage(totals, units, false, status, ReceivablesSnapshotComposer.ReadAt(status), sqlMs);
    }

    /// <summary>
    /// One unit by its normalised key (tower + unit code), read with ONE statement: the PACT accounts that hold it (a missing PACT mobile changes nothing), the unit's own unpaid
    /// instalments (future ones too; the application classifies them by date), and its CRM link. Nothing is looked up by phone number or name.
    /// </summary>
    public async Task<PactUnitReceivables> ReadUnitAsync(string unitKey, int? companyId, DateOnly today, CancellationToken cancellationToken)
    {
        var companies = new List<SnapshotCompanyRaw>();
        var identities = new List<PactUnitIdentity>();
        var instalments = new List<PactUnitInstalment>();
        var links = new Dictionary<int, CrmUnitLink>();
        var crmLoaded = false;
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using (var command = Procedure(connection, "dbo.usp_CollectionsRcv_GetUnit"))
            {
                command.Parameters.Add("@UnitKey", SqlDbType.NVarChar, 220).Value = unitKey;
                command.Parameters.Add("@CompanyId", SqlDbType.Int).Value = (object?)companyId ?? DBNull.Value;
                command.Parameters.Add("@AsOfDate", SqlDbType.Date).Value = today.ToDateTime(TimeOnly.MinValue);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken)) companies.Add(ReadCompany(reader));
                await reader.NextResultAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                    identities.Add(new PactUnitIdentity(reader.GetInt32(reader.GetOrdinal("CompanyId")), reader.GetString(reader.GetOrdinal("TenantId")), (int)reader.GetInt64(reader.GetOrdinal("UnitId")),
                        reader.GetString(reader.GetOrdinal("UnitCode")), reader.GetString(reader.GetOrdinal("FullName")), reader.GetString(reader.GetOrdinal("Mobile")), reader.GetString(reader.GetOrdinal("Email")),
                        Text(reader, "TowerNumber"), Text(reader, "TowerName"), reader.GetInt32(reader.GetOrdinal("AllRows")), reader.GetInt32(reader.GetOrdinal("OpenRows"))));
                await reader.NextResultAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var remaining = reader.GetDecimal(reader.GetOrdinal("RemainingAmount"));
                    instalments.Add(new PactUnitInstalment(reader.GetInt32(reader.GetOrdinal("CompanyId")), reader.GetString(reader.GetOrdinal("TenantId")), (int)reader.GetInt64(reader.GetOrdinal("UnitId")),
                        reader.GetString(reader.GetOrdinal("VoucherNumber")), DateOnly.FromDateTime(reader.GetDateTime(reader.GetOrdinal("DueDate"))), remaining,
                        null, null, CollectionsPaymentFilters.Classify(remaining, null, null, Text(reader, "SourceStatus"), false)));
                }
                await reader.NextResultAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken)) links[reader.GetInt32(reader.GetOrdinal("CompanyId"))] = SqlCollectionsCrmOwnerStore.ReadLink(reader);
            }
            await using var state = Procedure(connection, "dbo.usp_Collections_GetCrmOwnerState");
            await using var stateReader = await state.ExecuteReaderAsync(cancellationToken);
            crmLoaded = await stateReader.ReadAsync(cancellationToken) && stateReader.GetBoolean(0);
        }
        catch (SqlException ex)
        {
            logger.LogWarning("Unit receivables read failed (SQL error {SqlNumber}).", ex.Number);
            throw new PactReceivablesSourceException("The local receivables snapshot could not be read.");
        }
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var status = WithSchedule(ReceivablesSnapshotComposer.BuildStatus(today, today, companies, [], now, options.MaxAgeMinutes, MaxRunMinutes));
        return new PactUnitReceivables(status, identities, instalments, links, crmLoaded, ReceivablesSnapshotComposer.ReadAt(status));
    }

    /// <summary>The unit/account directory entry (<c>usp_CollectionsRcv_GetUnitAccounts</c>): fully paid units included, other units of the same tenant account included. Complete only when the operator switched <c>AccountDirectoryComplete</c> on.</summary>
    public async Task<PactAccountDirectory> ReadAccountDirectoryAsync(string unitKey, int? companyId, CancellationToken cancellationToken)
    {
        var matches = new List<PactDirectoryUnit>();
        var account = new List<PactDirectoryUnit>();
        DateTime? asOf = null;
        static PactDirectoryUnit Read(SqlDataReader reader) => new(reader.GetInt32(reader.GetOrdinal("CompanyId")), reader.GetString(reader.GetOrdinal("TenantId")), (int)reader.GetInt64(reader.GetOrdinal("UnitId")),
            reader.GetString(reader.GetOrdinal("UnitCode")), reader.GetInt32(reader.GetOrdinal("OpenRows")), reader.GetInt32(reader.GetOrdinal("SettledRows")), reader.GetBoolean(reader.GetOrdinal("IsCancelled")));
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = Procedure(connection, "dbo.usp_CollectionsRcv_GetUnitAccounts");
            command.Parameters.Add("@UnitKey", SqlDbType.NVarChar, 220).Value = unitKey;
            command.Parameters.Add("@CompanyId", SqlDbType.Int).Value = (object?)companyId ?? DBNull.Value;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) matches.Add(Read(reader));
            await reader.NextResultAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) account.Add(Read(reader));
            await reader.NextResultAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) if (!reader.IsDBNull(1)) asOf = asOf is null || reader.GetDateTime(1) < asOf ? DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc) : asOf;
        }
        catch (SqlException ex)
        {
            logger.LogWarning("Unit/account directory read failed (SQL error {SqlNumber}).", ex.Number);
            throw new PactReceivablesSourceException("The PACT unit/account directory could not be read.");
        }
        return new PactAccountDirectory(matches, account, options.AccountDirectoryComplete, "PACT receivables procedure (p4/p32AccountReceivables), settled instalments included", asOf);
    }

    /// <summary>
    /// The local id of each tower by (company, trimmed tower number), from <c>dbo.CollectionsTowers</c>, the table the procedures filter towers with. A number that is listed more than once for
    /// one company resolves to no id: a tower is never guessed.
    /// </summary>
    private async Task<Dictionary<(int Company, string Number), int>> ReadTowerIdsAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        var found = new Dictionary<(int Company, string Number), int>();
        var ambiguous = new HashSet<(int Company, string Number)>();
        await using var command = new SqlCommand("SELECT TowerId, CompanyId, LTRIM(RTRIM(CONVERT(nvarchar(20), TowerNumber))) AS TowerNumber FROM dbo.CollectionsTowers", connection)
        { CommandTimeout = Math.Clamp(options.ReadCommandTimeoutSeconds, 1, 600) };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (reader.IsDBNull(2)) continue;
            var key = (reader.GetInt32(1), reader.GetString(2));
            if (key.Item2.Length == 0) continue;
            if (!found.TryAdd(key, reader.GetInt32(0))) ambiguous.Add(key);
        }
        foreach (var key in ambiguous) found.Remove(key);
        return found;
    }

    /// <summary>
    /// Each row with its tower's local id, resolved from the company and the tower NUMBER the procedure returns. A row whose tower is not in the table, or has no number, keeps a null id: the Manual
    /// tower filter then cannot match it, and the page says so instead of listing nobody silently. A row that already carries an id is left alone.
    /// </summary>
    public static List<PactReceivableInstalment> ResolveTowerIds(IReadOnlyList<PactReceivableInstalment> rows, IReadOnlyDictionary<(int Company, string Number), int> towers)
    {
        if (towers.Count == 0) return rows.ToList();
        return rows.Select(r => r.TowerId is null && r.TowerNumber is { } number && towers.TryGetValue((r.CompanyId, number.Trim()), out var id) ? r with { TowerId = id } : r).ToList();
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connectionString = configuration.GetConnectionString(options.ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new PactReceivablesSourceException("The receivables snapshot connection is not configured.");
        var connection = new SqlConnection(connectionString);
        try { await connection.OpenAsync(cancellationToken); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }

    private SqlCommand Procedure(SqlConnection connection, string name) =>
        new(name, connection) { CommandType = CommandType.StoredProcedure, CommandTimeout = Math.Clamp(options.ReadCommandTimeoutSeconds, 1, 600) };

    private static async Task ReadScopeAsync(SqlDataReader reader, CancellationToken cancellationToken)
    {
        if (!await reader.ReadAsync(cancellationToken) || !reader.GetBoolean(reader.GetOrdinal("ScopeValid")))
            throw new PactReceivablesScopeException("The selected tower is not available. Choose a tower from the list.");
        await reader.NextResultAsync(cancellationToken);
    }

    private SnapshotStatusDto WithSchedule(SnapshotStatusDto status) => schedule is null ? status : status with { ScheduledRefreshActive = schedule.ScheduledRefreshActive, RefreshSchedule = schedule.Describe() };

    /// <summary>The longest a refresh may legitimately run: its SQL command timeout plus five minutes. A "Running" attempt older than this is reported as Stuck.</summary>
    private int MaxRunMinutes => Math.Clamp(options.RefreshCommandTimeoutSeconds, 5, 7200) / 60 + 5;

    /// <summary>
    /// Per-company refresh state only (<c>usp_CollectionsRcv_GetStatus</c>): a few hundred bytes, no receivables. This is what the pages poll while a refresh runs, instead of re-reading
    /// and re-rendering the whole list. It closes orphaned attempts first (inside the procedure) but never starts a refresh.
    /// </summary>
    public async Task<SnapshotStatusDto> ReadStatusAsync(CancellationToken cancellationToken)
    {
        var companies = new List<SnapshotCompanyRaw>();
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = Procedure(connection, "dbo.usp_CollectionsRcv_GetStatus");
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) companies.Add(ReadCompany(reader));
        }
        catch (SqlException ex)
        {
            logger.LogWarning("Receivables snapshot status read failed (SQL error {SqlNumber}).", ex.Number);
            throw new PactReceivablesSourceException("The receivables snapshot status could not be read.");
        }
        var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime.AddHours(4));
        return WithSchedule(ReceivablesSnapshotComposer.BuildStatus(today, today, companies, [], timeProvider.GetUtcNow().UtcDateTime, options.MaxAgeMinutes, MaxRunMinutes));
    }

    /// <summary>One row of <c>usp_CollectionsRcv_GetStatus</c>. Coverage is the configured window (the whole window is always stored); nothing about paid instalments or breakdowns is claimed.</summary>
    private SnapshotCompanyRaw ReadCompany(SqlDataReader reader) => new(
        reader.GetInt32(reader.GetOrdinal("CompanyId")), reader.GetBoolean(reader.GetOrdinal("HasSnapshot")),
        Utc(reader, "LastSuccessUtc"), Utc(reader, "LastAttemptUtc"), Text(reader, "LastStatus"),
        Int(reader, "LastErrorNumber"), reader.GetInt32(reader.GetOrdinal("ConsecutiveFailures")),
        reader.GetInt32(reader.GetOrdinal("PublishedRows")),
        reader.GetBoolean(reader.GetOrdinal("HasSnapshot")) ? DateOnly.FromDateTime(options.SourceFromDate) : null,
        reader.GetBoolean(reader.GetOrdinal("HasSnapshot")) ? DateOnly.FromDateTime(options.SourceThroughDate) : null,
        0, 0m, 0, 0m, 0, 0,
        reader.GetBoolean(reader.GetOrdinal("IsLoading")), reader.GetBoolean(reader.GetOrdinal("IsLoading")) ? Utc(reader, "LastAttemptUtc") : null, Text(reader, "LastStatus"),
        false, false, 0,
        // Added by the extended R003: tolerated when an older procedure has not been redeployed yet.
        HasColumn(reader, "LastFinishedUtc") ? Utc(reader, "LastFinishedUtc") : null, HasColumn(reader, "LastError") ? Text(reader, "LastError") : null,
        HasColumn(reader, "LastFetchMs") ? Int(reader, "LastFetchMs") : null, HasColumn(reader, "LastRawRows") ? Int(reader, "LastRawRows") : null);

    private static bool HasColumn(SqlDataReader reader, string name)
    {
        for (var i = 0; i < reader.FieldCount; i++) if (string.Equals(reader.GetName(i), name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string? Text(SqlDataReader r, string name) { var i = r.GetOrdinal(name); return r.IsDBNull(i) ? null : Convert.ToString(r.GetValue(i), System.Globalization.CultureInfo.InvariantCulture); }
    private static int? Int(SqlDataReader r, string name) { var i = r.GetOrdinal(name); return r.IsDBNull(i) ? null : Convert.ToInt32(r.GetValue(i), System.Globalization.CultureInfo.InvariantCulture); }
    private static int? NullableInt(SqlDataReader r, string name) => Int(r, name);
    private static DateTime? Utc(SqlDataReader r, string name) { var i = r.GetOrdinal(name); return r.IsDBNull(i) ? null : DateTime.SpecifyKind(r.GetDateTime(i), DateTimeKind.Utc); }
}
