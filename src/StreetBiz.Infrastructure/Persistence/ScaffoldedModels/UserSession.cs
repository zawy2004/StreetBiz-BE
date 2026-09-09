using System;
using System.Collections.Generic;

namespace StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

public partial class UserSession
{
    public long session_id { get; set; }

    public long user_id { get; set; }

    public byte[] refresh_token_hash { get; set; } = null!;

    public string? device_info { get; set; }

    public string? ip_address { get; set; }

    public DateTime expires_at { get; set; }

    public DateTime created_at { get; set; }

    public DateTime? last_active_at { get; set; }

    public DateTime? revoked_at { get; set; }

    public virtual UserAccount user { get; set; } = null!;
}
