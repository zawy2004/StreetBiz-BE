using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using StreetBiz.Application.Common.Models;
using StreetBiz.Infrastructure.Persistence;
using StreetBiz.Infrastructure.Persistence.Repositories;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

namespace StreetBiz.Infrastructure.Tests;

/// <summary>ADM-06 counted from real rows (SQLite): periods, Vietnam-time days, and the platform-only scope.</summary>
public sealed class PlatformDashboardRepositoryTests
{
    // The period under test is 1–3 October 2026, Vietnam time (so 30 Sep 17:00 UTC to 3 Oct 17:00 UTC).
    private static readonly DashboardRange Range = new(
        new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 3),
        new DateOnly(2026, 9, 28), new DateOnly(2026, 9, 30));

    [Fact]
    public async Task An_empty_database_gives_zeroes_and_no_rows()
    {
        using var f = await Fixture.Create(seedOrders: false);

        var facts = await f.Repository.GetDashboardFactsAsync(Range, default);

        Assert.Equal(0, facts.Current.OrdersPlaced);
        Assert.Equal(0m, facts.Current.Gmv);
        Assert.Null(facts.Current.AverageReviewHours);
        Assert.Empty(facts.OrdersByStatus);
        Assert.Empty(facts.Days);
        Assert.Empty(facts.TopStorefronts);
        Assert.Equal(0, facts.Snapshot.PendingReports);
        Assert.Equal(0m, facts.Snapshot.PendingRefundAmount);
    }

    [Fact]
    public async Task Orders_are_counted_in_the_period_and_the_one_before_by_vietnam_day()
    {
        using var f = await Fixture.Create();

        var facts = await f.Repository.GetDashboardFactsAsync(Range, default);

        // In period: 3 created (one on 1 Oct local at 00:30, one on 2 Oct, one cancelled on 3 Oct) …
        Assert.Equal(3, facts.Current.OrdersPlaced);
        Assert.Equal(1, facts.Current.OrdersCancelled);
        Assert.Equal(0, facts.Current.OrdersRejected);
        Assert.Equal(2, facts.Current.OrdersCompleted);
        Assert.Equal(125_000m, facts.Current.Gmv);
        // … and the one on 29 Sep is the previous period's.
        Assert.Equal(1, facts.Previous.OrdersPlaced);
        Assert.Equal(1, facts.Previous.OrdersCompleted);
        Assert.Equal(40_000m, facts.Previous.Gmv);

        // 30 Sep 17:30 UTC is 1 Oct 00:30 in Da Nang: it belongs to the first day, not the day before.
        var first = facts.Days.Single(d => d.Day == new DateOnly(2026, 10, 1) && d.OrdersPlaced > 0);
        Assert.Equal(1, first.OrdersPlaced);
        Assert.DoesNotContain(facts.Days, d => d.Day == new DateOnly(2026, 9, 30));

        Assert.Equal(
            new[] { ("CANCELLED", 1), ("COMPLETED", 2) },
            facts.OrdersByStatus.OrderBy(s => s.Status).Select(s => (s.Status, s.Count)).ToArray());
    }

    [Fact]
    public async Task The_top_storefronts_are_ranked_by_revenue_in_the_period()
    {
        using var f = await Fixture.Create();

        var top = (await f.Repository.GetDashboardFactsAsync(Range, default)).TopStorefronts;

        Assert.Equal(["Quán Hai", "Quán Một"], top.Select(row => row.StorefrontName));
        Assert.Equal(75_000m, top[0].Revenue);
        Assert.Equal(1, top[1].CompletedOrders);
    }

    [Fact]
    public async Task The_snapshot_counts_accounts_storefronts_and_moderation_work_right_now()
    {
        using var f = await Fixture.Create();

        var snapshot = (await f.Repository.GetDashboardFactsAsync(Range, default)).Snapshot;

        Assert.Equal(3, snapshot.UsersTotal);
        Assert.Equal(1, snapshot.Customers);
        Assert.Equal(1, snapshot.Vendors);
        Assert.Equal(1, snapshot.PlatformAdmins);
        Assert.Equal(1, snapshot.SuspendedUsers);
        Assert.Equal(1, snapshot.StorefrontsOpen);
        Assert.Equal(1, snapshot.StorefrontsPaused);
        Assert.Equal(1, snapshot.StorefrontsHidden);
        Assert.Equal(1, snapshot.PendingReports);
        Assert.Equal(1, snapshot.OpenComplaints);
        Assert.Equal(1, snapshot.PendingRefunds);
        Assert.Equal(20_000m, snapshot.PendingRefundAmount);
    }

    [Fact]
    public async Task The_average_review_time_comes_from_reports_closed_in_the_period()
    {
        using var f = await Fixture.Create();

        var current = (await f.Repository.GetDashboardFactsAsync(Range, default)).Current;

        Assert.Equal(1, current.ReportsFiled);
        Assert.Equal(1, current.ReportsReviewed);
        Assert.Equal(6.0, current.AverageReviewHours!.Value, 3);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        public TestContext Db { get; private set; } = null!;
        public PlatformAdministrationRepository Repository { get; private set; } = null!;

        public static async Task<Fixture> Create(bool seedOrders = true)
        {
            var f = new Fixture();
            await f.connection.OpenAsync();
            f.Db = new TestContext(new DbContextOptionsBuilder<StreetBizDbContext>().UseSqlite(f.connection).Options);
            await f.Db.Database.EnsureCreatedAsync();
            f.Repository = new PlatformAdministrationRepository(f.Db, TimeProvider.System);
            if (!seedOrders)
            {
                return f;
            }

            var created = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
            f.Db.Roles.AddRange(
                new Role { role_code = "PLATFORM_ADMIN", role_name = "Admin" },
                new Role { role_code = "VENDOR", role_name = "Vendor" },
                new Role { role_code = "CUSTOMER", role_name = "Customer" });
            f.Db.AdministrativeUnits.Add(new AdministrativeUnit { unit_id = 1, unit_type = "WARD", unit_name = "Ward" });
            f.Db.UserAccounts.AddRange(
                User(1, "PLATFORM_ADMIN", "ACTIVE", created),
                User(2, "VENDOR", "ACTIVE", created),
                User(3, "CUSTOMER", "SUSPENDED", created));
            f.Db.Vendors.Add(new Vendor { vendor_id = 1, user_id = 2 });
            f.Db.PricingZones.Add(new PricingZone { zone_id = 1, ward_unit_id = 1, zone_name = "Zone", created_by = 1 });
            for (var i = 1; i <= 3; i++)
            {
                f.Db.BusinessRegistrations.Add(new BusinessRegistration { registration_id = i, vendor_id = 1, ward_unit_id = 1, vendor_type = "ITINERANT", display_name = $"V{i}", declared_address = "12 Nguyễn Văn Linh, Hải Châu, Đà Nẵng", registration_status = "APPROVED" });
                f.Db.SidewalkSlots.Add(new SidewalkSlot { slot_id = i, zone_id = 1, slot_code = $"SLOT{i}", source = "WARD_DEFINED", slot_status = "ACTIVE" });
                f.Db.RentalApplications.Add(new RentalApplication { application_id = i, registration_id = i, slot_id = i, application_method = "MANUAL_SELECTED", application_status = "APPROVED" });
                f.Db.RentalContracts.Add(new RentalContract { contract_id = i, application_id = i, slot_id = i, vendor_id = 1, start_date = new DateOnly(2026, 1, 1), end_date = new DateOnly(2026, 12, 31), contract_status = "ACTIVE" });
            }

            f.Db.Storefronts.AddRange(
                new Storefront { storefront_id = 1, registration_id = 1, contract_id = 1, storefront_name = "Quán Một", availability_status = "OPEN" },
                new Storefront { storefront_id = 2, registration_id = 2, contract_id = 2, storefront_name = "Quán Hai", availability_status = "PAUSED" },
                new Storefront { storefront_id = 3, registration_id = 3, contract_id = 3, storefront_name = "Quán Ba", availability_status = "CLOSED" });
            f.Db.Orders.AddRange(
                // 30 Sep 17:30 UTC = 1 Oct 00:30 Da Nang: the first day of the period.
                Order(1, 1, "COMPLETED", 50_000, new DateTime(2026, 9, 30, 17, 30, 0, DateTimeKind.Utc), new DateTime(2026, 9, 30, 18, 0, 0, DateTimeKind.Utc)),
                Order(2, 2, "COMPLETED", 75_000, new DateTime(2026, 10, 2, 3, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 2, 4, 0, 0, DateTimeKind.Utc)),
                Order(3, 1, "CANCELLED", 99_000, new DateTime(2026, 10, 3, 3, 0, 0, DateTimeKind.Utc), null),
                // Previous period (29 Sep local).
                Order(4, 1, "COMPLETED", 40_000, new DateTime(2026, 9, 29, 3, 0, 0, DateTimeKind.Utc), new DateTime(2026, 9, 29, 4, 0, 0, DateTimeKind.Utc)),
                // Outside both periods.
                Order(5, 1, "COMPLETED", 10_000, new DateTime(2026, 9, 1, 3, 0, 0, DateTimeKind.Utc), new DateTime(2026, 9, 1, 4, 0, 0, DateTimeKind.Utc)));
            f.Db.PaymentTransactions.Add(new PaymentTransaction { transaction_id = 1, order_id = 2, idempotency_key = "PAY-1", payment_purpose = "ORDER", provider = "MOMO", amount = 75_000, transaction_status = "SUCCESS" });
            f.Db.RefundTransactions.Add(new RefundTransaction { refund_id = 1, order_id = 2, payment_transaction_id = 1, idempotency_key = "R-1", amount = 20_000, refund_reason = "COMPLAINT_RESOLVED", provider = "MOMO", refund_status = "PENDING", requested_at = created });
            f.Db.Complaints.Add(new Complaint { complaint_id = 1, order_id = 2, customer_user_id = 3, complaint_type = "COMPLAINT", description = "Thiếu món", status = "OPEN", created_at = created });
            f.Db.ReportedContents.AddRange(
                new ReportedContent { content_report_id = 1, content_type = "STOREFRONT", content_id = 2, reporter_user_id = 3, reason = "Sai món", status = "HIDDEN", reviewed_by = 1, created_at = new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc), reviewed_at = new DateTime(2026, 10, 2, 6, 0, 0, DateTimeKind.Utc) },
                new ReportedContent { content_report_id = 2, content_type = "MENU_ITEM", content_id = 9, reporter_user_id = 3, reason = "Ảnh lạ", status = "PENDING", created_at = created });
            await f.Db.SaveChangesAsync();
            f.Db.ChangeTracker.Clear();
            return f;
        }

        public void Dispose()
        {
            Db.Dispose();
            connection.Dispose();
        }

        private static UserAccount User(long id, string role, string status, DateTime createdAt) => new()
        {
            user_id = id,
            phone_number = $"090000000{id}",
            password_hash = "test-only",
            full_name = $"User {id}",
            role_code = role,
            account_status = status,
            created_at = createdAt,
        };

        private static Order Order(long id, long storefrontId, string status, decimal total, DateTime created, DateTime? completed) => new()
        {
            order_id = id,
            order_code = $"ORD-{id}",
            customer_user_id = 3,
            storefront_id = storefrontId,
            order_status = status,
            subtotal_amount = total,
            total_amount = total,
            created_at = created,
            completed_at = completed,
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
