using Bank.Transaction.Api.Tests.Infrastructure;
using Microsoft.Data.SqlClient;

namespace Bank.Transaction.Api.Tests.StateProtection;

// La unicidad por CorrelationId la garantiza la base de datos, no solo la búsqueda previa (research.md R1).
[Collection(SqlServerCollection.Name)]
public class SchemaTests(SqlServerFixture fixture)
{
    [Fact]
    public async Task Unique_index_on_correlation_id_exists()
    {
        var harness = await TransactionHarness.CreateAsync(fixture);

        var count = await harness.ScalarAsync<int>(
            "SELECT COUNT(*) FROM sys.indexes WHERE name = 'IX_Transaction_CorrelationId' AND is_unique = 1");

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Correlation_id_is_nvarchar_64()
    {
        var harness = await TransactionHarness.CreateAsync(fixture);

        var dataType = await harness.ScalarAsync<string>(
            "SELECT DATA_TYPE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'Transaction' AND COLUMN_NAME = 'CorrelationId'");
        var length = await harness.ScalarAsync<int>(
            "SELECT CHARACTER_MAXIMUM_LENGTH FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'Transaction' AND COLUMN_NAME = 'CorrelationId'");

        Assert.Equal("nvarchar", dataType);
        Assert.Equal(64, length);
    }

    [Fact]
    public async Task Second_insert_with_the_same_correlation_id_violates_uniqueness()
    {
        var harness = await TransactionHarness.CreateAsync(fixture);
        var correlationId = await harness.SeedTransactionAsync("Pending");

        var exception = await Assert.ThrowsAsync<Microsoft.EntityFrameworkCore.DbUpdateException>(
            () => harness.SeedTransactionAsync("Pending", correlationId));

        var sql = Assert.IsType<SqlException>(exception.InnerException);
        Assert.Contains(sql.Number, new[] { 2601, 2627 });
    }
}
