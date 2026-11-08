using Azure.Messaging.ServiceBus;
using System.Text.Json;

// Herramienta manual para validar la idempotencia: publica N copias del mismo transfer-initiated (mismo CorrelationId).
// Uso: dotnet run -- <correlationId> [copias=3] [monto=1500] [evento=transfer-initiated] [clienteId=7] [origen] [destino]
// Para repetir un mensaje real hay que usar el mismo monto, cliente y cuentas; si no, el servicio lo trata como contenido distinto.
// Con los servicios en marcha, Transfer debe registrar una sola transferencia y emitir un solo resultado.
// Cadena de conexión: variable de entorno SERVICEBUSCONSTR, o la del emulador local (no es un secreto).
//
// Modo crudo (mensajes venenosos y reintentos): dotnet run -- --raw <evento> <cuerpo> [retryAttempt]
//   Publica el cuerpo tal cual, sin validarlo, con el Subject indicado; sirve para enviar cuerpos inválidos ("", "null",
//   "{ no es json", sin CorrelationId...). retryAttempt añade la propiedad RetryAttempt (p. ej. 5 para simular reintentos agotados).
//   Ejemplo: dotnet run -- --raw balance-initiated "{\"CorrelationId\":\"abc\",\"CustomerId\":\"x\"}"

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
