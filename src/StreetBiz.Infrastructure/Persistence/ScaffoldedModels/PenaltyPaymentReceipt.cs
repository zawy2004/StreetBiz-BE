namespace StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

/// <summary>
/// PenaltyPaymentReceipts: the receipt a household uploads after paying a fine to the State
/// Treasury, and the ward officer's decision on it.
/// </summary>
public partial class PenaltyPaymentReceipt
{
    public long receipt_id { get; set; }

    public long penalty_id { get; set; }

    public long submitted_by { get; set; }

    public string receipt_url { get; set; } = null!;

    public string? reference { get; set; }

    public DateOnly paid_on { get; set; }

    public string? note { get; set; }

    public string status { get; set; } = null!;

    public DateTime submitted_at { get; set; }

    public long? reviewed_by { get; set; }

    public string? reviewer_role { get; set; }

    public DateTime? reviewed_at { get; set; }

    public string? review_note { get; set; }

    public virtual Penalty penalty { get; set; } = null!;
}
