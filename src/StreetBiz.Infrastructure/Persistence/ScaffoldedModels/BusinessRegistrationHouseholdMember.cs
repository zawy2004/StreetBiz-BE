using System;

namespace StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

/// <summary>
/// A household member co-registering the hộ kinh doanh and their capital
/// contribution (Mẫu số 01 Phụ lục II, Thông tư 68/2025/TT-BTC, "Thành viên
/// hộ gia đình cùng góp vốn"). Pending schema: see
/// docs/business-registration-real-requirements-schema.sql.
/// </summary>
public partial class BusinessRegistrationHouseholdMember
{
    public long member_id { get; set; }

    public long registration_id { get; set; }

    public string full_name { get; set; } = null!;

    public DateOnly? date_of_birth { get; set; }

    public string? id_number { get; set; }

    public string? relationship_to_owner { get; set; }

    public decimal? capital_contribution { get; set; }

    public DateTime created_at { get; set; }

    public virtual BusinessRegistration registration { get; set; } = null!;
}
