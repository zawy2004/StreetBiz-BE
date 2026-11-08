using Microsoft.EntityFrameworkCore;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

namespace StreetBiz.Infrastructure.Persistence;

/// <summary>
/// Mapping of the household tax tables (db/StreetBiz_SQL_Server.sql, section 12). Foreign keys to
/// users, vendors and wards live in the schema only: these entities are queried by id and never
/// traversed, so the model carries no navigations to them.
/// </summary>
public partial class StreetBizDbContext
{
    public DbSet<TaxPolicy> TaxPolicies => Set<TaxPolicy>();
    public DbSet<TaxIndustryRate> TaxIndustryRates => Set<TaxIndustryRate>();
    public DbSet<VendorTaxProfile> VendorTaxProfiles => Set<VendorTaxProfile>();
    public DbSet<RevenueEntry> RevenueEntries => Set<RevenueEntry>();
    public DbSet<TaxDeclaration> TaxDeclarations => Set<TaxDeclaration>();
    public DbSet<TaxReferral> TaxReferrals => Set<TaxReferral>();
    public DbSet<TaxAnnouncement> TaxAnnouncements => Set<TaxAnnouncement>();

    private static void ConfigureTax(ModelBuilder builder)
    {
        builder.Entity<TaxPolicy>(entity =>
        {
            entity.ToTable("TaxPolicies");
            entity.HasKey(e => e.policy_id);
            entity.HasIndex(e => new { e.tax_year, e.version_no }).IsUnique();
            entity.Property(e => e.legal_basis).HasMaxLength(500);
            entity.Property(e => e.note).HasMaxLength(1000);
            entity.Property(e => e.revenue_threshold).HasColumnType("decimal(18,0)");
            entity.Property(e => e.einvoice_threshold).HasColumnType("decimal(18,0)");
            entity.Property(e => e.group2_ceiling).HasColumnType("decimal(18,0)");
            entity.Property(e => e.group3_ceiling).HasColumnType("decimal(18,0)");
            entity.Property(e => e.pit_profit_rate_g2).HasColumnType("decimal(5,4)");
            entity.Property(e => e.pit_profit_rate_g3).HasColumnType("decimal(5,4)");
            entity.Property(e => e.pit_profit_rate_g4).HasColumnType("decimal(5,4)");
            entity.Property(e => e.warn_ratio).HasColumnType("decimal(5,4)");
            entity.Property(e => e.creator_role)
                .HasMaxLength(30)
                .HasComputedColumnSql("(CONVERT([nvarchar](30),N'PLATFORM_ADMIN'))", true);
            entity.Property(e => e.created_at).HasDefaultValueSql("(sysutcdatetime())");
        });

        builder.Entity<TaxIndustryRate>(entity =>
        {
            entity.ToTable("TaxIndustryRates");
            entity.HasKey(e => e.rate_id);
            entity.HasIndex(e => new { e.policy_id, e.industry_code }).IsUnique();
            entity.Property(e => e.industry_code).HasMaxLength(30);
            entity.Property(e => e.industry_name).HasMaxLength(200);
            entity.Property(e => e.note).HasMaxLength(300);
            entity.Property(e => e.vat_rate).HasColumnType("decimal(5,4)");
            entity.Property(e => e.pit_rate).HasColumnType("decimal(5,4)");
            entity.HasOne(d => d.policy).WithMany(p => p.TaxIndustryRates)
                .HasForeignKey(d => d.policy_id)
                .OnDelete(DeleteBehavior.Cascade)
                .HasConstraintName("FK_TaxIndustryRates_Policy");
        });

        builder.Entity<VendorTaxProfile>(entity =>
        {
            entity.ToTable("VendorTaxProfiles");
            entity.HasKey(e => e.vendor_id);
            entity.Property(e => e.vendor_id).ValueGeneratedNever();
            entity.Property(e => e.tax_code).HasMaxLength(14);
            entity.Property(e => e.industry_code).HasMaxLength(30).HasDefaultValue("FOOD_SERVICE");
            entity.Property(e => e.created_at).HasDefaultValueSql("(sysutcdatetime())");
        });

        builder.Entity<RevenueEntry>(entity =>
        {
            entity.ToTable("RevenueEntries");
            entity.HasKey(e => e.entry_id);
            entity.HasIndex(e => new { e.vendor_id, e.entry_date }, "IX_RevenueEntries_Vendor_Date");
            entity.Property(e => e.amount).HasColumnType("decimal(18,0)");
            entity.Property(e => e.channel).HasMaxLength(20);
            entity.Property(e => e.note).HasMaxLength(300);
            entity.Property(e => e.void_reason).HasMaxLength(300);
            entity.Property(e => e.created_at).HasDefaultValueSql("(sysutcdatetime())");
        });

        builder.Entity<TaxDeclaration>(entity =>
        {
            entity.ToTable("TaxDeclarations");
            entity.HasKey(e => e.declaration_id);
            entity.HasIndex(e => new { e.vendor_id, e.tax_year, e.period_type, e.period_no }).IsUnique();
            entity.Property(e => e.period_type).HasMaxLength(10);
            entity.Property(e => e.revenue_declared).HasColumnType("decimal(18,0)");
            entity.Property(e => e.revenue_system).HasColumnType("decimal(18,0)");
            entity.Property(e => e.authority_reference).HasMaxLength(100);
            entity.Property(e => e.receipt_url).HasMaxLength(500);
            entity.Property(e => e.note).HasMaxLength(500);
            entity.Property(e => e.created_at).HasDefaultValueSql("(sysutcdatetime())");
        });

        builder.Entity<TaxReferral>(entity =>
        {
            entity.ToTable("TaxReferrals");
            entity.HasKey(e => e.referral_id);
            entity.HasIndex(e => new { e.reason_code, e.source_type, e.source_id }).IsUnique();
            entity.HasIndex(e => new { e.ward_unit_id, e.referral_status }, "IX_TaxReferrals_Ward_Status");
            entity.Property(e => e.ward_unit_type)
                .HasMaxLength(20)
                .HasComputedColumnSql("(CONVERT([nvarchar](20),N'WARD'))", true);
            entity.Property(e => e.reason_code).HasMaxLength(30);
            entity.Property(e => e.source_type).HasMaxLength(20);
            entity.Property(e => e.summary).HasMaxLength(500);
            entity.Property(e => e.referral_status).HasMaxLength(10).HasDefaultValue("PENDING");
            entity.Property(e => e.authority_reference).HasMaxLength(100);
            entity.Property(e => e.handling_note).HasMaxLength(500);
            entity.Property(e => e.created_at).HasDefaultValueSql("(sysutcdatetime())");
        });

        builder.Entity<TaxAnnouncement>(entity =>
        {
            entity.ToTable("TaxAnnouncements");
            entity.HasKey(e => e.announcement_id);
            entity.Property(e => e.ward_unit_type)
                .HasMaxLength(20)
                .HasComputedColumnSql("(CONVERT([nvarchar](20),N'WARD'))", true);
            entity.Property(e => e.creator_role)
                .HasMaxLength(30)
                .HasComputedColumnSql("(CONVERT([nvarchar](30),N'WARD_AUTHORITY'))", true);
            entity.Property(e => e.segment).HasMaxLength(20);
            entity.Property(e => e.title).HasMaxLength(150);
            entity.Property(e => e.body).HasMaxLength(800);
            entity.Property(e => e.created_at).HasDefaultValueSql("(sysutcdatetime())");
        });
    }
}
