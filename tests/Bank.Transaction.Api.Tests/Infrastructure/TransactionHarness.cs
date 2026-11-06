using System.Text.Json;
using Bank.Transaction.Api.Application.Features.Process;
using Bank.Transaction.Api.Domain.Constants;
using Bank.Transaction.Api.Domain.Entities;
using Microsoft.Data.SqlClient;

namespace Bank.Transaction.Api.Tests.Infrastructure;

// Arma un ProcessService como lo haría una entrega real: un DatabaseService nuevo por mensaje (un scope por entrega),
// con el sender y el logger compartidos para poder afirmar sobre los eventos emitidos y los registros.
public class TransactionHarness
{
    // Los cinco eventos que consume Transaction (nombres de suscripción).
    public static readonly string[] AllEvents =
    {
        ReceivedSubscriptionsConstants.TRANSACTION_INITIATED,
        ReceivedSubscriptionsConstants.BALANCE_CONFIRMED,
        ReceivedSubscriptionsConstants.BALANCE_FAILED,
        ReceivedSubscriptionsConstants.TRANSFER_FAILED,
        ReceivedSubscriptionsConstants.TRANSFER_CONFIRMED
    };

    public string ConnectionString { get; }
    public FakeServiceBusSender Sender { get; } = new();
    public CapturingLogger<ProcessService> Logger { get; } = new();

    private TransactionHarness(string connectionString) => ConnectionString = connectionString;

    public static async Task<TransactionHarness> CreateAsync(SqlServerFixture fixture) =>
        new(await fixture.CreateDatabaseAsync());

    public async Task DeliverAsync(string subscription, string message)
    {
        await using var db = SqlServerFixture.NewContext(ConnectionString);
        var service = new ProcessService(db, Sender, Logger);
        await service.Execute(message, subscription);
    }

    // transaction-initiated: lo emite el Gateway con los datos completos de la operación.
    public static string InitiatedMessage(string? correlationId, decimal amount = 1500m, int customerId = 7) =>
        JsonSerializer.Serialize(new
        {
            CorrelationId = correlationId,
            Amount = amount,
            SourceAccount = "111111111111111",
            DestinationAccount = "222222222222222",
            CustomerId = customerId
        });

    // Resto de eventos (balance-*, transfer-*): solo llevan el identificador y el cliente.
    public static string EventMessage(string? correlationId, int customerId = 7) =>
        JsonSerializer.Serialize(new { CorrelationId = correlationId, CustomerId = customerId });

    public static string MessageFor(string subscription, string? correlationId) =>
        subscription == ReceivedSubscriptionsConstants.TRANSACTION_INITIATED
            ? InitiatedMessage(correlationId)
            : EventMessage(correlationId);

    public static string NewCorrelationId() => Guid.NewGuid().ToString();

    public async Task<string> SeedTransactionAsync(string state, string? correlationId = null)
    {
        correlationId ??= NewCorrelationId();
        await using var db = SqlServerFixture.NewContext(ConnectionString);
        db.Transaction.Add(new TransactionEntity
        {
            CorrelationId = correlationId,
            TransactionDate = DateTime.UtcNow,
            CurrentState = state,
            Amount = 1500m,
            SourceAccount = "111111111111111",
            DestinationAccount = "222222222222222",
            CustomerId = 7
        });
        await db.SaveChangesAsync();
        return correlationId;
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

    public Task<int> CountTransactionsAsync(string? correlationId = null) =>
        ScalarAsync<int>(correlationId == null
            ? "SELECT COUNT(*) FROM [Transaction]"
            : $"SELECT COUNT(*) FROM [Transaction] WHERE CorrelationId = '{correlationId}'");

    public async Task<string?> StateOfAsync(string correlationId)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT CurrentState FROM [Transaction] WHERE CorrelationId = '{correlationId}'";
        return (string?)await command.ExecuteScalarAsync();
    }
}
