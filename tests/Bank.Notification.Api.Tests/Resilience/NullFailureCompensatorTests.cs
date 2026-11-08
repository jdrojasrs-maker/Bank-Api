using Bank.Notification.Api.Application.Resilience;

namespace Bank.Notification.Api.Tests.Resilience;

public class NullFailureCompensatorTests
{
    [Fact]
    public async Task Compensate_returns_false_and_does_nothing()
    {
        var result = await new NullFailureCompensator().CompensateAsync("""{"CorrelationId":"c-1"}""", "any-event", CancellationToken.None);

        Assert.False(result);
    }
}
