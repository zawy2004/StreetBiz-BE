using Microsoft.EntityFrameworkCore;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

namespace StreetBiz.Infrastructure.Persistence;

/// <summary>
/// Mapping for payment-to-payee (payOS receiving accounts) and Treasury receipts for fines.
/// Tables and constraints are defined in db/StreetBiz_SQL_Server.sql, section 5. The composite
/// role/ward foreign keys stay database-only, as for the other ward-scoped tables.
/// </summary>
public partial class StreetBizDbContext
{
    public DbSet<PayeeAccount> PayeeAccounts => Set<PayeeAccount>();

    public DbSet<PenaltyPaymentReceipt> PenaltyPaymentReceipts => Set<PenaltyPaymentReceipt>();

    private static void ConfigurePayees(ModelBuilder builder)
    {
        builder.Entity<PayeeAccount>(entity =>
        {
            entity.ToTable("PayeeAccounts");
            entity.HasKey(e => e.payee_account_id);
            entity.HasIndex(e => e.webhook_token).IsUnique();
            entity.HasIndex(e => e.vendor_id, "UX_PayeeAccounts_Vendor").IsUnique().HasFilter("([vendor_id] IS NOT NULL)");
            entity.HasIndex(e => e.ward_unit_id, "UX_PayeeAccounts_Ward").IsUnique().HasFilter("([ward_unit_id] IS NOT NULL)");

            entity.Property(e => e.owner_type).HasMaxLength(10);
            entity.Property(e => e.ward_unit_type)
                .HasMaxLength(20)
                .HasComputedColumnSql("(CONVERT([nvarchar](20),N'WARD'))", true);
            entity.Property(e => e.provider).HasMaxLength(20).HasDefaultValue("PAYOS");
            entity.Property(e => e.client_id).HasMaxLength(100);
            entity.Property(e => e.api_key_protected).HasMaxLength(1000);
            entity.Property(e => e.checksum_key_protected).HasMaxLength(1000);
            entity.Property(e => e.bank_name).HasMaxLength(100);
            entity.Property(e => e.account_number).HasMaxLength(50);
            entity.Property(e => e.account_name).HasMaxLength(150);
            entity.Property(e => e.webhook_token).HasMaxLength(64);
            entity.Property(e => e.status).HasMaxLength(20).HasDefaultValue("ACTIVE");
            entity.Property(e => e.last_error).HasMaxLength(500);
            entity.Property(e => e.created_at).HasDefaultValueSql("(sysutcdatetime())");

            entity.HasOne(e => e.vendor).WithMany()
                .HasForeignKey(e => e.vendor_id)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_PayeeAccounts_Vendor");
        });

        builder.Entity<PaymentTransaction>(entity =>
        {
            entity.HasIndex(e => e.payee_account_id, "IX_PaymentTransactions_PayeeAccount")
                .HasFilter("([payee_account_id] IS NOT NULL)");
            entity.HasOne(e => e.payee_account).WithMany(p => p.PaymentTransactions)
                .HasForeignKey(e => e.payee_account_id)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_PaymentTransactions_PayeeAccount");
        });

        builder.Entity<PenaltyPaymentReceipt>(entity =>
        {
            entity.ToTable("PenaltyPaymentReceipts");
            entity.HasKey(e => e.receipt_id);
            entity.HasIndex(e => e.penalty_id, "UX_PenaltyPaymentReceipts_OnePending")
                .IsUnique()
                .HasFilter("([status]='SUBMITTED')");

            entity.Property(e => e.receipt_url).HasMaxLength(500);
            entity.Property(e => e.reference).HasMaxLength(100);
            entity.Property(e => e.note).HasMaxLength(500);
            entity.Property(e => e.status).HasMaxLength(20).HasDefaultValue("SUBMITTED");
            entity.Property(e => e.submitted_at).HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.reviewer_role)
                .HasMaxLength(30)
                .HasComputedColumnSql("(CONVERT([nvarchar](30),N'WARD_AUTHORITY'))", true);
            entity.Property(e => e.review_note).HasMaxLength(500);

            entity.HasOne(e => e.penalty).WithMany()
                .HasForeignKey(e => e.penalty_id)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_PenaltyPaymentReceipts_Penalty");
        });
    }
}
