using System;

namespace StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

/// <summary>
/// One recorded eKYC check (CCCD OCR or face match), written where it is computed so the
/// reviewing officer reads a server-side score rather than one the client claimed.
/// Schema: see db/StreetBiz_SQL_Server.sql.
/// </summary>
public partial class KycVerificationResult
{
    public long kyc_result_id { get; set; }

    public long user_id { get; set; }

    /// <summary>Null until the wizard creates the registration these checks belong to.</summary>
    public long? registration_id { get; set; }

    public string check_type { get; set; } = null!;

    public string provider { get; set; } = null!;

    public bool? is_match { get; set; }

    public decimal? similarity_percent { get; set; }

    public int? confidence_percent { get; set; }

    public string? extracted_id_number { get; set; }

    public string? warnings { get; set; }

    public DateTime created_at { get; set; }
}
