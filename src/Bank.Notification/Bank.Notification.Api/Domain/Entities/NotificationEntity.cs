using Newtonsoft.Json;

namespace Bank.Notification.Api.Domain.Entities;

public class NotificationEntity
{
    [JsonProperty("id")]
    public string Id { get; set; }
    [JsonProperty("correlationId")]
    public string CorrelationId { get; set; }
    [JsonProperty("notificationDate")]
    public DateTime NotificationDate { get; set; }
    [JsonProperty("type")]
    public string Type { get; set; }
    [JsonProperty("content")]
    public string Content { get; set; }
    [JsonProperty("transactionState")]
    public bool TransactionState { get; set; }
    [JsonProperty("customerId")]
    public int CustomerId { get; set; }
}
