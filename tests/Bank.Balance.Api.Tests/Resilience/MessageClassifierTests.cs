using Bank.Balance.Api.Application.Resilience;
using Bank.Balance.Api.Tests.Infrastructure;

namespace Bank.Balance.Api.Tests.Resilience;

public class MessageClassifierTests
{
    [Theory]
    [MemberData(nameof(PoisonData))]
    public void Check_poison_body_is_flagged_with_detail_and_readable_correlation_id(string body, string? correlationId)
    {
        var result = MessageClassifier.Check(body);

        Assert.True(result.IsPoison);
        Assert.False(string.IsNullOrWhiteSpace(result.Detail));
        Assert.Equal(correlationId, result.CorrelationId);
    }

    public static TheoryData<string, string?> PoisonData => PoisonCases.All;

    [Fact]
    public void Check_correlation_id_of_65_characters_mentions_the_length()
    {
        var body = "{\"CorrelationId\":\"" + new string('a', 65) + "\",\"CustomerId\":7}";

        var result = MessageClassifier.Check(body);

        Assert.True(result.IsPoison);
        Assert.Contains("65", result.Detail);
    }

    [Fact]
    public void Check_correlation_id_of_exactly_64_characters_is_valid()
    {
        var correlationId = new string('b', 64);
        var body = "{\"CorrelationId\":\"" + correlationId + "\",\"CustomerId\":7}";

        var result = MessageClassifier.Check(body);

        Assert.False(result.IsPoison);
        Assert.Equal(correlationId, result.CorrelationId);
    }

    [Fact]
    public void Check_well_formed_message_is_not_poison()
    {
        var result = MessageClassifier.Check("""{"CorrelationId":"c-1","CustomerId":7}""");

        Assert.False(result.IsPoison);
        Assert.Equal("c-1", result.CorrelationId);
    }

    [Fact]
    public void Check_correlation_id_is_matched_case_insensitively()
    {
        var result = MessageClassifier.Check("""{"correlationId":"c-1","customerId":7}""");

        Assert.False(result.IsPoison);
        Assert.Equal("c-1", result.CorrelationId);
    }
}
