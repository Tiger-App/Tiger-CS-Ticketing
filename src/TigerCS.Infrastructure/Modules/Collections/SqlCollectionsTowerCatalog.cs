using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Domain.Modules.Collections;

namespace TigerCS.Infrastructure.Modules.Collections;

/// <summary>The tower dropdown: the local <c>dbo.CollectionsTowers</c> table through <c>dbo.usp_CollectionsRcv_GetTowers</c> (no PACT access, no invented towers), newest tower first.</summary>
public sealed class SqlCollectionsTowerCatalog(IConfiguration configuration, ReceivablesSnapshotOptions options, ILogger<SqlCollectionsTowerCatalog> logger)
    : ICollectionsTowerCatalog
{
    // The tower list changes rarely and is read on every page view: keep it for a minute so the page shell never waits on it twice.
    private static readonly object CacheLock = new();
    private static (DateTime AtUtc, IReadOnlyList<CollectionsTowerDto> Towers)? cache;

    public async Task<IReadOnlyList<CollectionsTowerDto>> ListActiveAsync(CancellationToken cancellationToken)
    {
        lock (CacheLock) { if (cache is { } hit && DateTime.UtcNow - hit.AtUtc < TimeSpan.FromSeconds(60)) return hit.Towers; }
        var towers = await ReadAsync(cancellationToken);
        lock (CacheLock) { cache = (DateTime.UtcNow, towers); }
        return towers;
    }

    // The campaign / receivables data carries its own tower numbers (dbo.CollectionsRcvLine of each company's current run). The procedure lists ACTIVE towers of companies 4 and 32 only, so a
    // tower that has customers in the data but is not flagged active (or whose flag is empty) never reached the picker. Every tower of the CURRENT data that has a CollectionsTowers
    // row (the filter needs its local id) is added here; nothing is invented and towers without data are not added. A failure here only leaves the procedure's list as it was.
    private async Task AddTowersOfCampaignDataAsync(SqlConnection connection, List<CollectionsTowerDto> towers, CancellationToken cancellationToken)
    {
        try
        {
            await using var command = new SqlCommand("""
                SELECT t.TowerId, LTRIM(RTRIM(CONVERT(nvarchar(20), t.TowerNumber))) AS TowerNumber, COALESCE(n.TowerName, CONVERT(nvarchar(400), t.TowerName)) AS TowerName, t.CompanyId
                  FROM dbo.CollectionsTowers t
                  LEFT JOIN dbo.fn_Collections_TowerNames() n ON n.CompanyId = t.CompanyId AND n.TowerNumber = LTRIM(RTRIM(CONVERT(nvarchar(20), t.TowerNumber)))
                 WHERE t.CompanyId IN (4, 32)
                   AND EXISTS (SELECT 1 FROM dbo.CollectionsRcvCompany c
                                 JOIN dbo.CollectionsRcvLine l ON l.CompanyId = c.CompanyId AND l.RunId = c.CurrentRunId
                                WHERE c.CompanyId = t.CompanyId AND c.CurrentRunId IS NOT NULL
                                  AND LTRIM(RTRIM(CONVERT(nvarchar(20), l.TowerNumber))) = LTRIM(RTRIM(CONVERT(nvarchar(20), t.TowerNumber))))
                """, connection) { CommandTimeout = Math.Clamp(options.ReadCommandTimeoutSeconds, 1, 600) };
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var known = towers.Select(t => t.TowerId).ToHashSet();
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetInt32(0);
                if (known.Add(id)) towers.Add(new CollectionsTowerDto(id, reader.GetString(1), reader.GetString(2), reader.GetInt32(3), true));
            }
        }
        catch (SqlException ex)
        {
            logger.LogWarning("Towers of the campaign data could not be added to the tower list (SQL error {SqlNumber}).", ex.Number);
        }
    }

    private async Task<IReadOnlyList<CollectionsTowerDto>> ReadAsync(CancellationToken cancellationToken)
    {
        var connectionString = configuration.GetConnectionString(options.ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new PactReceivablesSourceException("The receivables snapshot connection is not configured.");
        var towers = new List<CollectionsTowerDto>();
        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand("dbo.usp_CollectionsRcv_GetTowers", connection)
            { CommandType = CommandType.StoredProcedure, CommandTimeout = Math.Clamp(options.ReadCommandTimeoutSeconds, 1, 600) };
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                towers.Add(new CollectionsTowerDto(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetBoolean(4)));
            await reader.CloseAsync();
            await AddTowersOfCampaignDataAsync(connection, towers, cancellationToken);
        }
        catch (SqlException ex)
        {
            logger.LogWarning("Tower list read failed (SQL error {SqlNumber}).", ex.Number);
            throw new PactReceivablesSourceException("The tower list could not be read.");
        }
        // Newest tower first: the tower NUMBER descending (TP140 ... TP119), then the company and the id. The procedure already returns this order; it is applied again here so
        // the picker never depends on it.
        towers.Sort((a, b) => CollectionsTowerOrder.CompareTowers(a.TowerNumber, a.CompanyId, a.TowerId, b.TowerNumber, b.CompanyId, b.TowerId));
        return towers;
    }
}
