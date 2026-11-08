namespace Bank.Transfer.Api.Tests.Infrastructure;

// Cuerpos venenosos comunes a los cuatro servicios, con el CorrelationId que debe poder leerse de cada uno (null si ninguno).
public static class PoisonCases
{
    public static TheoryData<string, string?> All => new()
    {
        { "", null },
        { "   ", null },
        { "esto no es json", null },
        { "null", null },
        { "[]", null },
        { """{"CustomerId":7}""", null },
        { """{"CorrelationId":null,"CustomerId":7}""", null },
        { """{"CorrelationId":"   ","CustomerId":7}""", null },
        { "{\"CorrelationId\":\"" + new string('a', 65) + "\",\"CustomerId\":7}", null },
        { """{"CorrelationId":"c-1","CustomerId":"abc"}""", "c-1" },
    };
}
