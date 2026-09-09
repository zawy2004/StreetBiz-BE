using System;
using System.Collections.Generic;

namespace StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

public partial class RenewalRequest
{
    public long renewal_id { get; set; }

    public long contract_id { get; set; }

    public int requested_term_days { get; set; }

    public string renewal_status { get; set; } = null!;

    public DateOnly? new_end_date { get; set; }

    public long? reviewed_by { get; set; }

    public string? reviewer_role { get; set; }

    public string? review_decision_reason { get; set; }

    public DateTime? reviewed_at { get; set; }

    public DateTime created_at { get; set; }

    public virtual UserAccount? UserAccount { get; set; }

    public virtual RentalContract contract { get; set; } = null!;
}
