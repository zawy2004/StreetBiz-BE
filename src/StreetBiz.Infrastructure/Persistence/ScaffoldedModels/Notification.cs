using System;
using System.Collections.Generic;

namespace StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

public partial class Notification
{
    public long notification_id { get; set; }

    public long user_id { get; set; }

    public string notification_type { get; set; } = null!;

    public string title { get; set; } = null!;

    public string body { get; set; } = null!;

    public string? related_entity_type { get; set; }

    public long? related_entity_id { get; set; }

    public bool is_read { get; set; }

    public DateTime sent_at { get; set; }

    public virtual UserAccount user { get; set; } = null!;
}
