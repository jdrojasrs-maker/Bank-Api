using Bank.Transaction.Api.Application.Resilience;
using Bank.Transaction.Api.Tests.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Bank.Transaction.Api.Tests.Resilience;

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
}
