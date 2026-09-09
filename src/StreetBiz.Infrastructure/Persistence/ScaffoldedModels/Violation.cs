using System;
using System.Collections.Generic;

namespace StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

public partial class Violation
{
    public long violation_id { get; set; }

    public long? contract_id { get; set; }

    public long? slot_id { get; set; }

    public long? vendor_id { get; set; }

    public string violation_type { get; set; } = null!;

    public string? description { get; set; }

    public string? evidence_url { get; set; }

    public long recorded_by { get; set; }

    public string? recorder_role { get; set; }

    public string source { get; set; } = null!;

    public long? source_report_id { get; set; }

    public DateTime recorded_at { get; set; }

    public virtual Penalty? Penalty { get; set; }

    public virtual UserAccount? UserAccount { get; set; }

    public virtual RentalContract? contract { get; set; }

    public virtual SidewalkSlot? slot { get; set; }

    public virtual VendorReport? source_report { get; set; }

    public virtual Vendor? vendor { get; set; }

    public virtual ViolationType violation_typeNavigation { get; set; } = null!;
}
