using System.Text.Json;
using Bank.Transaction.Api.Domain.Constants;
using Bank.Transaction.Api.Tests.Infrastructure;

namespace Bank.Transaction.Api.Tests.StateProtection;

// Historia 2, escenario 4: las transiciones válidas desde Pending siguen funcionando, con los eventos del contrato.
[Collection(SqlServerCollection.Name)]
public class ValidTransitionTests(SqlServerFixture fixture)
{
    private static string[] EventNames(TransactionHarness harness) => harness.Sender.Sent.Select(x => x.Name).ToArray();

    [Fact]
    public async Task First_initiated_message_creates_a_pending_transaction_and_starts_the_balance_step()
    {
        var harness = await TransactionHarness.CreateAsync(fixture);
        var correlationId = TransactionHarness.NewCorrelationId();

        await harness.DeliverAsync(
            ReceivedSubscriptionsConstants.TRANSACTION_INITIATED,
            TransactionHarness.InitiatedMessage(correlationId));

        Assert.Equal(CurrentStateConstants.PENDING, await harness.StateOfAsync(correlationId));
        Assert.Equal(new[] { SendSubscriptionConstants.BALANCE_INITIATED }, EventNames(harness));
    }

    [Fact]
    public async Task Repeated_initiated_message_while_pending_keeps_the_state_and_emits_again_for_recovery()
    {
        var harness = await TransactionHarness.CreateAsync(fixture);
        var correlationId = TransactionHarness.NewCorrelationId();
        var message = TransactionHarness.InitiatedMessage(correlationId);

        await harness.DeliverAsync(ReceivedSubscriptionsConstants.TRANSACTION_INITIATED, message);
        await harness.DeliverAsync(ReceivedSubscriptionsConstants.TRANSACTION_INITIATED, message);

        Assert.Equal(1, await harness.CountTransactionsAsync(correlationId));
        Assert.Equal(CurrentStateConstants.PENDING, await harness.StateOfAsync(correlationId));
        Assert.Equal(2, harness.Sender.CountOf(SendSubscriptionConstants.BALANCE_INITIATED));
    }

    [Fact]
    public async Task Balance_confirmed_keeps_pending_and_starts_the_transfer_with_the_stored_data()
    {
        var harness = await TransactionHarness.CreateAsync(fixture);
        var correlationId = await harness.SeedTransactionAsync(CurrentStateConstants.PENDING);

        await harness.DeliverAsync(
            ReceivedSubscriptionsConstants.BALANCE_CONFIRMED,
            TransactionHarness.EventMessage(correlationId));

        Assert.Equal(CurrentStateConstants.PENDING, await harness.StateOfAsync(correlationId));
        var sent = Assert.Single(harness.Sender.Sent);
        Assert.Equal(SendSubscriptionConstants.TRANSFER_INITIATED, sent.Name);
        var payload = JsonDocument.Parse(sent.Json).RootElement;
        Assert.Equal(correlationId, payload.GetProperty("CorrelationId").GetString());
        Assert.Equal(1500m, payload.GetProperty("Amount").GetDecimal());
        Assert.Equal("111111111111111", payload.GetProperty("SourceAccount").GetString());
        Assert.Equal("222222222222222", payload.GetProperty("DestinationAccount").GetString());
        Assert.Equal(7, payload.GetProperty("CustomerId").GetInt32());
    }

    [Fact]
    public async Task Balance_failed_cancels_the_transaction()
    {
        var harness = await TransactionHarness.CreateAsync(fixture);
        var correlationId = await harness.SeedTransactionAsync(CurrentStateConstants.PENDING);

        await harness.DeliverAsync(
            ReceivedSubscriptionsConstants.BALANCE_FAILED,
            TransactionHarness.EventMessage(correlationId));

        Assert.Equal(CurrentStateConstants.CANCELED, await harness.StateOfAsync(correlationId));
        Assert.Equal(new[] { SendSubscriptionConstants.TRANSACTION_FAILED }, EventNames(harness));
    }

    [Fact]
    public async Task Transfer_failed_cancels_the_transaction_and_releases_the_balance()
    {
        var harness = await TransactionHarness.CreateAsync(fixture);
        var correlationId = await harness.SeedTransactionAsync(CurrentStateConstants.PENDING);

        await harness.DeliverAsync(
            ReceivedSubscriptionsConstants.TRANSFER_FAILED,
            TransactionHarness.EventMessage(correlationId));

        Assert.Equal(CurrentStateConstants.CANCELED, await harness.StateOfAsync(correlationId));
        Assert.Equal(
            new[] { SendSubscriptionConstants.TRANSACTION_FAILED, SendSubscriptionConstants.TRANSFER_FAILED_BALANCE },
            EventNames(harness));
    }

    [Fact]
    public async Task Transfer_confirmed_completes_the_transaction_and_confirms_the_balance()
    {
        var harness = await TransactionHarness.CreateAsync(fixture);
        var correlationId = await harness.SeedTransactionAsync(CurrentStateConstants.PENDING);

        await harness.DeliverAsync(
            ReceivedSubscriptionsConstants.TRANSFER_CONFIRMED,
            TransactionHarness.EventMessage(correlationId));

        Assert.Equal(CurrentStateConstants.COMPLETED, await harness.StateOfAsync(correlationId));
        Assert.Equal(
            new[] { SendSubscriptionConstants.TRANSACTION_COMPLETED, SendSubscriptionConstants.TRANSFER_CONFIRMED_BALANCE },
            EventNames(harness));
    }
}
