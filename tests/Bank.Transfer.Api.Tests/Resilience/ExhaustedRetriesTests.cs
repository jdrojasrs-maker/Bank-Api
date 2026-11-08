using Bank.Transfer.Api.Application.Resilience;
using Bank.Transfer.Api.Tests.Infrastructure;

namespace Bank.Transfer.Api.Tests.Resilience;

// Historia 3: al agotarse los reintentos se compensa (si el paso lo tiene) y el mensaje se aparta explicado.
public class ExhaustedRetriesTests
{
    private const string VALID = """{"CorrelationId":"c-1","CustomerId":7}""";

    private static (ResilientMessageProcessor Processor, FakeFailureCompensator Compensator) Create(
        Func<string, string, CancellationToken, Task>? process = null)
    {
        var compensator = new FakeFailureCompensator();
        var processor = new ResilientMessageProcessor(
            process ?? ((_, _, _) => throw new TimeoutException("db caída")),
            compensator, new CapturingLogger<ResilientMessageProcessor>(), new Random(7));
        return (processor, compensator);
    }

    private static IncomingMessage Exhausted(string body = VALID, string type = "some-event") => new(body, type, "m-1", 5);

    [Fact]
    public async Task Exhausted_retries_compensate_once_and_dead_letter_with_retries_exhausted()
    {
        var (processor, compensator) = Create();
        var actions = new FakeMessageActions();

        await processor.HandleAsync(Exhausted(), actions, CancellationToken.None);

        Assert.Single(compensator.Calls);
        Assert.Equal(new[] { "DeadLetter:RetriesExhausted" }, actions.Calls);
        Assert.Equal(6, actions.Attempts);
        Assert.Equal("c-1", actions.CorrelationId);
        Assert.Contains("6", actions.Detail);
        Assert.Contains("TimeoutException", actions.Detail);
    }

    [Fact]
    public async Task Detail_never_exceeds_the_maximum_length()
    {
        var (processor, _) = Create((_, _, _) => throw new TimeoutException(new string('x', 5000)));
        var actions = new FakeMessageActions();

        await processor.HandleAsync(Exhausted(), actions, CancellationToken.None);

        Assert.True(actions.Detail!.Length <= 1024, $"detalle de {actions.Detail.Length} caracteres");
        Assert.Contains("TimeoutException", actions.Detail);
    }

    [Fact]
    public async Task Step_without_failure_event_only_dead_letters()
    {
        var (processor, compensator) = Create();
        compensator.Result = false;
        var actions = new FakeMessageActions();

        await processor.HandleAsync(Exhausted(), actions, CancellationToken.None);

        Assert.Single(compensator.Calls);
        Assert.Equal(new[] { "DeadLetter:RetriesExhausted" }, actions.Calls);
    }

    [Fact]
    public async Task Exhausted_retries_never_schedule_or_complete()
    {
        var (processor, _) = Create();
        var actions = new FakeMessageActions();

        await processor.HandleAsync(Exhausted(), actions, CancellationToken.None);

        Assert.DoesNotContain(actions.Calls, c => c.StartsWith("ScheduleRetry") || c == "Complete");
    }

    [Fact]
    public async Task Compensator_failure_dead_letters_as_compensation_failed_with_both_errors()
    {
        var (processor, compensator) = Create();
        compensator.Throw = new InvalidOperationException("bus caído");
        var actions = new FakeMessageActions();

        await processor.HandleAsync(Exhausted(), actions, CancellationToken.None);

        Assert.Equal(new[] { "DeadLetter:CompensationFailed" }, actions.Calls);
        Assert.Contains("bus caído", actions.Detail);
        Assert.Contains("TimeoutException", actions.Detail);
        Assert.Equal(6, actions.Attempts);
    }

    [Fact]
    public async Task Compensator_failure_on_a_poison_message_dead_letters_as_compensation_failed()
    {
        var (processor, compensator) = Create();
        compensator.Throw = new InvalidOperationException("bus caído");
        var actions = new FakeMessageActions();

        await processor.HandleAsync(
            new IncomingMessage("""{"CorrelationId":"c-1","CustomerId":"abc"}""", "some-event", "m-1", 0),
            actions, CancellationToken.None);

        Assert.Equal(new[] { "DeadLetter:CompensationFailed" }, actions.Calls);
        Assert.Contains("bus caído", actions.Detail);
        Assert.Equal(1, actions.Attempts);
    }

    [Fact]
    public async Task Cancellation_while_exhausted_neither_compensates_nor_dead_letters()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var (processor, compensator) = Create((_, _, _) => throw new OperationCanceledException(cts.Token));
        var actions = new FakeMessageActions();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            processor.HandleAsync(Exhausted(), actions, cts.Token));

        Assert.Empty(compensator.Calls);
        Assert.Empty(actions.Calls);
    }
}
