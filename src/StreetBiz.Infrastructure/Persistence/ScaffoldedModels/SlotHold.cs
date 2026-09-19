using System;
using System.Collections.Generic;

namespace StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

public partial class SlotHold
{
    public long slot_id { get; set; }

    public long registration_id { get; set; }

    public DateTime held_at { get; set; }

    public DateTime expires_at { get; set; }

    public virtual BusinessRegistration registration { get; set; } = null!;

    public virtual SidewalkSlot slot { get; set; } = null!;
}
