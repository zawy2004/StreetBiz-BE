using System;
using System.Collections.Generic;

namespace StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

public partial class Invoice
{
    public long invoice_id { get; set; }

    public string invoice_number { get; set; } = null!;

    public long? fee_item_id { get; set; }

    public long? penalty_id { get; set; }

    public long vendor_id { get; set; }

    public decimal amount { get; set; }

    public DateTime issued_at { get; set; }

    public virtual FeeScheduleItem? fee_item { get; set; }

    public virtual Penalty? penalty { get; set; }

    public virtual Vendor vendor { get; set; } = null!;
}
