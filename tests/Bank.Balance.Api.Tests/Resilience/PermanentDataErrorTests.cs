using Bank.Balance.Api.Application.Features.Process;
using Bank.Balance.Api.Application.Resilience;
using Bank.Balance.Api.Domain.Constants;
using Bank.Balance.Api.Tests.Infrastructure;

namespace Bank.Balance.Api.Tests.Resilience;

// Historia 2: un error permanente de datos en Balance emite balance-failed y completa, sin reintento ni DLQ.
[Collection(SqlServerCollection.Name)]
public class PermanentDataErrorTests(SqlServerFixture fixture)
{
    [Fact]
    public async Task Constraint_violation_emits_balance_failed_and_completes_without_retry_or_dead_letter()
    {
        var harness = await BalanceHarness.CreateAsync(fixture);
        await harness.ExecuteSqlAsync("ALTER TABLE [Balance] WITH NOCHECK ADD CONSTRAINT CK_TEST_CUSTOMER CHECK (CustomerId <> 99)");   // SQL 547
        var sender = new FakeServiceBusSender();
        var processor = new ResilientMessageProcessor(async (body, type, _) =>
        {
            await using var db = SqlServerFixture.NewContext(harness.ConnectionString);
            await new ProcessService(db, sender, new CapturingLogger<ProcessService>()).Execute(body, type);
        }, new FakeFailureCompensator(), new CapturingLogger<ResilientMessageProcessor>(), new Random(7));
        var actions = new FakeMessageActions();
        var correlationId = BalanceHarness.NewCorrelationId();

        await processor.HandleAsync(
            new IncomingMessage(BalanceHarness.EventMessage(correlationId, customerId: 99), ReceivedSubscriptionsConstants.BALANCE_INITIATED, "m-1", 0),
            actions, CancellationToken.None);

        Assert.Equal(1, sender.CountOf(SendSubscriptionConstants.BALANCE_FAILED));
        Assert.Equal(new[] { "Complete" }, actions.Calls);
    }
}
