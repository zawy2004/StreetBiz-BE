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

        // The scaffolded singular navigations (RentalContract.DigitalPermit,
        // RentalContract.RenewalRequest, BusinessRegistration.AddressChangeRequest,
        // RentalContract.FeeSchedule) were generated to match the one-to-one Fluent config
        // below and cannot coexist with a one-to-many relationship on the same foreign key —
        // EF refuses to flip the multiplicity while a singular reference nav is still paired
        // to it. They are unmapped here; a repository queries the *current* row directly
        // (e.g. the live permit) rather than through contract.DigitalPermit.
        modelBuilder.Entity<RentalContract>().Ignore(e => e.DigitalPermit);
        modelBuilder.Entity<RentalContract>().Ignore(e => e.RenewalRequest);
        modelBuilder.Entity<RentalContract>().Ignore(e => e.FeeSchedule);
        modelBuilder.Entity<BusinessRegistration>().Ignore(e => e.AddressChangeRequest);

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
    }
}
