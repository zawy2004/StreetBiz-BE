using System;
using System.Collections.Generic;

namespace StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

public partial class RegistrationEvidence
{
    public long evidence_id { get; set; }

    public long registration_id { get; set; }

    public string evidence_type { get; set; } = null!;

    public string file_url { get; set; } = null!;

    public string? ocr_extracted_data { get; set; }

    public DateTime uploaded_at { get; set; }

    public DateTime? retention_expires_at { get; set; }

    public virtual BusinessRegistration registration { get; set; } = null!;
}
