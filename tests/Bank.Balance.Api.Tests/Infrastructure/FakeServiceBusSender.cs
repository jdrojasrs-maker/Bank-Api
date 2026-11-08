using System.Text.Json;
using Bank.Balance.Api.Application.External.ServiceBusSender;

namespace Bank.Balance.Api.Tests.Infrastructure;

public record SentEvent(string Name, string Json);

// Doble de IServiceBusSenderService: registra cada evento emitido y permite forzar un fallo de envío.
public class FakeServiceBusSender : IServiceBusSenderService
{
    private readonly object _lock = new();
    private readonly List<SentEvent> _sent = new();
    private bool _failNext;

    public IReadOnlyList<SentEvent> Sent
    {
        get { lock (_lock) { return _sent.ToList(); } }
    }

    public int CountOf(string eventName) => Sent.Count(x => x.Name == eventName);

    public void FailNextSend()
    {
        lock (_lock) { _failNext = true; }
    }

    public Task Execute(object eventModel, string subscriptionName)
    {
        lock (_lock)
        {
            if (_failNext)
            {
                _failNext = false;
                throw new InvalidOperationException("Fallo simulado al enviar el evento.");
            }

            _sent.Add(new SentEvent(subscriptionName, JsonSerializer.Serialize(eventModel)));
        }

        return Task.CompletedTask;
    }
}
