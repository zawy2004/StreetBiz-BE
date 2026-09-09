using System;
using System.Collections.Generic;

namespace StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

public partial class vw_PermitValidity
{
    public long permit_id { get; set; }

    public long contract_id { get; set; }

    public string qr_payload { get; set; } = null!;

    public long slot_id { get; set; }

    public long vendor_id { get; set; }

    public DateOnly start_date { get; set; }

    public DateOnly end_date { get; set; }

    public string permit_status { get; set; } = null!;

    public string contract_status { get; set; } = null!;

    public string effective_status { get; set; } = null!;
}
