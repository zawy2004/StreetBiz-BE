using FluentAssertions;
using Microsoft.Extensions.Options;
using StreetBiz.Infrastructure.Security;

namespace StreetBiz.Infrastructure.Tests;

public sealed class PermitTokenServiceTests
{
    private readonly PermitTokenService service = new(
        Options.Create(new PermitSettings { SigningKey = "unit-test-signing-key-not-for-prod" }));

    [Fact]
    public void Create_then_TryParse_round_trips_the_contract_id_and_issued_at()
    {
        var issuedAt = new DateTime(2026, 9, 17, 6, 0, 0, DateTimeKind.Utc);
        var token = service.Create(contractId: 42, issuedAt);

        var parsed = service.TryParse(token, out var claims);

        parsed.Should().BeTrue();
        claims.ContractId.Should().Be(42);
        claims.IssuedAtUtc.Should().Be(issuedAt);
    }

    [Fact]
    public void Two_tokens_for_the_same_contract_are_different()
    {
        // Uniqueness comes from the random component, not from contract_id or issued_at alone --
        // this is what lets DigitalPermits.qr_payload stay UNIQUE across reissues.
        var issuedAt = DateTime.UtcNow;
        var first = service.Create(42, issuedAt);
        var second = service.Create(42, issuedAt);

        first.Should().NotBe(second);
    }

    [Fact]
    public void A_token_with_a_tampered_signature_is_rejected()
    {
        var token = service.Create(42, DateTime.UtcNow);
        var parts = token.Split('.');
        var tamperedSignature = parts[2][0] == 'A' ? 'B' : 'A';
        var tampered = $"{parts[0]}.{parts[1]}.{tamperedSignature}{parts[2][1..]}";

        service.TryParse(tampered, out _).Should().BeFalse();
    }

    [Fact]
    public void A_token_signed_with_a_different_key_is_rejected()
    {
        var other = new PermitTokenService(Options.Create(new PermitSettings { SigningKey = "a-completely-different-key" }));
        var token = other.Create(42, DateTime.UtcNow);

        service.TryParse(token, out _).Should().BeFalse();
    }

    [Fact]
    public void Garbage_input_is_rejected_without_throwing()
    {
        service.TryParse("not-a-permit-token", out _).Should().BeFalse();
        service.TryParse("", out _).Should().BeFalse();
        service.TryParse("SBP1.onlytwoparts", out _).Should().BeFalse();
        service.TryParse("SBP1.!!!not-base64url!!!.also-not-base64url", out _).Should().BeFalse();
    }

    [Fact]
    public void The_token_fits_comfortably_within_qr_payload_NVARCHAR_500()
    {
        var token = service.Create(long.MaxValue, DateTime.UtcNow);
        token.Length.Should().BeLessThan(100);
    }
}
