using System;
using System.Collections.Generic;

namespace StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

public partial class OrderItem
{
    public long order_item_id { get; set; }

    public long order_id { get; set; }

    public long menu_item_id { get; set; }

    public string item_name_snapshot { get; set; } = null!;

    public decimal unit_price_snapshot { get; set; }

    public int quantity { get; set; }

    public string? note { get; set; }

    public virtual MenuItem menu_item { get; set; } = null!;

    public virtual Order order { get; set; } = null!;
}
