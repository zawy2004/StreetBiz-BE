using FluentAssertions;
using StreetBiz.API.Controllers;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.DTOs.Commerce;

namespace StreetBiz.API.Tests;

public sealed class OrderApiPagingTests
{
    private static OrderDto Order(long id, string status) => new(
        id, $"SB-{id:000000}", 7, "Khách", 1, "Gian hàng", status, 25_000, 25_000,
        null, "MOMO", "SUCCESS", null, null, null, null, null, null, null,
        new DateTime(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc).AddMinutes(id),
        [], []);

    // 25 orders, of which only 3 are closed: a tab showing closed orders must
    // page over those 3, not over all 25.
    private static readonly OrderDto[] Orders =
        Enumerable.Range(1, 25)
            .Select(id => Order(id, id switch
            {
                3 => "REJECTED",
                9 or 17 => "CANCELLED",
                _ => "COMPLETED",
            }))
            .ToArray();

    [Fact]
    public void A_status_list_pages_over_only_the_listed_statuses()
    {
        var page = OrderApiPaging.Filter(Orders, "REJECTED,CANCELLED", 1, 10, null, null, "createdAt_desc");

        page.TotalItems.Should().Be(3);
        page.TotalPages.Should().Be(1);
        page.Items.Select(order => order.OrderId).Should().Equal(17, 9, 3);
    }

    [Fact]
    public void A_status_list_tolerates_spacing_and_case()
    {
        OrderApiPaging.Filter(Orders, " rejected , Cancelled ", 1, 10, null, null, "createdAt_desc")
            .TotalItems.Should().Be(3);
    }

    [Fact]
    public void A_single_status_still_works()
    {
        OrderApiPaging.Filter(Orders, "REJECTED", 1, 10, null, null, "createdAt_desc")
            .Items.Should().ContainSingle().Which.OrderId.Should().Be(3);
    }

    [Theory]
    [InlineData("REJECTED,NOT_A_STATUS")]
    [InlineData(",")]
    public void An_unknown_status_in_the_list_is_refused(string status)
    {
        var action = () => OrderApiPaging.Filter(Orders, status, 1, 10, null, null, "createdAt_desc");

        action.Should().Throw<ValidationAppException>();
    }
}
