using System;
using System.Collections.Generic;

namespace StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

public partial class ZoneFeeComponent
{
    public int component_id { get; set; }

    public int zone_id { get; set; }

    public string component_name { get; set; } = null!;

    public string calc_basis { get; set; } = null!;

    public decimal unit_amount { get; set; }

    public int sort_order { get; set; }

    public virtual PricingZone zone { get; set; } = null!;
}
