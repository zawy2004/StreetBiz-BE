using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Options;
using Moq;
using System.Security.Cryptography;
using System.Text;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Features.Commerce;
using StreetBiz.Application.Features.FoodSafety;
using StreetBiz.Infrastructure.Persistence;
using StreetBiz.Infrastructure.Persistence.Repositories;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;
using StreetBiz.Infrastructure.Payments;
using StreetBiz.Infrastructure.Services;

namespace StreetBiz.Infrastructure.Tests;

public sealed class CommerceManagementTests
{
    /// <summary>An unfiltered marketplace search: every optional narrowing left unset.</summary>
    private static readonly MarketplaceMenuFilter NoFilter =
        new(null, null, null, null, null, null, MarketplaceMenuSorts.Name);

    /// <summary>A dish photo uploaded by vendor 1's account (user 1); the fake storage says it exists.</summary>
    private const string Photo = "/api/uploads/menu-images/1/0123456789abcdef0123456789abcdef.jpg";

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
        var added = await f.Service.SaveMenu(1, null, new(1, "Noodle", null, 30000, "AVAILABLE", Photo, PriceUnit: "phần"), default);
        Assert.Equal(Photo, added.ImageUrl);
        Assert.Equal("Noodle", (await f.Repository.GetMenuItemAsync(added.MenuItemId, default))!.ItemName);
        await f.Service.SaveMenu(1, added.MenuItemId, new(1, "Noodle updated", null, 35000, "SOLD_OUT", PriceUnit: "phần"), default);
        Assert.Equal("SOLD_OUT", (await f.Repository.GetMenuItemAsync(added.MenuItemId, default))!.AvailabilityStatus);
        await f.Service.ArchiveMenu(1, added.MenuItemId, default);
        Assert.Null(await f.Repository.GetMenuItemAsync(added.MenuItemId, default));
        Assert.True(await f.Db.MenuItems.AnyAsync(x => x.menu_item_id == added.MenuItemId));
    }

    [Fact]
    public async Task New_dish_needs_its_own_uploaded_photo()
    {
        using var f = await Fixture.Create();
        await Assert.ThrowsAsync<DomainRuleException>(() =>
            f.Service.SaveMenu(1, null, new(1, "No photo", null, 30000, "AVAILABLE", PriceUnit: "phần"), default));
        // Another user's photo, or a URL outside the menu-image channel, is refused.
        await Assert.ThrowsAsync<DomainRuleException>(() => f.Service.SaveMenu(1, null,
            new(1, "Stolen", null, 30000, "AVAILABLE", "/api/uploads/menu-images/2/0123456789abcdef0123456789abcdef.jpg", PriceUnit: "phần"), default));
        await Assert.ThrowsAsync<DomainRuleException>(() => f.Service.SaveMenu(1, null,
            new(1, "Evidence", null, 30000, "AVAILABLE", "/api/uploads/evidence/1/0123456789abcdef0123456789abcdef.jpg", PriceUnit: "phần"), default));
        // Editing an existing dish may keep its current photo.
        var edited = await f.Service.SaveMenu(1, 1, new(1, "Meal", "Now with notes", 25000, "AVAILABLE", PriceUnit: "phần"), default);
        Assert.Equal("Now with notes", edited.Description);
    }

    [Fact]
    public async Task Stall_sells_at_most_five_dishes()
    {
        using var f = await Fixture.Create();
        for (var i = 2; i <= MenuRules.MaxActiveItemsPerStorefront; i++)
            await f.Service.SaveMenu(1, null, new(1, $"Dish {i}", null, 20000, "AVAILABLE", Photo, PriceUnit: "phần"), default);
        var menu = await f.Service.Menu(1, default);
        Assert.Equal(MenuRules.MaxActiveItemsPerStorefront, menu.Items.Count);
        Assert.Equal(MenuRules.MaxActiveItemsPerStorefront, menu.MaxItems);
        await Assert.ThrowsAsync<ConflictException>(() =>
            f.Service.SaveMenu(1, null, new(1, "One too many", null, 20000, "AVAILABLE", Photo, PriceUnit: "phần"), default));
        // Archiving frees a place.
        await f.Service.ArchiveMenu(1, 1, default);
        await f.Service.SaveMenu(1, null, new(1, "Replacement", null, 20000, "AVAILABLE", Photo, PriceUnit: "phần"), default);
    }

    [Fact]
    public async Task High_risk_dish_is_off_sale_until_an_approved_certificate_covers_it()
    {
        using var f = await Fixture.Create();
        (await f.Db.FoodCategories.FindAsync(1))!.requires_food_safety = true;
        await f.Db.SaveChangesAsync();
        f.Db.ChangeTracker.Clear();
        Assert.Equal("MISSING", (await f.Service.Menu(1, default)).Items.Single().FoodSafetyStatus);
        Assert.Empty(await f.Repository.SearchMenuItemsAsync(NoFilter, 50, default));
        Assert.Equal(CartMutationOutcome.MenuItemUnavailable, (await f.Repository.AddCartItemAsync(2, 1, 1, null, default)).Outcome);

        var application = new FoodSafetyApplication { storefront_id = 1, vendor_id = 1, application_status = "FORWARDED" };
        application.FoodSafetyApplicationItems.Add(new FoodSafetyApplicationItem { menu_item_id = 1 });
        f.Db.FoodSafetyApplications.Add(application);
        await f.Db.SaveChangesAsync();
        f.Db.ChangeTracker.Clear();
        Assert.Equal("PENDING", (await f.Service.Menu(1, default)).Items.Single().FoodSafetyStatus);
        Assert.Empty(await f.Repository.SearchMenuItemsAsync(NoFilter, 50, default));

        var row = (await f.Db.FoodSafetyApplications.FindAsync(application.application_id))!;
        row.application_status = "APPROVED";
        row.certificate_number = "ATTP-1";
        row.issued_on = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-10));
        row.expires_on = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30));
        await f.Db.SaveChangesAsync();
        f.Db.ChangeTracker.Clear();
        Assert.Equal("APPROVED", (await f.Service.Menu(1, default)).Items.Single().FoodSafetyStatus);
        Assert.True(Assert.Single(await f.Repository.SearchMenuItemsAsync(NoFilter, 50, default)).FoodSafetyCertified);
        // A certified dish cannot be renamed into another dish.
        await Assert.ThrowsAsync<ConflictException>(() =>
            f.Service.SaveMenu(1, 1, new(1, "Different dish", null, 25000, "AVAILABLE", PriceUnit: "phần"), default));

        (await f.Db.FoodSafetyApplications.FindAsync(application.application_id))!.expires_on =
            DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-2));
        await f.Db.SaveChangesAsync();
        f.Db.ChangeTracker.Clear();
        Assert.Equal("MISSING", (await f.Service.Menu(1, default)).Items.Single().FoodSafetyStatus);
        Assert.Empty(await f.Repository.SearchMenuItemsAsync(NoFilter, 50, default));
    }

    [Fact]
    public async Task Hidden_menu_cannot_be_restored_by_seller()
    {
        using var f = await Fixture.Create();
        (await f.Db.MenuItems.FindAsync(1L))!.availability_status = "HIDDEN";
        await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<ConflictException>(() => f.Service.SaveMenu(1, 1, new(1, "Hidden", null, 10000, "AVAILABLE", PriceUnit: "phần"), default));
    }

    [Theory]
    [InlineData("PAUSED")] // what hiding a storefront sets
    [InlineData("CLOSED")]
    [InlineData("OPEN")]
    public async Task Store_hidden_by_a_moderator_cannot_be_reopened_or_edited_by_seller(string statusNow)
    {
        using var f = await Fixture.Create();
        (await f.Db.Storefronts.FindAsync(1L))!.availability_status = statusNow;
        f.Db.ReportedContents.Add(new ReportedContent
        {
            content_type = "STOREFRONT", content_id = 1, reporter_user_id = 2, reason = "Test",
            status = "HIDDEN", created_at = DateTime.UtcNow,
        });
        await f.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<ForbiddenException>(() => f.Service.SaveStore(1, new(1, 1, "Store", null, "OPEN"), default));
        Assert.Equal(statusNow, (await f.Db.Storefronts.FindAsync(1L))!.availability_status);
    }

    [Fact]
    public async Task Store_with_only_a_dismissed_report_stays_in_the_sellers_hands()
    {
        using var f = await Fixture.Create();
        f.Db.ReportedContents.Add(new ReportedContent
        {
            content_type = "STOREFRONT", content_id = 1, reporter_user_id = 2, reason = "Test",
            status = "DISMISSED", created_at = DateTime.UtcNow,
        });
        await f.Db.SaveChangesAsync();

        await f.Service.SaveStore(1, new(1, 1, "Store", null, "PAUSED"), default);
        Assert.Equal("PAUSED", (await f.Db.Storefronts.FindAsync(1L))!.availability_status);
    }

    /// <summary>Two reports hid store 1 (the second was a duplicate closed with the first); admin 3 reviewed them.</summary>
    private static async Task<long> HideStoreByModerator(Fixture f)
    {
        f.Db.Roles.Add(new Role { role_code = "PLATFORM_ADMIN", role_name = "Admin" });
        f.Db.UserAccounts.Add(new UserAccount { user_id = 3, phone_number = "0900000003", password_hash = "test-only", full_name = "Admin", role_code = "PLATFORM_ADMIN", account_status = "ACTIVE" });
        (await f.Db.Storefronts.FindAsync(1L))!.availability_status = "PAUSED";
        var reports = new[]
        {
            new ReportedContent { content_type = "STOREFRONT", content_id = 1, reporter_user_id = 2, reason = "Bán hàng giả", status = "HIDDEN", reviewed_by = 3, reviewed_at = DateTime.UtcNow, created_at = DateTime.UtcNow },
            new ReportedContent { content_type = "STOREFRONT", content_id = 1, reporter_user_id = 2, reason = "Trùng", status = "HIDDEN", reviewed_by = 3, reviewed_at = DateTime.UtcNow, created_at = DateTime.UtcNow },
        };
        f.Db.ReportedContents.AddRange(reports);
        await f.Db.SaveChangesAsync();
        f.Db.ChangeTracker.Clear();
        return reports[0].content_report_id;
    }

    [Fact]
    public async Task Restoring_a_hidden_store_lets_its_seller_reopen_it_and_tells_the_seller()
    {
        using var f = await Fixture.Create();
        var reportId = await HideStoreByModerator(f);
        await Assert.ThrowsAsync<ForbiddenException>(() => f.Service.SaveStore(1, new(1, 1, "Store", null, "OPEN"), default));

        var admin = new PlatformAdministrationRepository(f.Db, TimeProvider.System);
        var result = await admin.RestoreReportedContentAsync(reportId, "HIDDEN", 3, default);

        Assert.Equal(ContentDecisionOutcome.Updated, result.Outcome);
        Assert.Equal("DISMISSED", result.Report!.Status);
        // The duplicate that was hidden with it is closed too, or it would keep the store locked.
        Assert.All(await f.Db.ReportedContents.AsNoTracking().ToListAsync(), r => Assert.Equal("DISMISSED", r.status));
        // Restoring does not put the store on sale: it stays paused until the seller decides.
        Assert.Equal("PAUSED", (await f.Db.Storefronts.AsNoTracking().SingleAsync()).availability_status);
        Assert.Equal(1, await f.Db.Notifications.CountAsync(n => n.user_id == 1 && n.related_entity_id == reportId));
        Assert.Equal(1, await f.Db.AuditLogs.CountAsync(a => a.action == "ADM_CONTENT_RESTORE" && a.actor_user_id == 3));

        f.Db.ChangeTracker.Clear();
        await f.Service.SaveStore(1, new(1, 1, "Store", null, "OPEN"), default);
        Assert.Equal("OPEN", (await f.Db.Storefronts.AsNoTracking().SingleAsync()).availability_status);
    }

    [Theory]
    [InlineData("PENDING")]
    [InlineData("DISMISSED")]
    public async Task Only_a_hidden_report_can_be_restored(string status)
    {
        using var f = await Fixture.Create();
        await HideStoreByModerator(f);
        var other = new ReportedContent { content_type = "STOREFRONT", content_id = 1, reporter_user_id = 2, reason = "x", status = status, created_at = DateTime.UtcNow };
        f.Db.ReportedContents.Add(other);
        await f.Db.SaveChangesAsync();
        f.Db.ChangeTracker.Clear();

        var admin = new PlatformAdministrationRepository(f.Db, TimeProvider.System);
        var result = await admin.RestoreReportedContentAsync(other.content_report_id, status, 3, default);

        Assert.Equal(ContentDecisionOutcome.Conflict, result.Outcome);
        Assert.Equal(2, await f.Db.ReportedContents.CountAsync(r => r.status == "HIDDEN"));
        Assert.Equal(ContentDecisionOutcome.NotFound, (await admin.RestoreReportedContentAsync(9999, "HIDDEN", 3, default)).Outcome);
    }

    [Fact]
    public async Task Restoring_a_hidden_dish_returns_it_to_the_seller_as_sold_out()
    {
        using var f = await Fixture.Create();
        f.Db.Roles.Add(new Role { role_code = "PLATFORM_ADMIN", role_name = "Admin" });
        f.Db.UserAccounts.Add(new UserAccount { user_id = 3, phone_number = "0900000003", password_hash = "test-only", full_name = "Admin", role_code = "PLATFORM_ADMIN", account_status = "ACTIVE" });
        (await f.Db.MenuItems.FindAsync(1L))!.availability_status = "HIDDEN";
        var report = new ReportedContent { content_type = "MENU_ITEM", content_id = 1, reporter_user_id = 2, reason = "Ảnh phản cảm", status = "HIDDEN", reviewed_by = 3, reviewed_at = DateTime.UtcNow, created_at = DateTime.UtcNow };
        f.Db.ReportedContents.Add(report);
        await f.Db.SaveChangesAsync();
        f.Db.ChangeTracker.Clear();
        await Assert.ThrowsAsync<ConflictException>(() => f.Service.SaveMenu(1, 1, new(1, "Meal", null, 25000, "AVAILABLE", PriceUnit: "phần"), default));

        var admin = new PlatformAdministrationRepository(f.Db, TimeProvider.System);
        Assert.Equal(ContentDecisionOutcome.Updated, (await admin.RestoreReportedContentAsync(report.content_report_id, "HIDDEN", 3, default)).Outcome);
        Assert.Equal("SOLD_OUT", (await f.Db.MenuItems.AsNoTracking().SingleAsync()).availability_status);

        f.Db.ChangeTracker.Clear();
        await f.Service.SaveMenu(1, 1, new(1, "Meal", null, 25000, "AVAILABLE", PriceUnit: "phần"), default);
        Assert.Equal("AVAILABLE", (await f.Db.MenuItems.AsNoTracking().SingleAsync()).availability_status);
    }

    [Fact]
    public async Task Cancelled_contract_cannot_open_a_store()
    {
        using var f = await Fixture.Create();
        (await f.Db.RentalContracts.FindAsync(1L))!.contract_status = "CANCELLED";
        await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<DomainRuleException>(() => f.Service.SaveStore(1, new(1, 1, "Store", null, "OPEN"), default));
        Assert.Empty(await f.Repository.SearchMenuItemsAsync(NoFilter, 50, default));
        Assert.Empty(await f.Repository.SearchMenuItemsAsync(
            new MarketplaceMenuFilter(null, null, null, null, null, null, MarketplaceMenuSorts.Name), 50, default));
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
    public async Task Sandbox_failure_cancels_order_and_cannot_be_resurrected()
    {
        using var f = await Fixture.Create();
        (await f.Db.Orders.FindAsync(1L))!.order_status = "PENDING_PAYMENT";
        var payment = (await f.Db.PaymentTransactions.FindAsync(1L))!;
        payment.transaction_status = "PENDING";
        payment.provider_reference = null;
        await f.Db.SaveChangesAsync();
        await f.Payments.Fail(1, default);
        Assert.Equal("FAILED", (await f.Db.PaymentTransactions.FindAsync(1L))!.transaction_status);
        Assert.Equal("CANCELLED", (await f.Db.Orders.FindAsync(1L))!.order_status);
        var paid = await f.Repository.ConfirmSandboxPaymentAsync(2, 1, default);
        Assert.Equal(OrderMutationOutcome.Conflict, paid.Outcome);
        Assert.Equal("CANCELLED", (await f.Db.Orders.FindAsync(1L))!.order_status);
        Assert.Single(await f.Db.OrderStatusHistories.ToListAsync());
        Assert.Single(await f.Db.PaymentTransactions.ToListAsync());
        Assert.Single(await f.Db.Notifications.ToListAsync());
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

    [Fact]
    public async Task Checkout_uses_database_price_and_keeps_cart_active()
    {
        using var f = await Fixture.Create();
        var cartId = await AddCart(f, quantity: 2);
        var cart = await f.Repository.GetActiveCartAsync(2, default);
        var result = await f.Repository.CheckoutAsync(2, cartId, "MOMO", "checkout-price", default);
        Assert.Equal("12 Nguyễn Văn Linh, Hải Châu, Đà Nẵng", cart!.StorefrontAddress);
        Assert.Equal(OrderMutationOutcome.Updated, result.Outcome);
        Assert.Equal(50000, result.Order!.TotalAmount);
        Assert.Equal("PENDING_PAYMENT", result.Order.OrderStatus);
        Assert.Equal("12 Nguyễn Văn Linh, Hải Châu, Đà Nẵng", result.Order.StorefrontAddress);
        Assert.Equal(
            "12 Nguyễn Văn Linh, Hải Châu, Đà Nẵng",
            (await f.Db.Orders.FindAsync(result.Order.OrderId))!.storefront_address_snapshot);
        Assert.Equal("ACTIVE", (await f.Db.ShoppingCarts.FindAsync(cartId))!.cart_status);

        var registration = await f.Db.BusinessRegistrations.FindAsync(1L);
        registration!.declared_address = "Địa chỉ đã thay đổi";
        await f.Db.SaveChangesAsync();
        f.Db.ChangeTracker.Clear();
        var reread = await f.Repository.GetCustomerOrderAsync(2, result.Order.OrderId, default);
        Assert.Equal("12 Nguyễn Văn Linh, Hải Châu, Đà Nẵng", reread!.StorefrontAddress);
    }

    [Fact]
    public async Task Pending_checkout_freezes_the_cart_and_says_so_until_the_order_is_cancelled()
    {
        using var f = await Fixture.Create();
        var cartId = await AddCart(f);
        var order = (await f.Repository.CheckoutAsync(2, cartId, "MOMO", "freeze", default)).Order!;
        f.Db.ChangeTracker.Clear();

        // The cart tells the client which order holds it, so the UI can offer pay/cancel.
        Assert.Equal(order.OrderId, (await f.Repository.GetActiveCartAsync(2, default))!.PendingOrderId);
        Assert.Equal(CartMutationOutcome.CheckoutPending, (await f.Repository.AddCartItemAsync(2, 1, 1, null, default)).Outcome);
        Assert.Equal(CartMutationOutcome.CheckoutPending, (await f.Repository.UpdateCartItemAsync(2, 1, 3, null, default)).Outcome);
        Assert.Equal(CartMutationOutcome.CheckoutPending, (await f.Repository.RemoveCartItemAsync(2, 1, default)).Outcome);

        await f.Repository.CancelCustomerOrderAsync(2, order.OrderId, "PENDING_PAYMENT", default);
        f.Db.ChangeTracker.Clear();

        var cart = await f.Repository.GetActiveCartAsync(2, default);
        Assert.Null(cart!.PendingOrderId);
        Assert.Equal(CartMutationOutcome.Updated, (await f.Repository.UpdateCartItemAsync(2, 1, 3, null, default)).Outcome);
    }

    [Fact]
    public async Task Adding_past_99_of_one_item_reports_the_limit_not_a_conflict()
    {
        using var f = await Fixture.Create();
        await AddCart(f, quantity: 99);

        Assert.Equal(CartMutationOutcome.QuantityLimit, (await f.Repository.AddCartItemAsync(2, 1, 1, null, default)).Outcome);
    }

    [Fact]
    public async Task Checkout_is_idempotent_for_same_customer_and_key()
    {
        using var f = await Fixture.Create();
        var cartId = await AddCart(f);
        var first = await f.Repository.CheckoutAsync(2, cartId, "MOMO", "same-key", default);
        var second = await f.Repository.CheckoutAsync(2, cartId, "MOMO", "same-key", default);
        Assert.Equal(first.Order!.OrderId, second.Order!.OrderId);
        Assert.Equal(2, await f.Db.Orders.CountAsync());
        Assert.Equal(2, await f.Db.PaymentTransactions.CountAsync());
    }

    [Fact]
    public async Task Checkout_rejects_empty_cart()
    {
        using var f = await Fixture.Create();
        var cart = new ShoppingCart { customer_user_id = 2, storefront_id = 1, cart_status = "ACTIVE" };
        f.Db.ShoppingCarts.Add(cart);
        await f.Db.SaveChangesAsync();
        var result = await f.Repository.CheckoutAsync(2, cart.cart_id, "MOMO", "empty-cart", default);
        Assert.Equal(OrderMutationOutcome.EmptyCart, result.Outcome);
    }

    [Fact]
    public async Task Checkout_rejects_closed_storefront()
    {
        using var f = await Fixture.Create();
        var cartId = await AddCart(f);
        (await f.Db.Storefronts.FindAsync(1L))!.availability_status = "CLOSED";
        await f.Db.SaveChangesAsync();
        var result = await f.Repository.CheckoutAsync(2, cartId, "MOMO", "closed-store", default);
        Assert.Equal(OrderMutationOutcome.StorefrontUnavailable, result.Outcome);
    }

    [Fact]
    public async Task Checkout_rejects_unavailable_menu_item()
    {
        using var f = await Fixture.Create();
        var cartId = await AddCart(f);
        (await f.Db.MenuItems.FindAsync(1L))!.availability_status = "SOLD_OUT";
        await f.Db.SaveChangesAsync();
        var result = await f.Repository.CheckoutAsync(2, cartId, "MOMO", "sold-out", default);
        Assert.Equal(OrderMutationOutcome.MenuItemUnavailable, result.Outcome);
    }

    [Fact]
    public async Task Valid_success_callback_places_order_checks_out_cart_and_notifies_both_parties()
    {
        using var f = await Fixture.Create();
        var cartId = await AddCart(f);
        var checkout = await f.Repository.CheckoutAsync(2, cartId, "MOMO", "callback-ok", default);
        var callback = await ApplyCallback(f, "callback-ok", 25000, "SUCCESS", true);
        Assert.Equal(PaymentCallbackOutcome.Applied, callback.Outcome);
        Assert.Equal("PLACED", (await f.Db.Orders.FindAsync(checkout.Order!.OrderId))!.order_status);
        Assert.Equal("CHECKED_OUT", (await f.Db.ShoppingCarts.FindAsync(cartId))!.cart_status);
        Assert.Equal(2, await f.Db.Notifications.CountAsync());
        Assert.Null((await f.Db.OrderStatusHistories.OrderByDescending(x => x.history_id).FirstAsync()).changed_by);
    }

    [Fact]
    public async Task Duplicate_success_callback_does_not_duplicate_history_or_notifications()
    {
        using var f = await Fixture.Create();
        var cartId = await AddCart(f);
        var checkout = await f.Repository.CheckoutAsync(2, cartId, "MOMO", "callback-duplicate", default);
        await ApplyCallback(f, "callback-duplicate", 25000, "SUCCESS", true);
        var historyCount = await f.Db.OrderStatusHistories.CountAsync(x => x.order_id == checkout.Order!.OrderId);
        var notificationCount = await f.Db.Notifications.CountAsync();
        var duplicate = await ApplyCallback(f, "callback-duplicate", 25000, "SUCCESS", true);
        Assert.Equal(PaymentCallbackOutcome.Duplicate, duplicate.Outcome);
        Assert.Equal(historyCount, await f.Db.OrderStatusHistories.CountAsync(x => x.order_id == checkout.Order!.OrderId));
        Assert.Equal(notificationCount, await f.Db.Notifications.CountAsync());
        Assert.Equal(2, await f.Db.PaymentCallbackEvents.CountAsync());
    }

    [Fact]
    public async Task Invalid_signature_callback_is_recorded_and_rejected()
    {
        using var f = await Fixture.Create();
        var cartId = await AddCart(f);
        var checkout = await f.Repository.CheckoutAsync(2, cartId, "MOMO", "bad-signature", default);
        var callback = await ApplyCallback(f, "bad-signature", 25000, "SUCCESS", false);
        Assert.Equal(PaymentCallbackOutcome.Rejected, callback.Outcome);
        Assert.Equal("PENDING_PAYMENT", (await f.Db.Orders.FindAsync(checkout.Order!.OrderId))!.order_status);
        Assert.False((await f.Db.PaymentCallbackEvents.SingleAsync()).signature_valid);
    }

    [Fact]
    public async Task Wrong_amount_callback_is_recorded_and_rejected()
    {
        using var f = await Fixture.Create();
        var cartId = await AddCart(f);
        var checkout = await f.Repository.CheckoutAsync(2, cartId, "MOMO", "bad-amount", default);
        var callback = await ApplyCallback(f, "bad-amount", 1, "SUCCESS", true);
        Assert.Equal(PaymentCallbackOutcome.Rejected, callback.Outcome);
        Assert.Equal("PENDING_PAYMENT", (await f.Db.Orders.FindAsync(checkout.Order!.OrderId))!.order_status);
        Assert.Equal("REJECTED", (await f.Db.PaymentCallbackEvents.SingleAsync()).processing_result);
    }

    [Fact]
    public async Task Failed_callback_cancels_order_keeps_cart_active_and_does_not_notify_vendor()
    {
        using var f = await Fixture.Create();
        var cartId = await AddCart(f);
        var checkout = await f.Repository.CheckoutAsync(2, cartId, "MOMO", "callback-failed", default);
        var callback = await ApplyCallback(f, "callback-failed", 25000, "FAILED", true);
        Assert.Equal(PaymentCallbackOutcome.Applied, callback.Outcome);
        Assert.Equal("CANCELLED", (await f.Db.Orders.FindAsync(checkout.Order!.OrderId))!.order_status);
        Assert.Equal("ACTIVE", (await f.Db.ShoppingCarts.FindAsync(cartId))!.cart_status);
        var notification = await f.Db.Notifications.SingleAsync();
        Assert.Equal(2, notification.user_id);
    }

    [Fact]
    public async Task Customer_cannot_read_another_customers_order()
    {
        using var f = await Fixture.Create();
        Assert.Null(await f.Repository.GetCustomerOrderAsync(99, 1, default));
    }

    [Fact]
    public async Task Seller_cannot_transition_another_vendors_order()
    {
        using var f = await Fixture.Create();
        var result = await f.Repository.DecideSellerOrderAsync(
            99, 1, 1, "ACCEPT", null, "PLACED", default);
        Assert.Equal(OrderMutationOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task Invalid_seller_transition_is_rejected()
    {
        using var f = await Fixture.Create();
        f.Db.OrderStatusHistories.Add(new OrderStatusHistory
        {
            order_id = 1,
            from_status = "PENDING_PAYMENT",
            to_status = "PLACED",
            changed_at = DateTime.UtcNow,
        });
        await f.Db.SaveChangesAsync();
        var result = await f.Repository.UpdateSellerOrderStatusAsync(
            1, 1, 1, "READY_FOR_PICKUP", "PLACED", default);
        Assert.Equal(OrderMutationOutcome.Conflict, result.Outcome);
    }

    [Fact]
    public async Task Paid_customer_cancel_creates_one_pending_refund()
    {
        using var f = await Fixture.Create();
        var cartId = await AddCart(f);
        var checkout = await f.Repository.CheckoutAsync(2, cartId, "MOMO", "cancel-paid", default);
        await ApplyCallback(f, "cancel-paid", 25000, "SUCCESS", true);
        var cancelled = await f.Repository.CancelCustomerOrderAsync(
            2, checkout.Order!.OrderId, "PLACED", default);
        var repeated = await f.Repository.CancelCustomerOrderAsync(
            2, checkout.Order.OrderId, "PLACED", default);
        Assert.Equal(OrderMutationOutcome.Updated, cancelled.Outcome);
        Assert.Equal(OrderMutationOutcome.Conflict, repeated.Outcome);
        Assert.Single(await f.Db.RefundTransactions.Where(x => x.order_id == checkout.Order.OrderId).ToListAsync());
    }

    [Fact]
    public async Task Order_item_keeps_price_snapshot_after_menu_price_changes()
    {
        using var f = await Fixture.Create();
        var cartId = await AddCart(f);
        var checkout = await f.Repository.CheckoutAsync(2, cartId, "MOMO", "snapshot-price", default);
        (await f.Db.MenuItems.FindAsync(1L))!.unit_price = 99000;
        await f.Db.SaveChangesAsync();
        var tracked = await f.Repository.GetCustomerOrderAsync(2, checkout.Order!.OrderId, default);
        Assert.Equal(25000, tracked!.Items.Single().UnitPrice);
        Assert.Equal(25000, tracked.TotalAmount);
    }

    [Fact]
    public async Task Sales_summary_counts_only_completed_orders_in_period()
    {
        using var f = await Fixture.Create();
        var completed = (await f.Db.Orders.FindAsync(1L))!;
        completed.completed_at = DateTime.UtcNow;
        f.Db.Orders.Add(new Order { order_code = "NOT-COMPLETED", customer_user_id = 2,
            storefront_id = 1, order_status = "PLACED", subtotal_amount = 90000,
            total_amount = 90000, created_at = DateTime.UtcNow });
        await f.Db.SaveChangesAsync();
        var summary = await f.Repository.GetSalesSummaryAsync(1, "DAY", default);
        Assert.Equal(1, summary.CompletedOrderCount);
        Assert.Equal(25000, summary.GrossSales);
    }

    [Fact]
    public async Task Gateway_accepts_only_the_exact_hmac_signature()
    {
        const string raw = "{\"idempotencyKey\":\"key\",\"amount\":25000,\"status\":\"SUCCESS\"}";
        const string secret = "test-callback-secret";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var signature = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(raw)));
        var gateway = new ConfiguredPaymentGateway(Options.Create(new PaymentGatewaySettings
        {
            Momo = new PaymentProviderSettings { CallbackSecret = secret }
        }));
        var valid = await gateway.VerifyCallbackAsync("MOMO", raw, signature, default);
        var invalid = await gateway.VerifyCallbackAsync("MOMO", raw, "00", default);
        Assert.True(valid.SignatureValid);
        Assert.False(invalid.SignatureValid);
    }

    // ---- STORE-03: business hours ------------------------------------------------------------

    private static StorefrontHoursInput Week(params (short Day, string Open, string Close)[] rows) =>
        new(rows.Select(r => new BusinessHourItem(r.Day, r.Open, r.Close)).ToList());

    private static async Task ZoneHours(Fixture f, string? from, string? to)
    {
        var zone = (await f.Db.PricingZones.FindAsync(1))!;
        zone.available_from = from is null ? null : TimeOnly.Parse(from);
        zone.available_to = to is null ? null : TimeOnly.Parse(to);
        await f.Db.SaveChangesAsync();
        f.Db.ChangeTracker.Clear();
    }

    [Fact]
    public async Task Business_hours_are_written_and_read_back_ordered_with_the_zone_window()
    {
        using var f = await Fixture.Create();
        await ZoneHours(f, "06:00", "22:00");

        var saved = await f.Service.SaveBusinessHours(1, Week((2, "16:00", "21:00"), (1, "15:00", "18:00"), (1, "06:30", "10:00")), default);

        Assert.Equal(
            new[] { "1 06:30-10:00", "1 15:00-18:00", "2 16:00-21:00" },
            saved.Hours.Select(h => $"{h.DayOfWeek} {h.OpensAt}-{h.ClosesAt}").ToArray());
        Assert.Equal(new RentalWindowDto("06:00", "22:00", false), saved.AllowedWindow);
        var again = await f.Service.BusinessHours(1, default);
        Assert.Equal(saved.Hours, again.Hours);
        var audit = Assert.Single(f.Db.AuditLogs.Where(a => a.action == "STORE_HOURS_UPDATE"));
        Assert.Equal(1, audit.entity_id);
    }

    [Fact]
    public async Task Saving_replaces_the_whole_week_and_an_empty_week_clears_it()
    {
        using var f = await Fixture.Create();
        await f.Service.SaveBusinessHours(1, Week((1, "08:00", "12:00"), (2, "08:00", "12:00")), default);

        var replaced = await f.Service.SaveBusinessHours(1, Week((3, "09:00", "10:00")), default);
        Assert.Equal([(short)3], replaced.Hours.Select(h => h.DayOfWeek).ToArray());

        var cleared = await f.Service.SaveBusinessHours(1, new StorefrontHoursInput([]), default);
        Assert.Empty(cleared.Hours);
        Assert.Empty(f.Db.StorefrontBusinessHours);
    }

    [Fact]
    public async Task A_window_outside_the_rental_hours_is_refused_and_nothing_is_changed()
    {
        using var f = await Fixture.Create();
        await ZoneHours(f, "17:00", "22:00");
        await f.Service.SaveBusinessHours(1, Week((1, "18:00", "20:00")), default);

        var error = await Assert.ThrowsAsync<ValidationAppException>(() =>
            f.Service.SaveBusinessHours(1, Week((1, "18:00", "20:00"), (2, "16:00", "21:00")), default));

        Assert.Contains("17:00–22:00", error.Errors["hours[1]"][0]);
        Assert.Contains("Thứ Ba", error.Errors["hours[1]"][0]);
        f.Db.ChangeTracker.Clear();
        Assert.Single(f.Db.StorefrontBusinessHours);
    }

    [Fact]
    public async Task A_zone_with_no_hours_places_no_limit()
    {
        using var f = await Fixture.Create();

        var saved = await f.Service.SaveBusinessHours(1, Week((1, "00:00", "23:59")), default);

        Assert.Null(saved.AllowedWindow);
        Assert.Single(saved.Hours);
    }

    [Fact]
    public async Task Overnight_zone_hours_accept_an_evening_and_a_morning_window_but_not_the_afternoon()
    {
        using var f = await Fixture.Create();
        await ZoneHours(f, "18:00", "02:00");

        var saved = await f.Service.SaveBusinessHours(1, Week((5, "18:30", "23:59"), (6, "00:00", "02:00")), default);
        Assert.True(saved.AllowedWindow!.Overnight);

        await Assert.ThrowsAsync<ValidationAppException>(() =>
            f.Service.SaveBusinessHours(1, Week((5, "10:00", "14:00")), default));
    }

    [Fact]
    public async Task Another_sellers_storefront_cannot_be_edited_or_read()
    {
        using var f = await Fixture.Create();
        f.Vendor.Setup(x => x.RequireVendorIdAsync(default)).ReturnsAsync(99);

        await Assert.ThrowsAsync<NotFoundException>(() => f.Service.SaveBusinessHours(1, Week((1, "08:00", "12:00")), default));
        await Assert.ThrowsAsync<NotFoundException>(() => f.Service.BusinessHours(1, default));
        Assert.Empty(f.Db.StorefrontBusinessHours);
    }

    [Fact]
    public async Task Open_now_follows_the_saved_hours_in_vietnam_time_with_opening_inclusive_and_closing_exclusive()
    {
        // Monday 2026-10-05 08:00 in Da Nang is 01:00 UTC.
        foreach (var (utc, expected) in new[]
        {
            (new DateTime(2026, 10, 5, 0, 59, 0, DateTimeKind.Utc), false), // 07:59 local
            (new DateTime(2026, 10, 5, 1, 0, 0, DateTimeKind.Utc), true),   // 08:00 local: opens
            (new DateTime(2026, 10, 5, 4, 59, 0, DateTimeKind.Utc), true),  // 11:59 local
            (new DateTime(2026, 10, 5, 5, 0, 0, DateTimeKind.Utc), false),  // 12:00 local: closed
        })
        {
            using var f = await Fixture.Create(new FakeClock(utc));
            var dto = await f.Service.SaveBusinessHours(1, Week((1, "08:00", "12:00")), default);
            Assert.Equal(expected, dto.IsOpenNow);
        }
    }

    [Fact]
    public async Task A_closed_storefront_is_never_open_now_whatever_its_hours()
    {
        using var f = await Fixture.Create(new FakeClock(new DateTime(2026, 10, 5, 2, 0, 0, DateTimeKind.Utc)));
        var store = (await f.Db.Storefronts.FindAsync(1L))!;
        store.availability_status = "CLOSED";
        await f.Db.SaveChangesAsync();
        f.Db.ChangeTracker.Clear();

        var dto = await f.Service.SaveBusinessHours(1, Week((1, "08:00", "12:00")), default);

        Assert.False(dto.IsOpenNow);
    }

    [Fact]
    public async Task Hours_saved_by_the_seller_are_what_buyers_discover()
    {
        using var f = await Fixture.Create();
        // EnsureCreated does not build views; discovery only reads these columns of the ratings view.
        await f.Db.Database.ExecuteSqlRawAsync(
            "CREATE VIEW vw_VendorRatings AS SELECT vendor_id, 0 AS verified_count, NULL AS verified_rating, " +
            "0 AS community_count, NULL AS community_rating FROM Vendors");
        await f.Service.SaveBusinessHours(1, Week((1, "06:00", "10:30"), (1, "16:00", "20:00")), default);

        var detail = await f.Repository.GetStorefrontAsync(1, default);

        Assert.NotNull(detail);
        Assert.Equal(
            new[] { "1 06:00-10:30", "1 16:00-20:00" },
            detail.Storefront.Hours.OrderBy(h => h.OpensAt).Select(h => $"{h.DayOfWeek} {h.OpensAt:HH:mm}-{h.ClosesAt:HH:mm}").ToArray());
        // Monday 07:00 local is open, Monday 12:00 is not, Tuesday (no windows) is closed once hours exist.
        Assert.True(StorefrontHours.IsOpen(detail.Storefront.Hours, new MarketplaceOpenAt(1, new TimeOnly(7, 0))));
        Assert.False(StorefrontHours.IsOpen(detail.Storefront.Hours, new MarketplaceOpenAt(1, new TimeOnly(12, 0))));
        Assert.False(StorefrontHours.IsOpen(detail.Storefront.Hours, new MarketplaceOpenAt(2, new TimeOnly(7, 0))));

        await f.Service.SaveBusinessHours(1, new StorefrontHoursInput([]), default);
        var cleared = await f.Repository.GetStorefrontAsync(1, default);
        Assert.Empty(cleared!.Storefront.Hours);
        Assert.True(StorefrontHours.IsOpen(cleared.Storefront.Hours, new MarketplaceOpenAt(3, new TimeOnly(3, 0))));
    }

    private sealed class FakeClock(DateTime utcNow) : TimeProvider
    {
        public DateTime Now { get; set; } = utcNow;
        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(Now, DateTimeKind.Utc));
    }

    // ---- REV-02: editing a review ------------------------------------------------------------

    private static readonly DateTime ReviewedAt = new(2026, 10, 5, 8, 0, 0, DateTimeKind.Utc);

    private static async Task<(Fixture Fixture, FakeClock Clock)> WithCompletedOrder()
    {
        var clock = new FakeClock(ReviewedAt);
        var f = await Fixture.Create(clock);
        var order = (await f.Db.Orders.FindAsync(1L))!;
        order.order_status = "COMPLETED";
        order.completed_at = ReviewedAt.AddHours(-1);
        await f.Db.SaveChangesAsync();
        f.Db.ChangeTracker.Clear();
        return (f, clock);
    }

    [Fact]
    public async Task A_first_review_reports_when_it_can_be_edited_until()
    {
        var (f, _) = await WithCompletedOrder();
        using var _f = f;

        var saved = await f.Service.SaveReview(1, new OrderReviewInput(5, "Ngon"), default);

        Assert.Equal(ReviewedAt.AddDays(7), saved.EditableUntil);
        Assert.Equal(ReviewedAt.AddDays(7), (await f.Service.Review(1, default))!.EditableUntil);
    }

    [Fact]
    public async Task A_review_can_be_edited_up_to_and_including_the_last_instant_of_the_window()
    {
        var (f, clock) = await WithCompletedOrder();
        using var _f = f;
        await f.Service.SaveReview(1, new OrderReviewInput(5, "Ngon"), default);

        clock.Now = ReviewedAt.AddDays(7);
        var edited = await f.Service.SaveReview(1, new OrderReviewInput(4, "Hơi nguội"), default);

        Assert.Equal((short)4, edited.Rating);
        Assert.Equal("Hơi nguội", edited.Text);
        f.Db.ChangeTracker.Clear();
        Assert.NotNull(f.Db.Reviews.Single().edited_at);
    }

    [Fact]
    public async Task An_edit_after_the_window_is_refused_and_the_review_is_left_as_it_was()
    {
        var (f, clock) = await WithCompletedOrder();
        using var _f = f;
        await f.Service.SaveReview(1, new OrderReviewInput(5, "Ngon"), default);

        clock.Now = ReviewedAt.AddDays(7).AddSeconds(1);
        var error = await Assert.ThrowsAsync<DomainRuleException>(() =>
            f.Service.SaveReview(1, new OrderReviewInput(1, "Tệ"), default));

        Assert.Equal(ReviewRules.EditWindowClosed, error.Message);
        f.Db.ChangeTracker.Clear();
        var review = f.Db.Reviews.Single();
        Assert.Equal((short)5, review.rating);
        Assert.Null(review.edited_at);
    }

    [Fact]
    public async Task Editing_does_not_extend_the_window_it_is_counted_from_the_first_submission()
    {
        var (f, clock) = await WithCompletedOrder();
        using var _f = f;
        await f.Service.SaveReview(1, new OrderReviewInput(5, "Ngon"), default);
        clock.Now = ReviewedAt.AddDays(6);
        await f.Service.SaveReview(1, new OrderReviewInput(4, "Sửa lần 1"), default);

        clock.Now = ReviewedAt.AddDays(7).AddHours(1);

        await Assert.ThrowsAsync<DomainRuleException>(() =>
            f.Service.SaveReview(1, new OrderReviewInput(3, "Sửa lần 2"), default));
    }

    [Fact]
    public async Task A_review_hidden_by_a_moderator_still_cannot_be_edited_back_into_view()
    {
        var (f, _) = await WithCompletedOrder();
        using var _f = f;
        var saved = await f.Service.SaveReview(1, new OrderReviewInput(1, "Vi phạm"), default);
        f.Db.ReportedContents.Add(new ReportedContent { content_type = "REVIEW", content_id = saved.ReviewId, reporter_user_id = 1, reason = "x", status = "HIDDEN", reviewed_by = 1, created_at = ReviewedAt });
        await f.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<ForbiddenException>(() =>
            f.Service.SaveReview(1, new OrderReviewInput(5, "Đã sửa"), default));
    }

    // ---- ZaloPay callback through the whole pipeline (gateway verification + the shared repository) ----

    private const string ZaloKey2 = "kLtgPl8HHhfvMuDHPwKfgfsY4Ydm9eIz";
    private const string ZaloAppId = "2553";
    private const string ZaloAppTransId = "261023_SB42abcd1234";

    private static string ZaloCallback(long amount, string key2 = ZaloKey2, string appTransId = ZaloAppTransId)
    {
        var data = System.Text.Json.JsonSerializer.Serialize(new
        {
            app_id = long.Parse(ZaloAppId),
            app_trans_id = appTransId,
            amount,
            embed_data = System.Text.Json.JsonSerializer.Serialize(new { idempotencyKey = "zalo-key-1" }),
            zp_trans_id = 210000000001,
        });
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key2));
        var mac = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(data))).ToLowerInvariant();
        return System.Text.Json.JsonSerializer.Serialize(new { data, mac, type = 1 });
    }

    private static ProcessPaymentCallbackCommandHandler ZaloHandler(Fixture f)
    {
        var settings = new PaymentGatewaySettings
        {
            ZaloPay = new PaymentProviderSettings
            {
                Enabled = true,
                ApiBaseUrl = "https://sb-openapi.zalopay.vn/v2",
                AppId = ZaloAppId,
                Key1 = "key1-for-tests-only",
                Key2 = ZaloKey2,
                ReturnUrl = "http://localhost/x/{referenceId}",
                CallbackUrl = "http://localhost/api/payments/zalopay/callback",
            },
        };
        var gateway = new ConfiguredPaymentGateway(Options.Create(settings), new NoHttpFactory());
        return new ProcessPaymentCallbackCommandHandler(gateway, f.Repository, Mock.Of<IFinanceRepository>());
    }

    private sealed class NoHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("A callback never calls out.");
    }

    private static async Task AddZaloOrder(Fixture f, decimal amount = 50_000)
    {
        f.Db.Orders.Add(new Order { order_id = 2, order_code = "ORDER-ZALO", customer_user_id = 2, storefront_id = 1, order_status = "PENDING_PAYMENT", total_amount = amount, subtotal_amount = amount });
        f.Db.PaymentTransactions.Add(new PaymentTransaction
        {
            transaction_id = 2,
            order_id = 2,
            idempotency_key = "zalo-key-1",
            payment_purpose = "ORDER",
            provider = "ZALOPAY",
            provider_reference = ZaloAppTransId,
            amount = amount,
            transaction_status = "PENDING",
        });
        await f.Db.SaveChangesAsync();
        f.Db.ChangeTracker.Clear();
    }

    [Fact]
    public async Task A_genuine_zalopay_callback_places_the_order_once_and_a_repeat_changes_nothing()
    {
        using var f = await Fixture.Create();
        await AddZaloOrder(f);
        var handler = ZaloHandler(f);
        var callback = ZaloCallback(50_000);

        var first = await handler.Handle(new ProcessPaymentCallbackCommand("zalopay", callback, null), default);
        var notificationsAfterFirst = f.Db.Notifications.Count();
        var repeat = await handler.Handle(new ProcessPaymentCallbackCommand("zalopay", callback, null), default);

        Assert.Equal("APPLIED", first.Result);
        Assert.Equal("PLACED", first.OrderStatus);
        Assert.Equal("DUPLICATE", repeat.Result);
        Assert.Equal(notificationsAfterFirst, f.Db.Notifications.Count());
        f.Db.ChangeTracker.Clear();
        Assert.Equal("SUCCESS", f.Db.PaymentTransactions.Single(p => p.transaction_id == 2).transaction_status);
        Assert.Equal("PLACED", f.Db.Orders.Single(o => o.order_id == 2).order_status);
    }

    [Fact]
    public async Task A_zalopay_callback_for_the_wrong_amount_is_rejected_and_the_order_stays_unpaid()
    {
        using var f = await Fixture.Create();
        await AddZaloOrder(f);

        var result = await ZaloHandler(f).Handle(
            new ProcessPaymentCallbackCommand("zalopay", ZaloCallback(1_000), null), default);

        Assert.Equal("REJECTED", result.Result);
        f.Db.ChangeTracker.Clear();
        Assert.Equal("PENDING_PAYMENT", f.Db.Orders.Single(o => o.order_id == 2).order_status);
        Assert.Equal("PENDING", f.Db.PaymentTransactions.Single(p => p.transaction_id == 2).transaction_status);
    }

    [Fact]
    public async Task A_zalopay_callback_with_a_bad_mac_is_rejected_and_logged_without_touching_the_order()
    {
        using var f = await Fixture.Create();
        await AddZaloOrder(f);
        var events = f.Db.PaymentCallbackEvents.Count();

        var result = await ZaloHandler(f).Handle(
            new ProcessPaymentCallbackCommand("zalopay", ZaloCallback(50_000, key2: "not-the-real-key2"), null), default);

        Assert.Equal("REJECTED", result.Result);
        f.Db.ChangeTracker.Clear();
        Assert.Equal("PENDING_PAYMENT", f.Db.Orders.Single(o => o.order_id == 2).order_status);
        Assert.Equal(events + 1, f.Db.PaymentCallbackEvents.Count());
    }

    [Fact]
    public async Task A_zalopay_callback_is_still_matched_by_its_embedded_idempotency_key_when_the_order_id_is_not_stored_yet()
    {
        using var f = await Fixture.Create();
        await AddZaloOrder(f);

        var result = await ZaloHandler(f).Handle(
            new ProcessPaymentCallbackCommand("zalopay", ZaloCallback(50_000, appTransId: "261023_SB999deadbeef"), null), default);

        // The callback can beat the write of provider_reference; the idempotency key ZaloPay echoes back still finds the payment.
        Assert.Equal("APPLIED", result.Result);
    }

    private static async Task<long> AddCart(Fixture f, int quantity = 1)
    {
        var cart = new ShoppingCart
        {
            customer_user_id = 2,
            storefront_id = 1,
            cart_status = "ACTIVE",
            created_at = DateTime.UtcNow,
        };
        cart.ShoppingCartItems.Add(new ShoppingCartItem
        {
            menu_item_id = 1,
            quantity = quantity,
        });
        f.Db.ShoppingCarts.Add(cart);
        await f.Db.SaveChangesAsync();
        f.Db.ChangeTracker.Clear();
        return cart.cart_id;
    }

    private static Task<PaymentCallbackMutationResult> ApplyCallback(
        Fixture f,
        string idempotencyKey,
        decimal amount,
        string status,
        bool signatureValid) =>
        f.Repository.ApplyPaymentCallbackAsync(new PaymentCallbackData(
            "MOMO", null, idempotencyKey, amount, status,
            $"{{\"idempotencyKey\":\"{idempotencyKey}\"}}", signatureValid), default);

    private sealed class Fixture : IDisposable
    {
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        public TestContext Db { get; private set; } = null!;
        public Mock<IVendorContext> Vendor { get; } = new();
        public Mock<ICustomerContext> Customer { get; } = new();
        public Mock<IFileStorage> Storage { get; } = new();
        public CommerceManagement Service { get; private set; } = null!;
        public OrderPaymentTesting Payments { get; private set; } = null!;
        public CommerceRepository Repository { get; private set; } = null!;
        public static async Task<Fixture> Create(TimeProvider? clock = null)
        {
            var f = new Fixture();
            await f.connection.OpenAsync();
            f.Db = new TestContext(new DbContextOptionsBuilder<StreetBizDbContext>().UseSqlite(f.connection).Options);
            await f.Db.Database.EnsureCreatedAsync();
            f.Vendor.Setup(x => x.RequireVendorIdAsync(default)).ReturnsAsync(1);
            f.Customer.Setup(x => x.RequireCustomerUserIdAsync(default)).ReturnsAsync(2);
            f.Storage.Setup(x => x.ExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
            f.Service = new(f.Db, f.Vendor.Object, f.Customer.Object, f.Storage.Object, clock ?? TimeProvider.System);
            f.Payments = new(f.Db, f.Customer.Object, TimeProvider.System);
            f.Repository = new(f.Db, TimeProvider.System);
            f.Db.Roles.AddRange(new Role { role_code = "VENDOR", role_name = "Vendor" }, new Role { role_code = "CUSTOMER", role_name = "Customer" });
            f.Db.AdministrativeUnits.Add(new AdministrativeUnit { unit_id = 1, unit_type = "WARD", unit_name = "Ward" });
            foreach (var id in new long[] { 1, 2 }) f.Db.UserAccounts.Add(new UserAccount { user_id = id, phone_number = $"090000000{id}", password_hash = "test-only", full_name = "Tester", role_code = id == 1 ? "VENDOR" : "CUSTOMER", account_status = "ACTIVE" });
            f.Db.Vendors.Add(new Vendor { vendor_id = 1, user_id = 1 });
            f.Db.PricingZones.Add(new PricingZone { zone_id = 1, ward_unit_id = 1, zone_name = "Zone", created_by = 1 });
            f.Db.BusinessRegistrations.Add(new BusinessRegistration { registration_id = 1, vendor_id = 1, ward_unit_id = 1, vendor_type = "ITINERANT", display_name = "Vendor", declared_address = "12 Nguyễn Văn Linh, Hải Châu, Đà Nẵng", registration_status = "APPROVED" });
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
