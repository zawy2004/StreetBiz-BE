using System;
using System.Collections.Generic;

namespace StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

public partial class UserAccount
{
    public long user_id { get; set; }

    public string phone_number { get; set; } = null!;

    public string password_hash { get; set; } = null!;

    public string? full_name { get; set; }

    public string role_code { get; set; } = null!;

    public int? ward_unit_id { get; set; }

    public string? ward_unit_type { get; set; }

    public string account_status { get; set; } = null!;

    public DateTime? phone_verified_at { get; set; }

    public DateTime created_at { get; set; }

    public DateTime? updated_at { get; set; }

    public virtual ICollection<AIAssistanceLog> AIAssistanceLogs { get; set; } = new List<AIAssistanceLog>();

    public virtual ICollection<AddressChangeRequest> AddressChangeRequests { get; set; } = new List<AddressChangeRequest>();

    public virtual AdministrativeUnit? AdministrativeUnit { get; set; }

    public virtual ICollection<AuditLog> AuditLogs { get; set; } = new List<AuditLog>();

    public virtual ICollection<BusinessRegistration> BusinessRegistrations { get; set; } = new List<BusinessRegistration>();

    public virtual ICollection<Complaint> ComplaintUserAccounts { get; set; } = new List<Complaint>();

    public virtual ICollection<Complaint> Complaintcustomer_users { get; set; } = new List<Complaint>();

    public virtual ICollection<FoodCategory> FoodCategories { get; set; } = new List<FoodCategory>();

    public virtual ICollection<Notification> Notifications { get; set; } = new List<Notification>();

    public virtual ICollection<OrderStatusHistory> OrderStatusHistories { get; set; } = new List<OrderStatusHistory>();

    public virtual ICollection<Order> Orders { get; set; } = new List<Order>();

    public virtual ICollection<Penalty> Penalties { get; set; } = new List<Penalty>();

    public virtual ICollection<PenaltyFeeSchedule> PenaltyFeeSchedules { get; set; } = new List<PenaltyFeeSchedule>();

    public virtual ICollection<PermitScanLog> PermitScanLogs { get; set; } = new List<PermitScanLog>();

    public virtual ICollection<PricingZone> PricingZones { get; set; } = new List<PricingZone>();

    public virtual ICollection<RenewalRequest> RenewalRequests { get; set; } = new List<RenewalRequest>();

    public virtual ICollection<RentalApplication> RentalApplications { get; set; } = new List<RentalApplication>();

    public virtual ICollection<RentalContract> RentalContracts { get; set; } = new List<RentalContract>();

    public virtual ICollection<ReportExport> ReportExports { get; set; } = new List<ReportExport>();

    public virtual ICollection<ReportedContent> ReportedContentUserAccounts { get; set; } = new List<ReportedContent>();

    public virtual ICollection<ReportedContent> ReportedContentreporter_users { get; set; } = new List<ReportedContent>();

    public virtual ICollection<Review> Reviews { get; set; } = new List<Review>();

    public virtual ICollection<ShoppingCart> ShoppingCarts { get; set; } = new List<ShoppingCart>();

    public virtual ICollection<SidewalkSlot> SidewalkSlots { get; set; } = new List<SidewalkSlot>();

    public virtual ICollection<SlotTransferRequest> SlotTransferRequests { get; set; } = new List<SlotTransferRequest>();

    public virtual ICollection<UserDevice> UserDevices { get; set; } = new List<UserDevice>();

    public virtual ICollection<UserSession> UserSessions { get; set; } = new List<UserSession>();

    public virtual Vendor? Vendor { get; set; }

    public virtual ICollection<VendorComment> VendorComments { get; set; } = new List<VendorComment>();

    public virtual ICollection<VendorReport> VendorReportUserAccounts { get; set; } = new List<VendorReport>();

    public virtual ICollection<VendorReport> VendorReportreporter_users { get; set; } = new List<VendorReport>();

    public virtual ICollection<Violation> Violations { get; set; } = new List<Violation>();

    public virtual Role role_codeNavigation { get; set; } = null!;
}
