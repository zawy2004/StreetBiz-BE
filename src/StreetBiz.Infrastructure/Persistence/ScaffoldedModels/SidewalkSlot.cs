using System;
using System.Collections.Generic;

namespace StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

public partial class SidewalkSlot
{
    public long slot_id { get; set; }

    public string slot_code { get; set; } = null!;

    public int zone_id { get; set; }

    public decimal latitude { get; set; }

    public decimal longitude { get; set; }

    public decimal? width_meters { get; set; }

    public decimal? length_meters { get; set; }

    public string slot_status { get; set; } = null!;

    public string source { get; set; } = null!;

    public long? proposed_by_registration_id { get; set; }

    public string? proposal_review_status { get; set; }

    public string? proposal_photo_url { get; set; }

    public long? proposal_reviewed_by { get; set; }

    public string? proposal_reviewer_role { get; set; }

    public string? proposal_review_reason { get; set; }

    public DateTime created_at { get; set; }

    public virtual ICollection<AddressChangeRequest> AddressChangeRequests { get; set; } = new List<AddressChangeRequest>();

    public virtual ICollection<RentalApplication> RentalApplications { get; set; } = new List<RentalApplication>();

    public virtual ICollection<RentalContract> RentalContracts { get; set; } = new List<RentalContract>();

    public virtual UserAccount? UserAccount { get; set; }

    public virtual ICollection<VendorReport> VendorReports { get; set; } = new List<VendorReport>();

    public virtual ICollection<Violation> Violations { get; set; } = new List<Violation>();

    public virtual BusinessRegistration? proposed_by_registration { get; set; }

    public virtual PricingZone zone { get; set; } = null!;
}
