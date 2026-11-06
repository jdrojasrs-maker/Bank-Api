using Bank.Transfer.Api.Domain.Constants;
using Bank.Transfer.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Bank.Transfer.Api.Tests.TransferInitiated;

// Historia 1, escenarios 1 y 4 (SC-001, SC-002): un mensaje repetido en serie no duplica la transferencia ni el resultado.
[Collection(SqlServerCollection.Name)]
public class DuplicateDeliveryTests(SqlServerFixture fixture)
{
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(10)]
    public async Task Same_message_delivered_many_times_registers_one_transfer_and_emits_one_result(int deliveries)
    {
        var harness = await TransferHarness.CreateAsync(fixture);
        var correlationId = TransferHarness.NewCorrelationId();
        var message = TransferHarness.Message(correlationId);

        for (var i = 0; i < deliveries; i++)
            await harness.DeliverAsync(message);

        Assert.Equal(1, await harness.CountTransfersAsync(correlationId));
        Assert.Equal(1, await harness.CountRequestsAsync(correlationId));
        Assert.Equal(1, harness.Sender.CountOf(SendSubscriptionConstants.TRANSFER_CONFIRMED));
        Assert.Equal(0, harness.Sender.CountOf(SendSubscriptionConstants.TRANSFER_FAILED));
    }

    [Fact]
    public async Task Duplicates_do_not_modify_the_original_record()
    {
        var harness = await TransferHarness.CreateAsync(fixture);
        var correlationId = TransferHarness.NewCorrelationId();
        var message = TransferHarness.Message(correlationId);

        await harness.DeliverAsync(message);
        await using var before = SqlServerFixture.NewContext(harness.ConnectionString);
        var original = await before.Transfer.AsNoTracking().SingleAsync(x => x.CorrelationId == correlationId);

        await harness.DeliverAsync(message);
        await harness.DeliverAsync(message);

        await using var after = SqlServerFixture.NewContext(harness.ConnectionString);
        var current = await after.Transfer.AsNoTracking().SingleAsync(x => x.CorrelationId == correlationId);
        Assert.Equal(original.Id, current.Id);
        Assert.Equal(original.TransferDate, current.TransferDate);
        Assert.Equal(original.Amount, current.Amount);
        Assert.Equal(original.CurrentState, current.CurrentState);
    }

    [Fact]
    public async Task Each_ignored_duplicate_leaves_a_trace_with_its_reason()
    {
        var harness = await TransferHarness.CreateAsync(fixture);
        var correlationId = TransferHarness.NewCorrelationId();
        var message = TransferHarness.Message(correlationId);

        await harness.DeliverAsync(message);
        await harness.DeliverAsync(message);
        await harness.DeliverAsync(message);

        Assert.Equal(2, harness.Logger.Reasons.Count(x => x == IgnoredMessageReasonConstants.DUPLICATE_ALREADY_EMITTED));
        Assert.All(
            harness.Logger.Entries.Where(x => x.Properties.ContainsKey("Reason")),
            entry => Assert.Equal(correlationId, entry.Properties["CorrelationId"]?.ToString()));
    }
}
