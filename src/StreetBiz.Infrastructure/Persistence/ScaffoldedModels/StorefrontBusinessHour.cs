using System;
using System.Collections.Generic;

namespace StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

public partial class StorefrontBusinessHour
{
    public long hour_id { get; set; }

    public long storefront_id { get; set; }

    public short day_of_week { get; set; }

    public TimeOnly opens_at { get; set; }

    public TimeOnly closes_at { get; set; }

    public virtual Storefront storefront { get; set; } = null!;
}
