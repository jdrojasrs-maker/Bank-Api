using Bank.Transaction.Api.Application.Database;
using Bank.Transaction.Api.Domain.Constants;
using Bank.Transaction.Api.Domain.Entities;
using Bank.Transaction.API.Application.External.ServiceBusSender;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;

namespace Bank.Transaction.Api.Application.Features.Process;

public class ProcessService: IProcessService
{
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
        switch (subscription)
        {
            case ReceivedSubscriptionsConstants.TRANSACTION_INITIATED:
                await TransactionInitiated(message);
                break;
            case ReceivedSubscriptionsConstants.BALANCE_CONFIRMED:
                await BalanceConfirmed(message);
                break;
            case ReceivedSubscriptionsConstants.BALANCE_FAILED:
                await BalanceFailed(message);
                break;
            case ReceivedSubscriptionsConstants.TRANSFER_FAILED:
                await TransferFailed(message);
                break;
            case ReceivedSubscriptionsConstants.TRANSFER_CONFIRMED:
                await TransferConfirmed(message);
                break;

                
        }
    }

    private async Task TransactionInitiated(string message)
    {
        var transaction = JsonConvert.DeserializeObject<TransactionEntity>(message);
        transaction.CurrentState = CurrentStateConstants.PENDING;
        var eventModel = new { transaction.CorrelationId, transaction.CustomerId };

        try
        {
            await ProcessDataBase(transaction);
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

        await _sb.Execute(eventModel, SendSubscriptionConstants.BALANCE_INITIATED);
    }

    private async Task BalanceConfirmed(string message)
    {
        var transaction = JsonConvert.DeserializeObject<TransactionEntity>(message);
        transaction.CurrentState = CurrentStateConstants.PENDING;
        var saveTrn = await ProcessDataBase(transaction);

        var eventModel = new {
            saveTrn.CorrelationId,
            saveTrn.Amount,
            saveTrn.SourceAccount,
            saveTrn.DestinationAccount,
            saveTrn.CustomerId 
        };

        await _sb.Execute(eventModel, SendSubscriptionConstants.TRANSFER_INITIATED);
    }

    private async Task BalanceFailed(string message)
    {
        var transaction = JsonConvert.DeserializeObject<TransactionEntity>(message);
        transaction.CurrentState = CurrentStateConstants.CANCELED;
        var saveTrn = await ProcessDataBase(transaction);

        var eventModel = new
        {
            saveTrn.CorrelationId,
            saveTrn.Amount,
            saveTrn.CustomerId
        };

        await _sb.Execute(eventModel, SendSubscriptionConstants.TRANSACTION_FAILED);
    }

    private async Task TransferFailed(string message)
    {
        var transaction = JsonConvert.DeserializeObject<TransactionEntity>(message);
        transaction.CurrentState = CurrentStateConstants.CANCELED;
        var saveTrn = await ProcessDataBase(transaction);

        var eventModel = new
        {
            saveTrn.CorrelationId,
            saveTrn.Amount,
            saveTrn.CustomerId
        };

        await _sb.Execute(eventModel, SendSubscriptionConstants.TRANSACTION_FAILED);
        await _sb.Execute(eventModel, SendSubscriptionConstants.TRANSFER_FAILED_BALANCE);
    }

    private async Task TransferConfirmed(string message)
    {
        var transaction = JsonConvert.DeserializeObject<TransactionEntity>(message);
        transaction.CurrentState = CurrentStateConstants.COMPLETED;
        var saveTrn = await ProcessDataBase(transaction);

        var eventModel = new
        {
            saveTrn.CorrelationId,
            saveTrn.Amount,
            saveTrn.CustomerId
        };

        await _sb.Execute(eventModel, SendSubscriptionConstants.TRANSACTION_COMPLETED);
        await _sb.Execute(eventModel, SendSubscriptionConstants.TRANSFER_CONFIRMED_BALANCE);
    }

    public async Task<TransactionEntity> ProcessDataBase(TransactionEntity transaction)
    {
        var currrentTransaction = await _db.Transaction.FirstOrDefaultAsync(x => x.CorrelationId == transaction.CorrelationId);

        if (currrentTransaction == null)
        {
            transaction.TransactionDate = DateTime.UtcNow;
            await _db.Transaction.AddAsync(transaction);
            await _db.SaveAsync();
            return transaction;
        }
        else
        {
            currrentTransaction.TransactionDate = DateTime.UtcNow;
            currrentTransaction.CurrentState = transaction.CurrentState;
            _db.Transaction.Update(currrentTransaction);
            await _db.SaveAsync();
            return currrentTransaction;
        }
    }
}
