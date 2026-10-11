using Bank.Transfer.Api.Application.Database;
using Bank.Transfer.Api.Application.External.ServiceBusSender;
using Bank.Transfer.Api.Domain.Constants;
using Bank.Transfer.Api.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;

namespace Bank.Transfer.Api.Application.Features.Process;

public class ProcessService: IProcessService
{
    private readonly IDatabaseService _db;
    private readonly IServiceBusSenderService _sb;
    public ProcessService(IDatabaseService db, IServiceBusSenderService sb)
    {
        _db = db;
        _sb = sb;
    }

    public async Task Execute(string message)
    {
        await TransferInitiated(message);                
    }

    private async Task TransferInitiated(string message)
    {
        var transfer = JsonConvert.DeserializeObject<TransferEntity>(message);
        await ProcessDataBase(transfer);

        var transferModel = new { transfer.CorrelationId, transfer.CustomerId };

        if (transfer.Id != 0)
            await _sb.Execute(transferModel, SendSubscriptionConstants.TRANSFER_CONFIRMED);
        else
            await _sb.Execute(transferModel, SendSubscriptionConstants.TRANSFER_FAILED);
    }

    public async Task<TransferEntity> ProcessDataBase(TransferEntity transfer)
    {
        transfer.TransferDate = DateTime.UtcNow;
        transfer.CurrentState = CurrentStateConstants.COMPLETE;
        await _db.Transfer.AddAsync(transfer);
        await _db.SaveAsync();
        return transfer;
    }
}
