using System;
using System.Collections.Generic;

namespace StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

public partial class Complaint
{
    public long complaint_id { get; set; }

    public long order_id { get; set; }

    public long customer_user_id { get; set; }

    public string complaint_type { get; set; } = null!;

    public string description { get; set; } = null!;

    public decimal? requested_refund_amount { get; set; }

    public string status { get; set; } = null!;

    public long? resolved_by { get; set; }

    public string? resolver_role { get; set; }

    public string? resolution_notes { get; set; }

    public DateTime created_at { get; set; }

    public DateTime? resolved_at { get; set; }

    public virtual ICollection<RefundTransaction> RefundTransactions { get; set; } = new List<RefundTransaction>();

    public virtual UserAccount? UserAccount { get; set; }

    public virtual UserAccount customer_user { get; set; } = null!;

    public virtual Order order { get; set; } = null!;
}
