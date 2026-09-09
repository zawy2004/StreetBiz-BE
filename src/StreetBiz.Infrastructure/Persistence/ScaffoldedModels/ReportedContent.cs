using System;
using System.Collections.Generic;

namespace StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

public partial class ReportedContent
{
    public long content_report_id { get; set; }

    public string content_type { get; set; } = null!;

    public long content_id { get; set; }

    public long reporter_user_id { get; set; }

    public string reason { get; set; } = null!;

    public string status { get; set; } = null!;

    public long? reviewed_by { get; set; }

    public string? reviewer_role { get; set; }

    public DateTime? reviewed_at { get; set; }

    public DateTime created_at { get; set; }

    public virtual UserAccount? UserAccount { get; set; }

    public virtual UserAccount reporter_user { get; set; } = null!;
}
