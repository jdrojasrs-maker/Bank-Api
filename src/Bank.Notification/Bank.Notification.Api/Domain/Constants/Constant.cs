namespace Bank.Notification.Api.Domain.Constants;

public class ReceivedSubscriptionsConstants
{
    public const string TRANSACTION_FAILED = "transaction-failed";
    public const string TRANSACTION_COMPLETED = "transaction-completed";
}

public static class DeadLetterReasonConstants
{
    public const string POISON_MESSAGE = "PoisonMessage";
    public const string RETRIES_EXHAUSTED = "RetriesExhausted";
    public const string COMPENSATION_FAILED = "CompensationFailed";
}
public static class RetryConstants
{
    public const int MAX_RETRIES = 5;
    public const int BASE_DELAY_SECONDS = 5;
    public const int GROWTH_FACTOR = 3;
    public const int MAX_DELAY_SECONDS = 300;
    public const double JITTER = 0.2;
    public const string RETRY_ATTEMPT_PROPERTY = "RetryAttempt";
    public const int MAX_DETAIL_LENGTH = 1024;
    public const int MAX_CORRELATION_ID_LENGTH = 64;
}
