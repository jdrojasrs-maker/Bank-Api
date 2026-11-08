namespace Bank.Transfer.Api.Application.Resilience;

public class NullFailureCompensator : IFailureCompensator
{
    public Task<bool> CompensateAsync(string body, string messageType, CancellationToken ct) => Task.FromResult(false);
}
