using Azure.Messaging.ServiceBus;
using Bank.Transfer.Api.Application.Resilience;
using Bank.Transfer.Api.Domain.Constants;
using Bank.Transfer.Api.Domain.Events;
using MediatR;

namespace Bank.Transfer.Api.External.ServiceBusRecive;

public class ServiceBusReciveService : BackgroundService
{
    private readonly ServiceBusClient _client;
    private readonly List<ServiceBusProcessor> _processors;
    private readonly Azure.Messaging.ServiceBus.ServiceBusSender _retrySender;
    private readonly ResilientMessageProcessor _messageProcessor;
    private readonly ILogger<ServiceBusReciveService> _receiverLogger;

    public ServiceBusReciveService(IConfiguration configuration, IMediator mediator,
        IFailureCompensator compensator, ILogger<ResilientMessageProcessor> logger,
        ILogger<ServiceBusReciveService> receiverLogger)
    {
        _receiverLogger = receiverLogger;
        _client = new ServiceBusClient(configuration["SERVICEBUSCONSTR"]);
        _retrySender = _client.CreateSender(configuration["SERVICEBUSTOPIC"]);
        _messageProcessor = new ResilientMessageProcessor(
            (body, type, ct) => mediator.Publish(new ProcessEvents(body, type), ct), compensator, logger);

        var subscriptions = new[]
        {
            ReceivedSubscriptionsConstants.TRANSFER_INITIATED
        };

        // Sin autocompletar: el procesador resiliente decide si completa, reprograma o aparta cada mensaje.
        _processors = subscriptions.Select(subscription =>
        {
            var processor = _client.CreateProcessor(configuration["SERVICEBUSTOPIC"], subscription,
                new ServiceBusProcessorOptions { AutoCompleteMessages = false });
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
        var message = new IncomingMessage(args.Message.Body.ToString(), subscription, args.Message.MessageId,
            ReadRetryAttempt(args.Message));
        var actions = new ServiceBusMessageActions(args, _retrySender);

        await _messageProcessor.HandleAsync(message, actions, args.CancellationToken);
    }

    // 0 si la propiedad falta o no es un entero: el mensaje original no la trae.
    private static int ReadRetryAttempt(ServiceBusReceivedMessage message) =>
        message.ApplicationProperties.TryGetValue(RetryConstants.RETRY_ATTEMPT_PROPERTY, out var value)
            ? value switch { int i => i, long l => (int)l, _ => 0 }
            : 0;

    // Solo registra: el error del procesador no pertenece a un mensaje concreto, así que no aparta ni completa nada.
    private Task ProcessError(ProcessErrorEventArgs args)
    {
        ProcessorErrorHandler.Log(_receiverLogger, args.Exception, args.ErrorSource.ToString(), args.EntityPath,
            args.FullyQualifiedNamespace, args.CancellationToken.IsCancellationRequested);
        return Task.CompletedTask;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var processor in _processors)
        {
            await processor.StopProcessingAsync(cancellationToken);
            await processor.DisposeAsync();
        }
        await _retrySender.DisposeAsync();
        await _client.DisposeAsync();

        await base.StopAsync(cancellationToken);
    }
}
