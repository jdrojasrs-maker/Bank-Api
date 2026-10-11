namespace Bank.Gateway.API.Application.External.ServiceBusSender;

public interface IServiceBusSenderService
{
    Task Execute(object eventModel, string subscriptionName);
}
