using Bank.Transaction.Api.Application.Database;
using Bank.Transaction.Api.Domain.Constants;
using Bank.Transaction.Api.Domain.Entities;
using Bank.Transaction.API.Application.External.ServiceBusSender;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;

namespace Bank.Transaction.Api.Application.Features.Process;

public class ProcessService: IProcessService
{
    private const int MAX_CORRELATION_ID_LENGTH = 64;

    private readonly IDatabaseService _db;
    private readonly IServiceBusSenderService _sb;
    private readonly ILogger<ProcessService> _logger;
    public ProcessService(IDatabaseService databaseService, IServiceBusSenderService sb, ILogger<ProcessService> logger)
    {
        _db = databaseService;
        _sb = sb;
        _logger = logger;
    }

    public async Task Execute(string message, string subscription)
    {
        var transaction = ParseTransaction(message, subscription);
        if (transaction is null)
            return;

        switch (subscription)
        {
            case ReceivedSubscriptionsConstants.TRANSACTION_INITIATED:
                await TransactionInitiated(transaction);
                break;
            case ReceivedSubscriptionsConstants.BALANCE_CONFIRMED:
                await BalanceConfirmed(transaction, subscription);
                break;
            case ReceivedSubscriptionsConstants.BALANCE_FAILED:
                await BalanceFailed(transaction, subscription);
                break;
            case ReceivedSubscriptionsConstants.TRANSFER_FAILED:
                await TransferFailed(transaction, subscription);
                break;
            case ReceivedSubscriptionsConstants.TRANSFER_CONFIRMED:
                await TransferConfirmed(transaction, subscription);
                break;
        }
    }

    private async Task TransactionInitiated(TransactionEntity transaction)
    {
        transaction.CurrentState = CurrentStateConstants.PENDING;
        transaction.TransactionDate = DateTime.UtcNow;
        var eventModel = new { transaction.CorrelationId, transaction.CustomerId };

        var existing = await FindAsync(transaction.CorrelationId);
        if (existing is null)
        {
            try
            {
                await _db.Transaction.AddAsync(transaction);
                await _db.SaveAsync();
            }
            catch (Exception ex) when (DatabaseErrors.IsUniqueViolation(ex))
            {
                // Otra entrega del mismo mensaje insertó primero: se decide según el estado que dejó.
                _db.ClearChanges();
                existing = await FindAsync(transaction.CorrelationId);
            }
            catch (Exception ex) when (DatabaseErrors.IsPermanent(ex))
            {
                // Error permanente: reintentar no sirve. Nada que compensar aguas abajo (aún no se emitió ningún evento).
                // Un error transitorio no entra aquí: se propaga y Service Bus reintenta.
                _logger.LogError(ex, "Transaction {CorrelationId} rechazada por la base de datos; se emite {Event}",
                    transaction.CorrelationId, SendSubscriptionConstants.TRANSACTION_FAILED);
                await _sb.Execute(eventModel, SendSubscriptionConstants.TRANSACTION_FAILED);
                return;
            }
        }

        if (existing is not null && existing.CurrentState != CurrentStateConstants.PENDING)
        {
            IgnoreMessage(ReceivedSubscriptionsConstants.TRANSACTION_INITIATED,
                IgnoredMessageReasonConstants.FINAL_STATE_PROTECTED, transaction.CorrelationId);
            return;
        }

        // Si ya estaba en Pending (entrega repetida) se vuelve a emitir: así se recupera un envío que falló antes.
        await _sb.Execute(eventModel, SendSubscriptionConstants.BALANCE_INITIATED);
    }

    private async Task BalanceConfirmed(TransactionEntity transaction, string subscription)
    {
        var current = await FindAsync(transaction.CorrelationId);
        if (!IsPending(current, transaction.CorrelationId, subscription))
            return;

        var eventModel = new {
            current!.CorrelationId,
            current.Amount,
            current.SourceAccount,
            current.DestinationAccount,
            current.CustomerId
        };

        await _sb.Execute(eventModel, SendSubscriptionConstants.TRANSFER_INITIATED);
    }

    private async Task BalanceFailed(TransactionEntity transaction, string subscription)
    {
        var current = await FindAsync(transaction.CorrelationId);
        if (!IsPending(current, transaction.CorrelationId, subscription))
            return;

        var eventModel = new
        {
            current!.CorrelationId,
            current.Amount,
            current.CustomerId
        };

        // Se emite antes de confirmar la transición: si el envío falla el estado sigue en Pending y la reentrega reintenta.
        await _sb.Execute(eventModel, SendSubscriptionConstants.TRANSACTION_FAILED);
        await Transition(current, CurrentStateConstants.CANCELED, subscription);
    }

    private async Task TransferFailed(TransactionEntity transaction, string subscription)
    {
        var current = await FindAsync(transaction.CorrelationId);
        if (!IsPending(current, transaction.CorrelationId, subscription))
            return;

        var eventModel = new
        {
            current!.CorrelationId,
            current.Amount,
            current.CustomerId
        };

        await _sb.Execute(eventModel, SendSubscriptionConstants.TRANSACTION_FAILED);
        await _sb.Execute(eventModel, SendSubscriptionConstants.TRANSFER_FAILED_BALANCE);
        await Transition(current, CurrentStateConstants.CANCELED, subscription);
    }

    private async Task TransferConfirmed(TransactionEntity transaction, string subscription)
    {
        var current = await FindAsync(transaction.CorrelationId);
        if (!IsPending(current, transaction.CorrelationId, subscription))
            return;

        var eventModel = new
        {
            current!.CorrelationId,
            current.Amount,
            current.CustomerId
        };

        await _sb.Execute(eventModel, SendSubscriptionConstants.TRANSACTION_COMPLETED);
        await _sb.Execute(eventModel, SendSubscriptionConstants.TRANSFER_CONFIRMED_BALANCE);
        await Transition(current, CurrentStateConstants.COMPLETED, subscription);
    }

    // Transición atómica: solo se aplica si la transacción sigue en Pending. Completed y Canceled son definitivos.
    private async Task Transition(TransactionEntity current, string newState, string subscription)
    {
        var now = DateTime.UtcNow;
        var affected = await _db.Transaction
            .Where(x => x.CorrelationId == current.CorrelationId && x.CurrentState == CurrentStateConstants.PENDING)
            .ExecuteUpdateAsync(set => set
                .SetProperty(x => x.CurrentState, newState)
                .SetProperty(x => x.TransactionDate, now));

        if (affected == 0)
            IgnoreMessage(subscription, IgnoredMessageReasonConstants.FINAL_STATE_PROTECTED, current.CorrelationId);
    }

    private bool IsPending(TransactionEntity? current, string correlationId, string subscription)
    {
        if (current is null)
        {
            IgnoreMessage(subscription, IgnoredMessageReasonConstants.NO_SUCH_TRANSACTION, correlationId);
            return false;
        }

        if (current.CurrentState != CurrentStateConstants.PENDING)
        {
            IgnoreMessage(subscription, IgnoredMessageReasonConstants.FINAL_STATE_PROTECTED, correlationId);
            return false;
        }

        return true;
    }

    private Task<TransactionEntity?> FindAsync(string correlationId) =>
        _db.Transaction.AsNoTracking().FirstOrDefaultAsync(x => x.CorrelationId == correlationId);

    private TransactionEntity? ParseTransaction(string message, string subscription)
    {
        TransactionEntity? transaction;
        try
        {
            transaction = JsonConvert.DeserializeObject<TransactionEntity>(message);
        }
        catch (JsonException)
        {
            transaction = null;
        }

        if (transaction is null
            || string.IsNullOrWhiteSpace(transaction.CorrelationId)
            || transaction.CorrelationId.Length > MAX_CORRELATION_ID_LENGTH)
        {
            var correlationId = transaction?.CorrelationId;
            IgnoreMessage(subscription, IgnoredMessageReasonConstants.INVALID_MESSAGE,
                correlationId is { Length: > MAX_CORRELATION_ID_LENGTH } ? correlationId[..MAX_CORRELATION_ID_LENGTH] : correlationId);
            return null;
        }

        return transaction;
    }

    private void IgnoreMessage(string messageType, string reason, string? correlationId)
    {
        _logger.LogWarning("Mensaje {MessageType} ignorado ({Reason}); CorrelationId {CorrelationId}",
            messageType, reason, correlationId);
    }
}
