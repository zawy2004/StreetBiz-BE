using System;
using System.Collections.Generic;

namespace StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

public partial class MenuItem
{
    public long menu_item_id { get; set; }

    public long storefront_id { get; set; }

    public int category_id { get; set; }

    public string item_name { get; set; } = null!;

    public string? description { get; set; }

    public string? image_url { get; set; }

    public decimal unit_price { get; set; }

    public string availability_status { get; set; } = null!;

    public DateTime created_at { get; set; }

    public DateTime? updated_at { get; set; }

    public virtual ICollection<OrderItem> OrderItems { get; set; } = new List<OrderItem>();

    public virtual ICollection<ShoppingCartItem> ShoppingCartItems { get; set; } = new List<ShoppingCartItem>();

    public virtual FoodCategory category { get; set; } = null!;

    public virtual Storefront storefront { get; set; } = null!;
}
