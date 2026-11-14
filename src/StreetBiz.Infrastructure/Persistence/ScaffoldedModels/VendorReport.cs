using System;
using System.Collections.Generic;

namespace StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

public partial class VendorReport
{
    public long report_id { get; set; }

    public long reporter_user_id { get; set; }

    public long? vendor_id { get; set; }

    public long? slot_id { get; set; }

    public long? scanned_permit_id { get; set; }

    public string report_reason { get; set; } = null!;

    public string? evidence_url { get; set; }

    public string? ai_extracted_location { get; set; }

    public string report_status { get; set; } = null!;

    public long? reviewed_by { get; set; }

    public string? reviewer_role { get; set; }

    public DateTime? reviewed_at { get; set; }

    public DateTime created_at { get; set; }

    public string report_type { get; set; } = null!;

    public long? menu_item_id { get; set; }

    public long? price_history_id { get; set; }

    public int? quantity { get; set; }

    public decimal? posted_amount { get; set; }

    public decimal? charged_amount { get; set; }

    public DateTime? purchased_at { get; set; }

    public DateTime? due_at { get; set; }

    public string? resolution { get; set; }

    public string? resolution_note { get; set; }

    public decimal? refund_amount { get; set; }

    public virtual UserAccount? UserAccount { get; set; }

    public virtual MenuItem? menu_item { get; set; }

    public virtual MenuItemPriceHistory? price_history { get; set; }

    public virtual ICollection<Violation> Violations { get; set; } = new List<Violation>();

    public virtual UserAccount reporter_user { get; set; } = null!;

    public virtual DigitalPermit? scanned_permit { get; set; }

    public virtual SidewalkSlot? slot { get; set; }

    public virtual Vendor? vendor { get; set; }
}
