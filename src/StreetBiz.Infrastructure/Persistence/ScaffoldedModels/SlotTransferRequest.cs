using System;
using System.Collections.Generic;

namespace StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

public partial class SlotTransferRequest
{
    public long transfer_id { get; set; }

    public long contract_id { get; set; }

    public long from_vendor_id { get; set; }

    public long to_vendor_id { get; set; }

    public string transfer_status { get; set; } = null!;

    public DateTime initiated_at { get; set; }

    public DateTime? accepted_at { get; set; }

    public long? reviewed_by { get; set; }

    public string? reviewer_role { get; set; }

    public string? review_decision_reason { get; set; }

    public DateTime? reviewed_at { get; set; }

    public virtual UserAccount? UserAccount { get; set; }

    public virtual RentalContract contract { get; set; } = null!;

    public virtual Vendor from_vendor { get; set; } = null!;

    public virtual Vendor to_vendor { get; set; } = null!;
}
