using Bank.Balance.Api.Persistence.Database;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Testcontainers.MsSql;

namespace Bank.Balance.Api.Tests.Infrastructure;

// Un único contenedor de SQL Server por ejecución de la colección; cada prueba recibe su propia base,
// creada aplicando las migraciones reales del servicio.
public class SqlServerFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _container =
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public async Task<string> CreateDatabaseAsync()
    {
        var databaseName = $"BankBalanceTest_{Guid.NewGuid():N}";

        await using (var master = new SqlConnection(_container.GetConnectionString()))
        {
            await master.OpenAsync();
            await using var command = master.CreateCommand();
            command.CommandText = $"CREATE DATABASE [{databaseName}]";
            await command.ExecuteNonQueryAsync();
        }

        var connectionString = new SqlConnectionStringBuilder(_container.GetConnectionString())
        {
            InitialCatalog = databaseName
        }.ConnectionString;

        await using var db = NewContext(connectionString);
        await db.Database.MigrateAsync();

        return connectionString;
    }

    public static DatabaseService NewContext(string connectionString) =>
        new(new DbContextOptionsBuilder<DatabaseService>().UseSqlServer(connectionString).Options);
}

[CollectionDefinition(Name)]
public class SqlServerCollection : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "SqlServer";
}
