using System.Collections.Concurrent;
using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Abstractions;

namespace TigerCS.Infrastructure.Modules.Collections;

/// <summary>
/// Refreshes the local receivables snapshot: ONE call of <c>dbo.usp_CollectionsRcv_Refresh</c> per company (PACT -> temp table -> validated publish, all inside SQL), so a slow, hung or failed
/// company never blocks, rolls back or marks the other one. A company is never refreshed twice at the same time: an in-process gate here and the company's application lock in SQL.
/// A refresh whose call does not return (timeout, cancellation, restart) is closed through <c>usp_CollectionsRcv_ReapDeadRuns</c> once its SQL session is gone; whatever it cannot close
/// (the session is still executing the PACT call) stays honestly "loading" until that session ends, and is closed by the next status read or refresh.
/// </summary>
public sealed class RcvReceivablesRefresher(IConfiguration configuration, ReceivablesSnapshotOptions options, ILogger<RcvReceivablesRefresher> logger) : IReceivablesRefresher
{
    private static readonly ConcurrentDictionary<int, SemaphoreSlim> Gates = new();
    private static readonly int[] Companies = [4, 32];

    /// <remarks>
    /// <paramref name="fromDate"/> / <paramref name="throughDate"/> are ignored: the snapshot always holds the whole configured window, so there is nothing to "load" for a range.
    /// <paramref name="companyId"/> null refreshes Dubai then Sharjah, each independently (the scheduler runs one job per company instead, in parallel).
    /// </remarks>
    public async Task<ReceivablesRefreshResult> RefreshAsync(string triggerSource, int? companyId, CancellationToken cancellationToken,
        DateOnly? fromDate = null, DateOnly? throughDate = null)
    {
        if (companyId is not (null or 4 or 32)) throw new ArgumentOutOfRangeException(nameof(companyId));
        var results = new List<ReceivablesRefreshCompanyResult>();
        Guid? firstRun = null;
        foreach (var company in Companies.Where(c => companyId is null || c == companyId))
        {
            var (runId, result) = await RefreshCompanyAsync(triggerSource, company, cancellationToken);
            firstRun ??= runId;
            results.Add(result);
        }
        var ok = results.Count(r => r.Status == "Succeeded");
        // A newly published snapshot may hold units whose CRM owners were still without a company (a tower number that exists in both companies): tie them now.
        // The towers of the newly published snapshot join dbo.CollectionsTowers (the tower picker / filter), before the CRM owners are tied to companies.
        if (ok > 0) await SyncTowersAsync(cancellationToken);
        if (ok > 0) await ResolveCrmOwnerCompaniesAsync(cancellationToken);
        var failed = results.Count(r => r.Status == "Failed");
        var status = failed == 0 ? (ok > 0 ? "Succeeded" : "AlreadyRunning") : ok > 0 ? "PartialFailure" : "Failed";
        var message = status switch
        {
            "AlreadyRunning" => "A refresh of this company is already running; this call did nothing.",
            "Succeeded" => null,
            _ => string.Join(" ", results.Where(r => r.Status == "Failed").Select(r => $"Company {r.CompanyId}: {r.ErrorMessage}"))
        };
        return new ReceivablesRefreshResult(firstRun, status, message, results);
    }

    /// <summary>
    /// Best effort and never throws (a failure here never fails or undoes the refresh): <c>dbo.usp_CollectionsRcv_SyncTowers</c> (V014) adds the towers the current snapshot holds to
    /// <c>dbo.CollectionsTowers</c> and names a placeholder from the single CRM name. Idempotent; never deletes or overwrites a real name; touches no financial data.
    /// </summary>
    private async Task SyncTowersAsync(CancellationToken cancellationToken)
    {
        try
        {
            var connectionString = configuration.GetConnectionString(options.ConnectionStringName);
            if (string.IsNullOrWhiteSpace(connectionString)) return;
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand("dbo.usp_CollectionsRcv_SyncTowers", connection) { CommandType = CommandType.StoredProcedure, CommandTimeout = 120 };
            command.Parameters.Add("@Apply", SqlDbType.Bit).Value = true;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
                logger.LogInformation("Towers synchronised from the receivables snapshot: {Inserted} added, {Renamed} named, {Skipped} skipped (not a plain numeric number), {Seen} in the snapshot.",
                    reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3));
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        { logger.LogWarning("The towers could not be synchronised after the refresh ({ExceptionType}); the tower list keeps its previous content.", ex.GetType().Name); }
    }

    /// <summary>
    /// Best effort and never throws: ties each CRM owner that is still without a company to the one company whose snapshot holds its EXACT unit
    /// (<c>dbo.usp_Collections_ResolveCrmOwnerCompanies</c>, V012). A unit held by both companies stays untied and is reported as ambiguous.
    /// </summary>
    private async Task ResolveCrmOwnerCompaniesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var connectionString = configuration.GetConnectionString(options.ConnectionStringName);
            if (string.IsNullOrWhiteSpace(connectionString)) return;
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand("dbo.usp_Collections_ResolveCrmOwnerCompanies", connection) { CommandType = CommandType.StoredProcedure, CommandTimeout = 300 };
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
                logger.LogInformation("CRM owners tied to a company from the receivables snapshot: {Resolved} resolved, {Ambiguous} held by both companies.", reader.GetInt32(0), reader.GetInt32(1));
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        { logger.LogWarning("CRM owners could not be tied to companies after the refresh ({ExceptionType}).", ex.GetType().Name); }
    }

    private async Task<(Guid? RunId, ReceivablesRefreshCompanyResult Result)> RefreshCompanyAsync(string triggerSource, int company, CancellationToken cancellationToken)
    {
        var gate = Gates.GetOrAdd(company, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, cancellationToken))
            return (null, new ReceivablesRefreshCompanyResult(company, "AlreadyRunning", null, null, null, null, null, null, "A refresh of this company is already running in this process."));
        var startedAt = DateTime.UtcNow;
        var runId = Guid.NewGuid();   // ours, so an attempt whose call never returns can still be found
        string? connectionString = null;
        SqlConnection? connection = null;
        var returned = false; int closeNumber = -1; string closeMessage = "The refresh did not finish.";
        try
        {
            connectionString = configuration.GetConnectionString(options.ConnectionStringName)
                ?? throw new PactReceivablesSourceException("The receivables snapshot connection is not configured.");
            connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand("dbo.usp_CollectionsRcv_Refresh", connection)
            { CommandType = CommandType.StoredProcedure, CommandTimeout = Math.Clamp(options.RefreshCommandTimeoutSeconds, 5, 7200) };
            command.Parameters.Add("@CompanyId", SqlDbType.Int).Value = company;
            command.Parameters.Add("@TriggerSource", SqlDbType.NVarChar, 50).Value = triggerSource.Length > 50 ? triggerSource[..50] : triggerSource;
            command.Parameters.Add("@RunId", SqlDbType.UniqueIdentifier).Value = runId;
            command.Parameters.Add("@SourceFromDate", SqlDbType.Date).Value = options.SourceFromDate.Date;
            command.Parameters.Add("@SourceThroughDate", SqlDbType.Date).Value = options.SourceThroughDate.Date;
            command.Parameters.Add("@SourceMinAmount", SqlDbType.Int).Value = options.SourceMinAmount;
            command.Parameters.Add("@MaxRawRows", SqlDbType.Int).Value = options.MaxRawRows;
            command.Parameters.Add("@MaxShrinkPercent", SqlDbType.Int).Value = Math.Clamp(options.MaxShrinkPercent, 0, 100);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            ReceivablesRefreshCompanyResult? result = null; Guid? resultRun = null;
            if (await reader.ReadAsync(cancellationToken))
            {
                resultRun = reader.IsDBNull(reader.GetOrdinal("RunId")) ? null : reader.GetGuid(reader.GetOrdinal("RunId"));
                result = new ReceivablesRefreshCompanyResult(company, reader.GetString(reader.GetOrdinal("Status")), Int(reader, "RawRows"), Int(reader, "PublishedRows"),
                    Int(reader, "ExcludedSettledRows"), SumOf(Int(reader, "ExcludedInvalidRows"), Int(reader, "ExcludedCancelledRows")), null, Int(reader, "ErrorNumber"),
                    reader.IsDBNull(reader.GetOrdinal("ErrorMessage")) ? null : reader.GetString(reader.GetOrdinal("ErrorMessage")),
                    Int(reader, "FetchMs"), null, Int(reader, "PublishMs"));
            }
            returned = true;   // the procedure ran to its end: the attempt has its own terminal state
            var elapsedMs = (DateTime.UtcNow - startedAt).TotalMilliseconds;
            if (result is { Status: "Succeeded" })
                logger.LogInformation("Receivables refresh of company {CompanyId} run {RunId} Succeeded in {ElapsedMs:F0} ms (trigger {Trigger}): {RawRows} raw, {PublishedRows} published, fetch {FetchMs} ms, publish {PublishMs} ms.",
                    company, resultRun ?? runId, elapsedMs, triggerSource, result.RawRows, result.PublishedRows, result.FetchMs, result.PublishMs);
            else if (result is { Status: "AlreadyRunning" })
                logger.LogInformation("Receivables refresh of company {CompanyId} skipped after {ElapsedMs:F0} ms (trigger {Trigger}): another refresh of this company holds its lock.", company, elapsedMs, triggerSource);
            else
                logger.LogWarning("Receivables refresh of company {CompanyId} run {RunId} ended {Status} after {ElapsedMs:F0} ms (trigger {Trigger}) in the phase \"{Phase}\" (SQL error {SqlNumber}): {ErrorMessage}. The previous snapshot is kept.",
                    company, resultRun ?? runId, result?.Status ?? "Failed", elapsedMs, triggerSource,
                    TigerCS.Application.Modules.Collections.Services.SnapshotFailureText.Phase(result?.ErrorNumber, result?.FetchMs), result?.ErrorNumber, result?.ErrorMessage);
            return (resultRun ?? runId, result ?? new ReceivablesRefreshCompanyResult(company, "Failed", null, null, null, null, null, null, "The refresh procedure returned no result."));
        }
        catch (SqlException ex)
        {
            closeNumber = ex.Number;
            // The whole PACT fetch runs INSIDE the one long procedure call, so the client connection sits idle for minutes. A network device that drops idle TCP sessions then
            // surfaces here as a transport error (-1 / 10054 / 10060) although the server may still be finishing. Say so, instead of a bare number.
            var transport = new ReviewWriteRetryPolicy().IsTransient(ex) && ex.Number != -2;
            closeMessage = ex.Number == -2
                ? $"The refresh exceeded its {options.RefreshCommandTimeoutSeconds} s command timeout and was stopped."
                : transport
                    ? $"The connection to SQL Server was lost while the refresh was running (SQL error {ex.Number}: network / transport). The previous snapshot is kept; the server session may still be finishing and the next refresh or status read closes it."
                    : $"The refresh procedure failed (SQL error {ex.Number}).";
            // Everything needed to find the dropped session in the SQL Server logs and on the network path; never the connection string or any data.
            logger.LogError("Receivables refresh of company {CompanyId} run {RunId} failed after {ElapsedSeconds:F0} s (SQL error {SqlNumber}, class {Class}, state {State}, transport={Transport}, clientConnectionId={ClientConnectionId}, server={Server}, commandTimeoutSeconds={CommandTimeout}).",
                company, runId, (DateTime.UtcNow - startedAt).TotalSeconds, ex.Number, ex.Class, ex.State, transport, ex.ClientConnectionId, ex.Server, options.RefreshCommandTimeoutSeconds);
            return (runId, new ReceivablesRefreshCompanyResult(company, "Failed", null, null, null, null, null, ex.Number, closeMessage));
        }
        catch (OperationCanceledException)
        {
            closeMessage = "The refresh was cancelled (application shutdown or job cancellation).";
            throw;
        }
        catch (Exception ex)
        {
            closeMessage = $"The refresh stopped unexpectedly ({ex.GetType().Name}).";
            throw;
        }
        finally
        {
            if (connection is not null)
            {
                // A batch that did not return must not go back to the pool with its session (and the company's application lock) attached: closing the connection ends it.
                if (!returned) SqlConnection.ClearPool(connection);
                await connection.DisposeAsync();
            }
            if (!returned && connectionString is not null) await ReapAsync(connectionString, company, runId, closeNumber, closeMessage);
            gate.Release();
        }
    }

    /// <summary>
    /// Gives the attempt this call started a terminal state when the call ended without a result. Does nothing while the attempt's SQL session still holds the company's lock
    /// (the session is still inside the PACT call): it is then genuinely still running, and the next status read / refresh closes it once the session ends. Never throws.
    /// </summary>
    private async Task ReapAsync(string connectionString, int company, Guid runId, int errorNumber, string message)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cts.Token);
            await using (var note = new SqlCommand("dbo.usp_CollectionsRcv_NoteClientAbort", connection) { CommandType = CommandType.StoredProcedure, CommandTimeout = 30 })
            {
                note.Parameters.Add("@RunId", SqlDbType.UniqueIdentifier).Value = runId;
                note.Parameters.Add("@ErrorNumber", SqlDbType.Int).Value = errorNumber;
                note.Parameters.Add("@Message", SqlDbType.NVarChar, 1000).Value = message;
                await note.ExecuteNonQueryAsync(cts.Token);
            }
            await using var command = new SqlCommand("dbo.usp_CollectionsRcv_ReapDeadRuns", connection) { CommandType = CommandType.StoredProcedure, CommandTimeout = 30 };
            command.Parameters.Add("@CompanyId", SqlDbType.Int).Value = company;
            command.Parameters.Add("@ErrorNumber", SqlDbType.Int).Value = errorNumber;
            command.Parameters.Add("@Message", SqlDbType.NVarChar, 1000).Value = message;
            command.Parameters.Add("@Status", SqlDbType.VarChar, 12).Value = "Failed";
            await command.ExecuteNonQueryAsync(cts.Token);
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException or OperationCanceledException)
        { logger.LogWarning("Could not close the unfinished receivables refresh of company {CompanyId} ({ExceptionType}); the next status read or refresh will.", company, ex.GetType().Name); }
    }

    private static int? Int(SqlDataReader r, string name) { var i = r.GetOrdinal(name); return r.IsDBNull(i) ? null : Convert.ToInt32(r.GetValue(i), System.Globalization.CultureInfo.InvariantCulture); }
    private static int? SumOf(int? a, int? b) => a is null && b is null ? null : (a ?? 0) + (b ?? 0);
}
