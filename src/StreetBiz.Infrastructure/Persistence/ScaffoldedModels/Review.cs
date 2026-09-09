using System;
using System.Collections.Generic;

namespace StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

public partial class Review
{
    public long review_id { get; set; }

    public long order_id { get; set; }

    public long customer_user_id { get; set; }

    public short rating { get; set; }

    public string? review_text { get; set; }

    public DateTime created_at { get; set; }

    public DateTime? edited_at { get; set; }

    public virtual UserAccount customer_user { get; set; } = null!;

    public virtual Order order { get; set; } = null!;
}
