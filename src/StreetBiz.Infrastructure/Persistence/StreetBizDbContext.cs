using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

namespace StreetBiz.Infrastructure.Persistence;

public partial class StreetBizDbContext : DbContext
{
    public StreetBizDbContext(DbContextOptions<StreetBizDbContext> options)
        : base(options)
    {
    }

    public virtual DbSet<AIAssistanceLog> AIAssistanceLogs { get; set; }

    public virtual DbSet<AddressChangeRequest> AddressChangeRequests { get; set; }

    public virtual DbSet<AdministrativeUnit> AdministrativeUnits { get; set; }

    public virtual DbSet<AuditLog> AuditLogs { get; set; }

    public virtual DbSet<BusinessRegistration> BusinessRegistrations { get; set; }

    // Schema: see db/StreetBiz_SQL_Server.sql.
    public virtual DbSet<BusinessRegistrationHouseholdMember> BusinessRegistrationHouseholdMembers { get; set; }

    // Schema: see db/StreetBiz_SQL_Server.sql.
    public virtual DbSet<KycVerificationResult> KycVerificationResults { get; set; }

    public virtual DbSet<Complaint> Complaints { get; set; }

    public virtual DbSet<DigitalPermit> DigitalPermits { get; set; }

    public virtual DbSet<FeeSchedule> FeeSchedules { get; set; }

    public virtual DbSet<FeeScheduleItem> FeeScheduleItems { get; set; }

    public virtual DbSet<FoodCategory> FoodCategories { get; set; }

    public virtual DbSet<Invoice> Invoices { get; set; }

    public virtual DbSet<MenuItem> MenuItems { get; set; }

    public virtual DbSet<Notification> Notifications { get; set; }

    public virtual DbSet<Order> Orders { get; set; }

    public virtual DbSet<OrderItem> OrderItems { get; set; }

    public virtual DbSet<OrderStatusHistory> OrderStatusHistories { get; set; }

    public virtual DbSet<OtpChallenge> OtpChallenges { get; set; }

    public virtual DbSet<PaymentCallbackEvent> PaymentCallbackEvents { get; set; }

    public virtual DbSet<PaymentTransaction> PaymentTransactions { get; set; }

    public virtual DbSet<Penalty> Penalties { get; set; }

    public virtual DbSet<PenaltyFeeSchedule> PenaltyFeeSchedules { get; set; }

    public virtual DbSet<PermitScanLog> PermitScanLogs { get; set; }

    public virtual DbSet<PricingZone> PricingZones { get; set; }

    public virtual DbSet<RefundTransaction> RefundTransactions { get; set; }

    public virtual DbSet<RegistrationEvidence> RegistrationEvidences { get; set; }

    public virtual DbSet<RenewalRequest> RenewalRequests { get; set; }

    public virtual DbSet<RentalApplication> RentalApplications { get; set; }

    public virtual DbSet<RentalContract> RentalContracts { get; set; }

    public virtual DbSet<ReportExport> ReportExports { get; set; }

    public virtual DbSet<ReportedContent> ReportedContents { get; set; }

    public virtual DbSet<Review> Reviews { get; set; }

    public virtual DbSet<Role> Roles { get; set; }

    public virtual DbSet<ShoppingCart> ShoppingCarts { get; set; }

    public virtual DbSet<ShoppingCartItem> ShoppingCartItems { get; set; }

    public virtual DbSet<SidewalkSlot> SidewalkSlots { get; set; }

    public virtual DbSet<SlotHold> SlotHolds { get; set; }

    public virtual DbSet<SlotTransferRequest> SlotTransferRequests { get; set; }

    public virtual DbSet<Storefront> Storefronts { get; set; }

    public virtual DbSet<StorefrontBusinessHour> StorefrontBusinessHours { get; set; }

    public virtual DbSet<StreetFeature> StreetFeatures { get; set; }

    public virtual DbSet<UserAccount> UserAccounts { get; set; }

    public virtual DbSet<UserDevice> UserDevices { get; set; }

    public virtual DbSet<UserSession> UserSessions { get; set; }

    public virtual DbSet<Vendor> Vendors { get; set; }

    public virtual DbSet<VendorComment> VendorComments { get; set; }

    public virtual DbSet<VendorReport> VendorReports { get; set; }

    public virtual DbSet<Violation> Violations { get; set; }

    public virtual DbSet<ViolationType> ViolationTypes { get; set; }

    public virtual DbSet<ZoneFeeComponent> ZoneFeeComponents { get; set; }

    public virtual DbSet<vw_PermitValidity> vw_PermitValidities { get; set; }

    public virtual DbSet<vw_VendorRating> vw_VendorRatings { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AIAssistanceLog>(entity =>
        {
            entity.HasKey(e => e.ai_log_id).HasName("PK__AIAssist__0A2315E748E62BBF");

            entity.Property(e => e.confidence).HasColumnType("decimal(5, 2)");
            entity.Property(e => e.created_at).HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.entity_type).HasMaxLength(50);
            entity.Property(e => e.feature_code).HasMaxLength(20);

            entity.HasOne(d => d.reviewed_byNavigation).WithMany(p => p.AIAssistanceLogs)
                .HasForeignKey(d => d.reviewed_by)
                .HasConstraintName("FK_AIAssistanceLogs_Reviewer");
        });

        modelBuilder.Entity<AddressChangeRequest>(entity =>
        {
            entity.HasKey(e => e.address_change_id).HasName("PK__AddressC__CDF6F9FF9CF7F87D");

            entity.HasIndex(e => e.registration_id, "UQ_AddressChangeRequests_OpenPerRegistration")
                .IsUnique()
                .HasFilter("([change_status] IN ('PENDING', 'UNDER_REVIEW'))");

            entity.Property(e => e.change_status)
                .HasMaxLength(30)
                .HasDefaultValue("PENDING");
            entity.Property(e => e.conflict_resolution_note).HasMaxLength(500);
            entity.Property(e => e.created_at).HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.new_address).HasMaxLength(500);
            entity.Property(e => e.new_latitude).HasColumnType("decimal(9, 6)");
            entity.Property(e => e.new_longitude).HasColumnType("decimal(9, 6)");
            entity.Property(e => e.reviewer_role)
                .HasMaxLength(30)
                .HasComputedColumnSql("(CONVERT([nvarchar](30),N'WARD_AUTHORITY'))", true);

            entity.HasOne(d => d.registration).WithOne(p => p.AddressChangeRequest)
                .HasForeignKey<AddressChangeRequest>(d => d.registration_id)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_AddressChangeRequests_Registration");

            entity.HasOne(d => d.released_contract).WithMany(p => p.AddressChangeRequests)
                .HasForeignKey(d => d.released_contract_id)
                .HasConstraintName("FK_AddressChangeRequests_ReleasedContract");

            entity.HasOne(d => d.requested_new_slot).WithMany(p => p.AddressChangeRequests)
                .HasForeignKey(d => d.requested_new_slot_id)
                .HasConstraintName("FK_AddressChangeRequests_NewSlot");

            entity.HasOne(d => d.UserAccount).WithMany(p => p.AddressChangeRequests)
                .HasPrincipalKey(p => new { p.user_id, p.role_code })
                .HasForeignKey(d => new { d.reviewed_by, d.reviewer_role })
                .HasConstraintName("FK_AddressChangeRequests_Reviewer");
        });

        modelBuilder.Entity<AdministrativeUnit>(entity =>
        {
            entity.HasKey(e => e.unit_id).HasName("PK__Administ__D3AF5BD7F7195982");

            entity.HasIndex(e => new { e.unit_id, e.unit_type }, "UQ_AdministrativeUnits_IdType").IsUnique();

            entity.Property(e => e.contact_name).HasMaxLength(150);
            entity.Property(e => e.contact_phone).HasMaxLength(20);
            entity.Property(e => e.created_at).HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.unit_name).HasMaxLength(200);
            entity.Property(e => e.unit_type).HasMaxLength(20);

            entity.HasOne(d => d.parent_unit).WithMany(p => p.Inverseparent_unit)
                .HasForeignKey(d => d.parent_unit_id)
                .HasConstraintName("FK_AdministrativeUnits_Parent");
        });

        modelBuilder.Entity<AuditLog>(entity =>
        {
            entity.HasKey(e => e.audit_id).HasName("PK__AuditLog__5AF33E33C21F2DED");

            entity.HasIndex(e => new { e.entity_type, e.entity_id }, "IX_AuditLogs_Entity");

            entity.Property(e => e.action).HasMaxLength(100);
            entity.Property(e => e.created_at).HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.entity_type).HasMaxLength(50);

            entity.HasOne(d => d.actor_user).WithMany(p => p.AuditLogs)
                .HasForeignKey(d => d.actor_user_id)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_AuditLogs_Actor");
        });

        modelBuilder.Entity<BusinessRegistration>(entity =>
        {
            entity.HasKey(e => e.registration_id).HasName("PK__Business__22A298F6FD116948");

            entity.HasIndex(e => e.registration_status, "IX_BusinessRegistrations_Status");

            entity.HasIndex(e => e.vendor_id, "IX_BusinessRegistrations_Vendor");

            entity.Property(e => e.address_latitude).HasColumnType("decimal(9, 6)");
            entity.Property(e => e.address_longitude).HasColumnType("decimal(9, 6)");
            entity.Property(e => e.created_at).HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.declared_address).HasMaxLength(500);
            entity.Property(e => e.display_name).HasMaxLength(180);
            entity.Property(e => e.id_number).HasMaxLength(12);
            entity.Property(e => e.registration_status)
                .HasMaxLength(30)
                .HasDefaultValue("SUBMITTED");
            entity.Property(e => e.review_decision_reason).HasMaxLength(500);
            entity.Property(e => e.reviewer_role)
                .HasMaxLength(30)
                .HasComputedColumnSql("(CONVERT([nvarchar](30),N'WARD_AUTHORITY'))", true);
            entity.Property(e => e.vendor_type).HasMaxLength(20);
            entity.Property(e => e.ward_unit_type)
                .HasMaxLength(20)
                .HasComputedColumnSql("(CONVERT([nvarchar](20),N'WARD'))", true);

            entity.HasOne(d => d.vendor).WithMany(p => p.BusinessRegistrations)
                .HasForeignKey(d => d.vendor_id)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_BusinessRegistrations_Vendor");

            entity.HasOne(d => d.UserAccount).WithMany(p => p.BusinessRegistrations)
                .HasPrincipalKey(p => new { p.user_id, p.role_code })
                .HasForeignKey(d => new { d.reviewed_by, d.reviewer_role })
                .HasConstraintName("FK_BusinessRegistrations_Reviewer");

            entity.HasOne(d => d.AdministrativeUnit).WithMany(p => p.BusinessRegistrations)
                .HasPrincipalKey(p => new { p.unit_id, p.unit_type })
                .HasForeignKey(d => new { d.ward_unit_id, d.ward_unit_type })
                .HasConstraintName("FK_BusinessRegistrations_Ward");
        });

        modelBuilder.Entity<Complaint>(entity =>
        {
            entity.HasKey(e => e.complaint_id).HasName("PK__Complain__A771F61CF7685B46");

            entity.Property(e => e.complaint_type).HasMaxLength(20);
            entity.Property(e => e.created_at).HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.description).HasMaxLength(1000);
            entity.Property(e => e.requested_refund_amount).HasColumnType("decimal(18, 0)");
            entity.Property(e => e.resolution_notes).HasMaxLength(1000);
            entity.Property(e => e.resolver_role)
                .HasMaxLength(30)
                .HasComputedColumnSql("(CONVERT([nvarchar](30),N'PLATFORM_ADMIN'))", true);
            entity.Property(e => e.status)
                .HasMaxLength(20)
                .HasDefaultValue("OPEN");

            entity.HasOne(d => d.customer_user).WithMany(p => p.Complaintcustomer_users)
                .HasForeignKey(d => d.customer_user_id)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_Complaints_Customer");

            entity.HasOne(d => d.order).WithMany(p => p.Complaints)
                .HasForeignKey(d => d.order_id)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_Complaints_Order");

            entity.HasOne(d => d.UserAccount).WithMany(p => p.ComplaintUserAccounts)
                .HasPrincipalKey(p => new { p.user_id, p.role_code })
                .HasForeignKey(d => new { d.resolved_by, d.resolver_role })
                .HasConstraintName("FK_Complaints_ResolvedBy");
        });

        modelBuilder.Entity<DigitalPermit>(entity =>
        {
            entity.HasKey(e => e.permit_id).HasName("PK__DigitalP__ADE3537D0F82C477");

            entity.HasIndex(e => e.contract_id, "UQ_DigitalPermits_LivePerContract")
                .IsUnique()
                .HasFilter("([permit_status]<>'REVOKED')");

            entity.HasIndex(e => e.qr_payload, "UQ__DigitalP__4A9101CF620A0D90").IsUnique();

            entity.Property(e => e.issued_at).HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.permit_status)
                .HasMaxLength(20)
                .HasDefaultValue("ACTIVE");
            entity.Property(e => e.qr_payload).HasMaxLength(500);
            entity.Property(e => e.revocation_reason).HasMaxLength(500);
            entity.Property(e => e.suspension_reason).HasMaxLength(500);

            entity.HasOne(d => d.contract).WithOne(p => p.DigitalPermit)
                .HasForeignKey<DigitalPermit>(d => d.contract_id)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_DigitalPermits_Contract");
        });

        modelBuilder.Entity<FeeSchedule>(entity =>
        {
            entity.HasKey(e => e.fee_schedule_id).HasName("PK__FeeSched__56DA23AE5A8A0AED");

            entity.HasIndex(e => new { e.contract_id, e.revision }, "UQ_FeeSchedules_ContractRevision").IsUnique();

            entity.HasIndex(e => e.contract_id, "UQ_FeeSchedules_CurrentPerContract")
                .IsUnique()
                .HasFilter("([superseded_at] IS NULL)");

            entity.Property(e => e.generated_at).HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.revision).HasDefaultValue(1);
            entity.Property(e => e.total_amount).HasColumnType("decimal(18, 0)");

            entity.HasOne(d => d.contract).WithOne(p => p.FeeSchedule)
                .HasForeignKey<FeeSchedule>(d => d.contract_id)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_FeeSchedules_Contract");
        });

        modelBuilder.Entity<FeeScheduleItem>(entity =>
        {
            entity.HasKey(e => e.fee_item_id).HasName("PK__FeeSched__CED35C72C7077097");

            entity.HasIndex(e => new { e.due_date, e.item_status }, "IX_FeeScheduleItems_DueDate");

            entity.Property(e => e.amount).HasColumnType("decimal(18, 0)");
            entity.Property(e => e.item_status)
                .HasMaxLength(20)
                .HasDefaultValue("PENDING");

            entity.HasOne(d => d.fee_schedule).WithMany(p => p.FeeScheduleItems)
                .HasForeignKey(d => d.fee_schedule_id)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_FeeScheduleItems_Schedule");
        });

        modelBuilder.Entity<FoodCategory>(entity =>
        {
            entity.HasKey(e => e.category_id).HasName("PK__FoodCate__D54EE9B4CE54C45A");

            entity.HasIndex(e => e.category_name, "UQ__FoodCate__5189E2554A440AAA").IsUnique();

            entity.Property(e => e.category_name).HasMaxLength(100);
            entity.Property(e => e.creator_role)
                .HasMaxLength(30)
                .HasComputedColumnSql("(CONVERT([nvarchar](30),N'PLATFORM_ADMIN'))", true);

            entity.HasOne(d => d.UserAccount).WithMany(p => p.FoodCategories)
                .HasPrincipalKey(p => new { p.user_id, p.role_code })
                .HasForeignKey(d => new { d.created_by, d.creator_role })
                .HasConstraintName("FK_FoodCategories_CreatedBy");
        });

        modelBuilder.Entity<Invoice>(entity =>
        {
            entity.HasKey(e => e.invoice_id).HasName("PK__Invoices__F58DFD4900F5A3E0");

            entity.HasIndex(e => e.invoice_number, "UQ__Invoices__8081A63A9588B1C5").IsUnique();

            entity.Property(e => e.amount).HasColumnType("decimal(18, 0)");
            entity.Property(e => e.invoice_number).HasMaxLength(40);
            entity.Property(e => e.issued_at).HasDefaultValueSql("(sysutcdatetime())");

            entity.HasOne(d => d.fee_item).WithMany(p => p.Invoices)
                .HasForeignKey(d => d.fee_item_id)
                .HasConstraintName("FK_Invoices_FeeItem");

            entity.HasOne(d => d.penalty).WithMany(p => p.Invoices)
                .HasForeignKey(d => d.penalty_id)
                .HasConstraintName("FK_Invoices_Penalty");

            entity.HasOne(d => d.vendor).WithMany(p => p.Invoices)
                .HasForeignKey(d => d.vendor_id)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_Invoices_Vendor");
        });

        modelBuilder.Entity<MenuItem>(entity =>
        {
            entity.HasKey(e => e.menu_item_id).HasName("PK__MenuItem__973431D52331EADA");

            entity.HasIndex(e => e.storefront_id, "IX_MenuItems_Storefront");

            entity.Property(e => e.availability_status)
                .HasMaxLength(20)
                .HasDefaultValue("AVAILABLE");
            entity.Property(e => e.created_at).HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.description).HasMaxLength(500);
            entity.Property(e => e.image_url).HasMaxLength(500);
            entity.Property(e => e.item_name).HasMaxLength(180);
            entity.Property(e => e.unit_price).HasColumnType("decimal(18, 0)");

            entity.HasOne(d => d.category).WithMany(p => p.MenuItems)
                .HasForeignKey(d => d.category_id)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_MenuItems_Category");

            entity.HasOne(d => d.storefront).WithMany(p => p.MenuItems)
                .HasForeignKey(d => d.storefront_id)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_MenuItems_Storefront");
        });

        modelBuilder.Entity<Notification>(entity =>
        {
            entity.HasKey(e => e.notification_id).HasName("PK__Notifica__E059842F85C6A724");

            entity.HasIndex(e => new { e.user_id, e.is_read }, "IX_Notifications_User_Read");

            entity.Property(e => e.body).HasMaxLength(1000);
            entity.Property(e => e.notification_type).HasMaxLength(50);
            entity.Property(e => e.related_entity_type).HasMaxLength(50);
            entity.Property(e => e.sent_at).HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.title).HasMaxLength(200);

            entity.HasOne(d => d.user).WithMany(p => p.Notifications)
                .HasForeignKey(d => d.user_id)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_Notifications_User");
        });

        modelBuilder.Entity<Order>(entity =>
        {
            entity.HasKey(e => e.order_id).HasName("PK__Orders__46596229153F429F");

            entity.HasIndex(e => e.customer_user_id, "IX_Orders_Customer");

            entity.HasIndex(e => e.storefront_id, "IX_Orders_Storefront");

            entity.HasIndex(e => e.order_code, "UQ__Orders__99D12D3FD02B875C").IsUnique();

            entity.Property(e => e.created_at).HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.order_code).HasMaxLength(30);
            entity.Property(e => e.order_status)
                .HasMaxLength(20)
                .HasDefaultValue("PENDING_PAYMENT");
            entity.Property(e => e.rejection_reason).HasMaxLength(500);
            entity.Property(e => e.storefront_address_snapshot).HasMaxLength(500);
            entity.Property(e => e.subtotal_amount).HasColumnType("decimal(18, 0)");
            entity.Property(e => e.total_amount).HasColumnType("decimal(18, 0)");

            entity.HasOne(d => d.customer_user).WithMany(p => p.Orders)
                .HasForeignKey(d => d.customer_user_id)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_Orders_Customer");

            entity.HasOne(d => d.storefront).WithMany(p => p.Orders)
                .HasForeignKey(d => d.storefront_id)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_Orders_Storefront");
        });

        modelBuilder.Entity<OrderItem>(entity =>
        {
            entity.HasKey(e => e.order_item_id).HasName("PK__OrderIte__3764B6BC0C88DD06");

            entity.Property(e => e.item_name_snapshot).HasMaxLength(180);
            entity.Property(e => e.note).HasMaxLength(300);
            entity.Property(e => e.unit_price_snapshot).HasColumnType("decimal(18, 0)");

            entity.HasOne(d => d.menu_item).WithMany(p => p.OrderItems)
                .HasForeignKey(d => d.menu_item_id)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_OrderItems_MenuItem");

            entity.HasOne(d => d.order).WithMany(p => p.OrderItems)
                .HasForeignKey(d => d.order_id)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_OrderItems_Order");
        });

        modelBuilder.Entity<OrderStatusHistory>(entity =>
        {
            entity.HasKey(e => e.history_id).HasName("PK__OrderSta__096AA2E90C2225BF");

            entity.ToTable("OrderStatusHistory");

            entity.HasIndex(e => new { e.order_id, e.changed_at }, "IX_OrderStatusHistory_Order");

            entity.Property(e => e.changed_at).HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.from_status).HasMaxLength(20);
            entity.Property(e => e.note).HasMaxLength(500);
            entity.Property(e => e.to_status).HasMaxLength(20);

            entity.HasOne(d => d.changed_byNavigation).WithMany(p => p.OrderStatusHistories)
                .HasForeignKey(d => d.changed_by)
                .HasConstraintName("FK_OrderStatusHistory_ChangedBy");

            entity.HasOne(d => d.order).WithMany(p => p.OrderStatusHistories)
                .HasForeignKey(d => d.order_id)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_OrderStatusHistory_Order");
        });

        modelBuilder.Entity<OtpChallenge>(entity =>
        {
            entity.HasKey(e => e.challenge_id).HasName("PK__OtpChall__CF6351910E3A7479");

            entity.HasIndex(e => new { e.phone_number, e.purpose, e.expires_at }, "IX_OtpChallenges_Lookup");

            entity.Property(e => e.code_hash).HasMaxLength(32);
            entity.Property(e => e.created_at).HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.max_attempts).HasDefaultValue((byte)5);
            entity.Property(e => e.phone_number).HasMaxLength(15);
            entity.Property(e => e.purpose).HasMaxLength(20);
        });

        modelBuilder.Entity<PaymentCallbackEvent>(entity =>
        {
            entity.HasKey(e => e.callback_event_id).HasName("PK__PaymentC__F8EC28E548571383");

            entity.HasIndex(e => new { e.provider, e.provider_reference }, "IX_PaymentCallbackEvents_Reference");

            entity.Property(e => e.processing_result).HasMaxLength(20);
            entity.Property(e => e.provider).HasMaxLength(30);
            entity.Property(e => e.provider_reference).HasMaxLength(100);
            entity.Property(e => e.received_at).HasDefaultValueSql("(sysutcdatetime())");

            entity.HasOne(d => d.transaction).WithMany(p => p.PaymentCallbackEvents)
                .HasForeignKey(d => d.transaction_id)
                .HasConstraintName("FK_PaymentCallbackEvents_Transaction");
        });

        modelBuilder.Entity<PaymentTransaction>(entity =>
        {
            entity.HasKey(e => e.transaction_id).HasName("PK__PaymentT__85C600AF31BF28B7");

            entity.HasIndex(e => e.idempotency_key, "UQ__PaymentT__A7BA59F41B3C497A").IsUnique();

            entity.Property(e => e.amount).HasColumnType("decimal(18, 0)");
            entity.Property(e => e.created_at).HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.idempotency_key).HasMaxLength(100);
            entity.Property(e => e.payment_purpose).HasMaxLength(20);
            entity.Property(e => e.provider).HasMaxLength(30);
            entity.Property(e => e.provider_reference).HasMaxLength(100);
            entity.Property(e => e.transaction_status)
                .HasMaxLength(20)
                .HasDefaultValue("PENDING");

            entity.HasOne(d => d.fee_item).WithMany(p => p.PaymentTransactions)
                .HasForeignKey(d => d.fee_item_id)
                .HasConstraintName("FK_PaymentTransactions_FeeItem");

            entity.HasOne(d => d.order).WithMany(p => p.PaymentTransactions)
                .HasForeignKey(d => d.order_id)
                .HasConstraintName("FK_PaymentTransactions_Order");

            entity.HasOne(d => d.penalty).WithMany(p => p.PaymentTransactions)
                .HasForeignKey(d => d.penalty_id)
                .HasConstraintName("FK_PaymentTransactions_Penalty");
        });

        modelBuilder.Entity<Penalty>(entity =>
        {
            entity.HasKey(e => e.penalty_id).HasName("PK__Penaltie__0AAEFF0B9553075B");

            entity.ToTable(tb => tb.HasTrigger("TR_Penalties_RequireIdentifiedOffender"));

            entity.HasIndex(e => e.violation_id, "UQ__Penaltie__8A989362DC573823").IsUnique();

            entity.Property(e => e.amount).HasColumnType("decimal(18, 0)");
            entity.Property(e => e.created_at).HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.decision_number).HasMaxLength(50);
            entity.Property(e => e.signer_name).HasMaxLength(150);
            entity.Property(e => e.signer_title).HasMaxLength(100);
            entity.Property(e => e.penalty_status)
                .HasMaxLength(20)
                .HasDefaultValue("UNPAID");
            entity.Property(e => e.waiver_reason).HasMaxLength(500);
            entity.Property(e => e.waiver_role)
                .HasMaxLength(30)
                .HasComputedColumnSql("(CONVERT([nvarchar](30),N'WARD_AUTHORITY'))", true);

            entity.HasOne(d => d.penalty_schedule).WithMany(p => p.Penalties)
                .HasForeignKey(d => d.penalty_schedule_id)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_Penalties_Schedule");

            entity.HasOne(d => d.violation).WithOne(p => p.Penalty)
                .HasForeignKey<Penalty>(d => d.violation_id)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_Penalties_Violation");

            entity.HasOne(d => d.UserAccount).WithMany(p => p.Penalties)
                .HasPrincipalKey(p => new { p.user_id, p.role_code })
                .HasForeignKey(d => new { d.waived_by, d.waiver_role })
                .HasConstraintName("FK_Penalties_WaivedBy");
        });

        modelBuilder.Entity<PenaltyFeeSchedule>(entity =>
        {
            entity.HasKey(e => e.penalty_schedule_id).HasName("PK__PenaltyF__FB722DF4EE3BEE22");

            entity.HasIndex(e => new { e.ward_unit_id, e.violation_type }, "UQ_PenaltyFeeSchedules_CurrentRate")
                .IsUnique()
                .HasFilter("([effective_to] IS NULL)");

            entity.Property(e => e.created_at).HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.creator_role)
                .HasMaxLength(30)
                .HasComputedColumnSql("(CONVERT([nvarchar](30),N'WARD_AUTHORITY'))", true);
            entity.Property(e => e.effective_from).HasDefaultValueSql("(CONVERT([date],sysutcdatetime()))");
            entity.Property(e => e.legal_basis).HasMaxLength(500);
            entity.Property(e => e.penalty_amount).HasColumnType("decimal(18, 0)");
            entity.Property(e => e.violation_type).HasMaxLength(50);
            entity.Property(e => e.ward_unit_type)
                .HasMaxLength(20)
                .HasComputedColumnSql("(CONVERT([nvarchar](20),N'WARD'))", true);

            entity.HasOne(d => d.violation_typeNavigation).WithMany(p => p.PenaltyFeeSchedules)
                .HasForeignKey(d => d.violation_type)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_PenaltyFeeSchedules_ViolationType");

            entity.HasOne(d => d.UserAccount).WithMany(p => p.PenaltyFeeSchedules)
                .HasPrincipalKey(p => new { p.user_id, p.role_code })
                .HasForeignKey(d => new { d.created_by, d.creator_role })
                .HasConstraintName("FK_PenaltyFeeSchedules_CreatedBy");

            entity.HasOne(d => d.AdministrativeUnit).WithMany(p => p.PenaltyFeeSchedules)
                .HasPrincipalKey(p => new { p.unit_id, p.unit_type })
                .HasForeignKey(d => new { d.ward_unit_id, d.ward_unit_type })
                .HasConstraintName("FK_PenaltyFeeSchedules_Ward");
        });

        modelBuilder.Entity<PermitScanLog>(entity =>
        {
            entity.HasKey(e => e.scan_id).HasName("PK__PermitSc__9846B9BB5E321569");

            entity.HasIndex(e => new { e.permit_id, e.scanned_at }, "IX_PermitScanLogs_Permit");

            entity.Property(e => e.latitude).HasColumnType("decimal(9, 6)");
            entity.Property(e => e.longitude).HasColumnType("decimal(9, 6)");
            entity.Property(e => e.photo_url).HasMaxLength(500);
            entity.Property(e => e.qr_payload).HasMaxLength(500);
            entity.Property(e => e.scan_context).HasMaxLength(20);
            entity.Property(e => e.scan_result).HasMaxLength(20);
            entity.Property(e => e.scanned_at).HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.scanner_role)
                .HasMaxLength(30)
                .HasComputedColumnSql("(case when [scan_context]='WARD_INSPECTION' then CONVERT([nvarchar](30),N'WARD_AUTHORITY')  end)", true);

            entity.HasOne(d => d.permit).WithMany(p => p.PermitScanLogs)
                .HasForeignKey(d => d.permit_id)
                .HasConstraintName("FK_PermitScanLogs_Permit");

            entity.HasOne(d => d.UserAccount).WithMany(p => p.PermitScanLogs)
                .HasPrincipalKey(p => new { p.user_id, p.role_code })
                .HasForeignKey(d => new { d.scanned_by, d.scanner_role })
                .HasConstraintName("FK_PermitScanLogs_ScannedBy");
        });

        modelBuilder.Entity<PricingZone>(entity =>
        {
            entity.HasKey(e => e.zone_id).HasName("PK__PricingZ__80B401DF81D09834");

            entity.HasIndex(e => new { e.ward_unit_id, e.zone_name }, "UQ_PricingZones_NamePerWard").IsUnique();

            entity.Property(e => e.created_at).HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.creator_role)
                .HasMaxLength(30)
                .HasComputedColumnSql("(CONVERT([nvarchar](30),N'WARD_AUTHORITY'))", true);
            entity.Property(e => e.price_per_day).HasColumnType("decimal(18, 0)");
            entity.Property(e => e.regulation_ref).HasMaxLength(120);
            entity.Property(e => e.segment_from).HasMaxLength(150);
            entity.Property(e => e.segment_to).HasMaxLength(150);
            entity.Property(e => e.ward_unit_type)
                .HasMaxLength(20)
                .HasComputedColumnSql("(CONVERT([nvarchar](20),N'WARD'))", true);
            entity.Property(e => e.zone_code).HasMaxLength(30);
            entity.Property(e => e.zone_name).HasMaxLength(150);

            entity.HasOne(d => d.UserAccount).WithMany(p => p.PricingZones)
                .HasPrincipalKey(p => new { p.user_id, p.role_code })
                .HasForeignKey(d => new { d.created_by, d.creator_role })
                .HasConstraintName("FK_PricingZones_CreatedBy");

            entity.HasOne(d => d.AdministrativeUnit).WithMany(p => p.PricingZones)
                .HasPrincipalKey(p => new { p.unit_id, p.unit_type })
                .HasForeignKey(d => new { d.ward_unit_id, d.ward_unit_type })
                .HasConstraintName("FK_PricingZones_Ward");
        });

        modelBuilder.Entity<RefundTransaction>(entity =>
        {
            entity.HasKey(e => e.refund_id).HasName("PK__RefundTr__897E9EA3EC0A741E");

            entity.ToTable(tb => tb.HasTrigger("TR_RefundTransactions_NotMoreThanPaid"));

            entity.HasIndex(e => e.order_id, "IX_RefundTransactions_Order");

            entity.HasIndex(e => e.idempotency_key, "UQ__RefundTr__A7BA59F46A1D1DDC").IsUnique();

            entity.Property(e => e.amount).HasColumnType("decimal(18, 0)");
            entity.Property(e => e.idempotency_key).HasMaxLength(100);
            entity.Property(e => e.provider).HasMaxLength(30);
            entity.Property(e => e.provider_refund_reference).HasMaxLength(100);
            entity.Property(e => e.refund_reason).HasMaxLength(30);
            entity.Property(e => e.refund_status)
                .HasMaxLength(20)
                .HasDefaultValue("PENDING");
            entity.Property(e => e.requested_at).HasDefaultValueSql("(sysutcdatetime())");

            entity.HasOne(d => d.complaint).WithMany(p => p.RefundTransactions)
                .HasForeignKey(d => d.complaint_id)
                .HasConstraintName("FK_RefundTransactions_Complaint");

            entity.HasOne(d => d.order).WithMany(p => p.RefundTransactions)
                .HasForeignKey(d => d.order_id)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_RefundTransactions_Order");

            entity.HasOne(d => d.payment_transaction).WithMany(p => p.RefundTransactions)
                .HasForeignKey(d => d.payment_transaction_id)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_RefundTransactions_Payment");
        });

        modelBuilder.Entity<RegistrationEvidence>(entity =>
        {
            entity.HasKey(e => e.evidence_id).HasName("PK__Registra__C59A788E3C4A7C01");

            entity.ToTable("RegistrationEvidence");

            entity.HasIndex(e => e.registration_id, "IX_RegistrationEvidence_Registration");

            entity.Property(e => e.evidence_type).HasMaxLength(30);
            entity.Property(e => e.file_url).HasMaxLength(500);
            entity.Property(e => e.uploaded_at).HasDefaultValueSql("(sysutcdatetime())");

            entity.HasOne(d => d.registration).WithMany(p => p.RegistrationEvidences)
                .HasForeignKey(d => d.registration_id)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_RegistrationEvidence_Registration");
        });

        modelBuilder.Entity<RenewalRequest>(entity =>
        {
            entity.HasKey(e => e.renewal_id).HasName("PK__RenewalR__16DEFA38F2194158");

            entity.HasIndex(e => e.contract_id, "UQ_RenewalRequests_OpenPerContract")
                .IsUnique()
                .HasFilter("([renewal_status] IN ('PENDING', 'UNDER_REVIEW'))");

            entity.Property(e => e.created_at).HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.renewal_status)
                .HasMaxLength(30)
                .HasDefaultValue("PENDING");
            entity.Property(e => e.review_decision_reason).HasMaxLength(500);
            entity.Property(e => e.reviewer_role)
                .HasMaxLength(30)
                .HasComputedColumnSql("(CONVERT([nvarchar](30),N'WARD_AUTHORITY'))", true);

            entity.HasOne(d => d.contract).WithOne(p => p.RenewalRequest)
                .HasForeignKey<RenewalRequest>(d => d.contract_id)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_RenewalRequests_Contract");

            entity.HasOne(d => d.UserAccount).WithMany(p => p.RenewalRequests)
                .HasPrincipalKey(p => new { p.user_id, p.role_code })
                .HasForeignKey(d => new { d.reviewed_by, d.reviewer_role })
                .HasConstraintName("FK_RenewalRequests_Reviewer");
        });

        modelBuilder.Entity<RentalApplication>(entity =>
        {
            entity.HasKey(e => e.application_id).HasName("PK__RentalAp__3BCBDCF26FB792DE");

            entity.HasIndex(e => e.registration_id, "IX_RentalApplications_Registration");

            entity.HasIndex(e => e.slot_id, "IX_RentalApplications_Slot");

            entity.Property(e => e.application_method).HasMaxLength(20);
            entity.Property(e => e.application_status)
                .HasMaxLength(30)
                .HasDefaultValue("PENDING");
            entity.Property(e => e.created_at).HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.review_decision_reason).HasMaxLength(500);
            entity.Property(e => e.reviewer_role)
                .HasMaxLength(30)
                .HasComputedColumnSql("(CONVERT([nvarchar](30),N'WARD_AUTHORITY'))", true);

            entity.HasOne(d => d.registration).WithMany(p => p.RentalApplications)
                .HasForeignKey(d => d.registration_id)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_RentalApplications_Registration");

            entity.HasOne(d => d.slot).WithMany(p => p.RentalApplications)
                .HasForeignKey(d => d.slot_id)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_RentalApplications_Slot");

            entity.HasOne(d => d.UserAccount).WithMany(p => p.RentalApplications)
                .HasPrincipalKey(p => new { p.user_id, p.role_code })
                .HasForeignKey(d => new { d.reviewed_by, d.reviewer_role })
                .HasConstraintName("FK_RentalApplications_Reviewer");
        });

        modelBuilder.Entity<RentalContract>(entity =>
        {
            entity.HasKey(e => e.contract_id).HasName("PK__RentalCo__F8D664239881D220");

            entity.ToTable(tb =>
                {
                    tb.HasTrigger("TR_RentalContracts_NoCancelWithDebt");
                    tb.HasTrigger("TR_RentalContracts_NoOverlap");
                });

            entity.HasIndex(e => e.contract_status, "IX_RentalContracts_Status");

            entity.HasIndex(e => e.vendor_id, "IX_RentalContracts_Vendor");

            entity.HasIndex(e => e.application_id, "UQ__RentalCo__3BCBDCF310B09590").IsUnique();

            entity.Property(e => e.cancellation_reason).HasMaxLength(500);
            entity.Property(e => e.contract_status)
                .HasMaxLength(20)
                .HasDefaultValue("ACTIVE");
            entity.Property(e => e.created_at).HasDefaultValueSql("(sysutcdatetime())");

            entity.HasOne(d => d.application).WithOne(p => p.RentalContract)
                .HasForeignKey<RentalContract>(d => d.application_id)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_RentalContracts_Application");

            entity.HasOne(d => d.cancelled_byNavigation).WithMany(p => p.RentalContracts)
                .HasForeignKey(d => d.cancelled_by)
                .HasConstraintName("FK_RentalContracts_CancelledBy");

            entity.HasOne(d => d.slot).WithMany(p => p.RentalContracts)
                .HasForeignKey(d => d.slot_id)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_RentalContracts_Slot");

            entity.HasOne(d => d.vendor).WithMany(p => p.RentalContracts)
                .HasForeignKey(d => d.vendor_id)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_RentalContracts_Vendor");
        });

        modelBuilder.Entity<ReportExport>(entity =>
        {
            entity.HasKey(e => e.export_id).HasName("PK__ReportEx__323057CF5694CBBD");

            entity.HasIndex(e => new { e.requested_by, e.requested_at }, "IX_ReportExports_Requester");

            entity.Property(e => e.export_status)
                .HasMaxLength(20)
                .HasDefaultValue("PENDING");
            entity.Property(e => e.failure_reason).HasMaxLength(500);
            entity.Property(e => e.file_url).HasMaxLength(500);
            entity.Property(e => e.report_type).HasMaxLength(50);
            entity.Property(e => e.requested_at).HasDefaultValueSql("(sysutcdatetime())");

            entity.HasOne(d => d.requested_byNavigation).WithMany(p => p.ReportExports)
                .HasForeignKey(d => d.requested_by)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_ReportExports_RequestedBy");
        });

        modelBuilder.Entity<ReportedContent>(entity =>
        {
            entity.HasKey(e => e.content_report_id).HasName("PK__Reported__08571ADD0C491F39");

            entity.ToTable("ReportedContent");

            entity.Property(e => e.content_type).HasMaxLength(20);
            entity.Property(e => e.created_at).HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.reason).HasMaxLength(500);
            entity.Property(e => e.reviewer_role)
                .HasMaxLength(30)
                .HasComputedColumnSql("(CONVERT([nvarchar](30),N'PLATFORM_ADMIN'))", true);
            entity.Property(e => e.status)
                .HasMaxLength(20)
                .HasDefaultValue("PENDING");

            entity.HasOne(d => d.reporter_user).WithMany(p => p.ReportedContentreporter_users)
                .HasForeignKey(d => d.reporter_user_id)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_ReportedContent_Reporter");

            entity.HasOne(d => d.UserAccount).WithMany(p => p.ReportedContentUserAccounts)
                .HasPrincipalKey(p => new { p.user_id, p.role_code })
                .HasForeignKey(d => new { d.reviewed_by, d.reviewer_role })
                .HasConstraintName("FK_ReportedContent_Reviewer");
        });

        modelBuilder.Entity<Review>(entity =>
        {
            entity.HasKey(e => e.review_id).HasName("PK__Reviews__60883D907AA45F8F");

            entity.HasIndex(e => e.order_id, "UQ__Reviews__46596228FD6D7AE4").IsUnique();

            entity.Property(e => e.created_at).HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.review_text).HasMaxLength(1000);

            entity.HasOne(d => d.customer_user).WithMany(p => p.Reviews)
                .HasForeignKey(d => d.customer_user_id)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_Reviews_Customer");

            entity.HasOne(d => d.order).WithOne(p => p.Review)
                .HasForeignKey<Review>(d => d.order_id)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_Reviews_Order");
        });

        modelBuilder.Entity<Role>(entity =>
        {
            entity.HasKey(e => e.role_code).HasName("PK__Roles__BAE63074AEDB2F67");

            entity.Property(e => e.role_code).HasMaxLength(30);
            entity.Property(e => e.role_name).HasMaxLength(100);
        });

        modelBuilder.Entity<ShoppingCart>(entity =>
        {
            entity.HasKey(e => e.cart_id).HasName("PK__Shopping__2EF52A2778D21FCE");

            entity.HasIndex(e => new { e.customer_user_id, e.storefront_id }, "UQ_ShoppingCarts_ActivePerStorefront")
                .IsUnique()
                .HasFilter("([cart_status]='ACTIVE')");

            entity.Property(e => e.cart_status)
                .HasMaxLength(20)
                .HasDefaultValue("ACTIVE");
            entity.Property(e => e.created_at).HasDefaultValueSql("(sysutcdatetime())");

            entity.HasOne(d => d.customer_user).WithMany(p => p.ShoppingCarts)
                .HasForeignKey(d => d.customer_user_id)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_ShoppingCarts_Customer");

            entity.HasOne(d => d.storefront).WithMany(p => p.ShoppingCarts)
                .HasForeignKey(d => d.storefront_id)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_ShoppingCarts_Storefront");
        });

        modelBuilder.Entity<ShoppingCartItem>(entity =>
        {
            entity.HasKey(e => e.cart_item_id).HasName("PK__Shopping__5D9A6C6E9D93CF7C");

            entity.Property(e => e.note).HasMaxLength(300);

            entity.HasOne(d => d.cart).WithMany(p => p.ShoppingCartItems)
                .HasForeignKey(d => d.cart_id)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_ShoppingCartItems_Cart");

            entity.HasOne(d => d.menu_item).WithMany(p => p.ShoppingCartItems)
                .HasForeignKey(d => d.menu_item_id)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_ShoppingCartItems_MenuItem");
        });

        modelBuilder.Entity<SidewalkSlot>(entity =>
        {
            entity.HasKey(e => e.slot_id).HasName("PK__Sidewalk__971A01BB9F033008");

            entity.HasIndex(e => e.slot_status, "IX_SidewalkSlots_Status");

            entity.HasIndex(e => e.zone_id, "IX_SidewalkSlots_Zone");

            entity.HasIndex(e => e.slot_code, "UQ__Sidewalk__5D19A1A4F1795837").IsUnique();

            entity.Property(e => e.business_category).HasMaxLength(30);
            entity.Property(e => e.created_at).HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.has_power).HasDefaultValue(false);
            entity.Property(e => e.has_trash_bin).HasDefaultValue(false);
            entity.Property(e => e.has_water).HasDefaultValue(false);
            entity.Property(e => e.image_url).HasMaxLength(500);
            entity.Property(e => e.latitude).HasColumnType("decimal(9, 6)");
            entity.Property(e => e.length_meters).HasColumnType("decimal(5, 2)");
            entity.Property(e => e.longitude).HasColumnType("decimal(9, 6)");
            entity.Property(e => e.proposal_photo_url).HasMaxLength(500);
            entity.Property(e => e.proposal_review_reason).HasMaxLength(500);
            entity.Property(e => e.proposal_review_status).HasMaxLength(20);
            entity.Property(e => e.proposal_reviewer_role)
                .HasMaxLength(30)
                .HasComputedColumnSql("(CONVERT([nvarchar](30),N'WARD_AUTHORITY'))", true);
            entity.Property(e => e.slot_code).HasMaxLength(30);
            entity.Property(e => e.slot_status)
                .HasMaxLength(20)
                .HasDefaultValue("AVAILABLE");
            entity.Property(e => e.source)
                .HasMaxLength(20)
                .HasDefaultValue("WARD_DEFINED");
            entity.Property(e => e.width_meters).HasColumnType("decimal(5, 2)");

            entity.HasOne(d => d.proposed_by_registration).WithMany(p => p.SidewalkSlots)
                .HasForeignKey(d => d.proposed_by_registration_id)
                .HasConstraintName("FK_SidewalkSlots_ProposedBy");

            entity.HasOne(d => d.zone).WithMany(p => p.SidewalkSlots)
                .HasForeignKey(d => d.zone_id)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_SidewalkSlots_Zone");

            entity.HasOne(d => d.UserAccount).WithMany(p => p.SidewalkSlots)
                .HasPrincipalKey(p => new { p.user_id, p.role_code })
                .HasForeignKey(d => new { d.proposal_reviewed_by, d.proposal_reviewer_role })
                .HasConstraintName("FK_SidewalkSlots_ProposalReviewer");
        });

        modelBuilder.Entity<SlotHold>(entity =>
        {
            entity.HasKey(e => e.slot_id).HasName("PK__SlotHold__971A01BB0168B98B");

            entity.HasIndex(e => e.registration_id, "IX_SlotHolds_Registration");

            entity.Property(e => e.slot_id).ValueGeneratedNever();
            entity.Property(e => e.held_at).HasDefaultValueSql("(sysutcdatetime())");

            entity.HasOne(d => d.registration).WithMany(p => p.SlotHolds)
                .HasForeignKey(d => d.registration_id)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_SlotHolds_Registration");

            entity.HasOne(d => d.slot).WithOne(p => p.SlotHold)
                .HasForeignKey<SlotHold>(d => d.slot_id)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_SlotHolds_Slot");
        });

        modelBuilder.Entity<SlotTransferRequest>(entity =>
        {
            entity.HasKey(e => e.transfer_id).HasName("PK__SlotTran__78E6FD3330AAC388");

            entity.Property(e => e.initiated_at).HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.review_decision_reason).HasMaxLength(500);
            entity.Property(e => e.reviewer_role)
                .HasMaxLength(30)
                .HasComputedColumnSql("(CONVERT([nvarchar](30),N'WARD_AUTHORITY'))", true);
            entity.Property(e => e.transfer_status)
                .HasMaxLength(20)
                .HasDefaultValue("PENDING");

            entity.HasOne(d => d.contract).WithMany(p => p.SlotTransferRequests)
                .HasForeignKey(d => d.contract_id)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_SlotTransferRequests_Contract");

            entity.HasOne(d => d.from_vendor).WithMany(p => p.SlotTransferRequestfrom_vendors)
                .HasForeignKey(d => d.from_vendor_id)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_SlotTransferRequests_FromVendor");

            entity.HasOne(d => d.to_vendor).WithMany(p => p.SlotTransferRequestto_vendors)
                .HasForeignKey(d => d.to_vendor_id)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_SlotTransferRequests_ToVendor");

            entity.HasOne(d => d.UserAccount).WithMany(p => p.SlotTransferRequests)
                .HasPrincipalKey(p => new { p.user_id, p.role_code })
                .HasForeignKey(d => new { d.reviewed_by, d.reviewer_role })
                .HasConstraintName("FK_SlotTransferRequests_Reviewer");
        });

        modelBuilder.Entity<Storefront>(entity =>
        {
            entity.HasKey(e => e.storefront_id).HasName("PK__Storefro__3FC65BD9052194F4");

            entity.ToTable(tb => tb.HasTrigger("TR_Storefronts_Phase2Gate"));

            entity.HasIndex(e => e.contract_id, "UQ_Storefronts_Contract").IsUnique();

            entity.HasIndex(e => e.registration_id, "UQ_Storefronts_Registration").IsUnique();

            entity.Property(e => e.availability_status)
                .HasMaxLength(20)
                .HasDefaultValue("CLOSED");
            entity.Property(e => e.created_at).HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.description).HasMaxLength(1000);
            entity.Property(e => e.image_url).HasMaxLength(500);
            entity.Property(e => e.storefront_name).HasMaxLength(180);

            entity.HasOne(d => d.contract).WithOne(p => p.Storefront)
                .HasForeignKey<Storefront>(d => d.contract_id)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_Storefronts_Contract");

            entity.HasOne(d => d.registration).WithOne(p => p.Storefront)
                .HasForeignKey<Storefront>(d => d.registration_id)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_Storefronts_Registration");
        });

        modelBuilder.Entity<StorefrontBusinessHour>(entity =>
        {
            entity.HasKey(e => e.hour_id).HasName("PK__Storefro__21EED6C8BE6EE50D");

            entity.HasOne(d => d.storefront).WithMany(p => p.StorefrontBusinessHours)
                .HasForeignKey(d => d.storefront_id)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_StorefrontBusinessHours_Storefront");
        });

        modelBuilder.Entity<StreetFeature>(entity =>
        {
            entity.HasKey(e => e.feature_id).HasName("PK__StreetFe__7906CBD70F99586B");

            entity.HasIndex(e => e.zone_id, "IX_StreetFeatures_Zone");

            entity.Property(e => e.blocks_business).HasDefaultValue(false);
            entity.Property(e => e.feature_type).HasMaxLength(30);
            entity.Property(e => e.label).HasMaxLength(150);
            entity.Property(e => e.latitude).HasColumnType("decimal(9, 6)");
            entity.Property(e => e.longitude).HasColumnType("decimal(9, 6)");
            entity.Property(e => e.note).HasMaxLength(200);

            entity.HasOne(d => d.zone).WithMany(p => p.StreetFeatures)
                .HasForeignKey(d => d.zone_id)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_StreetFeatures_Zone");
        });

        modelBuilder.Entity<UserAccount>(entity =>
        {
            entity.HasKey(e => e.user_id).HasName("PK__UserAcco__B9BE370FD251E371");

            entity.HasIndex(e => new { e.user_id, e.role_code }, "UQ_UserAccounts_IdRole").IsUnique();

            entity.HasIndex(e => e.phone_number, "UQ__UserAcco__A1936A6BE4F670A3").IsUnique();

            entity.Property(e => e.account_status)
                .HasMaxLength(20)
                .HasDefaultValue("ACTIVE");
            entity.Property(e => e.created_at).HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.full_name).HasMaxLength(150);
            entity.Property(e => e.password_hash).HasMaxLength(255);
            entity.Property(e => e.phone_number).HasMaxLength(15);
            entity.Property(e => e.role_code).HasMaxLength(30);
            entity.Property(e => e.ward_unit_type)
                .HasMaxLength(20)
                .HasComputedColumnSql("(CONVERT([nvarchar](20),N'WARD'))", true);

            entity.HasOne(d => d.role_codeNavigation).WithMany(p => p.UserAccounts)
                .HasForeignKey(d => d.role_code)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_UserAccounts_Role");

            entity.HasOne(d => d.AdministrativeUnit).WithMany(p => p.UserAccounts)
                .HasPrincipalKey(p => new { p.unit_id, p.unit_type })
                .HasForeignKey(d => new { d.ward_unit_id, d.ward_unit_type })
                .HasConstraintName("FK_UserAccounts_Ward");
        });

        modelBuilder.Entity<UserDevice>(entity =>
        {
            entity.HasKey(e => e.user_device_id).HasName("PK__UserDevi__BE75B5E973D1142D");

            entity.HasIndex(e => e.user_id, "IX_UserDevices_ActiveByUser").HasFilter("([is_active]=(1))");

            entity.HasIndex(e => new { e.user_id, e.device_identifier }, "UQ_UserDevices_Device").IsUnique();

            entity.HasIndex(e => e.push_token, "UQ_UserDevices_PushToken")
                .IsUnique()
                .HasFilter("([push_token] IS NOT NULL)");

            entity.Property(e => e.created_at).HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.device_identifier).HasMaxLength(180);
            entity.Property(e => e.is_active).HasDefaultValue(true);
            entity.Property(e => e.platform).HasMaxLength(20);
            entity.Property(e => e.push_token).HasMaxLength(500);

            entity.HasOne(d => d.user).WithMany(p => p.UserDevices)
                .HasForeignKey(d => d.user_id)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_UserDevices_User");
        });

        modelBuilder.Entity<UserSession>(entity =>
        {
            entity.HasKey(e => e.session_id).HasName("PK__UserSess__69B13FDCB030BE5B");

            entity.HasIndex(e => e.user_id, "IX_UserSessions_User");

            entity.HasIndex(e => e.refresh_token_hash, "UQ_UserSessions_TokenHash").IsUnique();

            entity.Property(e => e.created_at).HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.device_info).HasMaxLength(255);
            entity.Property(e => e.ip_address).HasMaxLength(45);
            entity.Property(e => e.refresh_token_hash).HasMaxLength(32);

            entity.HasOne(d => d.user).WithMany(p => p.UserSessions)
                .HasForeignKey(d => d.user_id)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_UserSessions_User");
        });

        modelBuilder.Entity<Vendor>(entity =>
        {
            entity.HasKey(e => e.vendor_id).HasName("PK__Vendors__0F7D2B78D1819D79");

            entity.HasIndex(e => e.user_id, "UQ__Vendors__B9BE370E94CF3122").IsUnique();

            entity.Property(e => e.created_at).HasDefaultValueSql("(sysutcdatetime())");

            entity.HasOne(d => d.user).WithOne(p => p.Vendor)
                .HasForeignKey<Vendor>(d => d.user_id)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_Vendors_User");
        });

        modelBuilder.Entity<VendorComment>(entity =>
        {
            entity.HasKey(e => e.comment_id).HasName("PK__VendorCo__E7957687EF3B4D7C");

            entity.HasIndex(e => new { e.vendor_id, e.customer_user_id }, "UQ_VendorComments_OnePerCustomer").IsUnique();

            entity.Property(e => e.comment_text).HasMaxLength(1000);
            entity.Property(e => e.created_at).HasDefaultValueSql("(sysutcdatetime())");

            entity.HasOne(d => d.customer_user).WithMany(p => p.VendorComments)
                .HasForeignKey(d => d.customer_user_id)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_VendorComments_Customer");

            entity.HasOne(d => d.vendor).WithMany(p => p.VendorComments)
                .HasForeignKey(d => d.vendor_id)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_VendorComments_Vendor");
        });

        modelBuilder.Entity<VendorReport>(entity =>
        {
            entity.HasKey(e => e.report_id).HasName("PK__VendorRe__779B7C584924C5EC");

            entity.Property(e => e.ai_extracted_location).HasMaxLength(200);
            entity.Property(e => e.created_at).HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.evidence_url).HasMaxLength(500);
            entity.Property(e => e.report_reason).HasMaxLength(500);
            entity.Property(e => e.report_status)
                .HasMaxLength(20)
                .HasDefaultValue("PENDING");
            entity.Property(e => e.reviewer_role)
                .HasMaxLength(30)
                .HasComputedColumnSql("(CONVERT([nvarchar](30),N'WARD_AUTHORITY'))", true);

            entity.HasOne(d => d.reporter_user).WithMany(p => p.VendorReportreporter_users)
                .HasForeignKey(d => d.reporter_user_id)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_VendorReports_Reporter");

            entity.HasOne(d => d.scanned_permit).WithMany(p => p.VendorReports)
                .HasForeignKey(d => d.scanned_permit_id)
                .HasConstraintName("FK_VendorReports_Permit");

            entity.HasOne(d => d.slot).WithMany(p => p.VendorReports)
                .HasForeignKey(d => d.slot_id)
                .HasConstraintName("FK_VendorReports_Slot");

            entity.HasOne(d => d.vendor).WithMany(p => p.VendorReports)
                .HasForeignKey(d => d.vendor_id)
                .HasConstraintName("FK_VendorReports_Vendor");

            entity.HasOne(d => d.UserAccount).WithMany(p => p.VendorReportUserAccounts)
                .HasPrincipalKey(p => new { p.user_id, p.role_code })
                .HasForeignKey(d => new { d.reviewed_by, d.reviewer_role })
                .HasConstraintName("FK_VendorReports_Reviewer");
        });

        modelBuilder.Entity<Violation>(entity =>
        {
            entity.HasKey(e => e.violation_id).HasName("PK__Violatio__8A989363E5B38433");

            entity.HasIndex(e => e.contract_id, "IX_Violations_Contract");

            entity.HasIndex(e => e.slot_id, "IX_Violations_Slot");

            entity.HasIndex(e => e.vendor_id, "IX_Violations_Vendor");

            entity.Property(e => e.description).HasMaxLength(1000);
            entity.Property(e => e.evidence_url).HasMaxLength(500);
            entity.Property(e => e.recorded_at).HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.recorder_role)
                .HasMaxLength(30)
                .HasComputedColumnSql("(CONVERT([nvarchar](30),N'WARD_AUTHORITY'))", true);
            entity.Property(e => e.source)
                .HasMaxLength(20)
                .HasDefaultValue("ON_SITE");
            entity.Property(e => e.violation_type).HasMaxLength(50);

            entity.HasOne(d => d.contract).WithMany(p => p.Violations)
                .HasForeignKey(d => d.contract_id)
                .HasConstraintName("FK_Violations_Contract");

            entity.HasOne(d => d.slot).WithMany(p => p.Violations)
                .HasForeignKey(d => d.slot_id)
                .HasConstraintName("FK_Violations_Slot");

            entity.HasOne(d => d.source_report).WithMany(p => p.Violations)
                .HasForeignKey(d => d.source_report_id)
                .HasConstraintName("FK_Violations_SourceReport");

            entity.HasOne(d => d.vendor).WithMany(p => p.Violations)
                .HasForeignKey(d => d.vendor_id)
                .HasConstraintName("FK_Violations_Vendor");

            entity.HasOne(d => d.violation_typeNavigation).WithMany(p => p.Violations)
                .HasForeignKey(d => d.violation_type)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_Violations_Type");

            entity.HasOne(d => d.UserAccount).WithMany(p => p.Violations)
                .HasPrincipalKey(p => new { p.user_id, p.role_code })
                .HasForeignKey(d => new { d.recorded_by, d.recorder_role })
                .HasConstraintName("FK_Violations_RecordedBy");
        });

        modelBuilder.Entity<ViolationType>(entity =>
        {
            entity.HasKey(e => e.violation_type_code).HasName("PK__Violatio__D9A2A48CDD765108");

            entity.Property(e => e.violation_type_code).HasMaxLength(50);
            entity.Property(e => e.description).HasMaxLength(200);
            entity.Property(e => e.is_active).HasDefaultValue(true);
        });

        modelBuilder.Entity<ZoneFeeComponent>(entity =>
        {
            entity.HasKey(e => e.component_id).HasName("PK__ZoneFeeC__AEB1DA59DCF20270");

            entity.HasIndex(e => e.zone_id, "IX_ZoneFeeComponents_Zone");

            entity.Property(e => e.calc_basis).HasMaxLength(10);
            entity.Property(e => e.component_name).HasMaxLength(150);
            entity.Property(e => e.sort_order).HasDefaultValue(0);
            entity.Property(e => e.unit_amount).HasColumnType("decimal(18, 0)");

            entity.HasOne(d => d.zone).WithMany(p => p.ZoneFeeComponents)
                .HasForeignKey(d => d.zone_id)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_ZoneFeeComponents_Zone");
        });

        modelBuilder.Entity<vw_PermitValidity>(entity =>
        {
            entity
                .HasNoKey()
                .ToView("vw_PermitValidity");

            entity.Property(e => e.contract_status).HasMaxLength(20);
            entity.Property(e => e.effective_status)
                .HasMaxLength(13)
                .IsUnicode(false);
            entity.Property(e => e.permit_status).HasMaxLength(20);
            entity.Property(e => e.qr_payload).HasMaxLength(500);
        });

        modelBuilder.Entity<vw_VendorRating>(entity =>
        {
            entity
                .HasNoKey()
                .ToView("vw_VendorRatings");

            entity.Property(e => e.community_rating).HasColumnType("decimal(4, 2)");
            entity.Property(e => e.verified_rating).HasColumnType("decimal(4, 2)");
        });

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}
