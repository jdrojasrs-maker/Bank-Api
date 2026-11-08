using Microsoft.Extensions.Logging;

namespace Bank.Notification.Api.Tests.Infrastructure;

public record LogEntry(LogLevel Level, string Message, IReadOnlyDictionary<string, object?> Properties,
    Exception? Exception = null);

// ILogger que conserva el nivel, el mensaje y las propiedades estructuradas (CorrelationId, MessageType, Reason).
public class CapturingLogger<T> : ILogger<T>
{
    private readonly object _lock = new();
    private readonly List<LogEntry> _entries = new();

    public IReadOnlyList<LogEntry> Entries
    {
        get { lock (_lock) { return _entries.ToList(); } }
    }

    public IEnumerable<string> Reasons =>
        Entries.Select(x => x.Properties.TryGetValue("Reason", out var reason) ? reason?.ToString() : null)
               .Where(x => x != null)!;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var properties = new Dictionary<string, object?>();
        if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
        {
            foreach (var pair in pairs)
                properties[pair.Key] = pair.Value;
        }

        lock (_lock)
        {
            _entries.Add(new LogEntry(logLevel, formatter(state, exception), properties, exception));
        }
    }
}
