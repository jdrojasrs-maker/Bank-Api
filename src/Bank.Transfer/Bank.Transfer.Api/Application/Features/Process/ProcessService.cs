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
    private readonly ILogger<ProcessService> _logger;
    public ProcessService(IDatabaseService db, IServiceBusSenderService sb, ILogger<ProcessService> logger)
    {
        _db = db;
        _sb = sb;
        _logger = logger;
    }

    public async Task Execute(string message)
    {
        await TransferInitiated(message);                
    }

    private async Task TransferInitiated(string message)
    {
        var transfer = JsonConvert.DeserializeObject<TransferEntity>(message);
        var transferModel = new { transfer.CorrelationId, transfer.CustomerId };

        try
        {
            await ProcessDataBase(transfer);
        }
        catch (Exception ex) when (DatabaseErrors.IsPermanent(ex))
        {
            // Error permanente: reintentar no sirve. Se compensa en vez de dejar la saga colgada.
            // Un error transitorio no entra aquí: se propaga y Service Bus reintenta.
            _logger.LogError(ex, "Transfer {CorrelationId} rechazada por la base de datos; se emite {Event}",
                transfer.CorrelationId, SendSubscriptionConstants.TRANSFER_FAILED);
            await _sb.Execute(transferModel, SendSubscriptionConstants.TRANSFER_FAILED);
            return;
        }

        await _sb.Execute(transferModel, SendSubscriptionConstants.TRANSFER_CONFIRMED);
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
