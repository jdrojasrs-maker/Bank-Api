namespace Bank.Transfer.Api.Domain.Constants;

public static class CurrentStateConstants
{
    public const string COMPLETE = "Complete";
}
public class ReceivedSubscriptionsConstants
{
    public const string TRANSFER_INITIATED = "transfer-initiated";
}
public class SendSubscriptionConstants
{
    public const string TRANSFER_CONFIRMED = "transfer-confirmed";
    public const string TRANSFER_FAILED = "transfer-failed";
}
public static class TransferOutcomeConstants
{
    public const string CONFIRMED = "Confirmed";
    public const string FAILED = "Failed";
}
public static class IgnoredMessageReasonConstants
{
    public const string DUPLICATE_ALREADY_EMITTED = "DuplicateAlreadyEmitted";
    public const string DUPLICATE_IN_PROGRESS = "DuplicateInProgress";
    public const string CONTENT_MISMATCH = "ContentMismatch";
    public const string INVALID_MESSAGE = "InvalidMessage";
    public const string RECOVERED_EMISSION = "RecoveredEmission";
}
public static class EmissionConstants
{
    public const int EMISSION_LEASE_SECONDS = 30;
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
