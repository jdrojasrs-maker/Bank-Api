namespace Bank.Transfer.Api.Domain.Entities;

public class TransferRequestEntity
{
    public int Id { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
    public string Outcome { get; set; } = string.Empty;
    public string PayloadFingerprint { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? ClaimedAt { get; set; }
    public DateTime? EmittedAt { get; set; }
}
