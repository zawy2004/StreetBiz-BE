using System;
using System.Collections.Generic;

namespace StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

public partial class Storefront
{
    public long storefront_id { get; set; }

    public long registration_id { get; set; }

    public long contract_id { get; set; }

    public string storefront_name { get; set; } = null!;

    public string? description { get; set; }

    public string? image_url { get; set; }

    public string availability_status { get; set; } = null!;

    public DateTime created_at { get; set; }

    public DateTime? updated_at { get; set; }

    public virtual ICollection<MenuItem> MenuItems { get; set; } = new List<MenuItem>();

    public virtual ICollection<Order> Orders { get; set; } = new List<Order>();

    public virtual ICollection<ShoppingCart> ShoppingCarts { get; set; } = new List<ShoppingCart>();

    public virtual ICollection<StorefrontBusinessHour> StorefrontBusinessHours { get; set; } = new List<StorefrontBusinessHour>();

    public virtual RentalContract contract { get; set; } = null!;

    public virtual BusinessRegistration registration { get; set; } = null!;
}
