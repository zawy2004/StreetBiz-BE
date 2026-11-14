using StreetBiz.Application.Common.Exceptions;

namespace StreetBiz.Application.Common.Interfaces;

/// <summary>Who a receiving account belongs to (PayeeAccounts.owner_type).</summary>
public static class PayeeOwnerTypes
{
    /// <summary>A household: its orders are paid into it.</summary>
    public const string Vendor = "VENDOR";

    /// <summary>A ward: the rental fees of its slots are paid into it.</summary>
    public const string Ward = "WARD";
}

/// <summary>PayeeAccounts.status values.</summary>
public static class PayeeAccountStatuses
{
    public const string Active = "ACTIVE";

    /// <summary>The provider refused the keys (changed or revoked): nothing is offered until reconnected.</summary>
    public const string Error = "ERROR";

    /// <summary>Disconnected by its owner. Kept so late webhooks of earlier payments still verify.</summary>
    public const string Disabled = "DISABLED";
}

/// <summary>
/// The keys of one payee's payOS channel, decrypted for a single request. Never logged:
/// <see cref="ToString"/> shows only the account id and a masked client id.
/// </summary>
public sealed record PayeeCredentials(long PayeeAccountId, string ClientId, string ApiKey, string ChecksumKey)
{
    public override string ToString() =>
        $"PayeeCredentials {{ PayeeAccountId = {PayeeAccountId}, ClientId = {PayeeMasks.Tail(ClientId)} }}";
}

/// <summary>A receiving account as stored, without its keys.</summary>
public sealed record PayeeAccountRow(
    long PayeeAccountId,
    string OwnerType,
    long? VendorId,
    int? WardUnitId,
    string ClientId,
    string? BankName,
    string? AccountNumber,
    string? AccountName,
    string WebhookToken,
    string Status,
    string? LastError,
    DateTime? WebhookConfirmedAt,
    DateTime? VerifiedAt,
    DateTime? LastCheckedAt,
    DateTime CreatedAt,
    DateTime? UpdatedAt)
{
    public bool IsActive => Status == PayeeAccountStatuses.Active;
}

/// <summary>What the provider says about a channel once its keys are accepted.</summary>
public sealed record PayeeChannelInfo(
    string? BankName,
    string? AccountNumber,
    string? AccountName,
    bool WebhookConfirmed);

/// <summary>A new or replaced set of keys for an owner's receiving account.</summary>
public sealed record SavePayeeAccount(
    string OwnerType,
    long? VendorId,
    int? WardUnitId,
    string ClientId,
    string ApiKey,
    string ChecksumKey,
    string WebhookToken,
    PayeeChannelInfo Channel);

/// <summary>Who receives a payment, as the paying side sees it (no keys, account number masked).</summary>
public sealed record PayeeSummary(
    long PayeeAccountId,
    string OwnerType,
    string DisplayName,
    string? BankName,
    string? AccountNumberMasked,
    string? AccountName,
    bool Active);

/// <summary>Receiving accounts of households and wards. Keys are encrypted at rest.</summary>
public interface IPayeeAccountRepository
{
    Task<PayeeAccountRow?> GetForVendorAsync(long vendorId, CancellationToken cancellationToken);

    Task<PayeeAccountRow?> GetForWardAsync(int wardId, CancellationToken cancellationToken);

    /// <summary>Creates the owner's account or replaces its keys in place (same id, so earlier payments stay linked).</summary>
    Task<PayeeAccountRow> SaveAsync(SavePayeeAccount account, long userId, DateTime now, CancellationToken cancellationToken);

    /// <summary>Stops new payments to the account. Its keys stay, so late webhooks of earlier payments still verify.</summary>
    Task<bool> DisableAsync(long payeeAccountId, long userId, DateTime now, CancellationToken cancellationToken);

    /// <summary>The decrypted keys of an account in any status.</summary>
    Task<PayeeCredentials?> GetCredentialsAsync(long payeeAccountId, CancellationToken cancellationToken);

    /// <summary>The account whose webhook URL carries this token (any status), with its keys.</summary>
    Task<(PayeeAccountRow Account, PayeeCredentials Credentials)?> FindByWebhookTokenAsync(
        string webhookToken, CancellationToken cancellationToken);

    /// <summary>
    /// The provider refused the keys: the account goes to ERROR and its owner (the household, or
    /// every officer of the ward) is told to reconnect. Once per error; an account already in
    /// ERROR is left alone.
    /// </summary>
    Task<bool> MarkErrorAsync(long payeeAccountId, string error, DateTime now, CancellationToken cancellationToken);

    /// <summary>A successful check: ACTIVE again, with what the provider reported.</summary>
    Task MarkCheckedAsync(long payeeAccountId, PayeeChannelInfo channel, DateTime now, CancellationToken cancellationToken);

    /// <summary>The ward account that receives this rental-fee instalment (the ward of its slot).</summary>
    Task<PayeeSummary?> GetForFeeItemAsync(long feeItemId, CancellationToken cancellationToken);

    /// <summary>The household account that receives orders from this storefront.</summary>
    Task<PayeeSummary?> GetForStorefrontAsync(long storefrontId, CancellationToken cancellationToken);

    /// <summary>The household account that receives this customer's cart, when it is theirs.</summary>
    Task<PayeeSummary?> GetForCartAsync(long cartId, long customerUserId, CancellationToken cancellationToken);
}

/// <summary>Checks a payee's payOS channel and registers StreetBiz's webhook URL on it.</summary>
public interface IPayeeChannelGateway
{
    /// <summary>The webhook URL for an account's token, or null when no public URL is configured (local development).</summary>
    string? WebhookUrlFor(string webhookToken);

    /// <summary>
    /// Proves the keys work. With a webhook URL, registers it on the channel (payOS sends a test
    /// request to it) and returns the bank account payOS reports. Throws
    /// <see cref="PayeeChannelException"/> when the keys are refused or the provider is unreachable.
    /// </summary>
    Task<PayeeChannelInfo> VerifyChannelAsync(
        PayeeCredentials credentials, string? webhookUrl, CancellationToken cancellationToken);
}

/// <summary>Encrypts a payee's keys before they are stored, and decrypts them for a request.</summary>
public interface ICredentialProtector
{
    /// <summary>False when no key is configured: nothing can be protected, so PAYOS cannot be connected.</summary>
    bool IsConfigured { get; }

    string Protect(string plaintext);

    /// <summary>Throws when the value was tampered with or encrypted under another key.</summary>
    string Unprotect(string protectedValue);
}

/// <summary>A PaymentTransactions row as the payee-aware checkout and sync paths need it.</summary>
public sealed record PaymentCheckoutState(
    long TransactionId,
    string Purpose,
    string Provider,
    string? ProviderReference,
    string IdempotencyKey,
    decimal Amount,
    string Status,
    long? PayeeAccountId,
    string? CheckoutPayload,
    long? OrderId,
    DateTime CreatedAt);

/// <summary>Payee and stored-checkout bookkeeping on PaymentTransactions, for any purpose.</summary>
public interface IPaymentTransactionRepository
{
    Task<PaymentCheckoutState?> GetAsync(long transactionId, CancellationToken cancellationToken);

    /// <summary>Records whose account a PENDING transaction pays into. Done before the link is opened.</summary>
    Task AttachPayeeAsync(long transactionId, long payeeAccountId, CancellationToken cancellationToken);

    /// <summary>Stores the provider's reference and link for the attempt just opened (replacing an earlier attempt's).</summary>
    Task SaveCheckoutAsync(
        long transactionId, string providerReference, string? checkoutPayload, CancellationToken cancellationToken);

    /// <summary>Order payments to a payee still PENDING after <paramref name="createdBefore"/>, for the reconciliation sweep.</summary>
    Task<IReadOnlyList<PaymentCheckoutState>> ListStalePayeeOrderPaymentsAsync(
        DateTime createdBefore, CancellationToken cancellationToken);
}

/// <summary>
/// The payee's channel could not be used: its keys were refused (<see cref="Unauthorized"/>) or
/// the provider failed. 422 to the caller, with a message they can act on.
/// </summary>
public sealed class PayeeChannelException(string message, bool unauthorized = false) : AppException(message)
{
    public bool Unauthorized { get; } = unauthorized;

    public override int StatusCode => 422;

    public override string ErrorCode => "payee_channel";
}

/// <summary>How payee details are shown to anyone but their owner.</summary>
public static class PayeeMasks
{
    /// <summary>"0123456789" → "••••••6789"; short or empty values are shown as they are.</summary>
    public static string Tail(string? value, int keep = 4)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        var trimmed = value.Trim();
        return trimmed.Length <= keep ? trimmed : new string('•', Math.Min(6, trimmed.Length - keep)) + trimmed[^keep..];
    }
}
