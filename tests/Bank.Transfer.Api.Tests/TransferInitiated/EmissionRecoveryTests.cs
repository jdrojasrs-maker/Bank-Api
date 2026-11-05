using Bank.Transfer.Api.Domain.Constants;
using Bank.Transfer.Api.Tests.Infrastructure;

namespace Bank.Transfer.Api.Tests.TransferInitiated;

// Historia 3 (SC-004): si el envío del resultado falla tras registrar, la reentrega lo emite sin duplicar la transferencia.
[Collection(SqlServerCollection.Name)]
public class EmissionRecoveryTests(SqlServerFixture fixture)
{
    [Fact]
    public async Task Failed_send_releases_the_claim_and_the_redelivery_emits_the_result_once()
    {
        var harness = await TransferHarness.CreateAsync(fixture);
        var correlationId = TransferHarness.NewCorrelationId();
        var message = TransferHarness.Message(correlationId);

        harness.Sender.FailNextSend();
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.DeliverAsync(message));

        // Quedó registrada, sin emitir y con la emisión liberada para que la reentrega pueda tomarla.
        Assert.Equal(1, await harness.CountTransfersAsync(correlationId));
        Assert.Empty(harness.Sender.Sent);
        Assert.Equal(1, await harness.ScalarAsync<int>(
            $"SELECT COUNT(*) FROM TransferRequest WHERE CorrelationId = '{correlationId}' AND ClaimedAt IS NULL AND EmittedAt IS NULL"));

        await harness.DeliverAsync(message);

        Assert.Equal(1, await harness.CountTransfersAsync(correlationId));
        Assert.Equal(1, harness.Sender.CountOf(SendSubscriptionConstants.TRANSFER_CONFIRMED));
        Assert.Equal(1, await harness.ScalarAsync<int>(
            $"SELECT COUNT(*) FROM TransferRequest WHERE CorrelationId = '{correlationId}' AND EmittedAt IS NOT NULL"));
    }

    [Fact]
    public async Task Once_emitted_a_later_delivery_is_ignored()
    {
        var harness = await TransferHarness.CreateAsync(fixture);
        var correlationId = TransferHarness.NewCorrelationId();
        var message = TransferHarness.Message(correlationId);

        harness.Sender.FailNextSend();
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.DeliverAsync(message));
        await harness.DeliverAsync(message);
        await harness.DeliverAsync(message);

        Assert.Equal(1, harness.Sender.CountOf(SendSubscriptionConstants.TRANSFER_CONFIRMED));
        Assert.Contains(IgnoredMessageReasonConstants.DUPLICATE_ALREADY_EMITTED, harness.Logger.Reasons);
    }
}
