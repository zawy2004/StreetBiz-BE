using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Infrastructure.Persistence;
using StreetBiz.Infrastructure.Persistence.Repositories;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

namespace StreetBiz.Infrastructure.Tests;

/// <summary>
/// ADM-04 order complaints on real rows (SQLite): the list and detail with their payment and
/// refund figures, and every outcome of a decision, above all the refund limits (never more than
/// the buyer asked for, never more than was paid, counting refunds already under way).
/// </summary>
public sealed class PlatformOrderComplaintRepositoryTests
{
    private static ComplaintResolution Resolve(decimal? refund = null, string expected = ComplaintStatuses.Open) =>
        new(ComplaintDecisions.Resolve, "Đã kiểm tra", expected, refund);

    [Fact]
    public async Task The_list_is_newest_first_filters_by_status_and_carries_payment_and_refunds()
    {
        using var f = await Fixture.Create();

        var all = await f.Repository.ListOrderComplaintsAsync(null, 1, 10, default);
        Assert.Equal(4, all.TotalCount);
        Assert.Equal(new long[] { 4, 3, 2, 1 }, all.Items.Select(c => c.ComplaintId));

        var refund = all.Items.Single(c => c.ComplaintId == 2);
        Assert.Equal("ORD-2", refund.OrderCode);
        Assert.Equal("Quán Một", refund.StorefrontName);
        Assert.Equal("Khách", refund.CustomerName);
        Assert.Equal(100_000m, refund.PaymentAmount);
        Assert.Equal("MOMO", refund.PaymentProvider);
        // Only this complaint's own refunds count, and a failed one does not: the pending 10 000 does.
        Assert.Equal(10_000m, refund.RefundedAmount);
        Assert.Equal("PENDING", refund.LatestRefundStatus);

        var open = await f.Repository.ListOrderComplaintsAsync(ComplaintStatuses.Open, 1, 10, default);
        Assert.All(open.Items, c => Assert.Equal(ComplaintStatuses.Open, c.Status));
        Assert.Equal(3, open.TotalCount);

        var page2 = await f.Repository.ListOrderComplaintsAsync(null, 2, 3, default);
        Assert.Equal(new long[] { 1 }, page2.Items.Select(c => c.ComplaintId));
    }

    [Fact]
    public async Task The_detail_of_an_unknown_complaint_is_null_and_a_known_one_is_enriched()
    {
        using var f = await Fixture.Create();

        Assert.Null(await f.Repository.GetOrderComplaintAsync(999, default));
        var row = await f.Repository.GetOrderComplaintAsync(1, default);
        Assert.NotNull(row);
        Assert.Equal(ComplaintTypes.Complaint, row.ComplaintType);
        Assert.Null(row.ResolvedByName);
        // The resolver is joined on (resolved_by, resolver_role); SQL Server computes the role column,
        // SQLite does not, so the seeded closed complaint sets it by hand.
        Assert.Equal("Admin", (await f.Repository.GetOrderComplaintAsync(4, default))!.ResolvedByName);
    }

    [Fact]
    public async Task Resolving_without_money_closes_it_tells_the_buyer_and_audits()
    {
        using var f = await Fixture.Create();

        var result = await f.Repository.DecideOrderComplaintAsync(1, Resolve(), 1, default);

        Assert.Equal(ComplaintDecisionOutcome.Updated, result.Outcome);
        Assert.Equal(ComplaintStatuses.Resolved, result.Complaint!.Status);
        Assert.NotNull(result.Complaint.ResolvedAt);
        Assert.Equal("Đã kiểm tra", result.Complaint.ResolutionNotes);
        using var db = f.NewDb();
        Assert.Equal(0, await db.RefundTransactions.CountAsync(r => r.complaint_id == 1));
        Assert.Contains(await db.Notifications.ToListAsync(), n => n.user_id == 3 && n.body == "Khiếu nại đã được giải quyết.");
        Assert.True(await db.AuditLogs.AnyAsync(a => a.action == "ADM_COMPLAINT_RESOLVE" && a.entity_id == 1));
    }

    [Fact]
    public async Task Rejecting_closes_it_as_rejected()
    {
        using var f = await Fixture.Create();

        var result = await f.Repository.DecideOrderComplaintAsync(
            1, new ComplaintResolution(ComplaintDecisions.Reject, "Không đủ căn cứ", ComplaintStatuses.Open, null), 1, default);

        Assert.Equal(ComplaintStatuses.Rejected, result.Complaint!.Status);
        using var db = f.NewDb();
        Assert.Contains(await db.Notifications.ToListAsync(), n => n.body == "Khiếu nại đã bị từ chối.");
    }

    [Fact]
    public async Task An_approved_refund_queues_a_pending_refund_on_the_successful_payment()
    {
        using var f = await Fixture.Create();

        var result = await f.Repository.DecideOrderComplaintAsync(2, Resolve(30_000), 1, default);

        Assert.Equal(ComplaintDecisionOutcome.Updated, result.Outcome);
        using var db = f.NewDb();
        var refund = await db.RefundTransactions.SingleAsync(r => r.complaint_id == 2 && r.idempotency_key.StartsWith("ADM-COMPLAINT-"));
        Assert.Equal(30_000m, refund.amount);
        Assert.Equal("PENDING", refund.refund_status);
        Assert.Equal(1, refund.payment_transaction_id);
        Assert.Equal("COMPLAINT_RESOLVED", refund.refund_reason);
        Assert.StartsWith("ADM-COMPLAINT-2-", refund.idempotency_key);
        Assert.Contains(await db.Notifications.ToListAsync(), n => n.body.Contains("30,000") || n.body.Contains("30.000"));
    }

    [Theory]
    // More than the buyer asked for (50 000).
    [InlineData(60_000)]
    // Within the ask, but with the 10 000 already pending it would exceed it.
    [InlineData(45_000)]
    public async Task A_refund_beyond_the_ask_or_beyond_what_is_left_is_refused_and_nothing_changes(int amount)
    {
        using var f = await Fixture.Create();

        var result = await f.Repository.DecideOrderComplaintAsync(2, Resolve(amount), 1, default);

        Assert.Equal(ComplaintDecisionOutcome.RefundExceedsLimit, result.Outcome);
        Assert.Null(result.Complaint);
        using var db = f.NewDb();
        Assert.Equal(ComplaintStatuses.Open, (await db.Complaints.SingleAsync(c => c.complaint_id == 2)).status);
        Assert.Equal(0, await db.RefundTransactions.CountAsync(r => r.idempotency_key.StartsWith("ADM-COMPLAINT-")));
    }

    [Fact]
    public async Task Money_cannot_be_refunded_on_a_plain_complaint_or_on_a_rejection()
    {
        using var f = await Fixture.Create();

        Assert.Equal(ComplaintDecisionOutcome.RefundNotAllowed,
            (await f.Repository.DecideOrderComplaintAsync(1, Resolve(5_000), 1, default)).Outcome);
        Assert.Equal(ComplaintDecisionOutcome.RefundNotAllowed,
            (await f.Repository.DecideOrderComplaintAsync(
                2, new ComplaintResolution(ComplaintDecisions.Reject, "x", ComplaintStatuses.Open, 5_000), 1, default)).Outcome);
    }

    [Fact]
    public async Task A_refund_with_no_successful_payment_is_refused()
    {
        using var f = await Fixture.Create();

        Assert.Equal(ComplaintDecisionOutcome.PaymentNotFound,
            (await f.Repository.DecideOrderComplaintAsync(3, Resolve(5_000), 1, default)).Outcome);
    }

    [Fact]
    public async Task A_stale_status_a_closed_complaint_and_an_unknown_one()
    {
        using var f = await Fixture.Create();

        Assert.Equal(ComplaintDecisionOutcome.Conflict,
            (await f.Repository.DecideOrderComplaintAsync(1, Resolve(expected: ComplaintStatuses.UnderReview), 1, default)).Outcome);
        Assert.Equal(ComplaintDecisionOutcome.Conflict,
            (await f.Repository.DecideOrderComplaintAsync(4, Resolve(expected: ComplaintStatuses.Resolved), 1, default)).Outcome);
        Assert.Equal(ComplaintDecisionOutcome.NotFound,
            (await f.Repository.DecideOrderComplaintAsync(999, Resolve(), 1, default)).Outcome);
    }

    [Fact]
    public async Task A_category_in_use_stays_an_empty_one_is_deleted_and_audited()
    {
        using var f = await Fixture.Create();

        Assert.True(await f.Repository.FoodCategoryNameExistsAsync("Bánh mì", null, default));
        Assert.False(await f.Repository.FoodCategoryNameExistsAsync("Bánh mì", 1, default));
        Assert.Equal(CategoryDeleteOutcome.InUse, await f.Repository.DeleteFoodCategoryAsync(1, 1, default));
        Assert.Equal(CategoryDeleteOutcome.Deleted, await f.Repository.DeleteFoodCategoryAsync(2, 1, default));
        Assert.Equal(CategoryDeleteOutcome.NotFound, await f.Repository.DeleteFoodCategoryAsync(2, 1, default));
        using var db = f.NewDb();
        Assert.True(await db.AuditLogs.AnyAsync(a => a.action == "ADM_CATEGORY_DELETE" && a.entity_id == 2));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        private DbContextOptions<StreetBizDbContext> options = null!;
        public TestContext Db { get; private set; } = null!;
        public PlatformAdministrationRepository Repository { get; private set; } = null!;

        public TestContext NewDb() => new(options);

        public static async Task<Fixture> Create()
        {
            var f = new Fixture();
            await f.connection.OpenAsync();
            f.options = new DbContextOptionsBuilder<StreetBizDbContext>().UseSqlite(f.connection).Options;
            f.Db = new TestContext(f.options);
            await f.Db.Database.EnsureCreatedAsync();
            f.Repository = new PlatformAdministrationRepository(f.Db, TimeProvider.System);

            var t = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
            f.Db.Roles.AddRange(
                new Role { role_code = "PLATFORM_ADMIN", role_name = "Admin" },
                new Role { role_code = "VENDOR", role_name = "Vendor" },
                new Role { role_code = "CUSTOMER", role_name = "Customer" });
            f.Db.AdministrativeUnits.Add(new AdministrativeUnit { unit_id = 1, unit_type = "WARD", unit_name = "Ward" });
            f.Db.UserAccounts.AddRange(
                User(1, "PLATFORM_ADMIN", "Admin"), User(2, "VENDOR", "Chủ quán"), User(3, "CUSTOMER", "Khách"));
            f.Db.Vendors.Add(new Vendor { vendor_id = 1, user_id = 2 });
            f.Db.PricingZones.Add(new PricingZone { zone_id = 1, ward_unit_id = 1, zone_name = "Zone", created_by = 1 });
            f.Db.BusinessRegistrations.Add(new BusinessRegistration { registration_id = 1, vendor_id = 1, ward_unit_id = 1, vendor_type = "ITINERANT", display_name = "V", declared_address = "12 Nguyễn Văn Linh", registration_status = "APPROVED" });
            f.Db.SidewalkSlots.Add(new SidewalkSlot { slot_id = 1, zone_id = 1, slot_code = "S1", source = "WARD_DEFINED", slot_status = "ACTIVE" });
            f.Db.RentalApplications.Add(new RentalApplication { application_id = 1, registration_id = 1, slot_id = 1, application_method = "MANUAL_SELECTED", application_status = "APPROVED" });
            f.Db.RentalContracts.Add(new RentalContract { contract_id = 1, application_id = 1, slot_id = 1, vendor_id = 1, start_date = new DateOnly(2026, 1, 1), end_date = new DateOnly(2026, 12, 31), contract_status = "ACTIVE" });
            f.Db.Storefronts.Add(new Storefront { storefront_id = 1, registration_id = 1, contract_id = 1, storefront_name = "Quán Một", availability_status = "OPEN" });
            f.Db.FoodCategories.AddRange(
                new FoodCategory { category_id = 1, category_name = "Bánh mì", created_by = 1, creator_role = "PLATFORM_ADMIN" },
                new FoodCategory { category_id = 2, category_name = "Trống", created_by = 1, creator_role = "PLATFORM_ADMIN" });
            f.Db.MenuItems.Add(new MenuItem { menu_item_id = 1, storefront_id = 1, category_id = 1, item_name = "Bánh mì thịt", unit_price = 25_000, availability_status = "AVAILABLE" });
            for (var i = 1; i <= 4; i++)
            {
                f.Db.Orders.Add(new Order { order_id = i, order_code = $"ORD-{i}", customer_user_id = 3, storefront_id = 1, order_status = "COMPLETED", subtotal_amount = 100_000, total_amount = 100_000, created_at = t });
            }
            f.Db.PaymentTransactions.AddRange(
                new PaymentTransaction { transaction_id = 1, order_id = 2, idempotency_key = "PAY-2", payment_purpose = "ORDER", provider = "MOMO", amount = 100_000, transaction_status = "SUCCESS", created_at = t },
                // Order 3 only ever failed to pay.
                new PaymentTransaction { transaction_id = 2, order_id = 3, idempotency_key = "PAY-3", payment_purpose = "ORDER", provider = "MOMO", amount = 100_000, transaction_status = "FAILED", created_at = t });
            f.Db.RefundTransactions.AddRange(
                new RefundTransaction { refund_id = 1, order_id = 2, payment_transaction_id = 1, complaint_id = 2, idempotency_key = "R-1", amount = 10_000, refund_reason = "COMPLAINT_RESOLVED", provider = "MOMO", refund_status = "PENDING", requested_at = t.AddMinutes(2) },
                new RefundTransaction { refund_id = 2, order_id = 2, payment_transaction_id = 1, complaint_id = 2, idempotency_key = "R-2", amount = 90_000, refund_reason = "COMPLAINT_RESOLVED", provider = "MOMO", refund_status = "FAILED", requested_at = t.AddMinutes(1) });
            f.Db.Complaints.AddRange(
                new Complaint { complaint_id = 1, order_id = 1, customer_user_id = 3, complaint_type = ComplaintTypes.Complaint, description = "Thiếu món", status = ComplaintStatuses.Open, created_at = t.AddHours(1) },
                new Complaint { complaint_id = 2, order_id = 2, customer_user_id = 3, complaint_type = ComplaintTypes.RefundRequest, description = "Món hỏng", requested_refund_amount = 50_000, status = ComplaintStatuses.Open, created_at = t.AddHours(2) },
                new Complaint { complaint_id = 3, order_id = 3, customer_user_id = 3, complaint_type = ComplaintTypes.RefundRequest, description = "Chưa nhận", requested_refund_amount = 50_000, status = ComplaintStatuses.Open, created_at = t.AddHours(3) },
                new Complaint { complaint_id = 4, order_id = 4, customer_user_id = 3, complaint_type = ComplaintTypes.Complaint, description = "Cũ", status = ComplaintStatuses.Resolved, resolved_by = 1, resolver_role = "PLATFORM_ADMIN", resolution_notes = "Xong", resolved_at = t.AddHours(5), created_at = t.AddHours(4) });
            await f.Db.SaveChangesAsync();
            f.Db.ChangeTracker.Clear();
            return f;
        }

        public void Dispose()
        {
            Db.Dispose();
            connection.Dispose();
        }

        private static UserAccount User(long id, string role, string name) => new()
        {
            user_id = id,
            phone_number = $"090000000{id}",
            password_hash = "test-only",
            full_name = name,
            role_code = role,
            account_status = "ACTIVE",
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
