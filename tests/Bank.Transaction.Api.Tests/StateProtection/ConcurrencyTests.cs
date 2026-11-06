using Bank.Transaction.Api.Domain.Constants;
using Bank.Transaction.Api.Tests.Infrastructure;

namespace Bank.Transaction.Api.Tests.StateProtection;

// Entregas simultáneas: la garantía es sobre el estado y la unicidad de la fila. La emisión duplicada en esa ventana de
// milisegundos es un riesgo aceptado (research.md R4), por eso aquí no se afirma sobre la cantidad de eventos.
[Collection(SqlServerCollection.Name)]
public class ConcurrencyTests(SqlServerFixture fixture)
{
    private const int Iterations = 20;

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(10)]
    public async Task Parallel_initiated_messages_create_a_single_pending_transaction(int copies)
    {
        var harness = await TransactionHarness.CreateAsync(fixture);

        for (var i = 0; i < Iterations; i++)
        {
            var correlationId = TransactionHarness.NewCorrelationId();
            var message = TransactionHarness.InitiatedMessage(correlationId);

            await Task.WhenAll(Enumerable.Range(0, copies).Select(_ =>
                Task.Run(() => harness.DeliverAsync(ReceivedSubscriptionsConstants.TRANSACTION_INITIATED, message))));

            Assert.Equal(1, await harness.CountTransactionsAsync(correlationId));
            Assert.Equal(CurrentStateConstants.PENDING, await harness.StateOfAsync(correlationId));
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    public async Task Parallel_duplicates_of_transfer_confirmed_complete_the_transaction(int copies)
    {
        var harness = await TransactionHarness.CreateAsync(fixture);

        for (var i = 0; i < Iterations; i++)
        {
            var correlationId = await harness.SeedTransactionAsync(CurrentStateConstants.PENDING);
            var message = TransactionHarness.EventMessage(correlationId);

            await Task.WhenAll(Enumerable.Range(0, copies).Select(_ =>
                Task.Run(() => harness.DeliverAsync(ReceivedSubscriptionsConstants.TRANSFER_CONFIRMED, message))));

            Assert.Equal(CurrentStateConstants.COMPLETED, await harness.StateOfAsync(correlationId));
        }
    }

    [Fact]
    public async Task Conflicting_messages_at_the_same_time_leave_exactly_one_final_state()
    {
        var harness = await TransactionHarness.CreateAsync(fixture);

        for (var i = 0; i < Iterations; i++)
        {
            var correlationId = await harness.SeedTransactionAsync(CurrentStateConstants.PENDING);
            var message = TransactionHarness.EventMessage(correlationId);

            await Task.WhenAll(
                Task.Run(() => harness.DeliverAsync(ReceivedSubscriptionsConstants.TRANSFER_CONFIRMED, message)),
                Task.Run(() => harness.DeliverAsync(ReceivedSubscriptionsConstants.TRANSFER_FAILED, message)));

            var state = await harness.StateOfAsync(correlationId);
            Assert.Contains(state, new[] { CurrentStateConstants.COMPLETED, CurrentStateConstants.CANCELED });
        }
    }
}
