using System;
using System.Collections.Generic;

namespace StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

public partial class PaymentTransaction
{
    public long transaction_id { get; set; }

    public string idempotency_key { get; set; } = null!;

    public string payment_purpose { get; set; } = null!;

    public long? fee_item_id { get; set; }

    public long? penalty_id { get; set; }

    public long? order_id { get; set; }

    public string provider { get; set; } = null!;

    public string? provider_reference { get; set; }

    public decimal amount { get; set; }

    public string transaction_status { get; set; } = null!;

    public DateTime? callback_received_at { get; set; }

    public DateTime created_at { get; set; }

    public virtual ICollection<PaymentCallbackEvent> PaymentCallbackEvents { get; set; } = new List<PaymentCallbackEvent>();

    public virtual ICollection<RefundTransaction> RefundTransactions { get; set; } = new List<RefundTransaction>();

    public virtual FeeScheduleItem? fee_item { get; set; }

    public virtual Order? order { get; set; }

    public virtual Penalty? penalty { get; set; }
}
