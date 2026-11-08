using Bank.Transfer.Api.Application.Resilience;

namespace Bank.Transfer.Api.Tests.Infrastructure;

// Doble de IFailureCompensator: cuenta las llamadas y devuelve el resultado configurado.
public class FakeFailureCompensator : IFailureCompensator
{
    private readonly object _lock = new();
    private readonly List<(string Body, string MessageType)> _calls = new();

    public bool Result { get; set; } = true;

    public IReadOnlyList<(string Body, string MessageType)> Calls
    {
        get { lock (_lock) { return _calls.ToList(); } }
    }

    public Task<bool> CompensateAsync(string body, string messageType, CancellationToken ct)
    {
        lock (_lock) { _calls.Add((body, messageType)); }
        return Task.FromResult(Result);
    }
}
