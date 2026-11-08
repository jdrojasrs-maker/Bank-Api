using Bank.Transaction.Api.Domain.Constants;
using Bank.Transaction.Api.Domain.Entities;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Bank.Transaction.Api.Application.Resilience;

// Resultado de revisar un cuerpo antes de procesarlo. CorrelationId es null si no se pudo leer un identificador válido.
public record PoisonCheck(bool IsPoison, string Detail, string? CorrelationId);

// Detecta mensajes venenosos: los que nunca se podrán procesar, por mucho que se reintenten (ver data-model.md).
public static class MessageClassifier
{
    public static PoisonCheck Check(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return Poison("El cuerpo del mensaje está vacío", null);

        JToken token;
        try
        {
            token = Load(body);
        }
        catch (JsonReaderException ex)
        {
            return Poison($"El cuerpo no es JSON válido: {ex.Message}", null);
        }

        if (token is not JObject json)
            return Poison("El cuerpo es JSON pero no un objeto (es null, un arreglo o un valor suelto)", null);

        var correlationId = ReadRaw(json);
        if (string.IsNullOrWhiteSpace(correlationId))
            return Poison("Falta el CorrelationId o está en blanco", null);

        if (correlationId.Length > RetryConstants.MAX_CORRELATION_ID_LENGTH)
            return Poison($"El CorrelationId tiene {correlationId.Length} caracteres y el máximo es " +
                $"{RetryConstants.MAX_CORRELATION_ID_LENGTH} (empieza por '{correlationId[..RetryConstants.MAX_CORRELATION_ID_LENGTH]}')", null);

        try
        {
            JsonConvert.DeserializeObject<TransactionEntity>(body);
        }
        catch (JsonException ex)
        {
            return Poison($"El mensaje no corresponde a {nameof(TransactionEntity)}: {ex.Message}", correlationId);
        }

        return new PoisonCheck(false, string.Empty, correlationId);
    }

    // Lectura tolerante: nunca lanza. Devuelve null si el cuerpo no es un objeto o el identificador falta o excede el máximo.
    public static string? ReadCorrelationId(string body)
    {
        var json = TryLoadObject(body);
        var correlationId = json == null ? null : ReadRaw(json);
        return string.IsNullOrWhiteSpace(correlationId) || correlationId.Length > RetryConstants.MAX_CORRELATION_ID_LENGTH
            ? null
            : correlationId;
    }

    public static int? ReadCustomerId(string body)
    {
        var value = (TryLoadObject(body)?.GetValue("CustomerId", StringComparison.OrdinalIgnoreCase) as JValue)?.Value;
        return value switch
        {
            long number when number is >= int.MinValue and <= int.MaxValue => (int)number,
            string text when int.TryParse(text, out var parsed) => parsed,
            _ => null
        };
    }

    private static PoisonCheck Poison(string detail, string? correlationId) => new(true, detail, correlationId);

    private static JToken Load(string body)
    {
        using var reader = new JsonTextReader(new StringReader(body)) { DateParseHandling = DateParseHandling.None };
        return JToken.Load(reader);
    }

    private static JObject? TryLoadObject(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;
        try
        {
            return Load(body) as JObject;
        }
        catch (JsonReaderException)
        {
            return null;
        }
    }

    private static string? ReadRaw(JObject json) =>
        (json.GetValue("CorrelationId", StringComparison.OrdinalIgnoreCase) as JValue)?.Value?.ToString();
}
