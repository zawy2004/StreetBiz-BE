using System;
using System.Collections.Generic;

namespace StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

public partial class Vendor
{
    public long vendor_id { get; set; }

    public long user_id { get; set; }

    public DateTime created_at { get; set; }

    public virtual ICollection<BusinessRegistration> BusinessRegistrations { get; set; } = new List<BusinessRegistration>();

    public virtual ICollection<Invoice> Invoices { get; set; } = new List<Invoice>();

    public virtual ICollection<RentalContract> RentalContracts { get; set; } = new List<RentalContract>();

    public virtual ICollection<SlotTransferRequest> SlotTransferRequestfrom_vendors { get; set; } = new List<SlotTransferRequest>();

    public virtual ICollection<SlotTransferRequest> SlotTransferRequestto_vendors { get; set; } = new List<SlotTransferRequest>();

    public virtual ICollection<VendorComment> VendorComments { get; set; } = new List<VendorComment>();

    public virtual ICollection<VendorReport> VendorReports { get; set; } = new List<VendorReport>();

    public virtual ICollection<Violation> Violations { get; set; } = new List<Violation>();

    public virtual UserAccount user { get; set; } = null!;
}
