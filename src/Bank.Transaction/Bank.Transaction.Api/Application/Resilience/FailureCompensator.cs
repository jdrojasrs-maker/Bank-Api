using Bank.Transaction.Api.Application.Database;
using Bank.Transaction.Api.Domain.Constants;
using Bank.Transaction.API.Application.External.ServiceBusSender;
using Microsoft.EntityFrameworkCore;

namespace Bank.Transaction.Api.Application.Resilience;

// Compensa un fallo emitiendo el evento de fallo del primer paso del servicio. Regla de seguridad (FR-016): solo se emite
// si el servicio no tiene ya un registro del CorrelationId, porque un registro significa que el paso tuvo efecto.
public class FailureCompensator : IFailureCompensator
{
    private readonly IServiceBusSenderService _sender;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<FailureCompensator> _logger;

    public FailureCompensator(IServiceBusSenderService sender, IServiceScopeFactory scopeFactory,
        ILogger<FailureCompensator> logger)
    {
        _sender = sender;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<bool> CompensateAsync(string body, string messageType, CancellationToken ct)
    {
        if (messageType != Bank.Transaction.Api.Domain.Constants.ReceivedSubscriptionsConstants.TRANSACTION_INITIATED)
            return false;

        var correlationId = MessageClassifier.ReadCorrelationId(body);
        if (correlationId == null)
        {
            _logger.LogWarning("No se compensa {MessageType}: el CorrelationId no es legible; CorrelationId {CorrelationId}",
                messageType, null);
            return false;
        }

        if (await HasRecordAsync(correlationId, ct))
        {
            _logger.LogWarning("Compensación omitida para {MessageType}: ya hay registro; CorrelationId {CorrelationId}; Reason {Reason}",
                messageType, correlationId, "AlreadyRegistered");
            return false;
        }

        var failureEvent = Bank.Transaction.Api.Domain.Constants.SendSubscriptionConstants.TRANSACTION_FAILED;
        // Sin CustomerId legible se omite la propiedad: los consumidores lo leen como int y un null los haría rechazarlo.
        var customerId = MessageClassifier.ReadCustomerId(body);
        object payload = customerId is null
            ? new { CorrelationId = correlationId }
            : new { CorrelationId = correlationId, CustomerId = customerId.Value };
        await _sender.Execute(payload, failureEvent);
        _logger.LogInformation("Evento de fallo {Event} emitido para {MessageType}; CorrelationId {CorrelationId}",
            failureEvent, messageType, correlationId);
        return true;
    }

    // Si no se puede consultar, se asume que no hay registro: es preferible compensar a dejar la saga colgada.
    private async Task<bool> HasRecordAsync(string correlationId, CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<IDatabaseService>();
            return await db.Transaction.AsNoTracking().AnyAsync(x => x.CorrelationId == correlationId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "No se pudo comprobar el registro de {CorrelationId}; se emite {Event}",
                correlationId, Bank.Transaction.Api.Domain.Constants.SendSubscriptionConstants.TRANSACTION_FAILED);
            return false;
        }
    }
}
