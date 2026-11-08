using System.Text.Json;
using Bank.Balance.Api.Application.Database;
using Bank.Balance.Api.Application.Resilience;
using Bank.Balance.Api.Persistence.Database;
using Bank.Balance.Api.Tests.Infrastructure;
using Bank.Balance.Api.Application.External.ServiceBusSender;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Bank.Balance.Api.Tests.Resilience;

// Regla de seguridad FR-016 con SQL Server real: solo se emite el evento de fallo si el servicio no tiene registro del CorrelationId.
[Collection(SqlServerCollection.Name)]
public class FailureCompensatorTests
{
    private const string UNREACHABLE =
        "Server=127.0.0.1,1;Database=Unreachable;User Id=sa;Password=unused;Connect Timeout=1;TrustServerCertificate=True";

    private readonly SqlServerFixture _fixture;

    public FailureCompensatorTests(SqlServerFixture fixture) => _fixture = fixture;

    private static FailureCompensator Create(string connectionString, IServiceBusSenderService sender,
        ILogger<FailureCompensator> logger)
    {
        var services = new ServiceCollection();
        services.AddDbContext<DatabaseService>(options => options.UseSqlServer(connectionString));
        services.AddScoped<IDatabaseService, DatabaseService>();
        var provider = services.BuildServiceProvider();
        return new FailureCompensator(sender, provider.GetRequiredService<IServiceScopeFactory>(), logger);
    }

    private static string Body(string correlationId, int? customerId = 7) =>
        JsonSerializer.Serialize(new { CorrelationId = correlationId, CustomerId = customerId });

    [Fact]
    public async Task Without_record_emits_failure_event_with_correlation_and_customer()
    {
        var harness = await BalanceHarness.CreateAsync(_fixture);
        var sender = new FakeServiceBusSender();
        var compensator = Create(harness.ConnectionString, sender, new CapturingLogger<FailureCompensator>());
        var correlationId = BalanceHarness.NewCorrelationId();

        var result = await compensator.CompensateAsync(Body(correlationId), Bank.Balance.Api.Domain.Constants.ReceivedSubscriptionsConstants.BALANCE_INITIATED, CancellationToken.None);

        Assert.True(result);
        Assert.Equal(1, sender.CountOf(Bank.Balance.Api.Domain.Constants.SendSubscriptionConstants.BALANCE_FAILED));
        using var json = JsonDocument.Parse(sender.Sent[0].Json);
        Assert.Equal(correlationId, json.RootElement.GetProperty("CorrelationId").GetString());
        Assert.Equal(7, json.RootElement.GetProperty("CustomerId").GetInt32());
    }

    [Fact]
    public async Task Without_record_and_without_customer_omits_the_customer_property()
    {
        var harness = await BalanceHarness.CreateAsync(_fixture);
        var sender = new FakeServiceBusSender();
        var compensator = Create(harness.ConnectionString, sender, new CapturingLogger<FailureCompensator>());

        var result = await compensator.CompensateAsync("""{"CorrelationId":"c-1"}""",
            Bank.Balance.Api.Domain.Constants.ReceivedSubscriptionsConstants.BALANCE_INITIATED, CancellationToken.None);

        Assert.True(result);
        using var json = JsonDocument.Parse(sender.Sent[0].Json);
        Assert.False(json.RootElement.TryGetProperty("CustomerId", out _));
        Assert.Equal("c-1", json.RootElement.GetProperty("CorrelationId").GetString());
    }

    [Fact]
    public async Task With_existing_record_emits_nothing()
    {
        var harness = await BalanceHarness.CreateAsync(_fixture);
        var sender = new FakeServiceBusSender();
        var logger = new CapturingLogger<FailureCompensator>();
        var correlationId = await harness.SeedBalanceAsync("Completed");
        var compensator = Create(harness.ConnectionString, sender, logger);

        var result = await compensator.CompensateAsync(Body(correlationId),
            Bank.Balance.Api.Domain.Constants.ReceivedSubscriptionsConstants.BALANCE_INITIATED, CancellationToken.None);

        Assert.False(result);
        Assert.Empty(sender.Sent);
        Assert.Contains("AlreadyRegistered", logger.Reasons);
    }

    [Fact]
    public async Task Unreachable_database_emits_and_logs_warning()
    {
        var sender = new FakeServiceBusSender();
        var logger = new CapturingLogger<FailureCompensator>();
        var compensator = Create(UNREACHABLE, sender, logger);

        var result = await compensator.CompensateAsync(Body("c-1"), Bank.Balance.Api.Domain.Constants.ReceivedSubscriptionsConstants.BALANCE_INITIATED, CancellationToken.None);

        Assert.True(result);
        Assert.Equal(1, sender.CountOf(Bank.Balance.Api.Domain.Constants.SendSubscriptionConstants.BALANCE_FAILED));
        Assert.Contains(logger.Entries, x => x.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task Other_subscription_neither_queries_nor_sends()
    {
        var sender = new FakeServiceBusSender();
        var logger = new CapturingLogger<FailureCompensator>();
        var compensator = Create(UNREACHABLE, sender, logger);

        var result = await compensator.CompensateAsync(Body("c-1"), Bank.Balance.Api.Domain.Constants.ReceivedSubscriptionsConstants.TRANSFER_CONFIRMED_BALANCE, CancellationToken.None);

        Assert.False(result);
        Assert.Empty(sender.Sent);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task Unreadable_correlation_id_returns_false_without_querying()
    {
        var sender = new FakeServiceBusSender();
        var compensator = Create(UNREACHABLE, sender, new CapturingLogger<FailureCompensator>());

        var result = await compensator.CompensateAsync("""{"CustomerId":7}""",
            Bank.Balance.Api.Domain.Constants.ReceivedSubscriptionsConstants.BALANCE_INITIATED, CancellationToken.None);

        Assert.False(result);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task Send_failure_propagates()
    {
        var harness = await BalanceHarness.CreateAsync(_fixture);
        var sender = new FakeServiceBusSender();
        sender.FailNextSend();
        var compensator = Create(harness.ConnectionString, sender, new CapturingLogger<FailureCompensator>());

        await Assert.ThrowsAnyAsync<Exception>(() => compensator.CompensateAsync(
            Body(BalanceHarness.NewCorrelationId()), Bank.Balance.Api.Domain.Constants.ReceivedSubscriptionsConstants.BALANCE_INITIATED, CancellationToken.None));
    }

    [Fact]
    public async Task Defective_message_for_a_finished_saga_emits_nothing()
    {
        // SC-008: el registro ya existe y llega una entrega con el cuerpo dañado pero el CorrelationId legible.
        var harness = await BalanceHarness.CreateAsync(_fixture);
        var sender = new FakeServiceBusSender();
        var correlationId = await SeedFinishedAsync(harness);
        var compensator = Create(harness.ConnectionString, sender, new CapturingLogger<FailureCompensator>());

        var damaged = JsonSerializer.Serialize(new { CorrelationId = correlationId, CustomerId = "abc" });
        var result = await compensator.CompensateAsync(damaged, Bank.Balance.Api.Domain.Constants.ReceivedSubscriptionsConstants.BALANCE_INITIATED, CancellationToken.None);

        Assert.False(result);
        Assert.Empty(sender.Sent);
    }

    private static Task<string> SeedFinishedAsync(BalanceHarness harness) => harness.SeedBalanceAsync("Completed");
}
