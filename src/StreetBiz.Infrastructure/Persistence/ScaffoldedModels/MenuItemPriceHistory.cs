using System;
using System.Collections.Generic;

namespace StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

public partial class MenuItemPriceHistory
{
    public long price_history_id { get; set; }

    public long menu_item_id { get; set; }

    public long storefront_id { get; set; }

    public string item_name { get; set; } = null!;

    public string? item_name_en { get; set; }

    public string price_unit { get; set; } = null!;

    public decimal unit_price { get; set; }

    public bool is_listed { get; set; }

    public string change_kind { get; set; } = null!;

    public long changed_by { get; set; }

    public DateTime changed_at { get; set; }

    public virtual ICollection<VendorReport> VendorReports { get; set; } = new List<VendorReport>();

    public virtual UserAccount changed_byNavigation { get; set; } = null!;

    public virtual MenuItem menu_item { get; set; } = null!;

    public virtual Storefront storefront { get; set; } = null!;
}
