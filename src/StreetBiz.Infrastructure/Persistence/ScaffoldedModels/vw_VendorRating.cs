using System;
using System.Collections.Generic;

namespace StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

public partial class vw_VendorRating
{
    public long vendor_id { get; set; }

    public int verified_count { get; set; }

    public decimal? verified_rating { get; set; }

    public int community_count { get; set; }

    public decimal? community_rating { get; set; }
}
