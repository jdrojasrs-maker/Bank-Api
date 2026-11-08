namespace Bank.Transaction.Api.Application.Resilience;

// Acciones sobre el mensaje en el broker; el adaptador de Service Bus las implementa y las pruebas las simulan.
public interface IMessageActions
{
    Task CompleteAsync(CancellationToken ct);
    Task DeadLetterAsync(string reason, string detail, string? correlationId, int attempts, CancellationToken ct);
    Task ScheduleRetryAsync(int nextAttempt, TimeSpan delay, CancellationToken ct);
}
