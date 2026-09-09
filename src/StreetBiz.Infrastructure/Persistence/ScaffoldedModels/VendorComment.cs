using System;
using System.Collections.Generic;

namespace StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

public partial class VendorComment
{
    public long comment_id { get; set; }

    public long vendor_id { get; set; }

    public long customer_user_id { get; set; }

    public short? rating { get; set; }

    public string? comment_text { get; set; }

    public DateTime created_at { get; set; }

    public virtual UserAccount customer_user { get; set; } = null!;

    public virtual Vendor vendor { get; set; } = null!;
}
