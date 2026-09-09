using System;
using System.Collections.Generic;

namespace StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

public partial class OrderStatusHistory
{
    public long history_id { get; set; }

    public long order_id { get; set; }

    public string? from_status { get; set; }

    public string to_status { get; set; } = null!;

    public long? changed_by { get; set; }

    public string? note { get; set; }

    public DateTime changed_at { get; set; }

    public virtual UserAccount? changed_byNavigation { get; set; }

    public virtual Order order { get; set; } = null!;
}
