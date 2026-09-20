using FluentAssertions;
using StreetBiz.Application.Common.Security;

namespace StreetBiz.Application.Tests;

public sealed class EvidenceFilesTests
{
    private const string Name = "0123456789abcdef0123456789abcdef.png";

    [Fact]
    public void A_built_url_parses_back_to_its_owner_and_file()
    {
        EvidenceFiles.TryParseUrl(EvidenceFiles.BuildUrl(42, Name), out var owner, out var file).Should().BeTrue();
        owner.Should().Be(42);
        file.Should().Be(Name);
    }

    [Theory]
    [InlineData("blob:http://localhost:5173/abc")]
    [InlineData("https://example.com/id.jpg")]
    [InlineData("/api/uploads/evidence/42/../../appsettings.json")]
    [InlineData("/api/uploads/evidence/42/evil.exe")]
    [InlineData("/api/uploads/evidence/abc/0123456789abcdef0123456789abcdef.png")]
    [InlineData("")]
    public void Anything_not_issued_by_the_upload_endpoint_is_rejected(string url)
        => EvidenceFiles.TryParseUrl(url, out _, out _).Should().BeFalse();

    [Fact]
    public void File_type_comes_from_the_content_not_the_name()
    {
        EvidenceFiles.DetectExtension([0xFF, 0xD8, 0xFF, 0xE0]).Should().Be(".jpg");
        EvidenceFiles.DetectExtension([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]).Should().Be(".png");
        EvidenceFiles.DetectExtension("%PDF-1.7"u8).Should().Be(".pdf");
        EvidenceFiles.DetectExtension("RIFF\0\0\0\0WEBP"u8).Should().Be(".webp");
        EvidenceFiles.DetectExtension("MZ\x90\0"u8).Should().BeNull();
    }
}
