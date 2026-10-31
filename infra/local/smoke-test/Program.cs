using Azure.Messaging.ServiceBus;

// Prueba de humo: el filtro de correlación (Label) del Config.json debe enrutar por Subject.
// Uso: dotnet run [-- <evento1> <evento2>]   (por defecto transaction-initiated / balance-initiated)
// Cadena de conexión: variable de entorno SERVICEBUSCONSTR, o la del emulador local (no es un secreto).

const string DefaultConnection =
    "Endpoint=sb://localhost;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;";
const string Topic = "bank-transfer";

var event1 = args.Length > 0 ? args[0] : "transaction-initiated";
var event2 = args.Length > 1 ? args[1] : "balance-initiated";
var connection = Environment.GetEnvironmentVariable("SERVICEBUSCONSTR") ?? DefaultConnection;

await using var client = new ServiceBusClient(connection);
await using var sender = client.CreateSender(Topic);
await using var receiver1 = client.CreateReceiver(Topic, event1);
await using var receiver2 = client.CreateReceiver(Topic, event2);

var failures = 0;

void Check(string description, bool ok)
{
    Console.WriteLine($"  [{(ok ? "OK" : "FALLO")}] {description}");
    if (!ok) failures++;
}

async Task<List<ServiceBusReceivedMessage>> Receive(ServiceBusReceiver receiver, TimeSpan wait)
{
    var received = new List<ServiceBusReceivedMessage>();
    foreach (var message in await receiver.ReceiveMessagesAsync(10, wait))
    {
        await receiver.CompleteMessageAsync(message);
        received.Add(message);
    }
    return received;
}

async Task Send(string subject, string id) =>
    await sender.SendMessageAsync(new ServiceBusMessage($"{{\"testId\":\"{id}\"}}")
    {
        Subject = subject,
        ContentType = "application/json"
    });

Console.WriteLine($"Topic: {Topic} | evento 1: {event1} | evento 2: {event2}\n");

// 0. Limpiar restos de ejecuciones anteriores
await Receive(receiver1, TimeSpan.FromSeconds(2));
await Receive(receiver2, TimeSpan.FromSeconds(2));

// 1. Mensaje con Subject = evento 1
var id1 = Guid.NewGuid().ToString("N");
Console.WriteLine($"1) Enviando mensaje con Subject = \"{event1}\" (testId {id1[..8]})");
await Send(event1, id1);

var onSub1 = await Receive(receiver1, TimeSpan.FromSeconds(10));
var onSub2 = await Receive(receiver2, TimeSpan.FromSeconds(5));
Check($"llega a la suscripción \"{event1}\"",
    onSub1.Count == 1 && onSub1[0].Body.ToString().Contains(id1) && onSub1[0].Subject == event1);
Check($"NO llega a la suscripción \"{event2}\"", onSub2.Count == 0);

// 2. Control inverso: Subject = evento 2
var id2 = Guid.NewGuid().ToString("N");
Console.WriteLine($"\n2) Enviando mensaje con Subject = \"{event2}\" (testId {id2[..8]})");
await Send(event2, id2);

onSub2 = await Receive(receiver2, TimeSpan.FromSeconds(10));
onSub1 = await Receive(receiver1, TimeSpan.FromSeconds(5));
Check($"llega a la suscripción \"{event2}\"",
    onSub2.Count == 1 && onSub2[0].Body.ToString().Contains(id2) && onSub2[0].Subject == event2);
Check($"NO llega a la suscripción \"{event1}\"", onSub1.Count == 0);

Console.WriteLine(failures == 0
    ? "\nRESULTADO: OK - el filtro por Label enruta correctamente según Subject."
    : $"\nRESULTADO: FALLO - {failures} comprobación(es) fallida(s).");
return failures == 0 ? 0 : 1;
