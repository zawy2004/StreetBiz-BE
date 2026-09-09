using System;
using System.Collections.Generic;

namespace StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

public partial class FoodCategory
{
    public int category_id { get; set; }

    public string category_name { get; set; } = null!;

    public long created_by { get; set; }

    public string? creator_role { get; set; }

    public virtual ICollection<MenuItem> MenuItems { get; set; } = new List<MenuItem>();

    public virtual UserAccount? UserAccount { get; set; }
}
