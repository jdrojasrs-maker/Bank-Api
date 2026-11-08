using Bank.Transaction.Api.Domain.Constants;

namespace Bank.Transaction.Api.Application.Resilience;

// Espera antes de cada reintento: crece por GROWTH_FACTOR hasta MAX_DELAY_SECONDS, con una variación aleatoria de ±JITTER.
public static class RetryPolicy
{
    public static TimeSpan NextDelay(int retry, Random random)
    {
        if (retry < 1 || retry > RetryConstants.MAX_RETRIES)
            throw new ArgumentOutOfRangeException(nameof(retry), retry, $"Debe estar entre 1 y {RetryConstants.MAX_RETRIES}.");

        var nominal = Math.Min(
            RetryConstants.BASE_DELAY_SECONDS * Math.Pow(RetryConstants.GROWTH_FACTOR, retry - 1),
            RetryConstants.MAX_DELAY_SECONDS);
        var factor = 1 - RetryConstants.JITTER + random.NextDouble() * 2 * RetryConstants.JITTER;

        return TimeSpan.FromSeconds(nominal * factor);
    }
}
