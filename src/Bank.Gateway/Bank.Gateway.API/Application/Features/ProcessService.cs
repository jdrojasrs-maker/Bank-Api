using Bank.Gateway.API.Application.External.ServiceBusSender;
using Bank.Gateway.API.Application.Models;
using Bank.Gateway.API.Domain.Constants;

namespace Bank.Gateway.API.Application.Features;

public class ProcessService: IProcessService
{
    private readonly IServiceBusSenderService _serviceBusSenderService;

    public ProcessService(IServiceBusSenderService serviceBusSenderService)
    {
        _serviceBusSenderService = serviceBusSenderService;
    }

    public async Task Execute(EndPointModel transactionEvent)
    {
        var modelEvent = new 
        {
            CorrelationId = Guid.NewGuid().ToString(),
            Amount = transactionEvent.Amount,
            SourceAccount = transactionEvent.SourceAccount,
            DestinationAccount = transactionEvent.DestinationAccount,
            CustomerId = transactionEvent.CustomerId,
        };
        await _serviceBusSenderService.Execute(modelEvent, SendSubscriptionConstants.TRANSACTION_INITIATED);
    }
}
