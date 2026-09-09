using System;
using System.Collections.Generic;

namespace StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

public partial class ViolationType
{
    public string violation_type_code { get; set; } = null!;

    public string description { get; set; } = null!;

    public bool is_active { get; set; }

    public virtual ICollection<PenaltyFeeSchedule> PenaltyFeeSchedules { get; set; } = new List<PenaltyFeeSchedule>();

    public virtual ICollection<Violation> Violations { get; set; } = new List<Violation>();
}
