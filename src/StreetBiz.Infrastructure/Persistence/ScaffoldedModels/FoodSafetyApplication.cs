using System;
using System.Collections.Generic;

namespace StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

public partial class FoodSafetyApplication
{
    public long application_id { get; set; }

    public long storefront_id { get; set; }

    public long vendor_id { get; set; }

    public string application_status { get; set; } = null!;

    public string? vendor_note { get; set; }

    public long? reviewed_by { get; set; }

    public string? review_reason { get; set; }

    public DateTime? reviewed_at { get; set; }

    public DateTime? forwarded_at { get; set; }

    public string? department_name { get; set; }

    public string? certificate_number { get; set; }

    public DateOnly? issued_on { get; set; }

    public DateOnly? expires_on { get; set; }

    public string? result_reason { get; set; }

    public long? result_recorded_by { get; set; }

    public DateTime? result_recorded_at { get; set; }

    public DateTime submitted_at { get; set; }

    public DateTime created_at { get; set; }

    public DateTime? updated_at { get; set; }

    public virtual ICollection<FoodSafetyApplicationItem> FoodSafetyApplicationItems { get; set; } = new List<FoodSafetyApplicationItem>();

    public virtual ICollection<FoodSafetyEvidence> FoodSafetyEvidences { get; set; } = new List<FoodSafetyEvidence>();

    public virtual Storefront storefront { get; set; } = null!;

    public virtual Vendor vendor { get; set; } = null!;
}

public partial class FoodSafetyApplicationItem
{
    public long application_id { get; set; }

    public long menu_item_id { get; set; }

    public virtual FoodSafetyApplication application { get; set; } = null!;

    public virtual MenuItem menu_item { get; set; } = null!;
}

public partial class FoodSafetyEvidence
{
    public long evidence_id { get; set; }

    public long application_id { get; set; }

    public string evidence_type { get; set; } = null!;

    public string file_url { get; set; } = null!;

    public DateTime uploaded_at { get; set; }

    public virtual FoodSafetyApplication application { get; set; } = null!;
}
