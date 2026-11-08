namespace Bank.Balance.Api.Domain.Constants;

public static class CurrentStateConstants
{
    public const string PENDING = "Pending";
    public const string COMPLETED = "Completed";
    public const string CANCELED = "Canceled";
}
public class ReceivedSubscriptionsConstants
{
    public const string BALANCE_INITIATED = "balance-initiated";
    public const string TRANSFER_CONFIRMED_BALANCE = "transfer-confirmed-balance";
    public const string TRANSFER_FAILED_BALANCE = "transfer-failed-balance";
}
public class SendSubscriptionConstants
{
    public const string TRANSFER_INITIATED = "transfer-initiated";
    public const string BALANCE_CONFIRMED = "balance-confirmed";
    public const string BALANCE_FAILED = "balance-failed";
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
