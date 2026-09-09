using System;
using System.Collections.Generic;

namespace StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

public partial class UserDevice
{
    public long user_device_id { get; set; }

    public long user_id { get; set; }

    public string device_identifier { get; set; } = null!;

    public string platform { get; set; } = null!;

    public string? push_token { get; set; }

    public bool is_active { get; set; }

    public DateTime? last_seen_at { get; set; }

    public DateTime created_at { get; set; }

    public virtual UserAccount user { get; set; } = null!;
}
