using Bank.Transaction.Api.Domain.Constants;
using Bank.Transaction.Api.Tests.Infrastructure;

namespace Bank.Transaction.Api.Tests.StateProtection;

// Solo transaction-initiated puede crear la fila (research.md R4); los mensajes inválidos no crean ni cambian nada (FR-010).
[Collection(SqlServerCollection.Name)]
public class OrphanAndInvalidMessageTests(SqlServerFixture fixture)
{
    public static TheoryData<string> NonInitiatingEvents() => new(
        ReceivedSubscriptionsConstants.BALANCE_CONFIRMED,
        ReceivedSubscriptionsConstants.BALANCE_FAILED,
        ReceivedSubscriptionsConstants.TRANSFER_FAILED,
        ReceivedSubscriptionsConstants.TRANSFER_CONFIRMED);

    [Theory]
    [MemberData(nameof(NonInitiatingEvents))]
    public async Task Event_for_a_missing_transaction_is_ignored_and_creates_nothing(string subscription)
    {
        var harness = await TransactionHarness.CreateAsync(fixture);
        var correlationId = TransactionHarness.NewCorrelationId();

        await harness.DeliverAsync(subscription, TransactionHarness.EventMessage(correlationId));

        Assert.Equal(0, await harness.CountTransactionsAsync());
        Assert.Empty(harness.Sender.Sent);
        Assert.Contains(IgnoredMessageReasonConstants.NO_SUCH_TRANSACTION, harness.Logger.Reasons);
    }

    public static TheoryData<string, string> InvalidMessages()
    {
        var data = new TheoryData<string, string>();
        var invalidBodies = new[]
        {
            TransactionHarness.EventMessage(null),
            TransactionHarness.EventMessage(""),
            TransactionHarness.EventMessage("   "),
            TransactionHarness.EventMessage(new string('a', 65)),
            "esto no es json",
            "null"
        };
        foreach (var subscription in TransactionHarness.AllEvents)
            foreach (var body in invalidBodies)
                data.Add(subscription, body);
        return data;
    }

    [Theory]
    [MemberData(nameof(InvalidMessages))]
    public async Task Invalid_message_changes_nothing_emits_nothing_and_is_recorded(string subscription, string body)
    {
        var harness = await TransactionHarness.CreateAsync(fixture);
        var existing = await harness.SeedTransactionAsync(CurrentStateConstants.PENDING);

        await harness.DeliverAsync(subscription, body);   // no debe lanzar: el mensaje se completa

        Assert.Equal(1, await harness.CountTransactionsAsync());
        Assert.Equal(CurrentStateConstants.PENDING, await harness.StateOfAsync(existing));
        Assert.Empty(harness.Sender.Sent);
        Assert.Contains(IgnoredMessageReasonConstants.INVALID_MESSAGE, harness.Logger.Reasons);
    }
}
