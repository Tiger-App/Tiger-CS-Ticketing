using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Abstractions;

namespace TigerCS.Infrastructure.Modules.Collections;

/// <summary>Read-only report procedures on PACTRPT; never writes to TigerCS or PACT.</summary>
public sealed class PactSqlReceivablesSource(
    IConfiguration configuration, PactReceivablesOptions options, TimeProvider timeProvider,
    ILogger<PactSqlReceivablesSource> logger) : IPactReceivablesSource
{
    // Identifies the running code in the log (verifies the local checkout actually runs the expected commit).
    private static readonly string Build =
        typeof(PactSqlReceivablesSource).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    public Task<PactReceivablesSnapshot> ReadAsync(DateOnly throughDate, CancellationToken cancellationToken) =>
        ReadAsync(new PactReceivablesRequest(null, throughDate), cancellationToken);

    public Task<PactReceivablesSnapshot> ReadAsync(PactReceivablesRequest request, CancellationToken cancellationToken) =>
        ReadAsync(request, options.ProcedureSuffix, cancellationToken);

    /// <summary>Reads procedure <c>p{company}AccountReceivables{suffix}</c>; the suffix is explicit so a caller can name the exact procedure (the reconciliation reads the one the snapshot refresh calls).</summary>
    public async Task<PactReceivablesSnapshot> ReadAsync(PactReceivablesRequest request, string? procedureSuffix, CancellationToken cancellationToken)
    {
        var throughDate = request.ThroughDate;
        var startDate = (request.FromDate?.ToDateTime(TimeOnly.MinValue) ?? options.StartDate).Date;
        if (request.CompanyId is not (null or 4 or 32))
            throw new PactReceivablesSourceException("The PACT report company is not supported.");
        if (startDate > EndOfDay(throughDate))
            throw new PactReceivablesSourceException("The PACT report start date is after its end date.");
        var connectionString = configuration.GetConnectionString(options.ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new PactReceivablesSourceException("The PACT report connection is not configured.");

        var endDate = EndOfDay(throughDate);
        var requestStarted = Stopwatch.GetTimestamp();
        HashSet<(string UnitCode, DateTime DueDate)> downPayments = [];
        if (options.ApplyLegacyExclusions)
        {
            // Do not silently remove the original CRM filter when migrating this function.
            if (options.LegacyDownPaymentFromDate is null)
                throw new PactReceivablesSourceException("The legacy down-payment start date has not been configured.");
            if (!options.SpecialCasesConfigured)
                throw new PactReceivablesSourceException("The original special-case unit exclusions have not been configured.");
            downPayments = await ReadDownPaymentsAsync(cancellationToken);
        }

        // The two company procedures hit different databases, so they run concurrently on their own connections:
        // latency is the slower procedure rather than the sum. If one fails, the other is cancelled and the
        // original failure (not the follow-on cancellation) is reported. No partial list is ever returned.
        using var fault = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // A company-scoped request must not wait for (or be failed by) the other company's procedure.
        var work = new[] { (Company: 4, Procedure: ProcedureName(4, procedureSuffix)), (Company: 32, Procedure: ProcedureName(32, procedureSuffix)) }
            .Where(x => request.CompanyId is null || request.CompanyId == x.Company)
            .Select(x => Task.Run(() => ReadCompanyAsync(connectionString, x.Company, x.Procedure, startDate, endDate, fault.Token, cancellationToken), CancellationToken.None))
            .ToArray();
        try
        {
            await Task.WhenAll(work);
        }
        catch
        {
            await fault.CancelAsync();
            try { await Task.WhenAll(work); } catch { /* observed below */ }
            // Prefer the first real failure over the cancellation it caused in the sibling.
            var failure = work.Where(t => t.IsFaulted).Select(t => t.Exception!.InnerException!)
                .FirstOrDefault(e => e is not OperationCanceledException) ?? work.First(t => t.IsFaulted || t.IsCanceled).Exception?.InnerException;
            if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            throw;
        }
        var rows = work.SelectMany(t => t.Result).ToList();
        if (rows.Count > Math.Clamp(options.MaxSourceRows, 1, 1000000))
            throw new PactReceivablesSourceException("The PACT report exceeded the configured source-row limit; no partial list was returned.");

        logger.LogInformation("PACT receivables read complete: {Rows} rows from {Companies} in {ElapsedMs} ms (parallel); startDate={StartDate:O}, endDate={EndDate:O}; requestBudgetSeconds={Budget}; build={Build}.",
            rows.Count, request.CompanyId is { } only ? $"company {only}" : "companies 4 and 32", Stopwatch.GetElapsedTime(requestStarted).TotalMilliseconds, startDate, endDate, options.RequestBudgetSeconds, Build);
        if (options.ApplyLegacyExclusions)
            rows = ApplyExclusions(rows, downPayments, options).ToList();
        return new PactReceivablesSnapshot(rows, timeProvider.GetUtcNow().UtcDateTime, options.ApplyLegacyExclusions);
    }

    private async Task<List<PactReceivableInstalment>> ReadCompanyAsync(
        string connectionString, int company, string procedure, DateTime startDate, DateTime endDate, CancellationToken token, CancellationToken callerToken)
    {
        var rows = new List<PactReceivableInstalment>();
        var started = Stopwatch.GetTimestamp();
        var zeroRows = 0;
        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(token);
            using var command = new SqlCommand(procedure, connection)
            {
                CommandType = CommandType.StoredProcedure,
                CommandTimeout = Math.Clamp(options.CommandTimeoutSeconds, 1, 300)
            };
            command.Parameters.Add("@StartDate", SqlDbType.DateTime).Value = startDate;
            command.Parameters.Add("@EndDate", SqlDbType.DateTime).Value = endDate;
            // Include sub-dirham balances; only positive amounts survive in the service.
            command.Parameters.Add("@MinAmount", SqlDbType.Int).Value = 0;
            await using var reader = await command.ExecuteReaderAsync(token);
            var columns = Enumerable.Range(0, reader.FieldCount).ToDictionary(reader.GetName, x => x, StringComparer.OrdinalIgnoreCase);
            foreach (var name in new[] { "CompanyID", "TenantID", "FullName", "Mobile", "Email", "UnitID", "UnitCode", "VoucherNumber", "ChequeNumber", "DueDate", "Amount", "Status" })
                if (!columns.ContainsKey(name))
                    throw new PactReceivablesSourceException("The PACT report result does not match the expected column contract.");

            string Text(string name) => columns.TryGetValue(name, out var index) && !reader.IsDBNull(index)
                ? Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture) ?? "" : "";
            while (await reader.ReadAsync(token))
            {
                if (reader.IsDBNull(columns["DueDate"]) || reader.IsDBNull(columns["Amount"]))
                    throw new PactReceivablesSourceException("The PACT report returned an instalment without a date or amount.");
                var rowCompany = Convert.ToInt32(reader.GetValue(columns["CompanyID"]), CultureInfo.InvariantCulture);
                if (rowCompany != company)
                    throw new PactReceivablesSourceException("The PACT report returned an unexpected company identity.");
                var amount = Convert.ToDecimal(reader.GetValue(columns["Amount"]), CultureInfo.InvariantCulture);
                // The deployed procedures compute Amount in float; the reviewed V2 returns decimal(19,4). Record which, so floating-point
                // residue (e.g. 1E-12 on a settled instalment) can be told apart from a genuine sub-fils source amount.
                var amountType = reader.GetFieldType(columns["Amount"]);
                var amountIsFloat = amountType == typeof(double) || amountType == typeof(float);
                // PlanAmount (V2 only) lets the review check that the remainder is consistent with the original amount; the deployed procedures omit it.
                decimal? plan = columns.TryGetValue("PlanAmount", out var planIndex) && !reader.IsDBNull(planIndex)
                    ? Convert.ToDecimal(reader.GetValue(planIndex), CultureInfo.InvariantCulture) : null;
                // AllocatedAmount (V2 only) is the paid part of the instalment: original = paid + remaining.
                decimal? allocated = columns.TryGetValue("AllocatedAmount", out var allocatedIndex) && !reader.IsDBNull(allocatedIndex)
                    ? Convert.ToDecimal(reader.GetValue(allocatedIndex), CultureInfo.InvariantCulture) : null;
                rows.Add(new PactReceivableInstalment(rowCompany, Text("TenantID"), Text("FullName"), Text("Mobile"), Text("Email"),
                    reader.IsDBNull(columns["UnitID"]) ? null : Convert.ToInt32(reader.GetValue(columns["UnitID"]), CultureInfo.InvariantCulture),
                    Text("UnitCode"), Text("ProjectCode"), Text("VoucherNumber"), Text("ChequeNumber"),
                    reader.GetDateTime(columns["DueDate"]), amount, Text("Status"), PlanAmount: plan, AmountIsFloatingPoint: amountIsFloat, AllocatedAmount: allocated));
                if (amount == 0) zeroRows++;
                if (rows.Count > Math.Clamp(options.MaxSourceRows, 1, 1000000))
                    throw new PactReceivablesSourceException("The PACT report exceeded the configured source-row limit; no partial list was returned.");
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException || ex is SqlException { Number: -2 })
        {
            // Which timer fired: SQL command timeout (SqlException -2), the caller/HTTP request (callerCancelled),
            // or the API request budget (OperationCanceledException with callerCancelled=false). No secrets or row content.
            logger.LogWarning("PACT receivables procedure {Procedure} (company {CompanyId}) aborted after {ElapsedMs} ms with {Rows} rows read: {Kind}; callerCancelled={CallerCancelled}; tokenCancelled={TokenCancelled}; commandTimeoutSeconds={CommandTimeout}; build={Build}.",
                procedure, company, Stopwatch.GetElapsedTime(started).TotalMilliseconds, rows.Count,
                ex is SqlException ? "SQL command timeout" : "cancelled", callerToken.IsCancellationRequested, token.IsCancellationRequested,
                Math.Clamp(options.CommandTimeoutSeconds, 1, 300), Build);
            throw;
        }
        catch (Exception ex)
        {
            // Failure reason without message text (it can carry server names); no row content or credentials.
            logger.LogWarning("PACT receivables procedure {Procedure} (company {CompanyId}) failed after {ElapsedMs} ms with {Rows} rows read: {ExceptionType}{SqlNumber}; build={Build}.",
                procedure, company, Stopwatch.GetElapsedTime(started).TotalMilliseconds, rows.Count, ex.GetType().Name,
                ex is SqlException sql ? $" (SQL error {sql.Number})" : "", Build);
            throw;
        }
        logger.LogInformation("PACT receivables procedure {Procedure} (company {CompanyId}) returned {Rows} rows ({ZeroRows} with zero amount) in {ElapsedMs} ms; startDate={StartDate:O}, endDate={EndDate:O}, minAmount=0; build={Build}.",
            procedure, company, rows.Count, zeroRows, Stopwatch.GetElapsedTime(started).TotalMilliseconds, startDate, endDate, Build);
        return rows;
    }

    /// <summary>Fixed, allow-listed procedure names; the suffix is configuration (never caller input) and must be alphanumeric.</summary>
    public static string ProcedureName(int company, string? suffix)
    {
        suffix ??= "";
        if (company is not (4 or 32) || !System.Text.RegularExpressions.Regex.IsMatch(suffix, "^[A-Za-z0-9]{0,16}$"))
            throw new PactReceivablesSourceException("The PACT report procedure configuration is invalid.");
        return $"dbo.p{company}AccountReceivables{suffix}";
    }

    /// <summary>
    /// Last representable SQL <c>datetime</c> of the day (23:59:59.997). SQL Server datetime rounds to .000/.003/.007
    /// steps, so .999 would round up to midnight of the next day and pull in its first instalments.
    /// </summary>
    public static DateTime EndOfDay(DateOnly day) => day.ToDateTime(TimeOnly.MinValue).AddDays(1).AddMilliseconds(-3);

    private async Task<HashSet<(string UnitCode, DateTime DueDate)>> ReadDownPaymentsAsync(CancellationToken cancellationToken)
    {
        var connectionString = configuration.GetConnectionString(options.CrmConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new PactReceivablesSourceException("The CRM booking/down-payment exclusion connection is not configured.");
        var keys = new HashSet<(string UnitCode, DateTime DueDate)>();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        // The supplied EDSM method only executes its Booking & Downpayment branch.
        // Its Handover branches are commented out and TotDate is not referenced:
        // keep the original absence of an upper date/project filter.
        using var command = new SqlCommand("""
            SELECT TRIM(TRIM(l.ProjectCode) + '-' + TRIM(l.UnitNumber)) AS UnitCode,
                   p.Amount, p.DueDate
            FROM tblPayment p
            INNER JOIN tblLead l ON l.ID = p.LeadID
            WHERE p.[Type] IN (2, 3)
              AND l.Status != 6
              AND p.DueDate >= @FromDate
            """, connection)
        {
            CommandType = CommandType.Text,
            CommandTimeout = Math.Clamp(options.CommandTimeoutSeconds, 1, 300)
        };
        command.Parameters.Add("@FromDate", SqlDbType.DateTime).Value = options.LegacyDownPaymentFromDate!.Value;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var columns = Enumerable.Range(0, reader.FieldCount).ToDictionary(reader.GetName, x => x, StringComparer.OrdinalIgnoreCase);
        if (!columns.TryGetValue("UnitCode", out var unit) || !columns.TryGetValue("DueDate", out var date))
            throw new PactReceivablesSourceException("The CRM exclusion result does not match the expected column contract.");
        var rowCount = 0;
        while (await reader.ReadAsync(cancellationToken))
        {
            if (reader.IsDBNull(unit) || reader.IsDBNull(date))
                throw new PactReceivablesSourceException("The CRM exclusions returned a row without a unit or date.");
            keys.Add((reader.GetString(unit), reader.GetDateTime(date)));
            if (++rowCount > Math.Clamp(options.MaxSourceRows, 1, 1000000))
                throw new PactReceivablesSourceException("The CRM exclusions exceeded the source-row limit.");
        }
        return keys;
    }

    /// <summary>Exact date matching and unit-level PDC exclusions reproduce the supplied EDSM function.</summary>
    public static IEnumerable<PactReceivableInstalment> ApplyExclusions(
        IEnumerable<PactReceivableInstalment> rows, IReadOnlySet<(string UnitCode, DateTime DueDate)> downPayments,
        PactReceivablesOptions options)
    {
        var special = options.SpecialCaseUnitCodes.ToHashSet(StringComparer.Ordinal);
        var pdc = options.PdcExcludedUnitCodes.ToHashSet(StringComparer.Ordinal);
        return rows.Where(r => !downPayments.Contains((r.UnitCode, r.DueDate))
            && !(r.UnitCode.StartsWith("TP121", StringComparison.Ordinal) && r.DueDate > new DateTime(2025, 12, 15))
            && !(r.UnitCode.StartsWith("TP122", StringComparison.Ordinal) && r.DueDate > new DateTime(2026, 2, 20))
            && !(r.UnitCode.StartsWith("TP123", StringComparison.Ordinal) && r.DueDate > new DateTime(2026, 3, 20))
            && !new[] { "TP103", "TP102", "TP104", "TP101" }.Any(prefix => r.UnitCode.StartsWith(prefix, StringComparison.Ordinal))
            && !pdc.Contains(r.UnitCode) && !r.FullName.Contains('*') && !special.Contains(r.UnitCode));
    }
}
