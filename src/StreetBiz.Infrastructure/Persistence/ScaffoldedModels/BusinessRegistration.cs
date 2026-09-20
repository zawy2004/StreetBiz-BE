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

    public bool fast_track_flag { get; set; }

    public long? reviewed_by { get; set; }

    public string? reviewer_role { get; set; }

    public string? review_decision_reason { get; set; }

    public DateTime? reviewed_at { get; set; }

    public DateTime created_at { get; set; }

    public DateTime? updated_at { get; set; }

    public virtual AddressChangeRequest? AddressChangeRequest { get; set; }

    public virtual AdministrativeUnit? AdministrativeUnit { get; set; }

    public virtual ICollection<RegistrationEvidence> RegistrationEvidences { get; set; } = new List<RegistrationEvidence>();

    public virtual ICollection<RentalApplication> RentalApplications { get; set; } = new List<RentalApplication>();

    public virtual ICollection<SidewalkSlot> SidewalkSlots { get; set; } = new List<SidewalkSlot>();

    public virtual ICollection<SlotHold> SlotHolds { get; set; } = new List<SlotHold>();

    public virtual Storefront? Storefront { get; set; }

    public virtual UserAccount? UserAccount { get; set; }

    public virtual Vendor vendor { get; set; } = null!;
}
