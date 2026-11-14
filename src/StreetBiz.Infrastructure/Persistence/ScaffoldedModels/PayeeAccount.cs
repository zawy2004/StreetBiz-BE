namespace StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

/// <summary>
/// PayeeAccounts: the payOS channel a household (orders) or a ward (rental fees) is paid into.
/// <c>api_key_protected</c> and <c>checksum_key_protected</c> are encrypted by
/// <c>ICredentialProtector</c>; this row never holds them in clear.
/// </summary>
public partial class PayeeAccount
{
    public long payee_account_id { get; set; }

    public string owner_type { get; set; } = null!;

    public long? vendor_id { get; set; }

    public int? ward_unit_id { get; set; }

    public string? ward_unit_type { get; set; }

    public string provider { get; set; } = null!;

    public string client_id { get; set; } = null!;

    public string api_key_protected { get; set; } = null!;

    public string checksum_key_protected { get; set; } = null!;

    public string? bank_name { get; set; }

    public string? account_number { get; set; }

    public string? account_name { get; set; }

    public string webhook_token { get; set; } = null!;

    public string status { get; set; } = null!;

    public string? last_error { get; set; }

    public DateTime? webhook_confirmed_at { get; set; }

    public DateTime? verified_at { get; set; }

    public DateTime? last_checked_at { get; set; }

    public long created_by { get; set; }

    public DateTime created_at { get; set; }

    public DateTime? updated_at { get; set; }

    public virtual Vendor? vendor { get; set; }

    public virtual ICollection<PaymentTransaction> PaymentTransactions { get; set; } = new List<PaymentTransaction>();
}
