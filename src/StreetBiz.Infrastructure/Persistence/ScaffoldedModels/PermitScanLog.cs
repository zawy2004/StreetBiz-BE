using System;
using System.Collections.Generic;

namespace StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

public partial class PermitScanLog
{
    public long scan_id { get; set; }

    public long? permit_id { get; set; }

    public string qr_payload { get; set; } = null!;

    public long? scanned_by { get; set; }

    public string scan_context { get; set; } = null!;

    public string? scanner_role { get; set; }

    public string scan_result { get; set; } = null!;

    public string? photo_url { get; set; }

    public decimal? latitude { get; set; }

    public decimal? longitude { get; set; }

    public DateTime scanned_at { get; set; }

    public virtual UserAccount? UserAccount { get; set; }

    public virtual DigitalPermit? permit { get; set; }
}
