using System.Text.Json;
using Bank.Balance.Api.Domain.Entities;
using Microsoft.Data.SqlClient;

namespace Bank.Balance.Api.Tests.Infrastructure;

// Utilidades de base de datos para las pruebas de Balance: esquema creado con las migraciones reales y filas de Balance.
public class BalanceHarness
{
    public string ConnectionString { get; }

    private BalanceHarness(string connectionString) => ConnectionString = connectionString;

    public static async Task<BalanceHarness> CreateAsync(SqlServerFixture fixture) =>
        new(await fixture.CreateDatabaseAsync());

    public static string NewCorrelationId() => Guid.NewGuid().ToString();

    // balance-initiated y los eventos de transferencia solo llevan el identificador y el cliente.
    public static string EventMessage(string? correlationId, int customerId = 7) =>
        JsonSerializer.Serialize(new { CorrelationId = correlationId, CustomerId = customerId });

    public async Task<string> SeedBalanceAsync(string state, string? correlationId = null)
    {
        correlationId ??= NewCorrelationId();
        await using var db = SqlServerFixture.NewContext(ConnectionString);
        db.Balance.Add(new BalanceEntity
        {
            CorrelationId = correlationId,
            BalanceDate = DateTime.UtcNow,
            CurrentState = state,
            CustomerId = 7
        });
        await db.SaveChangesAsync();
        return correlationId;
    }

    public async Task ExecuteSqlAsync(string sql)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    public async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var result = await command.ExecuteScalarAsync();
        return (T)Convert.ChangeType(result!, typeof(T));
    }

    public Task<int> CountBalancesAsync(string? correlationId = null) =>
        ScalarAsync<int>(correlationId == null
            ? "SELECT COUNT(*) FROM [Balance]"
            : $"SELECT COUNT(*) FROM [Balance] WHERE CorrelationId = '{correlationId}'");

    public async Task<string?> StateOfAsync(string correlationId)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT CurrentState FROM [Balance] WHERE CorrelationId = '{correlationId}'";
        return (string?)await command.ExecuteScalarAsync();
    }
}
