using System;
using System.Collections.Generic;

namespace StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

public partial class PenaltyFeeSchedule
{
    public int penalty_schedule_id { get; set; }

    public int ward_unit_id { get; set; }

    public string? ward_unit_type { get; set; }

    public string violation_type { get; set; } = null!;

    public decimal penalty_amount { get; set; }

    public DateOnly effective_from { get; set; }

    public DateOnly? effective_to { get; set; }

    public long created_by { get; set; }

    public string? creator_role { get; set; }

    public DateTime created_at { get; set; }

    public virtual AdministrativeUnit? AdministrativeUnit { get; set; }

    public virtual ICollection<Penalty> Penalties { get; set; } = new List<Penalty>();

    public virtual UserAccount? UserAccount { get; set; }

    public virtual ViolationType violation_typeNavigation { get; set; } = null!;
}
