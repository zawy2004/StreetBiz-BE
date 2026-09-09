using System;
using System.Collections.Generic;

namespace StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

public partial class Penalty
{
    public long penalty_id { get; set; }

    public long violation_id { get; set; }

    public int penalty_schedule_id { get; set; }

    public decimal amount { get; set; }

    public string penalty_status { get; set; } = null!;

    public DateTime created_at { get; set; }

    public DateTime? paid_at { get; set; }

    public long? waived_by { get; set; }

    public string? waiver_role { get; set; }

    public string? waiver_reason { get; set; }

    public DateTime? waived_at { get; set; }

    public virtual ICollection<Invoice> Invoices { get; set; } = new List<Invoice>();

    public virtual ICollection<PaymentTransaction> PaymentTransactions { get; set; } = new List<PaymentTransaction>();

    public virtual UserAccount? UserAccount { get; set; }

    public virtual PenaltyFeeSchedule penalty_schedule { get; set; } = null!;

    public virtual Violation violation { get; set; } = null!;
}
