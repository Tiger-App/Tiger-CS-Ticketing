using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using TigerCS.Application.Modules.Collections.Review;

namespace TigerCS.Infrastructure.Modules.Collections;

/// <summary>
/// One file export records its repeat check and audit entry at a time. An in-process gate and, when the database is SQL Server, a session-scoped <c>sp_getapplock</c> on a dedicated connection,
/// so a second application server waits as well. The database lock is released when its connection closes, so a crashed process can never leave it held.
/// </summary>
public sealed class GenesysExportRunLock(IConfiguration configuration) : IGenesysExportLock
{
    public const string Resource = "TigerCS.CollectionsGenesysExport";
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public async Task<IAsyncDisposable?> TryAcquireAsync(TimeSpan wait, CancellationToken ct)
    {
        if (!await Gate.WaitAsync(wait, ct)) return null;
        SqlConnection? connection = null;
        try
        {
            var connectionString = configuration.GetConnectionString("TigerCsDatabase");
            if (!string.IsNullOrWhiteSpace(connectionString))
            {
                connection = new SqlConnection(connectionString);
                await connection.OpenAsync(ct);
                await using var command = connection.CreateCommand();
                command.CommandText = "DECLARE @r int; EXEC @r = sp_getapplock @Resource = @res, @LockMode = N'Exclusive', @LockOwner = N'Session', @LockTimeout = @timeout; SELECT @r;";
                command.Parameters.AddWithValue("@res", Resource);
                command.Parameters.AddWithValue("@timeout", (int)wait.TotalMilliseconds);
                if (Convert.ToInt32(await command.ExecuteScalarAsync(ct)) < 0)
                {
                    await connection.DisposeAsync();
                    Gate.Release();
                    return null;
                }
            }

            return new Handle(connection);
        }
        catch
        {
            if (connection is not null) await connection.DisposeAsync();
            Gate.Release();
            throw;
        }
    }

    private sealed class Handle(SqlConnection? connection) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                if (connection is not null) await connection.DisposeAsync();   // closing the session releases the application lock
            }
            finally
            {
                Gate.Release();
            }
        }
    }
}
