using Bank.Transaction.Api.Domain.Constants;
using Bank.Transaction.Api.Tests.Infrastructure;

namespace Bank.Transaction.Api.Tests.StateProtection;

// Historia 2, escenarios 1 a 3 (SC-003): Completed y Canceled son definitivos para cualquier mensaje duplicado o tardío.
[Collection(SqlServerCollection.Name)]
public class FinalStateProtectionTests(SqlServerFixture fixture)
{
    public static TheoryData<string, string> FinalStateAndMessage()
    {
        var data = new TheoryData<string, string>();
        foreach (var state in new[] { CurrentStateConstants.COMPLETED, CurrentStateConstants.CANCELED })
            foreach (var subscription in TransactionHarness.AllEvents)
                data.Add(state, subscription);
        return data;
    }

    [Theory]
    [MemberData(nameof(FinalStateAndMessage))]
    public async Task Final_state_never_changes_and_nothing_is_emitted(string finalState, string subscription)
    {
        var harness = await TransactionHarness.CreateAsync(fixture);
        var correlationId = await harness.SeedTransactionAsync(finalState);

        await harness.DeliverAsync(subscription, TransactionHarness.MessageFor(subscription, correlationId));

        Assert.Equal(finalState, await harness.StateOfAsync(correlationId));
        Assert.Equal(1, await harness.CountTransactionsAsync(correlationId));
        Assert.Empty(harness.Sender.Sent);
        Assert.Contains(IgnoredMessageReasonConstants.FINAL_STATE_PROTECTED, harness.Logger.Reasons);
    }

    [Theory]
    [MemberData(nameof(FinalStateAndMessage))]
    public async Task Ignored_message_is_recorded_with_its_correlation_id_and_type(string finalState, string subscription)
    {
        var harness = await TransactionHarness.CreateAsync(fixture);
        var correlationId = await harness.SeedTransactionAsync(finalState);

        await harness.DeliverAsync(subscription, TransactionHarness.MessageFor(subscription, correlationId));

        var entry = Assert.Single(harness.Logger.Entries, x =>
            x.Properties.TryGetValue("Reason", out var reason)
            && reason?.ToString() == IgnoredMessageReasonConstants.FINAL_STATE_PROTECTED);
        Assert.Equal(correlationId, entry.Properties["CorrelationId"]?.ToString());
        Assert.Equal(subscription, entry.Properties["MessageType"]?.ToString());
    }

    [Fact]
    public async Task Late_failure_cannot_cancel_a_completed_transaction()
    {
        var harness = await TransactionHarness.CreateAsync(fixture);
        var correlationId = await harness.SeedTransactionAsync(CurrentStateConstants.COMPLETED);

        await harness.DeliverAsync(
            ReceivedSubscriptionsConstants.TRANSFER_FAILED,
            TransactionHarness.EventMessage(correlationId));

        Assert.Equal(CurrentStateConstants.COMPLETED, await harness.StateOfAsync(correlationId));
        Assert.Empty(harness.Sender.Sent);
    }
}
