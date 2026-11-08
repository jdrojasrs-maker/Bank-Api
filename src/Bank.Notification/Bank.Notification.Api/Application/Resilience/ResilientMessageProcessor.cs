namespace Bank.Notification.Api.Application.Resilience;

public class ResilientMessageProcessor
{
    private readonly Func<string, string, CancellationToken, Task> _process;
    private readonly IFailureCompensator _compensator;
    private readonly ILogger<ResilientMessageProcessor> _logger;
    private readonly Random _random;

    public ResilientMessageProcessor(Func<string, string, CancellationToken, Task> process,
        IFailureCompensator compensator, ILogger<ResilientMessageProcessor> logger, Random? random = null)
    {
        _process = process;
        _compensator = compensator;
        _logger = logger;
        _random = random ?? Random.Shared;
    }

    public async Task HandleAsync(IncomingMessage message, IMessageActions actions, CancellationToken ct)
    {
        await _process(message.Body, message.MessageType, ct);
        await actions.CompleteAsync(ct);
    }
}
