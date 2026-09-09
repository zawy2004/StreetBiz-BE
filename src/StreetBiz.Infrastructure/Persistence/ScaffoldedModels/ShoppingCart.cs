using System;
using System.Collections.Generic;

namespace StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

public partial class ShoppingCart
{
    public long cart_id { get; set; }

    public long customer_user_id { get; set; }

    public long storefront_id { get; set; }

    public string cart_status { get; set; } = null!;

    public DateTime created_at { get; set; }

    public virtual ICollection<ShoppingCartItem> ShoppingCartItems { get; set; } = new List<ShoppingCartItem>();

    public virtual UserAccount customer_user { get; set; } = null!;

    public virtual Storefront storefront { get; set; } = null!;
}
