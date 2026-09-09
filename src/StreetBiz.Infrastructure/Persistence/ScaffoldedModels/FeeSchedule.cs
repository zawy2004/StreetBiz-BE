using System;
using System.Collections.Generic;

namespace StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

public partial class FeeSchedule
{
    public long fee_schedule_id { get; set; }

    public long contract_id { get; set; }

    public int revision { get; set; }

    public decimal total_amount { get; set; }

    public DateTime generated_at { get; set; }

    public DateTime? superseded_at { get; set; }

    public virtual ICollection<FeeScheduleItem> FeeScheduleItems { get; set; } = new List<FeeScheduleItem>();

    public virtual RentalContract contract { get; set; } = null!;
}
