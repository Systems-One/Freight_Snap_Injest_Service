using FreightSnapIngestService.Db;
using Microsoft.Data.SqlClient;

namespace FreightSnapIngestService.Tests.Db;

public class SqlServerTestFixture : IAsyncLifetime
{
    // Defaults to LocalDB, which ships pre-installed on GitHub-hosted Windows runners
    // (and most dev machines with Visual Studio/SQL tooling). Override with the
    // FREIGHT_TEST_SQL_SERVER environment variable to point at a different reachable
    // instance, e.g. "." for a full local SQL Server / SQL Server Express install.
    private static readonly string ServerInstance =
        Environment.GetEnvironmentVariable("FREIGHT_TEST_SQL_SERVER") ?? @"(localdb)\MSSQLLocalDB";

    private static string MasterConnectionString =>
        $@"Server={ServerInstance};Database=master;Trusted_Connection=True;TrustServerCertificate=True;";

    public string DatabaseName { get; } = $"FreightSnapIngestTest_{Guid.NewGuid():N}";

    public string ConnectionString =>
        $@"Server={ServerInstance};Database={DatabaseName};Trusted_Connection=True;TrustServerCertificate=True;";

    public async Task InitializeAsync()
    {
        await using (var master = new SqlConnection(MasterConnectionString))
        {
            await master.OpenAsync();
            await using var create = master.CreateCommand();
            create.CommandText = $"CREATE DATABASE [{DatabaseName}]";
            await create.ExecuteNonQueryAsync();
        }

        await using var db = new SqlConnection(ConnectionString);
        await db.OpenAsync();
        await using var schema = db.CreateCommand();
        schema.CommandText = SchemaSql.CreateTables;
        await schema.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        await using var master = new SqlConnection(MasterConnectionString);
        await master.OpenAsync();
        await using var drop = master.CreateCommand();
        drop.CommandText =
            $"ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{DatabaseName}];";
        await drop.ExecuteNonQueryAsync();
    }
}
