using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Moq;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Features.Commerce;
using StreetBiz.Infrastructure.Persistence;
using StreetBiz.Infrastructure.Persistence.Repositories;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;
using StreetBiz.Infrastructure.Services;

namespace StreetBiz.Infrastructure.Tests;

public sealed class CommerceManagementTests
{
    [Fact]
    public async Task Seller_cannot_manage_another_vendors_store()
    {
        using var f = await Fixture.Create();
        f.Vendor.Setup(x => x.RequireVendorIdAsync(default)).ReturnsAsync(99);
        Assert.Empty(await f.Service.Stores(default));
        await Assert.ThrowsAsync<NotFoundException>(() => f.Service.Menu(1, default));
        await Assert.ThrowsAsync<NotFoundException>(() => f.Service.ArchiveMenu(1, 1, default));
    }

    [Fact]
    public async Task Menu_changes_reach_marketplace_and_archiving_preserves_history()
    {
        using var f = await Fixture.Create();
        var added = await f.Service.SaveMenu(1, null, new(1, "Noodle", null, 30000, "AVAILABLE"), default);
        Assert.Equal("Noodle", (await f.Repository.GetMenuItemAsync(added.MenuItemId, default))!.ItemName);
        await f.Service.SaveMenu(1, added.MenuItemId, new(1, "Noodle updated", null, 35000, "SOLD_OUT"), default);
        Assert.Equal("SOLD_OUT", (await f.Repository.GetMenuItemAsync(added.MenuItemId, default))!.AvailabilityStatus);
        await f.Service.ArchiveMenu(1, added.MenuItemId, default);
        Assert.Null(await f.Repository.GetMenuItemAsync(added.MenuItemId, default));
        Assert.True(await f.Db.MenuItems.AnyAsync(x => x.menu_item_id == added.MenuItemId));
    }

    [Fact]
    public async Task Hidden_menu_cannot_be_restored_by_seller()
    {
        using var f = await Fixture.Create();
        (await f.Db.MenuItems.FindAsync(1L))!.availability_status = "HIDDEN";
        await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<ConflictException>(() => f.Service.SaveMenu(1, 1, new(1, "Hidden", null, 10000, "AVAILABLE"), default));
    }

    [Fact]
    public async Task Cancelled_contract_cannot_open_a_store()
    {
        using var f = await Fixture.Create();
        (await f.Db.RentalContracts.FindAsync(1L))!.contract_status = "CANCELLED";
        await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<DomainRuleException>(() => f.Service.SaveStore(1, new(1, 1, "Store", null, "OPEN"), default));
        Assert.Empty(await f.Repository.SearchMenuItemsAsync(null, 50, default));
        Assert.Equal(CartMutationOutcome.StorefrontUnavailable, (await f.Repository.AddCartItemAsync(2, 1, 1, null, default)).Outcome);
    }

    [Fact]
    public async Task Complaint_checks_ownership_refund_limit_and_duplicate_open_request()
    {
        using var f = await Fixture.Create();
        await Assert.ThrowsAsync<DomainRuleException>(() => f.Service.Complain(1, new("REFUND_REQUEST", "Too much", 25001), default));
        var created = await f.Service.Complain(1, new("REFUND_REQUEST", "Partial refund", 5000), default);
        Assert.Equal("OPEN", created.Status);
        await Assert.ThrowsAsync<ConflictException>(() => f.Service.Complain(1, new("COMPLAINT", "Duplicate", null), default));
        f.Customer.Setup(x => x.RequireCustomerUserIdAsync(default)).ReturnsAsync(99);
        await Assert.ThrowsAsync<NotFoundException>(() => f.Service.Complaints(1, default));
    }

    [Fact]
    public async Task Review_requires_completed_order_and_updates_existing_row()
    {
        using var f = await Fixture.Create();
        var created = await f.Service.SaveReview(1, new(5, "Good"), default);
        var edited = await f.Service.SaveReview(1, new(4, "Updated"), default);
        Assert.Equal(created.ReviewId, edited.ReviewId);
        Assert.Equal(4, edited.Rating);
        (await f.Db.Orders.FindAsync(1L))!.order_status = "PLACED";
        await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<DomainRuleException>(() => f.Service.SaveReview(1, new(5, "Too early"), default));
    }

    [Fact]
    public async Task Sandbox_failure_can_retry_and_repeated_success_does_not_resurrect_completed_order()
    {
        using var f = await Fixture.Create();
        (await f.Db.Orders.FindAsync(1L))!.order_status = "PENDING_PAYMENT";
        var payment = (await f.Db.PaymentTransactions.FindAsync(1L))!;
        payment.transaction_status = "PENDING";
        payment.provider_reference = null;
        await f.Db.SaveChangesAsync();
        await f.Payments.Fail(1, default);
        Assert.Equal("FAILED", (await f.Db.PaymentTransactions.FindAsync(1L))!.transaction_status);
        var paid = await f.Repository.ConfirmSandboxPaymentAsync(2, 1, default);
        Assert.Equal(OrderMutationOutcome.Updated, paid.Outcome);
        Assert.Equal("PLACED", paid.Order!.OrderStatus);
        var order = (await f.Db.Orders.FindAsync(1L))!;
        order.order_status = "COMPLETED";
        await f.Db.SaveChangesAsync();
        var repeated = await f.Repository.ConfirmSandboxPaymentAsync(2, 1, default);
        Assert.Equal("COMPLETED", repeated.Order!.OrderStatus);
        Assert.Single(await f.Db.OrderStatusHistories.ToListAsync());
        Assert.Single(await f.Db.PaymentTransactions.ToListAsync());
    }

    [Fact]
    public async Task Sandbox_refund_is_idempotent_and_rejects_real_payment_reference()
    {
        using var f = await Fixture.Create();
        f.Db.RefundTransactions.Add(new RefundTransaction { order_id = 1, payment_transaction_id = 1,
            idempotency_key = "test-refund", amount = 5000, refund_reason = "ORDER_CANCELLED", provider = "MOMO", refund_status = "PENDING" });
        await f.Db.SaveChangesAsync();
        (await f.Db.PaymentTransactions.FindAsync(1L))!.provider_reference = "REAL-PROVIDER-1";
        await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<ConflictException>(() => f.Payments.Refund(1, default));
        (await f.Db.PaymentTransactions.FindAsync(1L))!.provider_reference = "SANDBOX-MOMO-1";
        await f.Db.SaveChangesAsync();
        await f.Payments.Refund(1, default);
        var first = (await f.Db.RefundTransactions.SingleAsync()).completed_at;
        await f.Payments.Refund(1, default);
        var refund = await f.Db.RefundTransactions.SingleAsync();
        Assert.Equal("SUCCESS", refund.refund_status);
        Assert.Equal(first, refund.completed_at);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        public TestContext Db { get; private set; } = null!;
        public Mock<IVendorContext> Vendor { get; } = new();
        public Mock<ICustomerContext> Customer { get; } = new();
        public CommerceManagement Service { get; private set; } = null!;
        public OrderPaymentTesting Payments { get; private set; } = null!;
        public CommerceRepository Repository { get; private set; } = null!;
        public static async Task<Fixture> Create()
        {
            var f = new Fixture();
            await f.connection.OpenAsync();
            f.Db = new TestContext(new DbContextOptionsBuilder<StreetBizDbContext>().UseSqlite(f.connection).Options);
            await f.Db.Database.EnsureCreatedAsync();
            f.Vendor.Setup(x => x.RequireVendorIdAsync(default)).ReturnsAsync(1);
            f.Customer.Setup(x => x.RequireCustomerUserIdAsync(default)).ReturnsAsync(2);
            f.Service = new(f.Db, f.Vendor.Object, f.Customer.Object, TimeProvider.System);
            f.Payments = new(f.Db, f.Customer.Object, TimeProvider.System);
            f.Repository = new(f.Db, TimeProvider.System);
            f.Db.Roles.AddRange(new Role { role_code = "VENDOR", role_name = "Vendor" }, new Role { role_code = "CUSTOMER", role_name = "Customer" });
            f.Db.AdministrativeUnits.Add(new AdministrativeUnit { unit_id = 1, unit_type = "WARD", unit_name = "Ward" });
            foreach (var id in new long[] { 1, 2 }) f.Db.UserAccounts.Add(new UserAccount { user_id = id, phone_number = $"090000000{id}", password_hash = "test-only", full_name = "Tester", role_code = id == 1 ? "VENDOR" : "CUSTOMER", account_status = "ACTIVE" });
            f.Db.Vendors.Add(new Vendor { vendor_id = 1, user_id = 1 });
            f.Db.PricingZones.Add(new PricingZone { zone_id = 1, ward_unit_id = 1, zone_name = "Zone", created_by = 1 });
            f.Db.BusinessRegistrations.Add(new BusinessRegistration { registration_id = 1, vendor_id = 1, ward_unit_id = 1, vendor_type = "ITINERANT", display_name = "Vendor", registration_status = "APPROVED" });
            f.Db.SidewalkSlots.Add(new SidewalkSlot { slot_id = 1, zone_id = 1, slot_code = "SLOT", source = "WARD_DEFINED", slot_status = "ACTIVE" });
            f.Db.RentalApplications.Add(new RentalApplication { application_id = 1, registration_id = 1, slot_id = 1, application_method = "MANUAL_SELECTED", application_status = "APPROVED" });
            f.Db.RentalContracts.Add(new RentalContract { contract_id = 1, application_id = 1, slot_id = 1, vendor_id = 1, start_date = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)), end_date = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)), contract_status = "ACTIVE" });
            f.Db.Storefronts.Add(new Storefront { storefront_id = 1, registration_id = 1, contract_id = 1, storefront_name = "Store", availability_status = "OPEN" });
            f.Db.FoodCategories.Add(new FoodCategory { category_id = 1, category_name = "Food" });
            f.Db.MenuItems.Add(new MenuItem { menu_item_id = 1, storefront_id = 1, category_id = 1, item_name = "Meal", unit_price = 25000, availability_status = "AVAILABLE" });
            f.Db.Orders.Add(new Order { order_id = 1, order_code = "ORDER-TEST", customer_user_id = 2, storefront_id = 1, order_status = "COMPLETED", total_amount = 25000, subtotal_amount = 25000 });
            f.Db.PaymentTransactions.Add(new PaymentTransaction { transaction_id = 1, order_id = 1, idempotency_key = "PAY-TEST", payment_purpose = "ORDER", provider = "MOMO", amount = 25000, transaction_status = "SUCCESS", provider_reference = "SANDBOX-MOMO-1" });
            await f.Db.SaveChangesAsync();
            f.Db.ChangeTracker.Clear();
            return f;
        }
        public void Dispose() { Db.Dispose(); connection.Dispose(); }
    }

    private sealed class TestContext(DbContextOptions<StreetBizDbContext> options) : StreetBizDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            foreach (var entity in modelBuilder.Model.GetEntityTypes())
                foreach (var property in entity.GetProperties())
                {
                    if (property.GetComputedColumnSql() is not null) { property.SetComputedColumnSql(null); property.ValueGenerated = ValueGenerated.Never; }
                    property.SetDefaultValueSql(null);
                    if (property.ClrType == typeof(decimal) || property.ClrType == typeof(decimal?))
                        modelBuilder.Entity(entity.ClrType).Property(property.Name).HasConversion<double>();
                }
        }
    }
}
