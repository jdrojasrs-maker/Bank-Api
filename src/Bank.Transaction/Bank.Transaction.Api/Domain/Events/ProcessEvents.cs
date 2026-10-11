using MediatR;

namespace Bank.Transaction.Api.Domain.Events;

public class ProcessEvents: INotification
{
    public string Message { get; set; }
    public string Subscription { get; set; }

    public ProcessEvents(string message, string subscription)
    {
        Message = message;
        Subscription = subscription;
    }
}
