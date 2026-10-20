using System;
using System.Collections.Generic;

namespace StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

public partial class WardCompliancePolicy
{
    public int ward_unit_id { get; set; }

    public string? ward_unit_type { get; set; }

    public int? violation_threshold_count { get; set; }

    public int? violation_window_days { get; set; }

    public int? unpaid_penalty_grace_days { get; set; }

    public long updated_by { get; set; }

    public DateTime updated_at { get; set; }

    public virtual AdministrativeUnit? AdministrativeUnit { get; set; }

    public virtual UserAccount? UserAccount { get; set; }
}
