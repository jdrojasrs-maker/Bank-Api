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
        catch (Exception ex)
        {
            // Cierre ordenado: el mensaje queda en la cola y se reentrega, no se reprograma.
            if (ct.IsCancellationRequested)
            {
                _logger.LogDebug("Procesamiento de {MessageType} interrumpido por cierre ordenado; CorrelationId {CorrelationId}; " +
                    "MessageId {MessageId}; Attempt {Attempt}", message.MessageType, check.CorrelationId, message.MessageId,
                    message.RetryAttempt + 1);
                throw;
            }

            if (message.RetryAttempt >= RetryConstants.MAX_RETRIES)
            {
                await ExhaustAsync(message, check.CorrelationId, ex, actions, ct);
                return;
            }

            // Primero se programa la copia y después se completa el original: si algo falla en medio, se reentrega
            // (duplicado posible, nunca pérdida).
            var nextAttempt = message.RetryAttempt + 1;
            var delay = RetryPolicy.NextDelay(nextAttempt, _random);
            _logger.LogWarning(ex, "Mensaje {MessageType} falló (transitorio) en el intento {Attempt}; reintento en {DelaySeconds}s; " +
                "CorrelationId {CorrelationId}; MessageId {MessageId}; Classification {Classification}",
                message.MessageType, message.RetryAttempt + 1, Math.Round(delay.TotalSeconds, 1), check.CorrelationId,
                message.MessageId, "Transient");
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

        var compensationError = await TryCompensateAsync(message, correlationId, ct);
        if (compensationError != null)
        {
            await DeadLetterCompensationFailedAsync(message, correlationId, compensationError, detail, actions, ct);
            return;
        }

        await actions.DeadLetterAsync(DeadLetterReasonConstants.POISON_MESSAGE, detail, correlationId,
            message.RetryAttempt + 1, ct);
    }

    private async Task ExhaustAsync(IncomingMessage message, string? correlationId, Exception error,
        IMessageActions actions, CancellationToken ct)
    {
        var attempts = message.RetryAttempt + 1;
        var detail = Truncate($"Reintentos agotados tras {attempts} intentos. Último error: {error.GetType().Name}: {error.Message}");

        _logger.LogError(error, "Mensaje {MessageType} apartado a la DLQ ({Reason}); CorrelationId {CorrelationId}; " +
            "MessageId {MessageId}; Attempt {Attempt}; Classification {Classification}",
            message.MessageType, DeadLetterReasonConstants.RETRIES_EXHAUSTED, correlationId, message.MessageId,
            attempts, "Transient");

        var compensationError = await TryCompensateAsync(message, correlationId, ct);
        if (compensationError != null)
        {
            await DeadLetterCompensationFailedAsync(message, correlationId, compensationError, detail, actions, ct);
            return;
        }

        await actions.DeadLetterAsync(DeadLetterReasonConstants.RETRIES_EXHAUSTED, detail, correlationId, attempts, ct);
    }

    // null si compensó o no aplica; la excepción si no se pudo emitir el evento de fallo.
    private async Task<Exception?> TryCompensateAsync(IncomingMessage message, string? correlationId, CancellationToken ct)
    {
        if (correlationId == null)
            return null;

        try
        {
            await _compensator.CompensateAsync(message.Body, message.MessageType, ct);
            return null;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return ex;
        }
    }

    private async Task DeadLetterCompensationFailedAsync(IncomingMessage message, string? correlationId,
        Exception compensationError, string originalDetail, IMessageActions actions, CancellationToken ct)
    {
        var attempts = message.RetryAttempt + 1;
        _logger.LogError(compensationError, "No se pudo compensar el mensaje {MessageType} ({Reason}); " +
            "CorrelationId {CorrelationId}; MessageId {MessageId}; Attempt {Attempt}",
            message.MessageType, DeadLetterReasonConstants.COMPENSATION_FAILED, correlationId, message.MessageId, attempts);

        var detail = Truncate($"No se pudo emitir el evento de fallo: {compensationError.GetType().Name}: " +
            $"{compensationError.Message}. Error original: {originalDetail}");
        await actions.DeadLetterAsync(DeadLetterReasonConstants.COMPENSATION_FAILED, detail, correlationId, attempts, ct);
    }

    private static string Truncate(string detail) =>
        detail.Length > RetryConstants.MAX_DETAIL_LENGTH ? detail[..RetryConstants.MAX_DETAIL_LENGTH] : detail;
}
