using Azure.Messaging.ServiceBus;
using Bank.Transaction.Api.Application.Resilience;
using Bank.Transaction.Api.Domain.Constants;

namespace Bank.Transaction.Api.External.ServiceBusRecive;

public class ServiceBusMessageActions : IMessageActions
{
    private readonly ProcessMessageEventArgs _args;
    private readonly Azure.Messaging.ServiceBus.ServiceBusSender _retrySender;

    public ServiceBusMessageActions(ProcessMessageEventArgs args, Azure.Messaging.ServiceBus.ServiceBusSender retrySender)
    {
        _args = args;
        _retrySender = retrySender;
    }

    public Task CompleteAsync(CancellationToken ct) => _args.CompleteMessageAsync(_args.Message, ct);

    public Task DeadLetterAsync(string reason, string detail, string? correlationId, int attempts, CancellationToken ct)
    {
        var properties = new Dictionary<string, object>();
        if (correlationId != null)
            properties["CorrelationId"] = correlationId;
        properties["Attempts"] = attempts;

        var truncated = detail.Length > RetryConstants.MAX_DETAIL_LENGTH
            ? detail[..RetryConstants.MAX_DETAIL_LENGTH]
            : detail;

        return _args.DeadLetterMessageAsync(_args.Message, properties, reason, truncated, ct);
    }

    // Si programar la copia falla, la excepción se propaga y el original no se completa: se reentrega.
    public async Task ScheduleRetryAsync(int nextAttempt, TimeSpan delay, CancellationToken ct)
    {
        var original = _args.Message;
        var copy = new ServiceBusMessage(original.Body)
        {
            Subject = original.Subject,
            ContentType = original.ContentType,
            MessageId = original.MessageId
        };
        foreach (var property in original.ApplicationProperties)
            copy.ApplicationProperties[property.Key] = property.Value;
        copy.ApplicationProperties[RetryConstants.RETRY_ATTEMPT_PROPERTY] = nextAttempt;

        await _retrySender.ScheduleMessageAsync(copy, DateTimeOffset.UtcNow + delay, ct);
    }
}
