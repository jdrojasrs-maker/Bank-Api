using Bank.Transfer.Api.Domain.Constants;
using Bank.Transfer.Api.Tests.Infrastructure;

namespace Bank.Transfer.Api.Tests.TransferInitiated;

// Historia 1, escenario 3: un rechazo repetido no emite un segundo fallo ni registra ninguna transferencia.
[Collection(SqlServerCollection.Name)]
public class RejectedTransferTests(SqlServerFixture fixture)
{
    [Fact]
    public async Task Rejected_transfer_emits_one_failure_and_repeats_do_not_emit_another()
    {
        var harness = await TransferHarness.CreateAsync(fixture);
        await harness.AddAmountCheckAsync(20000m);   // el INSERT de un monto >= 20000 viola el CHECK (SQL 547)
        var correlationId = TransferHarness.NewCorrelationId();
        var message = TransferHarness.Message(correlationId, amount: 25000m);

        await harness.DeliverAsync(message);

        Assert.Equal(1, harness.Sender.CountOf(SendSubscriptionConstants.TRANSFER_FAILED));
        Assert.Equal(0, harness.Sender.CountOf(SendSubscriptionConstants.TRANSFER_CONFIRMED));
        Assert.Equal(0, await harness.CountTransfersAsync(correlationId));
        Assert.Equal(1, await harness.CountRequestsAsync(correlationId));
        Assert.Equal(
            TransferOutcomeConstants.FAILED,
            await harness.ScalarAsync<string>($"SELECT Outcome FROM TransferRequest WHERE CorrelationId = '{correlationId}'"));

        await harness.DeliverAsync(message);
        await harness.DeliverAsync(message);
        await harness.DeliverAsync(message);

        Assert.Equal(1, harness.Sender.CountOf(SendSubscriptionConstants.TRANSFER_FAILED));
        Assert.Equal(0, harness.Sender.CountOf(SendSubscriptionConstants.TRANSFER_CONFIRMED));
        Assert.Equal(0, await harness.CountTransfersAsync(correlationId));
        Assert.Equal(1, await harness.CountRequestsAsync(correlationId));
    }

    [Fact]
    public async Task Valid_amount_on_the_same_database_still_confirms()
    {
        var harness = await TransferHarness.CreateAsync(fixture);
        await harness.AddAmountCheckAsync(20000m);
        var correlationId = TransferHarness.NewCorrelationId();

        await harness.DeliverAsync(TransferHarness.Message(correlationId, amount: 1500m));

        Assert.Equal(1, harness.Sender.CountOf(SendSubscriptionConstants.TRANSFER_CONFIRMED));
        Assert.Equal(1, await harness.CountTransfersAsync(correlationId));
    }
}
