using Bank.Transfer.Api.Tests.Infrastructure;
using Microsoft.Data.SqlClient;

namespace Bank.Transfer.Api.Tests.TransferInitiated;

// La unicidad por CorrelationId la garantiza la base de datos, no solo la búsqueda previa (research.md R1).
[Collection(SqlServerCollection.Name)]
public class SchemaTests(SqlServerFixture fixture)
{
    [Theory]
    [InlineData("IX_Transfer_CorrelationId")]
    [InlineData("IX_TransferRequest_CorrelationId")]
    public async Task Unique_index_on_correlation_id_exists(string indexName)
    {
        var harness = await TransferHarness.CreateAsync(fixture);

        var count = await harness.ScalarAsync<int>(
            $"SELECT COUNT(*) FROM sys.indexes WHERE name = '{indexName}' AND is_unique = 1");

        Assert.Equal(1, count);
    }

    [Theory]
    [InlineData("Transfer")]
    [InlineData("TransferRequest")]
    public async Task Correlation_id_is_nvarchar_64(string table)
    {
        var harness = await TransferHarness.CreateAsync(fixture);

        var dataType = await harness.ScalarAsync<string>(
            $"SELECT DATA_TYPE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = '{table}' AND COLUMN_NAME = 'CorrelationId'");
        var length = await harness.ScalarAsync<int>(
            $"SELECT CHARACTER_MAXIMUM_LENGTH FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = '{table}' AND COLUMN_NAME = 'CorrelationId'");

        Assert.Equal("nvarchar", dataType);
        Assert.Equal(64, length);
    }

    [Fact]
    public async Task Second_insert_with_the_same_correlation_id_violates_uniqueness()
    {
        var harness = await TransferHarness.CreateAsync(fixture);
        var insert = "INSERT INTO TransferRequest (CorrelationId, Outcome, PayloadFingerprint, CreatedAt) " +
                     "VALUES ('same-id', 'Confirmed', 'x', SYSUTCDATETIME())";
        await harness.ExecuteSqlAsync(insert);

        var exception = await Assert.ThrowsAsync<SqlException>(() => harness.ExecuteSqlAsync(insert));

        Assert.Contains(exception.Number, new[] { 2601, 2627 });
    }
}
