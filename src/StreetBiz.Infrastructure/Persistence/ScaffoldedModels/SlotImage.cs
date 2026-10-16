using System;
using System.Collections.Generic;

namespace StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

public partial class SlotImage
{
    public long image_id { get; set; }

    public long slot_id { get; set; }

    public string image_url { get; set; } = null!;

    public int sort_order { get; set; }

    public DateTime created_at { get; set; }

    public virtual SidewalkSlot slot { get; set; } = null!;
}
