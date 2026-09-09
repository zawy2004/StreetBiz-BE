using System;
using System.Collections.Generic;

namespace StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

public partial class ShoppingCartItem
{
    public long cart_item_id { get; set; }

    public long cart_id { get; set; }

    public long menu_item_id { get; set; }

    public int quantity { get; set; }

    public string? note { get; set; }

    public virtual ShoppingCart cart { get; set; } = null!;

    public virtual MenuItem menu_item { get; set; } = null!;
}
