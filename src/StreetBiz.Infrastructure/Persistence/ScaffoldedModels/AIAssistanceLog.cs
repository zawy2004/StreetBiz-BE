using System;
using System.Collections.Generic;

namespace StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

public partial class AIAssistanceLog
{
    public long ai_log_id { get; set; }

    public string feature_code { get; set; } = null!;

    public string entity_type { get; set; } = null!;

    public long entity_id { get; set; }

    public string ai_output { get; set; } = null!;

    public decimal? confidence { get; set; }

    public long? reviewed_by { get; set; }

    public bool? accepted { get; set; }

    public DateTime created_at { get; set; }

    public virtual UserAccount? reviewed_byNavigation { get; set; }
}
