using Azure.Messaging.ServiceBus;
using Bank.Gateway.API.Application.External.ServiceBusSender;
using System.Text.Json;

namespace Bank.Gateway.API.External.ServiceBusSender;

public class ServiceBusSenderService: IServiceBusSenderService
{
    private readonly ServiceBusClient _serviceBusClient;
    private readonly string _topicName;

    public ServiceBusSenderService(IConfiguration configuration)
    {
        _serviceBusClient = new ServiceBusClient(configuration["SERVICEBUSCONSTR"]);
        _topicName = configuration["SERVICEBUSTOPIC"];
    }

    public async Task Execute(object eventModel, string subscriptionName)
    {
        var sender = _serviceBusClient.CreateSender(_topicName);
        var message = new ServiceBusMessage(JsonSerializer.Serialize(eventModel))
        {
            Subject = subscriptionName,
            ContentType = "application/json"
        };
        await sender.SendMessageAsync(message);
    }
}
