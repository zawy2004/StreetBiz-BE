using FluentAssertions;
using StreetBiz.Application.Common.Security;

namespace StreetBiz.Application.Tests;

/// <summary>
/// The printed QR only wraps the signed SBP1 token in a link (section 3, rule 2): reading it back must
/// give exactly that token, from a bare payload or from any /q/ link, and never invent one.
/// </summary>
public sealed class PermitQrTextTests
{
    private const string Token = "SBP1.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA.BBBBBBBBBBBBBBBBBBBBBB";

    [Theory]
    [InlineData(Token)]
    [InlineData("  " + Token + "  ")]
    public void A_bare_token_is_taken_as_it_is(string scanned) =>
        PermitQrText.ExtractToken(scanned).Should().Be(Token);

    [Theory]
    [InlineData("http://192.168.1.5:5173/q/" + Token)]
    [InlineData("https://streetbiz.vn/q/" + Token + "?v=7")]
    [InlineData("https://streetbiz.vn/q/" + Token + "/print")]
    [InlineData("https://old-demo.local:8080/app/q/" + Token + "?v=2#top")]
    public void A_link_from_any_host_gives_back_the_token_inside_it(string scanned) =>
        PermitQrText.ExtractToken(scanned).Should().Be(Token);

    [Theory]
    [InlineData("https://streetbiz.vn/customer/scan")]
    [InlineData("ftp://streetbiz.vn/q/" + Token)]
    [InlineData("123")]
    [InlineData("")]
    public void Anything_else_is_returned_unchanged_for_the_signature_check_to_refuse(string scanned) =>
        PermitQrText.ExtractToken(scanned).Should().Be(scanned.Trim());

    [Theory]
    [InlineData("https://streetbiz.vn/q/" + Token + "?v=7", 7)]
    [InlineData("https://streetbiz.vn/q/" + Token + "?x=1&v=12", 12)]
    [InlineData("https://streetbiz.vn/q/" + Token, null)]
    [InlineData("https://streetbiz.vn/q/" + Token + "?v=0", null)]
    [InlineData("https://streetbiz.vn/q/" + Token + "?v=-3", null)]
    [InlineData("https://streetbiz.vn/q/" + Token + "?v=abc", null)]
    [InlineData(Token, null)]
    public void The_printed_board_version_is_read_from_the_link(string scanned, int? expected) =>
        PermitQrText.PrintedVersion(scanned).Should().Be(expected);

    [Theory]
    [InlineData("http://192.168.1.5:5173/", "http://192.168.1.5:5173")]
    [InlineData("https://streetbiz.vn/app", "https://streetbiz.vn/app")]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("streetbiz.vn", null)]
    [InlineData("https://streetbiz.vn/?x=1", null)]
    public void Only_an_absolute_http_base_url_is_used(string? configured, string? expected) =>
        PermitQrText.NormalizeBaseUrl(configured).Should().Be(expected);

    [Fact]
    public void Links_wrap_the_token_and_round_trip()
    {
        var scan = PermitQrText.ScanUrl("http://192.168.1.5:5173", Token, 4);
        var print = PermitQrText.PrintUrl("http://192.168.1.5:5173", Token);

        scan.Should().Be($"http://192.168.1.5:5173/q/{Token}?v=4");
        print.Should().Be($"http://192.168.1.5:5173/q/{Token}/print");
        PermitQrText.ExtractToken(scan).Should().Be(Token);
        PermitQrText.ExtractToken(print).Should().Be(Token);
        PermitQrText.PrintedVersion(scan).Should().Be(4);
        PermitQrText.ScanUrl(null, Token).Should().BeNull();
        PermitQrText.PrintUrl(null, Token).Should().BeNull();
    }
}
