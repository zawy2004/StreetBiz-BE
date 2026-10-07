using Microsoft.EntityFrameworkCore;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

namespace StreetBiz.Infrastructure.Persistence;

/// <summary>
/// Corrects relationships the EF scaffolder mis-modelled as one-to-one because it read a
/// *filtered* unique index (only one open/live row) as an unconditional one. Each of these
/// tables legitimately holds multiple rows per parent over time; only one at a time is
/// "current". See db/StreetBiz_SQL_Server.sql for the filtered index that caused this.
/// </summary>
public partial class StreetBizDbContext
{
    partial void OnModelCreatingPartial(ModelBuilder modelBuilder)
    {
        // The scaffold uses --use-database-names, so property names already equal
        // column names. Keep the commerce table names explicit so a future DbSet
        // rename cannot silently redirect order/payment mappings.
        modelBuilder.Entity<ShoppingCart>().ToTable("ShoppingCarts");
        modelBuilder.Entity<ShoppingCartItem>().ToTable("ShoppingCartItems");
        modelBuilder.Entity<Storefront>().ToTable("Storefronts",
            table => table.HasTrigger("TR_Storefronts_Phase2Gate"));
        modelBuilder.Entity<MenuItem>().ToTable("MenuItems");
        modelBuilder.Entity<Order>().ToTable("Orders");
        modelBuilder.Entity<OrderItem>().ToTable("OrderItems");
        modelBuilder.Entity<OrderStatusHistory>().ToTable("OrderStatusHistory");
        modelBuilder.Entity<PaymentTransaction>().ToTable("PaymentTransactions");
        modelBuilder.Entity<PaymentCallbackEvent>().ToTable("PaymentCallbackEvents");
        modelBuilder.Entity<RefundTransaction>().ToTable("RefundTransactions",
            table => table.HasTrigger("TR_RefundTransactions_NotMoreThanPaid"));
        modelBuilder.Entity<Notification>().ToTable("Notifications");

        // SQL Server rejects OUTPUT without INTO for tables with enabled triggers.
        modelBuilder.Entity<RentalContract>()
            .ToTable("RentalContracts", table => table.UseSqlOutputClause(false));

        // DigitalPermits, RenewalRequests, FeeSchedules and AddressChangeRequests are
        // one-to-many (configured below), so RentalContract/BusinessRegistration carry no
        // singular navigation to them; a repository queries the *current* row directly
        // (e.g. the live permit). The scaffolder generates those navigations plus a
        // WithOne(...) mapping from the filtered unique indexes — if the DbContext is ever
        // re-scaffolded, delete them again and keep WithMany() in StreetBizDbContext.cs,
        // or EF logs "navigation ... was first mapped explicitly and then ignored" on startup.

        // Schema: see db/StreetBiz_SQL_Server.sql.
        modelBuilder.Entity<BusinessRegistration>()
            .Property(e => e.capital_amount).HasColumnType("decimal(18,0)");

        // Schema: see db/StreetBiz_SQL_Server.sql (UserAccounts). Who may sign a WARD-13 sanction.
        modelBuilder.Entity<UserAccount>()
            .Property(e => e.sanction_authority_title).HasMaxLength(100);

        // UQ_DigitalPermits_LivePerContract filters WHERE permit_status <> 'REVOKED': a
        // contract may accumulate a REVOKED permit plus a replacement (SIDE-08, BR-19/20).
        modelBuilder.Entity<DigitalPermit>()
            .HasOne(d => d.contract)
            .WithMany()
            .HasForeignKey(d => d.contract_id)
            .OnDelete(DeleteBehavior.ClientSetNull)
            .HasConstraintName("FK_DigitalPermits_Contract");

        // UQ_RenewalRequests_OpenPerContract filters WHERE renewal_status IN (PENDING,
        // UNDER_REVIEW): a contract can be renewed more than once over its lifetime (SIDE-06).
        modelBuilder.Entity<RenewalRequest>()
            .HasOne(d => d.contract)
            .WithMany()
            .HasForeignKey(d => d.contract_id)
            .OnDelete(DeleteBehavior.ClientSetNull)
            .HasConstraintName("FK_RenewalRequests_Contract");

        // UQ_AddressChangeRequests_OpenPerRegistration filters WHERE change_status IN (PENDING,
        // UNDER_REVIEW): a registration can raise more than one address change over time (SIDE-09).
        modelBuilder.Entity<AddressChangeRequest>()
            .HasOne(d => d.registration)
            .WithMany()
            .HasForeignKey(d => d.registration_id)
            .OnDelete(DeleteBehavior.ClientSetNull)
            .HasConstraintName("FK_AddressChangeRequests_Registration");

        // UQ_FeeSchedules_CurrentPerContract filters WHERE superseded_at IS NULL: a contract
        // accumulates one FeeSchedule per fee revision (UQ_FeeSchedules_ContractRevision is the
        // real per-revision uniqueness), and only the current, non-superseded one is "live".
        modelBuilder.Entity<FeeSchedule>()
            .HasOne(d => d.contract)
            .WithMany()
            .HasForeignKey(d => d.contract_id)
            .OnDelete(DeleteBehavior.ClientSetNull)
            .HasConstraintName("FK_FeeSchedules_Contract");

        // Schema: see db/StreetBiz_SQL_Server.sql. No navigation properties: the rows are
        // written before the registration exists and are read back by id, never traversed.
        modelBuilder.Entity<KycVerificationResult>(entity =>
        {
            entity.ToTable("KycVerificationResults");
            entity.HasKey(e => e.kyc_result_id);
            entity.Property(e => e.similarity_percent).HasColumnType("decimal(5,2)");
        });

        // Schema: see db/StreetBiz_SQL_Server.sql.
        // Not yet scaffolded from the live database, so the mapping is explicit here.
        modelBuilder.Entity<BusinessRegistrationHouseholdMember>(entity =>
        {
            entity.ToTable("BusinessRegistrationHouseholdMembers");
            entity.HasKey(e => e.member_id);
            entity.Property(e => e.capital_contribution).HasColumnType("decimal(18,0)");
            entity.HasOne(d => d.registration)
                .WithMany(r => r.HouseholdMembers)
                .HasForeignKey(d => d.registration_id)
                .OnDelete(DeleteBehavior.Cascade)
                .HasConstraintName("FK_HouseholdMembers_Registration");
        });
    }
}
