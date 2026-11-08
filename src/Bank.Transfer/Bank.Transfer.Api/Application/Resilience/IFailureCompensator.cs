namespace Bank.Transfer.Api.Application.Resilience;

public interface IFailureCompensator
{
    // true si emitió un evento de fallo; false si ese paso no tiene (no hace nada).
    Task<bool> CompensateAsync(string body, string messageType, CancellationToken ct);
}
