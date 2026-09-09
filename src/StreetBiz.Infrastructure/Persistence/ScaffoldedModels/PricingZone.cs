using System;
using System.Collections.Generic;

namespace StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

public partial class PricingZone
{
    public int zone_id { get; set; }

    public int ward_unit_id { get; set; }

    public string? ward_unit_type { get; set; }

    public string zone_name { get; set; } = null!;

    public decimal price_per_day { get; set; }

    public TimeOnly? available_from { get; set; }

    public TimeOnly? available_to { get; set; }

    public long created_by { get; set; }

    public string? creator_role { get; set; }

    public DateTime created_at { get; set; }

    public virtual AdministrativeUnit? AdministrativeUnit { get; set; }

    public virtual ICollection<SidewalkSlot> SidewalkSlots { get; set; } = new List<SidewalkSlot>();

    public virtual UserAccount? UserAccount { get; set; }
}
