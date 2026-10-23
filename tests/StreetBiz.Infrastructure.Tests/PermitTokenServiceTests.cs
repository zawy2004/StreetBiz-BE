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
    public void A_printed_link_carries_the_signed_token_and_tampering_inside_it_is_caught()
    {
        // Section 3 rule 2: the /q/ link only wraps the SBP1 token; it adds no code of its own.
        var token = service.Create(42, DateTime.UtcNow);
        var links = new PermitLinks(Options.Create(new PermitSettings { PublicScanBaseUrl = "http://192.168.1.5:5173/" }));
        var link = links.ScanUrl(token, 3)!;

        link.Should().Be($"http://192.168.1.5:5173/q/{token}?v=3");
        service.TryParse(StreetBiz.Application.Common.Security.PermitQrText.ExtractToken(link), out var claims).Should().BeTrue();
        claims.ContractId.Should().Be(42);

        var payload = token.Split('.')[1];
        var flipped = payload[..^1] + (payload[^1] == 'A' ? 'B' : 'A');
        var tampered = link.Replace(payload, flipped, StringComparison.Ordinal);
        service.TryParse(StreetBiz.Application.Common.Security.PermitQrText.ExtractToken(tampered), out _).Should().BeFalse();
        // Nor can a sequential contract id stand in for the token.
        service.TryParse(StreetBiz.Application.Common.Security.PermitQrText.ExtractToken("http://192.168.1.5:5173/q/42"), out _)
            .Should().BeFalse();
        new PermitLinks(Options.Create(new PermitSettings())).ScanUrl(token).Should().BeNull();
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
