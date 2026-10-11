namespace Bank.Transaction.API.Application.External.ServiceBusSender;

public interface IServiceBusSenderService
{
    Task Execute(object eventModel, string subscriptionName);
}
