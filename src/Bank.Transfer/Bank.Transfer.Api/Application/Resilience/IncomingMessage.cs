namespace Bank.Transfer.Api.Application.Resilience;

// Lo que el procesador necesita saber de una entrega, sin depender del SDK de Service Bus.
public record IncomingMessage(string Body, string MessageType, string MessageId, int RetryAttempt);
