using FluentAssertions;
using Microsoft.Extensions.Options;
using StreetBiz.Infrastructure.Security;

namespace StreetBiz.Infrastructure.Tests;

public sealed class OrderPickupTokenServiceTests
{
    private const long OrderId = 15;
    private const long CustomerUserId = 7;

    private static OrderPickupTokenService Service(
        string key = "unit-test-order-pickup-key-not-for-prod") =>
        new(Options.Create(new OrderPickupSettings { SigningKey = key }));

    [Fact]
    public void Round_trips_the_order_and_its_customer()
    {
        var service = Service();

        service.TryParse(service.Create(OrderId, CustomerUserId), out var claims).Should().BeTrue();

        claims.OrderId.Should().Be(OrderId);
        claims.CustomerUserId.Should().Be(CustomerUserId);
    }

    [Fact]
    public void The_same_order_always_gets_the_same_code()
    {
        var service = Service();
        // The buyer must see one unchanging code, not a new one on every refresh.
        service.Create(OrderId, CustomerUserId)
            .Should().Be(service.Create(OrderId, CustomerUserId));
    }

    [Fact]
    public void Different_orders_and_different_customers_get_different_codes()
    {
        var service = Service();
        var code = service.Create(OrderId, CustomerUserId);

        code.Should().NotBe(service.Create(OrderId + 1, CustomerUserId));
        code.Should().NotBe(service.Create(OrderId, CustomerUserId + 1));
    }

    [Fact]
    public void Carries_no_readable_identifiers()
    {
        // A QR photographed over someone's shoulder must not reveal anything.
        Service().Create(OrderId, CustomerUserId).Should().NotContain(OrderId.ToString());
    }

    [Fact]
    public void A_code_signed_with_another_key_is_refused()
    {
        var token = Service("a-completely-different-signing-key-value")
            .Create(OrderId, CustomerUserId);

        Service().TryParse(token, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-token")]
    [InlineData("SBO1.AAAA")]
    [InlineData("SBO1..")]
    public void Anything_that_is_not_a_pickup_code_is_refused(string token)
    {
        Service().TryParse(token, out _).Should().BeFalse();
    }

    [Fact]
    public void A_permit_code_does_not_pass_as_a_pickup_code()
    {
        var permit = new PermitTokenService(
                Options.Create(new PermitSettings { SigningKey = "unit-test-signing-key-not-for-prod" }))
            .Create(OrderId, DateTime.UtcNow);

        Service().TryParse(permit, out _).Should().BeFalse();
    }

    [Fact]
    public void A_payload_swapped_under_a_valid_signature_is_refused()
    {
        var service = Service();
        var original = service.Create(OrderId, CustomerUserId).Split('.');
        var otherPayload = service.Create(OrderId + 99, CustomerUserId).Split('.')[1];

        service.TryParse($"{original[0]}.{otherPayload}.{original[2]}", out _).Should().BeFalse();
    }

    [Fact]
    public void Surrounding_whitespace_from_a_scanner_is_tolerated()
    {
        var service = Service();

        service.TryParse($"  {service.Create(OrderId, CustomerUserId)}\n", out var claims)
            .Should().BeTrue();
        claims.OrderId.Should().Be(OrderId);
    }

    // ---- the typed fallback ----

    [Fact]
    public void A_short_code_is_eight_unambiguous_characters()
    {
        var code = Service().CreateShortCode(OrderId, CustomerUserId);

        code.Should().HaveLength(8);
        // Crockford's base32 drops the characters people misread.
        code.Should().MatchRegex("^[0-9A-HJKMNP-TV-Z]{8}$");
    }

    [Fact]
    public void The_same_order_always_gets_the_same_short_code()
    {
        var service = Service();
        service.CreateShortCode(OrderId, CustomerUserId)
            .Should().Be(service.CreateShortCode(OrderId, CustomerUserId));
    }

    [Fact]
    public void Different_orders_get_different_short_codes()
    {
        var service = Service();
        var codes = Enumerable.Range(1, 200)
            .Select(id => service.CreateShortCode(id, CustomerUserId))
            .ToHashSet();

        codes.Should().HaveCount(200);
    }

    [Fact]
    public void The_short_code_is_not_derivable_from_the_qr_payload()
    {
        var service = Service();
        // Separate domains: seeing one must not hand over the other.
        service.Create(OrderId, CustomerUserId)
            .Should().NotContain(service.CreateShortCode(OrderId, CustomerUserId));
    }

    [Fact]
    public void A_short_code_signed_with_another_key_does_not_match()
    {
        var other = Service("a-completely-different-signing-key-value")
            .CreateShortCode(OrderId, CustomerUserId);

        Service().ShortCodeMatches(other, OrderId, CustomerUserId).Should().BeFalse();
    }

    [Fact]
    public void How_a_seller_types_it_does_not_matter()
    {
        var service = Service();
        var code = service.CreateShortCode(OrderId, CustomerUserId);

        foreach (var typed in new[] { code.ToLowerInvariant(), $" {code} ", string.Join('-', code.Chunk(4).Select(c => new string(c))) })
        {
            service.ShortCodeMatches(service.NormaliseShortCode(typed)!, OrderId, CustomerUserId)
                .Should().BeTrue($"'{typed}' is the same code");
        }
    }

    [Fact]
    public void Letters_people_confuse_for_digits_are_accepted()
    {
        var service = Service();
        // O/I/L never appear in a real code, so a seller typing them means 0/1.
        service.NormaliseShortCode("OIL12345").Should().Be("01112345");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ABC")]                 // too short
    [InlineData("ABCDEFGHJ")]           // too long
    [InlineData("!!!!!!!!")]            // nothing usable
    public void A_code_that_cannot_be_one_is_rejected(string typed)
    {
        Service().NormaliseShortCode(typed).Should().BeNull();
    }
}
