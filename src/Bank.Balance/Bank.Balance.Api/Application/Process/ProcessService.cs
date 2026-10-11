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
    public ProcessService(IDatabaseService databaseService, IServiceBusSenderService sb)
    {
        _db = databaseService;
        _sb = sb;
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
        var saveBalance = await ProcessDataBase(balance);

        var eventModel = new { saveBalance.CorrelationId, saveBalance.CustomerId };

        if (saveBalance.Id != 0)
            await _sb.Execute(eventModel, SendSubscriptionConstants.BALANCE_CONFIRMED);
        else
            await _sb.Execute(eventModel, SendSubscriptionConstants.BALANCE_FAILED);
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
