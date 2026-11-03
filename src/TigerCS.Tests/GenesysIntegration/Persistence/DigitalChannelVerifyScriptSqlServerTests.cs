using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using TigerCS.Infrastructure.Persistence;
using TigerCS.Tests.Ticketing.Integration;

namespace TigerCS.Tests.GenesysIntegration.Persistence;

/// <summary>
/// Runs the read-only Production diagnostic <c>Verify_TigerCS_DigitalChannelVisibility_ReadOnly.sql</c> against a freshly MIGRATED SQL Server
/// schema, so a typo, a wrong column or a wrong status number is caught here and not on the Production server. Also proves the migrated seed
/// satisfies the script's own verdict (Instagram = 10 and Facebook = 11 active and Genesys-enabled) and that the script changes nothing.
/// </summary>
public sealed class DigitalChannelVerifyScriptSqlServerTests : IDisposable
{
    private readonly string? _master = Environment.GetEnvironmentVariable(VisibilitySqlServerFactAttribute.Variable);
    private readonly string _database = $"TigerCsVerifyScript_{Guid.NewGuid():N}";

    private string ConnectionString => new SqlConnectionStringBuilder(_master!) { InitialCatalog = _database }.ConnectionString;

    public void Dispose()
    {
        if (string.IsNullOrWhiteSpace(_master))
        {
            return;
        }

        SqlConnection.ClearAllPools();
        using var master = new SqlConnection(_master);
        master.Open();
        using var command = master.CreateCommand();
        command.CommandText = $"IF DB_ID(N'{_database}') IS NOT NULL BEGIN ALTER DATABASE [{_database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_database}]; END";
        command.ExecuteNonQuery();
    }

    private static string ScriptPath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "Verify_TigerCS_DigitalChannelVisibility_ReadOnly.sql");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("Verify_TigerCS_DigitalChannelVisibility_ReadOnly.sql not found above the test output folder.");
    }

    [VisibilitySqlServerFact]
    public async Task TheScript_RunsCleanlyOnTheMigratedSchema_PassesTheChannelVerdict_AndIsReadOnly()
    {
        using (var master = new SqlConnection(_master))
        {
            master.Open();
            using var create = master.CreateCommand();
            create.CommandText = $"CREATE DATABASE [{_database}]";
            create.ExecuteNonQuery();
        }

        using (var context = new TigerCsDbContext(new DbContextOptionsBuilder<TigerCsDbContext>().UseSqlServer(ConnectionString).Options))
        {
            context.Database.Migrate();
        }

        var script = await File.ReadAllTextAsync(ScriptPath());

        // Read-only by construction: no statement other than SELECT / SET.
        var withoutComments = System.Text.RegularExpressions.Regex.Replace(script, @"/\*.*?\*/|--[^\r\n]*", string.Empty, System.Text.RegularExpressions.RegexOptions.Singleline);
        Assert.DoesNotMatch(@"(?i)\b(insert|update|delete|merge|drop|alter|create|truncate|exec|execute)\b", withoutComments);

        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = script;
        await using var reader = await command.ExecuteReaderAsync();

        var verdicts = new List<string>();
        var resultSets = 0;
        do
        {
            resultSets++;
            var hasVerdict = Enumerable.Range(0, reader.FieldCount).Any(i => reader.GetName(i) == "Verdict");
            while (await reader.ReadAsync())
            {
                if (hasVerdict)
                {
                    verdicts.Add(reader.GetString(reader.GetOrdinal("Verdict")));
                }
            }
        }
        while (await reader.NextResultAsync());

        Assert.Equal(["PASS", "PASS"], verdicts);        // Instagram (10) and Facebook (11): active and Genesys-enabled in the migrated seed
        Assert.Equal(10, resultSets);                     // sections 1, 1b, 2, 2b, 3, 4, 5, 6, 7 (summary + list): every section ran
    }
}
