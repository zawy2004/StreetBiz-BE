using System;
using System.Collections.Generic;

namespace StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

public partial class OtpChallenge
{
    public long challenge_id { get; set; }

    public string phone_number { get; set; } = null!;

    public string purpose { get; set; } = null!;

    public byte[] code_hash { get; set; } = null!;

    public byte attempt_count { get; set; }

    public byte max_attempts { get; set; }

    public DateTime expires_at { get; set; }

    public DateTime? consumed_at { get; set; }

    public DateTime created_at { get; set; }
}
