using System;
using System.Collections.Generic;

namespace StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

public partial class RefundTransaction
{
    public long refund_id { get; set; }

    public long order_id { get; set; }

    public long payment_transaction_id { get; set; }

    public long? complaint_id { get; set; }

    public string idempotency_key { get; set; } = null!;

    public decimal amount { get; set; }

    public string refund_reason { get; set; } = null!;

    public string provider { get; set; } = null!;

    public string? provider_refund_reference { get; set; }

    public string refund_status { get; set; } = null!;

    public DateTime requested_at { get; set; }

    public DateTime? completed_at { get; set; }

    public virtual Complaint? complaint { get; set; }

    public virtual Order order { get; set; } = null!;

    public virtual PaymentTransaction payment_transaction { get; set; } = null!;
}
