using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Infrastructure.Persistence;
using StreetBiz.Infrastructure.Persistence.Repositories;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

namespace StreetBiz.Infrastructure.Tests;

/// <summary>ADM-07: the ReportExports history and the row sets, on a real (SQLite) schema.</summary>
public sealed class PlatformReportRepositoryTests
{
    private static readonly DateOnly From = new(2026, 10, 1);
    private static readonly DateOnly To = new(2026, 10, 3);
    private static readonly DashboardRange Range = new(From, To, new DateOnly(2026, 9, 28), new DateOnly(2026, 9, 30));

    [Fact]
    public async Task An_export_goes_from_pending_to_ready_with_an_audit_row()
    {
        using var f = await Fixture.Create();

        var id = await f.Repository.CreateExportAsync(1, PlatformReportTypes.Orders, From, To, "xlsx", default);
        Assert.Equal(ExportStatuses.Pending, (await f.Repository.GetExportAsync(id, 1, default))!.Status);

        await f.Repository.CompleteExportAsync(id, "platform-exports/1/f.xlsx", "f.xlsx", 1, default);

        var row = (await f.Repository.GetExportAsync(id, 1, default))!;
        Assert.Equal(ExportStatuses.Ready, row.Status);
        Assert.Equal("platform-exports/1/f.xlsx", row.FileUrl);
        Assert.Equal("f.xlsx", row.FileName);
        Assert.Equal(From, row.From);
        Assert.Equal(To, row.To);
        Assert.Equal("xlsx", row.Format);
        Assert.NotNull(row.CompletedAt);
        var audit = Assert.Single(f.Db.AuditLogs.Where(a => a.action == PlatformReportLimits.AuditAction));
        Assert.Equal(id, audit.entity_id);
        Assert.Equal(1, audit.actor_user_id);
    }

    [Fact]
    public async Task A_failed_export_keeps_its_reason_and_cannot_become_ready_later()
    {
        using var f = await Fixture.Create();
        var id = await f.Repository.CreateExportAsync(1, PlatformReportTypes.Orders, From, To, "csv", default);

        await f.Repository.FailExportAsync(id, new string('x', 900), default);

        var row = (await f.Repository.GetExportAsync(id, 1, default))!;
        Assert.Equal(ExportStatuses.Failed, row.Status);
        Assert.Equal(500, row.FailureReason!.Length);
        Assert.Null(row.FileUrl);
        Assert.Empty(f.Db.AuditLogs.Where(a => a.action == PlatformReportLimits.AuditAction));
    }

    [Fact]
    public async Task The_history_is_the_callers_own_newest_first_and_other_admins_exports_stay_hidden()
    {
        using var f = await Fixture.Create();
        var first = await f.Repository.CreateExportAsync(1, PlatformReportTypes.Orders, From, To, "xlsx", default);
        var second = await f.Repository.CreateExportAsync(1, PlatformReportTypes.Accounts, From, To, "csv", default);
        var other = await f.Repository.CreateExportAsync(2, PlatformReportTypes.Orders, From, To, "xlsx", default);

        var page = await f.Repository.ListExportsAsync(1, 1, 10, default);

        Assert.Equal([second, first], page.Items.Select(r => r.ExportId));
        Assert.Equal(2, page.TotalCount);
        Assert.Null(await f.Repository.GetExportAsync(other, 1, default));
        Assert.NotNull(await f.Repository.GetExportAsync(other, 2, default));
    }

    [Fact]
    public async Task The_orders_sheet_keeps_a_header_even_for_an_empty_period_and_hides_customers()
    {
        using var f = await Fixture.Create();

        var dataset = await f.Repository.GetDatasetAsync(PlatformReportTypes.Orders, Range, default);

        var sheet = Assert.Single(dataset.Sheets);
        Assert.Equal("Mã đơn", sheet.Headers[0]);
        Assert.DoesNotContain(sheet.Headers, h => h.Contains("khách", StringComparison.OrdinalIgnoreCase));
        Assert.Single(sheet.Rows);
        Assert.Equal("ORD-1", sheet.Rows[0][0]);
        Assert.Equal("Quán Một", sheet.Rows[0][1]);
        // 30 Sep 17:30 UTC is 1 Oct 00:30 in Da Nang.
        Assert.Equal("01/10/2026 00:30", sheet.Rows[0][5]);
        Assert.False(dataset.Truncated);

        var empty = await f.Repository.GetDatasetAsync(PlatformReportTypes.Orders,
            new DashboardRange(new DateOnly(2025, 1, 1), new DateOnly(2025, 1, 2), new DateOnly(2024, 12, 30), new DateOnly(2024, 12, 31)), default);
        Assert.Empty(Assert.Single(empty.Sheets).Rows);
    }

    [Fact]
    public async Task The_accounts_sheet_masks_phones_and_leaves_officers_out()
    {
        using var f = await Fixture.Create();

        var sheet = Assert.Single((await f.Repository.GetDatasetAsync(PlatformReportTypes.Accounts, Range, default)).Sheets);

        Assert.Equal(["Khách mua"], sheet.Rows.Select(row => row[1]));
        Assert.Equal("090****003", sheet.Rows[0][2]);
        Assert.Equal("CUSTOMER", sheet.Rows[0][3]);
    }

    [Fact]
    public async Task The_moderation_report_has_reports_and_complaints_and_keeps_hostile_text_as_data()
    {
        using var f = await Fixture.Create();

        var dataset = await f.Repository.GetDatasetAsync(PlatformReportTypes.Moderation, Range, default);

        Assert.Equal(["Nội dung bị báo cáo", "Khiếu nại đơn hàng"], dataset.Sheets.Select(s => s.Name));
        // Neutralising formulas is the renderer's job; the dataset carries the text as typed.
        Assert.Equal("=1+1", Assert.Single(dataset.Sheets[0].Rows)[3]);
        Assert.Equal("Thiếu món", Assert.Single(dataset.Sheets[1].Rows)[3]);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        public TestContext Db { get; private set; } = null!;
        public PlatformReportRepository Repository { get; private set; } = null!;

        public static async Task<Fixture> Create()
        {
            var f = new Fixture();
            await f.connection.OpenAsync();
            f.Db = new TestContext(new DbContextOptionsBuilder<StreetBizDbContext>().UseSqlite(f.connection).Options);
            await f.Db.Database.EnsureCreatedAsync();
            f.Repository = new PlatformReportRepository(f.Db, TimeProvider.System);

            var inPeriod = new DateTime(2026, 9, 30, 17, 30, 0, DateTimeKind.Utc);
            f.Db.Roles.AddRange(
                new Role { role_code = "PLATFORM_ADMIN", role_name = "Admin" },
                new Role { role_code = "VENDOR", role_name = "Vendor" },
                new Role { role_code = "CUSTOMER", role_name = "Customer" },
                new Role { role_code = "WARD_AUTHORITY", role_name = "Ward" });
            f.Db.AdministrativeUnits.Add(new AdministrativeUnit { unit_id = 1, unit_type = "WARD", unit_name = "Ward" });
            f.Db.UserAccounts.AddRange(
                User(1, "PLATFORM_ADMIN", "Quản trị", inPeriod),
                User(2, "PLATFORM_ADMIN", "Quản trị 2", inPeriod),
                User(3, "CUSTOMER", "Khách mua", inPeriod),
                User(4, "WARD_AUTHORITY", "Cán bộ", inPeriod),
                User(5, "VENDOR", "Hộ cũ", new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
            f.Db.Vendors.Add(new Vendor { vendor_id = 1, user_id = 5 });
            f.Db.PricingZones.Add(new PricingZone { zone_id = 1, ward_unit_id = 1, zone_name = "Zone", created_by = 1 });
            f.Db.BusinessRegistrations.Add(new BusinessRegistration { registration_id = 1, vendor_id = 1, ward_unit_id = 1, vendor_type = "ITINERANT", display_name = "Vendor", declared_address = "12 Nguyễn Văn Linh, Hải Châu, Đà Nẵng", registration_status = "APPROVED" });
            f.Db.SidewalkSlots.Add(new SidewalkSlot { slot_id = 1, zone_id = 1, slot_code = "SLOT", source = "WARD_DEFINED", slot_status = "ACTIVE" });
            f.Db.RentalApplications.Add(new RentalApplication { application_id = 1, registration_id = 1, slot_id = 1, application_method = "MANUAL_SELECTED", application_status = "APPROVED" });
            f.Db.RentalContracts.Add(new RentalContract { contract_id = 1, application_id = 1, slot_id = 1, vendor_id = 1, start_date = new DateOnly(2026, 1, 1), end_date = new DateOnly(2026, 12, 31), contract_status = "ACTIVE" });
            f.Db.Storefronts.Add(new Storefront { storefront_id = 1, registration_id = 1, contract_id = 1, storefront_name = "Quán Một", availability_status = "OPEN" });
            f.Db.Orders.Add(new Order { order_id = 1, order_code = "ORD-1", customer_user_id = 3, storefront_id = 1, order_status = "PLACED", subtotal_amount = 50_000, total_amount = 50_000, created_at = inPeriod });
            f.Db.Complaints.Add(new Complaint { complaint_id = 1, order_id = 1, customer_user_id = 3, complaint_type = "COMPLAINT", description = "Thiếu món", status = "OPEN", created_at = inPeriod });
            f.Db.ReportedContents.Add(new ReportedContent { content_report_id = 1, content_type = "STOREFRONT", content_id = 1, reporter_user_id = 3, reason = "=1+1", status = "PENDING", created_at = inPeriod });
            await f.Db.SaveChangesAsync();
            f.Db.ChangeTracker.Clear();
            return f;
        }

        public void Dispose()
        {
            Db.Dispose();
            connection.Dispose();
        }

        private static UserAccount User(long id, string role, string name, DateTime createdAt) => new()
        {
            user_id = id,
            phone_number = $"090000000{id}",
            password_hash = "test-only",
            full_name = name,
            role_code = role,
            account_status = "ACTIVE",
            created_at = createdAt,
        };
    }

    private sealed class TestContext(DbContextOptions<StreetBizDbContext> options) : StreetBizDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            foreach (var entity in modelBuilder.Model.GetEntityTypes())
                foreach (var property in entity.GetProperties())
                {
                    if (property.GetComputedColumnSql() is not null)
                    {
                        property.SetComputedColumnSql(null);
                        property.ValueGenerated = ValueGenerated.Never;
                    }

                    property.SetDefaultValueSql(null);
                    if (property.ClrType == typeof(decimal) || property.ClrType == typeof(decimal?))
                        modelBuilder.Entity(entity.ClrType).Property(property.Name).HasConversion<double>();
                }
        }
    }
}
