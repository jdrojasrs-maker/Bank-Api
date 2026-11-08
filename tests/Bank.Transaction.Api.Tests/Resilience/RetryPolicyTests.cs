using Bank.Transaction.Api.Application.Resilience;

namespace Bank.Transaction.Api.Tests.Resilience;

public class RetryPolicyTests
{
    private sealed class FixedRandom(double value) : Random
    {
        public override double NextDouble() => value;
    }

    private static readonly double[] Nominals = { 5, 15, 45, 135, 300 };

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Delay_stays_within_jitter_bounds_of_the_nominal(int retry)
    {
        var nominal = Nominals[retry - 1];
        var delay = RetryPolicy.NextDelay(retry, new Random(42)).TotalSeconds;

        Assert.InRange(delay, nominal * 0.8, nominal * 1.2);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Minimum_and_maximum_random_give_exactly_0_8_and_1_2_times_nominal(int retry)
    {
        var nominal = Nominals[retry - 1];

        Assert.Equal(nominal * 0.8, RetryPolicy.NextDelay(retry, new FixedRandom(0)).TotalSeconds, 6);
        Assert.Equal(nominal * 1.2, RetryPolicy.NextDelay(retry, new FixedRandom(1)).TotalSeconds, 6);
    }

    [Fact]
    public void Nominals_grow_strictly_until_the_cap()
    {
        var neutral = Enumerable.Range(1, 5).Select(r => RetryPolicy.NextDelay(r, new FixedRandom(0.5)).TotalSeconds).ToArray();

        Assert.True(neutral[0] < neutral[1] && neutral[1] < neutral[2] && neutral[2] < neutral[3] && neutral[3] < neutral[4]);
    }

    [Fact]
    public void Accumulated_window_is_at_least_five_minutes()
    {
        var total = Enumerable.Range(1, 5).Sum(r => RetryPolicy.NextDelay(r, new FixedRandom(0.5)).TotalSeconds);

        Assert.True(total >= 300, $"ventana acumulada {total} s");
    }

    [Fact]
    public void Last_retry_does_not_exceed_cap_plus_jitter()
    {
        Assert.True(RetryPolicy.NextDelay(5, new FixedRandom(1)).TotalSeconds <= 300 * 1.2 + 0.001);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(6)]
    public void Retry_out_of_range_throws(int retry)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RetryPolicy.NextDelay(retry, new Random(1)));
    }
}
