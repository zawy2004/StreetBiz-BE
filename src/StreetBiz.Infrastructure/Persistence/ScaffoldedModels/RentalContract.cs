using System;
using System.Collections.Generic;

namespace StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

public partial class RentalContract
{
    public long contract_id { get; set; }

    public long application_id { get; set; }

    public long slot_id { get; set; }

    public long vendor_id { get; set; }

    public DateOnly start_date { get; set; }

    public DateOnly end_date { get; set; }

    public string contract_status { get; set; } = null!;

    public long? cancelled_by { get; set; }

    public string? cancellation_reason { get; set; }

    public DateTime? cancelled_at { get; set; }

    public DateTime created_at { get; set; }

    public DateTime? updated_at { get; set; }

    public virtual ICollection<AddressChangeRequest> AddressChangeRequests { get; set; } = new List<AddressChangeRequest>();

    public virtual DigitalPermit? DigitalPermit { get; set; }

    public virtual FeeSchedule? FeeSchedule { get; set; }

    public virtual RenewalRequest? RenewalRequest { get; set; }

    public virtual ICollection<SlotTransferRequest> SlotTransferRequests { get; set; } = new List<SlotTransferRequest>();

    public virtual Storefront? Storefront { get; set; }

    public virtual ICollection<Violation> Violations { get; set; } = new List<Violation>();

    public virtual RentalApplication application { get; set; } = null!;

    public virtual UserAccount? cancelled_byNavigation { get; set; }

    public virtual SidewalkSlot slot { get; set; } = null!;

    public virtual Vendor vendor { get; set; } = null!;
}
