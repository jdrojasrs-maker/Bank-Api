using Bank.Transfer.Api.Domain.Constants;
using Bank.Transfer.Api.Tests.Infrastructure;

namespace Bank.Transfer.Api.Tests.TransferInitiated;

// Historia 1, escenario 2: copias simultáneas del mismo mensaje registran una sola transferencia y emiten un solo resultado.
[Collection(SqlServerCollection.Name)]
public class ConcurrentDeliveryTests(SqlServerFixture fixture)
{
    private const int Iterations = 20;

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(10)]
    public async Task Parallel_copies_register_one_transfer_and_emit_one_result(int copies)
    {
        var harness = await TransferHarness.CreateAsync(fixture);

        for (var i = 0; i < Iterations; i++)
        {
            var correlationId = TransferHarness.NewCorrelationId();
            var message = TransferHarness.Message(correlationId);

            await Task.WhenAll(Enumerable.Range(0, copies).Select(_ => Task.Run(() => harness.DeliverAsync(message))));

            Assert.Equal(1, await harness.CountTransfersAsync(correlationId));
            Assert.Equal(1, await harness.CountRequestsAsync(correlationId));
            var resultsForThisTransfer = harness.Sender.Sent.Count(x =>
                x.Name == SendSubscriptionConstants.TRANSFER_CONFIRMED && x.Json.Contains(correlationId));
            Assert.Equal(1, resultsForThisTransfer);
        }

        Assert.Equal(0, harness.Sender.CountOf(SendSubscriptionConstants.TRANSFER_FAILED));
    }
}
