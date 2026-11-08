using System.Text.Json;
using Bank.Transfer.Api.Application.Database;
using Bank.Transfer.Api.Application.Resilience;
using Bank.Transfer.Api.Persistence.Database;
using Bank.Transfer.Api.Tests.Infrastructure;
using Bank.Transfer.Api.Application.External.ServiceBusSender;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Bank.Transfer.Api.Tests.Resilience;

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
        var harness = await TransferHarness.CreateAsync(_fixture);
        var sender = new FakeServiceBusSender();
        var compensator = Create(harness.ConnectionString, sender, new CapturingLogger<FailureCompensator>());
        var correlationId = TransferHarness.NewCorrelationId();

        var result = await compensator.CompensateAsync(Body(correlationId), Bank.Transfer.Api.Domain.Constants.ReceivedSubscriptionsConstants.TRANSFER_INITIATED, CancellationToken.None);

        Assert.True(result);
        Assert.Equal(1, sender.CountOf(Bank.Transfer.Api.Domain.Constants.SendSubscriptionConstants.TRANSFER_FAILED));
        using var json = JsonDocument.Parse(sender.Sent[0].Json);
        Assert.Equal(correlationId, json.RootElement.GetProperty("CorrelationId").GetString());
        Assert.Equal(7, json.RootElement.GetProperty("CustomerId").GetInt32());
    }

    [Fact]
    public async Task Without_record_and_without_customer_omits_the_customer_property()
    {
        var harness = await TransferHarness.CreateAsync(_fixture);
        var sender = new FakeServiceBusSender();
        var compensator = Create(harness.ConnectionString, sender, new CapturingLogger<FailureCompensator>());

        var result = await compensator.CompensateAsync("""{"CorrelationId":"c-1"}""",
            Bank.Transfer.Api.Domain.Constants.ReceivedSubscriptionsConstants.TRANSFER_INITIATED, CancellationToken.None);

        Assert.True(result);
        using var json = JsonDocument.Parse(sender.Sent[0].Json);
        Assert.False(json.RootElement.TryGetProperty("CustomerId", out _));
        Assert.Equal("c-1", json.RootElement.GetProperty("CorrelationId").GetString());
    }

    [Theory]
    [InlineData("Confirmed")]
    [InlineData("Failed")]
    public async Task With_existing_record_emits_nothing(string outcome)
    {
        var harness = await TransferHarness.CreateAsync(_fixture);
        var sender = new FakeServiceBusSender();
        var logger = new CapturingLogger<FailureCompensator>();
        var correlationId = await SeedRequestAsync(harness, outcome);
        var compensator = Create(harness.ConnectionString, sender, logger);

        var result = await compensator.CompensateAsync(Body(correlationId),
            Bank.Transfer.Api.Domain.Constants.ReceivedSubscriptionsConstants.TRANSFER_INITIATED, CancellationToken.None);

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

        var result = await compensator.CompensateAsync(Body("c-1"), Bank.Transfer.Api.Domain.Constants.ReceivedSubscriptionsConstants.TRANSFER_INITIATED, CancellationToken.None);

        Assert.True(result);
        Assert.Equal(1, sender.CountOf(Bank.Transfer.Api.Domain.Constants.SendSubscriptionConstants.TRANSFER_FAILED));
        Assert.Contains(logger.Entries, x => x.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task Other_subscription_neither_queries_nor_sends()
    {
        var sender = new FakeServiceBusSender();
        var logger = new CapturingLogger<FailureCompensator>();
        var compensator = Create(UNREACHABLE, sender, logger);

        var result = await compensator.CompensateAsync(Body("c-1"), "some-other-event", CancellationToken.None);

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
            Bank.Transfer.Api.Domain.Constants.ReceivedSubscriptionsConstants.TRANSFER_INITIATED, CancellationToken.None);

        Assert.False(result);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task Send_failure_propagates()
    {
        var harness = await TransferHarness.CreateAsync(_fixture);
        var sender = new FakeServiceBusSender();
        sender.FailNextSend();
        var compensator = Create(harness.ConnectionString, sender, new CapturingLogger<FailureCompensator>());

        await Assert.ThrowsAnyAsync<Exception>(() => compensator.CompensateAsync(
            Body(TransferHarness.NewCorrelationId()), Bank.Transfer.Api.Domain.Constants.ReceivedSubscriptionsConstants.TRANSFER_INITIATED, CancellationToken.None));
    }

    [Fact]
    public async Task Defective_message_for_a_finished_saga_emits_nothing()
    {
        // SC-008: el registro ya existe y llega una entrega con el cuerpo dañado pero el CorrelationId legible.
        var harness = await TransferHarness.CreateAsync(_fixture);
        var sender = new FakeServiceBusSender();
        var correlationId = await SeedFinishedAsync(harness);
        var compensator = Create(harness.ConnectionString, sender, new CapturingLogger<FailureCompensator>());

        var damaged = JsonSerializer.Serialize(new { CorrelationId = correlationId, CustomerId = "abc" });
        var result = await compensator.CompensateAsync(damaged, Bank.Transfer.Api.Domain.Constants.ReceivedSubscriptionsConstants.TRANSFER_INITIATED, CancellationToken.None);

        Assert.False(result);
        Assert.Empty(sender.Sent);
    }

    private static async Task<string> SeedRequestAsync(TransferHarness harness, string outcome)
    {
        var correlationId = TransferHarness.NewCorrelationId();
        await using var db = SqlServerFixture.NewContext(harness.ConnectionString);
        db.TransferRequest.Add(new Bank.Transfer.Api.Domain.Entities.TransferRequestEntity
        {
            CorrelationId = correlationId,
            Outcome = outcome,
            PayloadFingerprint = "fingerprint",
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        return correlationId;
    }

    private static Task<string> SeedFinishedAsync(TransferHarness harness) => SeedRequestAsync(harness, "Confirmed");
}
