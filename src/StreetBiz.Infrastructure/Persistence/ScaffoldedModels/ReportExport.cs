using System;
using System.Collections.Generic;

namespace StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

public partial class ReportExport
{
    public long export_id { get; set; }

    public long requested_by { get; set; }

    public string report_type { get; set; } = null!;

    public string? parameters { get; set; }

    public string export_status { get; set; } = null!;

    public string? file_url { get; set; }

    public string? failure_reason { get; set; }

    public DateTime requested_at { get; set; }

    public DateTime? completed_at { get; set; }

    public virtual UserAccount requested_byNavigation { get; set; } = null!;
}
