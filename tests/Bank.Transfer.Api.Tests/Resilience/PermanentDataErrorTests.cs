using Bank.Transfer.Api.Application.Resilience;
using Bank.Transfer.Api.Domain.Constants;
using Bank.Transfer.Api.Tests.Infrastructure;

namespace Bank.Transfer.Api.Tests.Resilience;

// Historia 2: un error permanente de datos se resuelve dentro de ProcessService y nunca llega al reintento ni a la DLQ.
[Collection(SqlServerCollection.Name)]
public class PermanentDataErrorTests(SqlServerFixture fixture)
{
    private static ResilientMessageProcessor Create(TransferHarness harness) =>
        new((body, _, _) => harness.DeliverAsync(body), new FakeFailureCompensator(),
            new CapturingLogger<ResilientMessageProcessor>(), new Random(7));

    [Fact]
    public async Task Check_constraint_violation_emits_failure_and_completes_without_retry_or_dead_letter()
    {
        var harness = await TransferHarness.CreateAsync(fixture);
        await harness.AddAmountCheckAsync(20000m);   // SQL 547
        var message = TransferHarness.Message(TransferHarness.NewCorrelationId(), amount: 25000m);
        var actions = new FakeMessageActions();

        await Create(harness).HandleAsync(new IncomingMessage(message, "transfer-initiated", "m-1", 0), actions, CancellationToken.None);

        Assert.Equal(1, harness.Sender.CountOf(SendSubscriptionConstants.TRANSFER_FAILED));
        Assert.Equal(new[] { "Complete" }, actions.Calls);
    }

    [Fact]
    public async Task Same_message_delivered_twice_completes_both_times_without_retry_or_dead_letter()
    {
        var harness = await TransferHarness.CreateAsync(fixture);
        var message = TransferHarness.Message(TransferHarness.NewCorrelationId());
        var processor = Create(harness);
        var first = new FakeMessageActions();
        var second = new FakeMessageActions();

        await processor.HandleAsync(new IncomingMessage(message, "transfer-initiated", "m-1", 0), first, CancellationToken.None);
        await processor.HandleAsync(new IncomingMessage(message, "transfer-initiated", "m-1", 0), second, CancellationToken.None);

        Assert.Equal(new[] { "Complete" }, first.Calls);
        Assert.Equal(new[] { "Complete" }, second.Calls);
        Assert.Equal(1, await harness.CountTransfersAsync());
    }
}
