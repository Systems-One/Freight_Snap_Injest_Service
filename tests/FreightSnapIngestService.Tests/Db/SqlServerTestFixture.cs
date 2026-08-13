using FreightSnapIngestService.Db;
using Microsoft.Data.SqlClient;

namespace FreightSnapIngestService.Tests.Db;

public class SqlServerTestFixture : IAsyncLifetime
{
    private const string MasterConnectionString =
        @"Server=.;Database=master;Trusted_Connection=True;TrustServerCertificate=True;";

    public string DatabaseName { get; } = $"FreightSnapIngestTest_{Guid.NewGuid():N}";

    public string ConnectionString =>
        $@"Server=.;Database={DatabaseName};Trusted_Connection=True;TrustServerCertificate=True;";

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
