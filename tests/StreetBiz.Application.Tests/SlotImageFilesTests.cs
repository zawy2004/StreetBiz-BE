using StreetBiz.Application.Common.Security;

namespace StreetBiz.Application.Tests;

public sealed class SlotImageFilesTests
{
    private const string Name = "0123456789abcdef0123456789abcdef.jpg";

    [Fact]
    public void A_url_built_for_a_photo_parses_back_to_its_owner_and_file()
    {
        var url = SlotImageFiles.BuildUrl(42, Name);

        Assert.Equal("/api/uploads/slot-images/42/" + Name, url);
        Assert.True(SlotImageFiles.TryParseUrl(url, out var owner, out var file));
        Assert.Equal(42, owner);
        Assert.Equal(Name, file);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://example.com/0123456789abcdef0123456789abcdef.jpg")]
    [InlineData("/api/uploads/menu-images/42/0123456789abcdef0123456789abcdef.jpg")]
    [InlineData("/api/uploads/evidence/42/0123456789abcdef0123456789abcdef.jpg")]
    [InlineData("/api/uploads/slot-images/42/short.jpg")]
    [InlineData("/api/uploads/slot-images/42/0123456789abcdef0123456789abcdef.pdf")]
    [InlineData("/api/uploads/slot-images/42/../0123456789abcdef0123456789abcdef.jpg")]
    [InlineData("/api/uploads/slot-images/notanumber/0123456789abcdef0123456789abcdef.jpg")]
    public void Only_urls_issued_by_the_slot_upload_endpoint_are_accepted(string? url)
    {
        Assert.False(SlotImageFiles.TryParseUrl(url, out _, out _));
    }

    [Fact]
    public void Photos_are_stored_apart_from_dish_photos_and_evidence()
    {
        Assert.Equal("slots/42/" + Name, SlotImageFiles.StoragePath(42, Name));
        Assert.NotEqual(MenuImageFiles.StoragePath(42, Name), SlotImageFiles.StoragePath(42, Name));
    }

    [Fact]
    public void A_pdf_is_not_a_photo()
    {
        var pdf = "%PDF-1.7\n"u8.ToArray();
        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1 };

        Assert.Null(SlotImageFiles.DetectExtension(pdf));
        Assert.Equal(".jpg", SlotImageFiles.DetectExtension(jpeg));
    }
}
