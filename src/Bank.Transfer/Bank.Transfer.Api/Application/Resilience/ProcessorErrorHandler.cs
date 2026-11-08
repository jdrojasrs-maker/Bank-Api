namespace Bank.Transfer.Api.Application.Resilience;

// Registra los errores del procesador de Service Bus (conexión, entidad, autorización). Solo registra: no aparta,
// no completa ni compensa mensajes, porque el error no pertenece a un mensaje concreto.
public static class ProcessorErrorHandler
{
    public static void Log(ILogger logger, Exception exception, string errorSource, string entityPath,
        string fullyQualifiedNamespace, bool cancellationRequested)
    {
        // Cierre ordenado del servicio: la cancelación es esperada y no es un fallo.
        if (cancellationRequested && exception is OperationCanceledException)
        {
            logger.LogDebug(exception, "Procesador {ErrorSource} cancelado por cierre ordenado en {EntityPath} ({FullyQualifiedNamespace})",
                errorSource, entityPath, fullyQualifiedNamespace);
            return;
        }

        logger.LogError(exception, "Error del procesador {ErrorSource} en {EntityPath} ({FullyQualifiedNamespace})",
            errorSource, entityPath, fullyQualifiedNamespace);
    }
}
