using Bank.Transfer.Api.Domain.Constants;
using Bank.Transfer.Api.Tests.Infrastructure;

namespace Bank.Transfer.Api.Tests.TransferInitiated;

// FR-008 y FR-010: mensajes inválidos no crean registros; el mismo identificador con contenido distinto conserva el primero.
[Collection(SqlServerCollection.Name)]
public class InvalidAndMismatchedMessageTests(SqlServerFixture fixture)
{
    public static TheoryData<string, string> InvalidMessages => new()
    {
        { "sin CorrelationId", """{"Amount":1500,"SourceAccount":"1","DestinationAccount":"2","CustomerId":7}""" },
        { "CorrelationId nulo", TransferHarness.Message(null) },
        { "CorrelationId vacío", TransferHarness.Message("") },
        { "CorrelationId solo espacios", TransferHarness.Message("   ") },
        { "CorrelationId de más de 64 caracteres", TransferHarness.Message(new string('a', 65)) },
        { "JSON inválido", "esto no es json" },
        { "cuerpo null", "null" }
    };

    [Theory]
    [MemberData(nameof(InvalidMessages))]
    public async Task Invalid_message_creates_nothing_emits_nothing_and_is_recorded(string description, string message)
    {
        var harness = await TransferHarness.CreateAsync(fixture);

        await harness.DeliverAsync(message);   // no debe lanzar: el mensaje se completa

        Assert.True(await harness.CountTransfersAsync() == 0, description);
        Assert.True(await harness.CountRequestsAsync() == 0, description);
        Assert.Empty(harness.Sender.Sent);
        Assert.Contains(IgnoredMessageReasonConstants.INVALID_MESSAGE, harness.Logger.Reasons);
    }

    [Fact]
    public async Task Same_identifier_with_different_content_keeps_the_first_and_records_the_mismatch()
    {
        var harness = await TransferHarness.CreateAsync(fixture);
        var correlationId = TransferHarness.NewCorrelationId();

        await harness.DeliverAsync(TransferHarness.Message(correlationId, amount: 1500m));
        await harness.DeliverAsync(TransferHarness.Message(correlationId, amount: 9999m));

        Assert.Equal(1, await harness.CountTransfersAsync(correlationId));
        Assert.Equal(
            1500m,
            await harness.ScalarAsync<decimal>($"SELECT Amount FROM Transfer WHERE CorrelationId = '{correlationId}'"));
        Assert.Equal(1, harness.Sender.CountOf(SendSubscriptionConstants.TRANSFER_CONFIRMED));
        Assert.Contains(IgnoredMessageReasonConstants.CONTENT_MISMATCH, harness.Logger.Reasons);
    }
}
