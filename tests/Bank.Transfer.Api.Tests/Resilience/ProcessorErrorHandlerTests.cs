using Bank.Transfer.Api.Application.Resilience;
using Bank.Transfer.Api.Tests.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Bank.Transfer.Api.Tests.Resilience;

// Historia 4: los errores del procesador (conexión, entidad, autorización) se registran; nunca apartan ni compensan.
public class ProcessorErrorHandlerTests
{
    [Fact]
    public void Processor_error_is_logged_as_error_with_source_entity_namespace_and_exception()
    {
        var logger = new CapturingLogger<ProcessorErrorHandlerTests>();
        var exception = new InvalidOperationException("conexión perdida");

        ProcessorErrorHandler.Log(logger, exception, "Receive", "bank-transfer/subscriptions/transfer-initiated",
            "localhost", cancellationRequested: false);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Equal("Receive", entry.Properties["ErrorSource"]);
        Assert.Equal("bank-transfer/subscriptions/transfer-initiated", entry.Properties["EntityPath"]);
        Assert.Equal("localhost", entry.Properties["FullyQualifiedNamespace"]);
        Assert.Same(exception, entry.Exception);
    }

    [Fact]
    public void Cancellation_during_shutdown_is_logged_as_debug_not_error()
    {
        var logger = new CapturingLogger<ProcessorErrorHandlerTests>();

        ProcessorErrorHandler.Log(logger, new OperationCanceledException(), "Receive", "entity", "localhost",
            cancellationRequested: true);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Debug, entry.Level);
    }

    [Fact]
    public void Other_errors_during_shutdown_are_still_errors()
    {
        var logger = new CapturingLogger<ProcessorErrorHandlerTests>();

        ProcessorErrorHandler.Log(logger, new InvalidOperationException("x"), "Receive", "entity", "localhost",
            cancellationRequested: true);

        Assert.Equal(LogLevel.Error, Assert.Single(logger.Entries).Level);
    }

    [Fact]
    public void Handler_has_no_dependency_on_message_actions()
    {
        var parameters = typeof(ProcessorErrorHandler).GetMethod(nameof(ProcessorErrorHandler.Log))!.GetParameters();

        Assert.DoesNotContain(parameters, p => p.ParameterType == typeof(IMessageActions));
    }
}
