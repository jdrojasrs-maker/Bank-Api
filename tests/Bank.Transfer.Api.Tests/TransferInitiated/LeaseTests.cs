using Bank.Transfer.Api.Domain.Constants;
using Bank.Transfer.Api.Tests.Infrastructure;

namespace Bank.Transfer.Api.Tests.TransferInitiated;

// Historia 3: recuperación tras una caída del procesador (research.md R3). Se simula con una solicitud registrada, sin emitir,
// cuyo claim se tomó hace más (o menos) que el lease de EMISSION_LEASE_SECONDS.
[Collection(SqlServerCollection.Name)]
public class LeaseTests(SqlServerFixture fixture)
{
    private const int ExpiredSeconds = EmissionConstants.EMISSION_LEASE_SECONDS + 1;
    private const int ActiveSeconds = 5;

    [Fact]
    public async Task Expired_lease_lets_a_delivery_recover_the_stored_confirmed_outcome()
    {
        var harness = await TransferHarness.CreateAsync(fixture);
        var correlationId = TransferHarness.NewCorrelationId();
        var message = TransferHarness.Message(correlationId);
        await harness.DeliverAsync(message);
        await harness.BackdateClaimAsync(correlationId, ExpiredSeconds);
        // Un CHECK que ahora rechazaría la transferencia: la recuperación debe usar el resultado guardado, no recalcularlo.
        await harness.AddAmountCheckAsync(1000m);
        var before = harness.Sender.Sent.Count;

        await harness.DeliverAsync(message);

        Assert.Equal(before + 1, harness.Sender.Sent.Count);
        Assert.Equal(2, harness.Sender.CountOf(SendSubscriptionConstants.TRANSFER_CONFIRMED));
        Assert.Equal(0, harness.Sender.CountOf(SendSubscriptionConstants.TRANSFER_FAILED));
        Assert.Equal(1, await harness.CountTransfersAsync(correlationId));
        Assert.Equal(1, await harness.ScalarAsync<int>(
            $"SELECT COUNT(*) FROM TransferRequest WHERE CorrelationId = '{correlationId}' AND EmittedAt IS NOT NULL"));
        Assert.Contains(IgnoredMessageReasonConstants.RECOVERED_EMISSION, harness.Logger.Reasons);
    }

    [Fact]
    public async Task Expired_lease_lets_a_delivery_recover_the_stored_failed_outcome()
    {
        var harness = await TransferHarness.CreateAsync(fixture);
        await harness.AddAmountCheckAsync(20000m);
        var correlationId = TransferHarness.NewCorrelationId();
        var message = TransferHarness.Message(correlationId, amount: 25000m);
        await harness.DeliverAsync(message);
        await harness.BackdateClaimAsync(correlationId, ExpiredSeconds);

        await harness.DeliverAsync(message);

        Assert.Equal(2, harness.Sender.CountOf(SendSubscriptionConstants.TRANSFER_FAILED));
        Assert.Equal(0, harness.Sender.CountOf(SendSubscriptionConstants.TRANSFER_CONFIRMED));
        Assert.Equal(0, await harness.CountTransfersAsync(correlationId));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    public async Task Simultaneous_deliveries_after_the_lease_expired_emit_only_once(int copies)
    {
        var harness = await TransferHarness.CreateAsync(fixture);

        for (var i = 0; i < 10; i++)
        {
            var correlationId = TransferHarness.NewCorrelationId();
            var message = TransferHarness.Message(correlationId);
            await harness.DeliverAsync(message);
            await harness.BackdateClaimAsync(correlationId, ExpiredSeconds);
            var before = harness.Sender.Sent.Count(x => x.Json.Contains(correlationId));

            await Task.WhenAll(Enumerable.Range(0, copies).Select(_ => Task.Run(() => harness.DeliverAsync(message))));

            Assert.Equal(before + 1, harness.Sender.Sent.Count(x => x.Json.Contains(correlationId)));
            Assert.Equal(1, await harness.CountTransfersAsync(correlationId));
        }
    }

    [Fact]
    public async Task Active_lease_makes_a_delivery_ignore_the_message_as_in_progress()
    {
        var harness = await TransferHarness.CreateAsync(fixture);
        var correlationId = TransferHarness.NewCorrelationId();
        var message = TransferHarness.Message(correlationId);
        await harness.DeliverAsync(message);
        await harness.BackdateClaimAsync(correlationId, ActiveSeconds);
        var before = harness.Sender.Sent.Count;

        await harness.DeliverAsync(message);

        Assert.Equal(before, harness.Sender.Sent.Count);
        Assert.Contains(IgnoredMessageReasonConstants.DUPLICATE_IN_PROGRESS, harness.Logger.Reasons);
        Assert.DoesNotContain(IgnoredMessageReasonConstants.RECOVERED_EMISSION, harness.Logger.Reasons);
    }
}
