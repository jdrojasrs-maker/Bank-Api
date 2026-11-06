using System.Text.Json;
using Bank.Transfer.Api.Application.Features.Process;
using Microsoft.Data.SqlClient;

namespace Bank.Transfer.Api.Tests.Infrastructure;

// Arma un ProcessService como lo haría una entrega real: un DatabaseService nuevo por mensaje (un scope por entrega),
// con el sender y el logger compartidos para poder afirmar sobre los eventos emitidos y los registros.
public class TransferHarness
{
    public string ConnectionString { get; }
    public FakeServiceBusSender Sender { get; } = new();
    public CapturingLogger<ProcessService> Logger { get; } = new();

    private TransferHarness(string connectionString) => ConnectionString = connectionString;

    public static async Task<TransferHarness> CreateAsync(SqlServerFixture fixture) =>
        new(await fixture.CreateDatabaseAsync());

    public async Task DeliverAsync(string message)
    {
        await using var db = SqlServerFixture.NewContext(ConnectionString);
        var service = new ProcessService(db, Sender, Logger);
        await service.Execute(message);
    }

    public static string Message(string? correlationId, decimal amount = 1500m, string source = "111111111111111",
        string destination = "222222222222222", int customerId = 7) =>
        JsonSerializer.Serialize(new
        {
            CorrelationId = correlationId,
            Amount = amount,
            SourceAccount = source,
            DestinationAccount = destination,
            CustomerId = customerId
        });

    public static string NewCorrelationId() => Guid.NewGuid().ToString();

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

    public Task<int> CountTransfersAsync(string? correlationId = null) =>
        ScalarAsync<int>(correlationId == null
            ? "SELECT COUNT(*) FROM Transfer"
            : $"SELECT COUNT(*) FROM Transfer WHERE CorrelationId = '{correlationId}'");

    public Task<int> CountRequestsAsync(string? correlationId = null) =>
        ScalarAsync<int>(correlationId == null
            ? "SELECT COUNT(*) FROM TransferRequest"
            : $"SELECT COUNT(*) FROM TransferRequest WHERE CorrelationId = '{correlationId}'");

    public Task AddAmountCheckAsync(decimal limit) =>
        // WITH NOCHECK: no revalida filas ya existentes; solo rechaza los INSERT posteriores.
        ExecuteSqlAsync($"ALTER TABLE Transfer WITH NOCHECK ADD CONSTRAINT CK_TEST_AMOUNT CHECK (Amount < {limit})");

    // Simula un procesador que cayó tras registrar: la solicitud queda tomada hace `secondsAgo` segundos y sin emitir.
    public Task BackdateClaimAsync(string correlationId, int secondsAgo) =>
        ExecuteSqlAsync(
            $"UPDATE TransferRequest SET ClaimedAt = DATEADD(second, -{secondsAgo}, SYSUTCDATETIME()), EmittedAt = NULL " +
            $"WHERE CorrelationId = '{correlationId}'");
}
