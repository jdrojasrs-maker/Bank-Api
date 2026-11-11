using Azure.Messaging.ServiceBus;
using System.Text.Json;

// Manual tool to validate idempotency: publishes N copies of the same transfer-initiated (same CorrelationId).
// Usage: dotnet run -- <correlationId> [copies=3] [amount=1500] [event=transfer-initiated] [customerId=7] [source] [destination]
// To repeat a real message you must use the same amount, customer and accounts; otherwise the service treats it as different content.
// With the services running, Transfer must record a single transfer and emit a single result.
// Connection string: SERVICEBUSCONSTR environment variable, or the local emulator one (not a secret).
//
// Raw mode (poison messages and retries): dotnet run -- --raw <event> <body> [retryAttempt]
//   Publishes the body as is, without validating it, with the given Subject; useful for sending invalid bodies ("", "null",
//   "{ no es json", no CorrelationId...). retryAttempt adds the RetryAttempt property (e.g. 5 to simulate exhausted retries).
//   Example: dotnet run -- --raw balance-initiated "{\"CorrelationId\":\"abc\",\"CustomerId\":\"x\"}"

const string DefaultConnection =
    "Endpoint=sb://localhost;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;";
const string Topic = "bank-transfer";

if (args.Length >= 3 && args[0] == "--raw")
{
    var rawConnection = Environment.GetEnvironmentVariable("SERVICEBUSCONSTR") ?? DefaultConnection;
    await using var rawClient = new ServiceBusClient(rawConnection);
    await using var rawSender = rawClient.CreateSender(Topic);
    var rawMessage = new ServiceBusMessage(args[2]) { Subject = args[1], ContentType = "application/json" };
    if (args.Length > 3)
        rawMessage.ApplicationProperties["RetryAttempt"] = int.Parse(args[3]);
    await rawSender.SendMessageAsync(rawMessage);
    Console.WriteLine($"  enviado {args[1]} (cuerpo de {args[2].Length} caracteres)");
    return 0;
}

if (args.Length == 0 || string.IsNullOrWhiteSpace(args[0]))
{
    Console.WriteLine("Uso: dotnet run -- <correlationId> [copias=3] [monto=1500] [evento=transfer-initiated] [clienteId=7] [origen] [destino]");
    Console.WriteLine("     dotnet run -- --raw <evento> <cuerpo> [retryAttempt]");
    return 1;
}

var correlationId = args[0];
var copies = args.Length > 1 ? int.Parse(args[1]) : 3;
var amount = args.Length > 2 ? decimal.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 1500m;
var eventName = args.Length > 3 ? args[3] : "transfer-initiated";
var customerId = args.Length > 4 ? int.Parse(args[4]) : 7;
var sourceAccount = args.Length > 5 ? args[5] : "111111111111111";
var destinationAccount = args.Length > 6 ? args[6] : "222222222222222";
var connection = Environment.GetEnvironmentVariable("SERVICEBUSCONSTR") ?? DefaultConnection;

var body = JsonSerializer.Serialize(new
{
    CorrelationId = correlationId,
    Amount = amount,
    SourceAccount = sourceAccount,
    DestinationAccount = destinationAccount,
    CustomerId = customerId
});

await using var client = new ServiceBusClient(connection);
await using var sender = client.CreateSender(Topic);

for (var i = 1; i <= copies; i++)
{
    await sender.SendMessageAsync(new ServiceBusMessage(body)
    {
        Subject = eventName,
        ContentType = "application/json"
    });
    Console.WriteLine($"  [{i}/{copies}] enviado {eventName} con CorrelationId {correlationId}");
}

Console.WriteLine("Listo. Comprobar en SQL (ver specs/001-idempotent-transfer-processing/quickstart.md, sección 2).");
return 0;
