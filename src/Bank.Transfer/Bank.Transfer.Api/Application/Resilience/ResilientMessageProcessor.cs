using Bank.Transfer.Api.Domain.Constants;

namespace Bank.Transfer.Api.Application.Resilience;

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
        var check = MessageClassifier.Check(message.Body);
        if (check.IsPoison)
        {
            await RejectPoisonAsync(message, check.Detail, check.CorrelationId, actions, ct);
            return;
        }

        try
        {
            await _process(message.Body, message.MessageType, ct);
        }
        catch (Exception ex) when (ex is Newtonsoft.Json.JsonException or System.Text.Json.JsonException)
        {
            // Segunda línea de defensa: un error de deserialización es permanente, reintentarlo no lo arregla.
            await RejectPoisonAsync(message, $"Error de deserialización al procesar: {ex.GetType().Name}: {ex.Message}",
                check.CorrelationId, actions, ct);
            return;
        }
        catch (Exception)
        {
            // Cierre ordenado: el mensaje queda en la cola y se reentrega, no se reprograma.
            if (ct.IsCancellationRequested)
                throw;

            // Sin reintentos disponibles se relanza; el agotamiento (compensar y DLQ) llega con la historia 3.
            if (message.RetryAttempt >= RetryConstants.MAX_RETRIES)
                throw;

            // Primero se programa la copia y después se completa el original: si algo falla en medio, se reentrega
            // (duplicado posible, nunca pérdida).
            var nextAttempt = message.RetryAttempt + 1;
            var delay = RetryPolicy.NextDelay(nextAttempt, _random);
            await actions.ScheduleRetryAsync(nextAttempt, delay, ct);
            await actions.CompleteAsync(ct);
            return;
        }

        await actions.CompleteAsync(ct);
    }

    private async Task RejectPoisonAsync(IncomingMessage message, string detail, string? correlationId,
        IMessageActions actions, CancellationToken ct)
    {
        _logger.LogError("Mensaje {MessageType} apartado a la DLQ ({Reason}); CorrelationId {CorrelationId}; " +
            "MessageId {MessageId}; Attempt {Attempt}; Classification {Classification}: {Detail}",
            message.MessageType, DeadLetterReasonConstants.POISON_MESSAGE, correlationId, message.MessageId,
            message.RetryAttempt + 1, "Poison", detail);

        if (correlationId != null)
            await _compensator.CompensateAsync(message.Body, message.MessageType, ct);

        await actions.DeadLetterAsync(DeadLetterReasonConstants.POISON_MESSAGE, detail, correlationId,
            message.RetryAttempt + 1, ct);
    }
}
