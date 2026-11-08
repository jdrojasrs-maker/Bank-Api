using Bank.Transaction.Api.Application.Resilience;
using Bank.Transaction.Api.Tests.Infrastructure;

namespace Bank.Transaction.Api.Tests.Resilience;

public class TransientRetryTests
{
    private const string VALID = """{"CorrelationId":"c-1","CustomerId":7}""";

    private static ResilientMessageProcessor Create(Func<string, string, CancellationToken, Task> process) =>
        new(process, new FakeFailureCompensator(), new CapturingLogger<ResilientMessageProcessor>(), new Random(7));

    private static Task Timeout(string _, string __, CancellationToken ___) => throw new TimeoutException("db caída");

    [Fact]
    public async Task Transient_failure_schedules_retry_one_then_completes_in_that_order()
    {
        var actions = new FakeMessageActions();

        await Create(Timeout).HandleAsync(new IncomingMessage(VALID, "some-event", "m-1", 0), actions, CancellationToken.None);

        Assert.Equal(new[] { "ScheduleRetry:1", "Complete" }, actions.Calls);
        Assert.Equal(1, actions.NextAttempt);
        Assert.InRange(actions.Delay!.Value.TotalSeconds, 5 * 0.8, 5 * 1.2);
    }

    [Fact]
    public async Task Third_retry_schedules_attempt_four_with_its_range()
    {
        var actions = new FakeMessageActions();

        await Create(Timeout).HandleAsync(new IncomingMessage(VALID, "some-event", "m-1", 3), actions, CancellationToken.None);

        Assert.Equal(new[] { "ScheduleRetry:4", "Complete" }, actions.Calls);
        Assert.InRange(actions.Delay!.Value.TotalSeconds, 135 * 0.8, 135 * 1.2);
    }

    [Fact]
    public async Task Schedule_failure_propagates_and_does_not_complete()
    {
        var actions = new FakeMessageActions();
        actions.FailOn("ScheduleRetry", new InvalidOperationException("broker caído"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Create(Timeout).HandleAsync(new IncomingMessage(VALID, "some-event", "m-1", 0), actions, CancellationToken.None));

        Assert.Equal(new[] { "ScheduleRetry:1" }, actions.Calls);
    }

    [Fact]
    public async Task Cancellation_during_shutdown_neither_schedules_nor_completes()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var actions = new FakeMessageActions();
        var processor = Create((_, _, _) => throw new OperationCanceledException(cts.Token));

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            processor.HandleAsync(new IncomingMessage(VALID, "some-event", "m-1", 0), actions, cts.Token));

        Assert.Empty(actions.Calls);
    }

    [Fact]
    public async Task Transient_failure_never_dead_letters_while_retries_remain()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var actions = new FakeMessageActions();

            await Create(Timeout).HandleAsync(new IncomingMessage(VALID, "some-event", "m-1", attempt), actions, CancellationToken.None);

            Assert.DoesNotContain(actions.Calls, c => c.StartsWith("DeadLetter"));
        }
    }
}
