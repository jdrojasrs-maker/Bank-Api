using Bank.Transaction.Api.Domain.Constants;
using Bank.Transaction.Api.Tests.Infrastructure;

namespace Bank.Transaction.Api.Tests.StateProtection;

// Orden "emitir y luego confirmar" (research.md R4): un fallo de envío no deja el estado final con los eventos sin salir.
[Collection(SqlServerCollection.Name)]
public class EmissionFailureTests(SqlServerFixture fixture)
{
    [Fact]
    public async Task Failed_send_keeps_pending_and_the_redelivery_completes_and_emits()
    {
        var harness = await TransactionHarness.CreateAsync(fixture);
        var correlationId = await harness.SeedTransactionAsync(CurrentStateConstants.PENDING);
        var message = TransactionHarness.EventMessage(correlationId);

        harness.Sender.FailNextSend();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.DeliverAsync(ReceivedSubscriptionsConstants.TRANSFER_CONFIRMED, message));

        Assert.Equal(CurrentStateConstants.PENDING, await harness.StateOfAsync(correlationId));
        Assert.Empty(harness.Sender.Sent);

        await harness.DeliverAsync(ReceivedSubscriptionsConstants.TRANSFER_CONFIRMED, message);

        Assert.Equal(CurrentStateConstants.COMPLETED, await harness.StateOfAsync(correlationId));
        Assert.Equal(1, harness.Sender.CountOf(SendSubscriptionConstants.TRANSACTION_COMPLETED));
        Assert.Equal(1, harness.Sender.CountOf(SendSubscriptionConstants.TRANSFER_CONFIRMED_BALANCE));
    }

    [Fact]
    public async Task Once_applied_a_replay_is_ignored_without_new_events()
    {
        var harness = await TransactionHarness.CreateAsync(fixture);
        var correlationId = await harness.SeedTransactionAsync(CurrentStateConstants.PENDING);
        var message = TransactionHarness.EventMessage(correlationId);

        await harness.DeliverAsync(ReceivedSubscriptionsConstants.TRANSFER_CONFIRMED, message);
        var emittedAfterFirst = harness.Sender.Sent.Count;

        await harness.DeliverAsync(ReceivedSubscriptionsConstants.TRANSFER_CONFIRMED, message);

        Assert.Equal(emittedAfterFirst, harness.Sender.Sent.Count);
        Assert.Equal(CurrentStateConstants.COMPLETED, await harness.StateOfAsync(correlationId));
        Assert.Contains(IgnoredMessageReasonConstants.FINAL_STATE_PROTECTED, harness.Logger.Reasons);
    }
}
