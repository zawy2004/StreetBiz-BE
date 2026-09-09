using System;
using System.Collections.Generic;

namespace StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

public partial class DigitalPermit
{
    public long permit_id { get; set; }

    public long contract_id { get; set; }

    public string qr_payload { get; set; } = null!;

    public string permit_status { get; set; } = null!;

    public DateTime issued_at { get; set; }

    public DateTime? suspended_at { get; set; }

    public string? suspension_reason { get; set; }

    public DateTime? revoked_at { get; set; }

    public string? revocation_reason { get; set; }

    public virtual ICollection<PermitScanLog> PermitScanLogs { get; set; } = new List<PermitScanLog>();

    public virtual ICollection<VendorReport> VendorReports { get; set; } = new List<VendorReport>();

    public virtual RentalContract contract { get; set; } = null!;
}
