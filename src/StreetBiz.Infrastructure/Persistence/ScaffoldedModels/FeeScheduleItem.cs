using System;
using System.Collections.Generic;

namespace StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

public partial class FeeScheduleItem
{
    public long fee_item_id { get; set; }

    public long fee_schedule_id { get; set; }

    public DateOnly due_date { get; set; }

    public decimal amount { get; set; }

    public string item_status { get; set; } = null!;

    public DateTime? paid_at { get; set; }

    public virtual ICollection<Invoice> Invoices { get; set; } = new List<Invoice>();

    public virtual ICollection<PaymentTransaction> PaymentTransactions { get; set; } = new List<PaymentTransaction>();

    public virtual FeeSchedule fee_schedule { get; set; } = null!;
}
