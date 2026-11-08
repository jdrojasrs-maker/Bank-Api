using Bank.Transaction.Api.Application.Resilience;

namespace Bank.Transaction.Api.Tests.Infrastructure;

// Doble de IMessageActions: guarda cada invocación en orden y permite forzar que una operación lance una excepción.
public class FakeMessageActions : IMessageActions
{
    private readonly object _lock = new();
    private readonly List<string> _calls = new();
    private readonly Dictionary<string, Exception> _failures = new();

    public IReadOnlyList<string> Calls
    {
        get { lock (_lock) { return _calls.ToList(); } }
    }

    public string? Reason { get; private set; }
    public string? Detail { get; private set; }
    public string? CorrelationId { get; private set; }
    public int? Attempts { get; private set; }
    public int? NextAttempt { get; private set; }
    public TimeSpan? Delay { get; private set; }

    // operation: "Complete", "DeadLetter" o "ScheduleRetry".
    public void FailOn(string operation, Exception exception)
    {
        lock (_lock) { _failures[operation] = exception; }
    }

    public Task CompleteAsync(CancellationToken ct)
    {
        Record("Complete", "Complete");
        return Task.CompletedTask;
    }

    public Task DeadLetterAsync(string reason, string detail, string? correlationId, int attempts, CancellationToken ct)
    {
        Reason = reason;
        Detail = detail;
        CorrelationId = correlationId;
        Attempts = attempts;
        Record("DeadLetter", $"DeadLetter:{reason}");
        return Task.CompletedTask;
    }

    public Task ScheduleRetryAsync(int nextAttempt, TimeSpan delay, CancellationToken ct)
    {
        NextAttempt = nextAttempt;
        Delay = delay;
        Record("ScheduleRetry", $"ScheduleRetry:{nextAttempt}");
        return Task.CompletedTask;
    }

    private void Record(string operation, string call)
    {
        lock (_lock)
        {
            _calls.Add(call);
            if (_failures.TryGetValue(operation, out var exception))
                throw exception;
        }
    }
}
