using System;
using System.Collections.Generic;

namespace StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

public partial class RentalApplication
{
    public long application_id { get; set; }

    public long registration_id { get; set; }

    public long slot_id { get; set; }

    public string application_method { get; set; } = null!;

    public int requested_term_days { get; set; }

    public string application_status { get; set; } = null!;

    public long? reviewed_by { get; set; }

    public string? reviewer_role { get; set; }

    public string? review_decision_reason { get; set; }

    public DateTime? reviewed_at { get; set; }

    public DateTime created_at { get; set; }

    public DateTime? commitments_accepted_at { get; set; }

    public virtual RentalContract? RentalContract { get; set; }

    public virtual UserAccount? UserAccount { get; set; }

    public virtual BusinessRegistration registration { get; set; } = null!;

    public virtual SidewalkSlot slot { get; set; } = null!;
}
