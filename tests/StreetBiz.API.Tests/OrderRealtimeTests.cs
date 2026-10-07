using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StreetBiz.API.Hubs;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;

namespace StreetBiz.API.Tests;

public sealed class OrderRealtimeTests
{
    private const long VendorId = 4;
    private const long OrderId = 15;

    // ---- the seller's board subscription ----

    private static (OrderHub Hub, Mock<IGroupManager> Groups) Hub(
        string role, Func<long>? vendorId = null)
    {
        var user = new Mock<ICurrentUser>();
        user.SetupGet(x => x.RoleCode).Returns(role);
        user.SetupGet(x => x.UserId).Returns(5);
        var vendors = new Mock<IVendorContext>();
        vendors.Setup(x => x.RequireVendorIdAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => (vendorId ?? (() => VendorId))());
        var context = new Mock<HubCallerContext>();
        context.SetupGet(x => x.ConnectionId).Returns("connection-1");
        context.SetupGet(x => x.ConnectionAborted).Returns(CancellationToken.None);
        var groups = new Mock<IGroupManager>();
        var hub = new OrderHub(user.Object, vendors.Object, Mock.Of<ICommerceRepository>())
        {
            Context = context.Object,
            Groups = groups.Object,
        };
        return (hub, groups);
    }

    [Fact]
    public async Task A_seller_follows_their_own_board()
    {
        var (hub, groups) = Hub(RoleCodes.Vendor);

        await hub.SubscribeVendorOrders();

        groups.Verify(x => x.AddToGroupAsync("connection-1", "vendor:4", It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task A_customer_cannot_follow_a_board()
    {
        var (hub, groups) = Hub(RoleCodes.Customer);

        var action = () => hub.SubscribeVendorOrders();

        await action.Should().ThrowAsync<HubException>();
        groups.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task A_vendor_account_with_no_seller_profile_has_no_board()
    {
        var (hub, groups) = Hub(
            RoleCodes.Vendor, () => throw new DomainRuleException("Chưa có hồ sơ người bán."));

        var action = () => hub.SubscribeVendorOrders();

        await action.Should().ThrowAsync<HubException>();
        groups.VerifyNoOtherCalls();
    }

    // ---- publishing ----

    private sealed class Sent
    {
        public List<(string Group, string Method)> Messages { get; } = [];
    }

    private static (OrderRealtimePublisher Publisher, Sent Sent, Mock<ICommerceRepository> Orders) Publisher()
    {
        var sent = new Sent();
        var clients = new Mock<IHubClients>();
        clients.Setup(x => x.Group(It.IsAny<string>())).Returns((string group) =>
        {
            var proxy = new Mock<IClientProxy>();
            proxy.Setup(x => x.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
                .Callback((string method, object?[] _, CancellationToken _) => sent.Messages.Add((group, method)))
                .Returns(Task.CompletedTask);
            return proxy.Object;
        });
        var hub = new Mock<IHubContext<OrderHub>>();
        hub.SetupGet(x => x.Clients).Returns(clients.Object);

        var orders = new Mock<ICommerceRepository>();
        orders.Setup(x => x.GetOrderVendorIdAsync(OrderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(VendorId);
        var services = new ServiceCollection()
            .AddScoped(_ => orders.Object)
            .BuildServiceProvider();

        return (
            new OrderRealtimePublisher(
                hub.Object,
                services.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<OrderRealtimePublisher>.Instance),
            sent,
            orders);
    }

    [Fact]
    public async Task A_paid_order_reaches_its_own_screen_and_its_sellers_board()
    {
        var (publisher, sent, _) = Publisher();

        await publisher.PublishAsync(OrderId, OrderStatuses.Placed);

        sent.Messages.Should().BeEquivalentTo(new[]
        {
            ("order:15", OrderHub.OrderUpdatedEvent),
            ("vendor:4", OrderHub.VendorOrderChangedEvent),
        });
    }

    [Fact]
    public async Task An_unpaid_order_stays_off_the_board_without_a_lookup()
    {
        var (publisher, sent, orders) = Publisher();

        await publisher.PublishAsync(OrderId, OrderStatuses.PendingPayment);

        sent.Messages.Should().ContainSingle().Which.Group.Should().Be("order:15");
        orders.Verify(x => x.GetOrderVendorIdAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_failed_lookup_never_fails_the_request_that_published()
    {
        var (publisher, sent, orders) = Publisher();
        orders.Setup(x => x.GetOrderVendorIdAsync(OrderId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database down"));

        var action = () => publisher.PublishAsync(OrderId, OrderStatuses.Cancelled);

        await action.Should().NotThrowAsync();
        sent.Messages.Should().ContainSingle().Which.Group.Should().Be("order:15");
    }
}
