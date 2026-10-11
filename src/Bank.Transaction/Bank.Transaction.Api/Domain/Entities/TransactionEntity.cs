namespace Bank.Transaction.Api.Domain.Entities;

public class TransactionEntity
{
    public int Id { get; set; }
    public string CorrelationId { get; set; }
    public DateTime TransactionDate { get; set; }
    public string CurrentState { get; set; }
    public decimal Amount { get; set; }
    public string SourceAccount { get; set; }
    public string DestinationAccount { get; set; }
    public int CustomerId { get; set; }
}
