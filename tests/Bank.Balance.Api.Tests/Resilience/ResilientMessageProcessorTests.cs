using Bank.Balance.Api.Application.Resilience;
using Bank.Balance.Api.Tests.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Bank.Balance.Api.Tests.Resilience;

// Andamiaje: el procesador ejecuta, completa y deja pasar cualquier excepción. El resto del comportamiento llega por historia.
public class ResilientMessageProcessorTests
{
    private static ResilientMessageProcessor Create(Func<string, string, CancellationToken, Task> process) =>
        new(process, new NullFailureCompensator(), new CapturingLogger<ResilientMessageProcessor>());

    private static IncomingMessage Message() => new("{\"CorrelationId\":\"abc\"}", "some-event", "msg-1", 0);

    [Fact]
    public async Task Process_succeeds_invokes_process_once_and_completes()
    {
        var calls = new List<(string Body, string Type)>();
        var processor = Create((body, type, _) =>
        {
            calls.Add((body, type));
            return Task.CompletedTask;
        });
        var actions = new FakeMessageActions();
        var message = Message();

        await processor.HandleAsync(message, actions, CancellationToken.None);

        Assert.Single(calls);
        Assert.Equal((message.Body, message.MessageType), calls[0]);
        Assert.Equal(new[] { "Complete" }, actions.Calls);
    }

    [Fact]
    public async Task Process_throws_with_no_retries_left_propagates_exception_and_does_not_complete()
    {
        var processor = Create((_, _, _) => throw new InvalidOperationException("boom"));
        var actions = new FakeMessageActions();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => processor.HandleAsync(Message() with { RetryAttempt = 5 }, actions, CancellationToken.None));

        Assert.Empty(actions.Calls);
    }
}
