using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using TigerCS.Application.Modules.Ticketing.Abstractions;

namespace TigerCS.Infrastructure.BackgroundJobs;

/// <summary>
/// One run at a time: an in-process gate plus, when the database is SQL Server, a session-scoped <c>sp_getapplock</c> on a dedicated
/// connection (so a second IIS worker / server skips instead of overlapping). The SQL lock is released when the connection closes, so a
/// crashed process can never leave it held.
/// </summary>
public sealed class CallCenterReconciliationRunLock(IConfiguration configuration) : ICallCenterReconciliationRunLock
{
    public const string Resource = "TigerCS.CallCenterUnassignedWorkReconciliation";
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public async Task<IAsyncDisposable?> TryAcquireAsync(CancellationToken cancellationToken = default)
    {
        if (!await Gate.WaitAsync(0, cancellationToken))
        {
            return null;
        }

        SqlConnection? connection = null;
        try
        {
            var connectionString = configuration.GetConnectionString("TigerCsDatabase");
            if (!string.IsNullOrWhiteSpace(connectionString))
            {
                connection = new SqlConnection(connectionString);
                await connection.OpenAsync(cancellationToken);
                await using var command = connection.CreateCommand();
                command.CommandText = "DECLARE @r int; EXEC @r = sp_getapplock @Resource = @res, @LockMode = N'Exclusive', @LockOwner = N'Session', @LockTimeout = 0; SELECT @r;";
                command.Parameters.AddWithValue("@res", Resource);
                if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) < 0)
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
                if (connection is not null) await connection.DisposeAsync();   // closing the session releases the applock
            }
            finally
            {
                Gate.Release();
            }
        }
    }
}
