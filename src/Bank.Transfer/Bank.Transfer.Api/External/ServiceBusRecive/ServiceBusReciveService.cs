using MediatR;
using Azure.Messaging.ServiceBus;
using Bank.Transfer.Api.Domain.Constants;
using Bank.Transfer.Api.Domain.Events;

namespace Bank.Transfer.Api.External.ServiceBusRecive;

public class ServiceBusReciveService : BackgroundService
{
    private readonly ServiceBusClient _client;
    private readonly List<ServiceBusProcessor> _processors;
    private readonly IMediator _mediator;


    public ServiceBusReciveService(IConfiguration configuration, IMediator mediator)
    {
        _mediator = mediator;
        _client = new ServiceBusClient(configuration["SERVICEBUSCONSTR"]);
        _processors = new List<ServiceBusProcessor>();

        var subscriptions = new[]
        {
            ReceivedSubscriptionsConstants.TRANSFER_INITIATED
        };

        _processors = subscriptions.Select(subscription =>
        {
            var processor = _client.CreateProcessor(configuration["SERVICEBUSTOPIC"], subscription);
            processor.ProcessMessageAsync += async args => await Process(args, subscription);
            processor.ProcessErrorAsync += args => ProcessError(args); // llamar al método y devolver el Task
            return processor;

        }).ToList();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        foreach (var processor in _processors)
        {
            await processor.StartProcessingAsync(stoppingToken);
        }
        var tcs = new TaskCompletionSource();
        using (stoppingToken.Register(s => ((TaskCompletionSource)s!).SetResult(), tcs))
        {
            await tcs.Task;
        }
    }

    private async Task Process(ProcessMessageEventArgs args, string subscription)
    {
        var message = args.Message.Body.ToString();

        await _mediator.Publish(new ProcessEvents(message, subscription), args.CancellationToken);
        await args.CompleteMessageAsync(args.Message, args.CancellationToken);
    }

    private Task ProcessError(ProcessErrorEventArgs args)
    {
        // Handle error
        return Task.CompletedTask;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var processor in _processors)
        {
            await processor.StopProcessingAsync(cancellationToken);
            await processor.DisposeAsync();
        }
        await _client.DisposeAsync();
        
        await base.StopAsync(cancellationToken);
    }
}
