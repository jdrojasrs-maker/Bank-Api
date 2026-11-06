using Bank.Balance.Api.Application.Database;
using Bank.Balance.Api.Domain.Constants;
using Bank.Balance.Api.Domain.Entities;
using Bank.Balance.Api.Application.External.ServiceBusSender;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;

namespace Bank.Balance.Api.Application.Features.Process;

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
            case ReceivedSubscriptionsConstants.BALANCE_INITIATED:
                await BalanceInitiated(message);
                break;
            case ReceivedSubscriptionsConstants.TRANSFER_CONFIRMED_BALANCE:
                await TransferConfirmedBalance(message);
                break;
            case ReceivedSubscriptionsConstants.TRANSFER_FAILED_BALANCE:
                await TransferFailedBalance(message);
                break;                
        }
    }

    private async Task BalanceInitiated(string message)
    {
        var balance = JsonConvert.DeserializeObject<BalanceEntity>(message);
        balance.CurrentState = CurrentStateConstants.PENDING;
        var eventModel = new { balance.CorrelationId, balance.CustomerId };

        try
        {
            await ProcessDataBase(balance);
        }
        catch (Exception ex) when (DatabaseErrors.IsPermanent(ex))
        {
            // Error permanente: reintentar no sirve. Se compensa en vez de dejar la saga colgada.
            // Un error transitorio no entra aquí: se propaga y Service Bus reintenta.
            _logger.LogError(ex, "Balance {CorrelationId} rechazado por la base de datos; se emite {Event}",
                balance.CorrelationId, SendSubscriptionConstants.BALANCE_FAILED);
            await _sb.Execute(eventModel, SendSubscriptionConstants.BALANCE_FAILED);
            return;
        }

        await _sb.Execute(eventModel, SendSubscriptionConstants.BALANCE_CONFIRMED);
    }

    private async Task TransferConfirmedBalance(string message)
    {
        var balance = JsonConvert.DeserializeObject<BalanceEntity>(message);
        balance.CurrentState = CurrentStateConstants.COMPLETED;
        var saveBalance = await ProcessDataBase(balance);
    }

    private async Task TransferFailedBalance(string message)
    {
        var balance = JsonConvert.DeserializeObject<BalanceEntity>(message);
        balance.CurrentState = CurrentStateConstants.CANCELED;
        var saveBalance = await ProcessDataBase(balance);
    }

    public async Task<BalanceEntity> ProcessDataBase(BalanceEntity balance)
    {
        var currrentTransaction = await _db.Balance.FirstOrDefaultAsync(x => x.CorrelationId == balance.CorrelationId);

        if (currrentTransaction == null)
        {
            balance.BalanceDate = DateTime.UtcNow;
            await _db.Balance.AddAsync(balance);
            await _db.SaveAsync();
            return balance;
        }
        else
        {
            currrentTransaction.BalanceDate = DateTime.UtcNow;
            currrentTransaction.CurrentState = balance.CurrentState;
            _db.Balance.Update(currrentTransaction);
            await _db.SaveAsync();
            return currrentTransaction;
        }
    }
}
