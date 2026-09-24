using System;
using System.Collections.Generic;

namespace StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

public partial class BusinessRegistration
{
    public long registration_id { get; set; }

    public long vendor_id { get; set; }

    public string vendor_type { get; set; } = null!;

    public string display_name { get; set; } = null!;

    public string? declared_address { get; set; }

    public decimal? address_latitude { get; set; }

    public decimal? address_longitude { get; set; }

    public int ward_unit_id { get; set; }

    public string? ward_unit_type { get; set; }

    public string registration_status { get; set; } = null!;

    public string? id_number { get; set; }

    public DateTime? biometric_consent_at { get; set; }

    // ---- Chủ hộ kinh doanh (Mẫu số 01 Phụ lục II, Thông tư 68/2025/TT-BTC) ----
    // Schema: see db/StreetBiz_SQL_Server.sql.
    public DateOnly? owner_date_of_birth { get; set; }

    public string? owner_gender { get; set; }

    public string? owner_ethnicity { get; set; }

    public string? owner_nationality { get; set; }

    public string? id_type { get; set; }

    public DateOnly? id_issued_date { get; set; }

    public string? id_issued_place { get; set; }

    public string? permanent_address { get; set; }

    public string? contact_address { get; set; }

    // ---- Ngành nghề, quy mô hộ kinh doanh ----
    public string? business_line { get; set; }

    public string? business_line_code { get; set; }

    public decimal? capital_amount { get; set; }

    public int? labor_count { get; set; }

    public DateOnly? planned_start_date { get; set; }

    // ---- Cam kết an toàn thực phẩm (tự khai, không phải giấy chứng nhận) ----
    public DateTime? food_safety_commitment_at { get; set; }

    // ---- Xác minh danh tính thủ công bởi cán bộ phường (KYC gate, BR-41) ----
    public long? identity_verified_by { get; set; }

    public DateTime? identity_verified_at { get; set; }

    public string? identity_verification_note { get; set; }

    // ---- Kết quả AI đối soát CCCD gần nhất (cache, không phải quyết định) ----
    public string? ai_check_result { get; set; }

    public DateTime? ai_checked_at { get; set; }

    public bool fast_track_flag { get; set; }

    public long? reviewed_by { get; set; }

    public string? reviewer_role { get; set; }

    public string? review_decision_reason { get; set; }

    public DateTime? reviewed_at { get; set; }

    public DateTime created_at { get; set; }

    public DateTime? updated_at { get; set; }

    public virtual AddressChangeRequest? AddressChangeRequest { get; set; }

    public virtual AdministrativeUnit? AdministrativeUnit { get; set; }

    // Schema: see db/StreetBiz_SQL_Server.sql.
    public virtual ICollection<BusinessRegistrationHouseholdMember> HouseholdMembers { get; set; } = new List<BusinessRegistrationHouseholdMember>();

    public virtual ICollection<RegistrationEvidence> RegistrationEvidences { get; set; } = new List<RegistrationEvidence>();

    public virtual ICollection<RentalApplication> RentalApplications { get; set; } = new List<RentalApplication>();

    public virtual ICollection<SidewalkSlot> SidewalkSlots { get; set; } = new List<SidewalkSlot>();

    public virtual ICollection<SlotHold> SlotHolds { get; set; } = new List<SlotHold>();

    public virtual Storefront? Storefront { get; set; }

    public virtual UserAccount? UserAccount { get; set; }

    public virtual Vendor vendor { get; set; } = null!;
}
