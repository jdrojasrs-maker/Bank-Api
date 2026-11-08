using Bank.Transaction.Api.Application.Resilience;
using Bank.Transaction.Api.Tests.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Bank.Transaction.Api.Tests.Resilience;

// Historia 4: cada mensaje apartado o reprogramado deja una entrada con los parámetros del contrato de registro.
public class ProcessorLoggingTests
{
    private const string VALID = """{"CorrelationId":"c-1","CustomerId":7}""";

    private static (ResilientMessageProcessor Processor, CapturingLogger<ResilientMessageProcessor> Logger) Create(
        Func<string, string, CancellationToken, Task>? process = null)
    {
        var logger = new CapturingLogger<ResilientMessageProcessor>();
        var processor = new ResilientMessageProcessor(process ?? ((_, _, _) => throw new TimeoutException("db caída")),
            new FakeFailureCompensator(), logger, new Random(7));
        return (processor, logger);
    }

    [Fact]
    public async Task Poison_message_logs_error_with_all_fields()
    {
        var (processor, logger) = Create();

        await processor.HandleAsync(
            new IncomingMessage("""{"CorrelationId":"c-1","CustomerId":"abc"}""", "some-event", "m-1", 0),
            new FakeMessageActions(), CancellationToken.None);

        var entry = Assert.Single(logger.Entries, e => e.Level == LogLevel.Error);
        Assert.Equal("c-1", entry.Properties["CorrelationId"]);
        Assert.Equal("m-1", entry.Properties["MessageId"]);
        Assert.Equal("some-event", entry.Properties["MessageType"]);
        Assert.Equal(1, entry.Properties["Attempt"]);
        Assert.Equal("Poison", entry.Properties["Classification"]);
        Assert.Equal("PoisonMessage", entry.Properties["Reason"]);
    }

    [Fact]
    public async Task Transient_failure_with_retry_logs_warning_with_delay_and_exception()
    {
        var (processor, logger) = Create();

        await processor.HandleAsync(new IncomingMessage(VALID, "some-event", "m-1", 2), new FakeMessageActions(), CancellationToken.None);

        var entry = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Equal("c-1", entry.Properties["CorrelationId"]);
        Assert.Equal("m-1", entry.Properties["MessageId"]);
        Assert.Equal("some-event", entry.Properties["MessageType"]);
        Assert.Equal(3, entry.Properties["Attempt"]);
        Assert.Equal("Transient", entry.Properties["Classification"]);
        Assert.InRange(Convert.ToDouble(entry.Properties["DelaySeconds"]), 45 * 0.8, 45 * 1.2);
        Assert.IsType<TimeoutException>(entry.Exception);
    }

    [Fact]
    public async Task Exhausted_retries_log_error_with_reason()
    {
        var (processor, logger) = Create();

        await processor.HandleAsync(new IncomingMessage(VALID, "some-event", "m-1", 5), new FakeMessageActions(), CancellationToken.None);

        var entry = Assert.Single(logger.Entries, e => e.Level == LogLevel.Error);
        Assert.Equal("RetriesExhausted", entry.Properties["Reason"]);
        Assert.Equal("Transient", entry.Properties["Classification"]);
        Assert.Equal(6, entry.Properties["Attempt"]);
        Assert.Equal("c-1", entry.Properties["CorrelationId"]);
    }

    [Fact]
    public async Task Unreadable_correlation_id_logs_null_and_keeps_message_id()
    {
        var (processor, logger) = Create();

        await processor.HandleAsync(new IncomingMessage("{ no es json", "some-event", "m-9", 0),
            new FakeMessageActions(), CancellationToken.None);

        var entry = Assert.Single(logger.Entries, e => e.Level == LogLevel.Error);
        Assert.True(entry.Properties.ContainsKey("CorrelationId"));
        Assert.Null(entry.Properties["CorrelationId"]);
        Assert.Equal("m-9", entry.Properties["MessageId"]);
    }
}
