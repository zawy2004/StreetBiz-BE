using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace StreetBiz.API.Tests;

/// <summary>
/// ORD-06: an order is marked collected by reading the buyer's code, or - when
/// there is no code to read - by a seller who writes down why, which the buyer
/// then sees in the order history. The seller-asserted endpoints that closed an
/// order on nothing at all are gone, and this keeps them gone.
/// </summary>
public sealed class HandoverRequiresPickupCodeTests
{
    [Theory]
    [InlineData("/api/vendor/orders/1/confirm-handover")]
    [InlineData("/api/seller/orders/1/handover")]
    public async Task No_endpoint_completes_an_order_on_nothing(string route)
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        var response = await client.PostAsync(route, null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_one_route_that_skips_the_code_is_behind_a_signed_in_seller()
    {
        // It exists (so it is not 404) and it refuses an anonymous caller, which
        // is what ties every reason written through it to a named seller.
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/vendor/orders/1/handover-without-code", new { reason = "Khách hết pin" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
