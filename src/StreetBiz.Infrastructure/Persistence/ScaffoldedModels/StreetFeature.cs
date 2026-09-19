using System;
using System.Collections.Generic;

namespace StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

public partial class StreetFeature
{
    public int feature_id { get; set; }

    public int zone_id { get; set; }

    public string feature_type { get; set; } = null!;

    public string label { get; set; } = null!;

    public decimal latitude { get; set; }

    public decimal longitude { get; set; }

    public bool blocks_business { get; set; }

    public string? note { get; set; }

    public virtual PricingZone zone { get; set; } = null!;
}
