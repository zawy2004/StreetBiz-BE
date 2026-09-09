using System;
using System.Collections.Generic;

namespace StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

public partial class AdministrativeUnit
{
    public int unit_id { get; set; }

    public string unit_type { get; set; } = null!;

    public string unit_name { get; set; } = null!;

    public int? parent_unit_id { get; set; }

    public DateTime created_at { get; set; }

    public virtual ICollection<BusinessRegistration> BusinessRegistrations { get; set; } = new List<BusinessRegistration>();

    public virtual ICollection<AdministrativeUnit> Inverseparent_unit { get; set; } = new List<AdministrativeUnit>();

    public virtual ICollection<PenaltyFeeSchedule> PenaltyFeeSchedules { get; set; } = new List<PenaltyFeeSchedule>();

    public virtual ICollection<PricingZone> PricingZones { get; set; } = new List<PricingZone>();

    public virtual ICollection<UserAccount> UserAccounts { get; set; } = new List<UserAccount>();

    public virtual AdministrativeUnit? parent_unit { get; set; }
}
