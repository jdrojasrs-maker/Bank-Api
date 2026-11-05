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
