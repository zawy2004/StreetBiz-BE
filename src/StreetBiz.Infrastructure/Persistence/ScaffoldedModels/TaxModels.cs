namespace StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

// Household tax support (db/StreetBiz_SQL_Server.sql, section 12). Mapped by hand in
// Configurations/TaxConfiguration.cs, like the chatbot tables: not part of the original scaffold.

public partial class TaxPolicy
{
    public int policy_id { get; set; }

    public int tax_year { get; set; }

    public int version_no { get; set; }

    public string legal_basis { get; set; } = null!;

    public decimal revenue_threshold { get; set; }

    public decimal einvoice_threshold { get; set; }

    public decimal group2_ceiling { get; set; }

    public decimal group3_ceiling { get; set; }

    public bool pit_deducts_threshold { get; set; }

    public decimal pit_profit_rate_g2 { get; set; }

    public decimal pit_profit_rate_g3 { get; set; }

    public decimal pit_profit_rate_g4 { get; set; }

    public byte annual_notice_month { get; set; }

    public byte annual_notice_day { get; set; }

    public decimal warn_ratio { get; set; }

    public string? note { get; set; }

    public long created_by { get; set; }

    public string? creator_role { get; set; }

    public DateTime created_at { get; set; }

    public virtual ICollection<TaxIndustryRate> TaxIndustryRates { get; set; } = new List<TaxIndustryRate>();
}

public partial class TaxIndustryRate
{
    public int rate_id { get; set; }

    public int policy_id { get; set; }

    public string industry_code { get; set; } = null!;

    public string industry_name { get; set; } = null!;

    public decimal vat_rate { get; set; }

    public decimal pit_rate { get; set; }

    public string? note { get; set; }

    public virtual TaxPolicy policy { get; set; } = null!;
}

public partial class VendorTaxProfile
{
    public long vendor_id { get; set; }

    public string? tax_code { get; set; }

    public string industry_code { get; set; } = "FOOD_SERVICE";

    public bool einvoice_registered { get; set; }

    public bool share_revenue_with_ward { get; set; }

    public DateTime? consent_updated_at { get; set; }

    public DateTime created_at { get; set; }

    public DateTime? updated_at { get; set; }
}

public partial class RevenueEntry
{
    public long entry_id { get; set; }

    public long vendor_id { get; set; }

    public DateOnly entry_date { get; set; }

    public decimal amount { get; set; }

    public string channel { get; set; } = null!;

    public string? note { get; set; }

    public DateTime created_at { get; set; }

    public DateTime? voided_at { get; set; }

    public string? void_reason { get; set; }
}

public partial class TaxDeclaration
{
    public long declaration_id { get; set; }

    public long vendor_id { get; set; }

    public int tax_year { get; set; }

    public string period_type { get; set; } = null!;

    public byte period_no { get; set; }

    public decimal revenue_declared { get; set; }

    public decimal revenue_system { get; set; }

    public DateOnly filed_on { get; set; }

    public string? authority_reference { get; set; }

    public string? receipt_url { get; set; }

    public string? note { get; set; }

    public DateTime created_at { get; set; }

    public DateTime? updated_at { get; set; }
}

public partial class TaxReferral
{
    public long referral_id { get; set; }

    public int ward_unit_id { get; set; }

    public string? ward_unit_type { get; set; }

    public long? vendor_id { get; set; }

    public string reason_code { get; set; } = null!;

    public string source_type { get; set; } = null!;

    public long source_id { get; set; }

    public string summary { get; set; } = null!;

    public DateTime detected_at { get; set; }

    public DateTime created_at { get; set; }

    public string referral_status { get; set; } = "PENDING";

    public long? handled_by { get; set; }

    public DateTime? handled_at { get; set; }

    public string? authority_reference { get; set; }

    public string? handling_note { get; set; }
}

public partial class TaxAnnouncement
{
    public long announcement_id { get; set; }

    public int ward_unit_id { get; set; }

    public string? ward_unit_type { get; set; }

    public string segment { get; set; } = null!;

    public int tax_year { get; set; }

    public string title { get; set; } = null!;

    public string body { get; set; } = null!;

    public int recipient_count { get; set; }

    public long created_by { get; set; }

    public string? creator_role { get; set; }

    public DateTime created_at { get; set; }
}
