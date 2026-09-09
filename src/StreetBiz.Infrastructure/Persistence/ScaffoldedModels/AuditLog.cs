using System;
using System.Collections.Generic;

namespace StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

public partial class AuditLog
{
    public long audit_id { get; set; }

    public long actor_user_id { get; set; }

    public string action { get; set; } = null!;

    public string entity_type { get; set; } = null!;

    public long entity_id { get; set; }

    public string? details { get; set; }

    public DateTime created_at { get; set; }

    public virtual UserAccount actor_user { get; set; } = null!;
}
