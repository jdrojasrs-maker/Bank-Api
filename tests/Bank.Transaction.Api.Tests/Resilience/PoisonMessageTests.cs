using Bank.Transaction.Api.Application.Resilience;
using Bank.Transaction.Api.Tests.Infrastructure;

namespace Bank.Transaction.Api.Tests.Resilience;

public class PoisonMessageTests
{
    private const string VALID = """{"CorrelationId":"c-1","CustomerId":7}""";

    private static (ResilientMessageProcessor Processor, FakeFailureCompensator Compensator, List<string> Processed)
        Create(Func<string, string, CancellationToken, Task>? process = null)
    {
        var compensator = new FakeFailureCompensator();
        var processed = new List<string>();
        var processor = new ResilientMessageProcessor(process ?? ((body, _, _) =>
        {
            processed.Add(body);
            return Task.CompletedTask;
        }), compensator, new CapturingLogger<ResilientMessageProcessor>());
        return (processor, compensator, processed);
    }

    public static TheoryData<string, string?> PoisonData => PoisonCases.All;

    [Theory]
    [MemberData(nameof(PoisonData))]
    public async Task Poison_body_is_dead_lettered_on_first_delivery_without_processing(string body, string? correlationId)
    {
        var (processor, compensator, processed) = Create();
        var actions = new FakeMessageActions();

        await processor.HandleAsync(new IncomingMessage(body, "some-event", "msg-1", 0), actions, CancellationToken.None);

        Assert.Empty(processed);
        Assert.Equal(new[] { "DeadLetter:PoisonMessage" }, actions.Calls);
        Assert.Equal(1, actions.Attempts);
        Assert.False(string.IsNullOrWhiteSpace(actions.Detail));
        Assert.Equal(correlationId, actions.CorrelationId);
        Assert.Equal(correlationId != null ? 1 : 0, compensator.Calls.Count);
    }

    [Fact]
    public async Task Poison_body_is_dead_lettered_even_when_compensator_does_nothing()
    {
        var (processor, compensator, _) = Create();
        compensator.Result = false;
        var actions = new FakeMessageActions();

        await processor.HandleAsync(
            new IncomingMessage("""{"CorrelationId":"c-1","CustomerId":"abc"}""", "some-event", "msg-1", 0),
            actions, CancellationToken.None);

        Assert.Single(compensator.Calls);
        Assert.Equal(new[] { "DeadLetter:PoisonMessage" }, actions.Calls);
    }

    [Fact]
    public async Task Poison_body_passes_the_message_type_to_the_compensator()
    {
        var (processor, compensator, _) = Create();
        var actions = new FakeMessageActions();
        var body = """{"CorrelationId":"c-1","CustomerId":"abc"}""";

        await processor.HandleAsync(new IncomingMessage(body, "first-step", "msg-1", 0), actions, CancellationToken.None);

        Assert.Equal((body, "first-step"), compensator.Calls[0]);
    }

    [Fact]
    public async Task Attempts_counts_previous_retries()
    {
        var (processor, _, _) = Create();
        var actions = new FakeMessageActions();

        await processor.HandleAsync(new IncomingMessage("null", "some-event", "msg-1", 2), actions, CancellationToken.None);

        Assert.Equal(3, actions.Attempts);
    }

    [Fact]
    public async Task Newtonsoft_json_exception_during_processing_is_treated_as_poison()
    {
        var (processor, compensator, _) = Create((_, _, _) => throw new Newtonsoft.Json.JsonSerializationException("campo inválido"));
        var actions = new FakeMessageActions();

        await processor.HandleAsync(new IncomingMessage(VALID, "some-event", "msg-1", 0), actions, CancellationToken.None);

        Assert.Equal(new[] { "DeadLetter:PoisonMessage" }, actions.Calls);
        Assert.Equal("c-1", actions.CorrelationId);
        Assert.Contains("campo inválido", actions.Detail);
        Assert.Single(compensator.Calls);
    }

    [Fact]
    public async Task System_text_json_exception_during_processing_is_treated_as_poison()
    {
        var (processor, _, _) = Create((_, _, _) => throw new System.Text.Json.JsonException("campo inválido"));
        var actions = new FakeMessageActions();

        await processor.HandleAsync(new IncomingMessage(VALID, "some-event", "msg-1", 0), actions, CancellationToken.None);

        Assert.Equal(new[] { "DeadLetter:PoisonMessage" }, actions.Calls);
    }

    [Fact]
    public async Task Poison_message_is_logged_as_error_with_structured_fields()
    {
        var logger = new CapturingLogger<ResilientMessageProcessor>();
        var processor = new ResilientMessageProcessor((_, _, _) => Task.CompletedTask, new FakeFailureCompensator(), logger);

        await processor.HandleAsync(
            new IncomingMessage("""{"CorrelationId":"c-1","CustomerId":"abc"}""", "some-event", "msg-9", 0),
            new FakeMessageActions(), CancellationToken.None);

        var entry = Assert.Single(logger.Entries, x => x.Level == Microsoft.Extensions.Logging.LogLevel.Error);
        Assert.Equal("c-1", entry.Properties["CorrelationId"]);
        Assert.Equal("msg-9", entry.Properties["MessageId"]);
        Assert.Equal("some-event", entry.Properties["MessageType"]);
        Assert.Equal("Poison", entry.Properties["Classification"]);
        Assert.Equal("PoisonMessage", entry.Properties["Reason"]);
    }
}
