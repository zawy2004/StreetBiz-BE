using Moq;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Features.Chatbot;
using StreetBiz.Infrastructure.Services.Chatbot;

namespace StreetBiz.Infrastructure.Tests;

public sealed class ChatbotPublicImagesTests
{
    [Theory]
    [InlineData("https://example.com/image.png")]
    [InlineData("http://127.0.0.1/private")]
    [InlineData("/api/uploads/evidence/1/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.png")]
    [InlineData("/api/uploads/menu-images/1/../../secret.png")]
    public async Task Never_fetches_external_or_private_images(string url)
    {
        var storage = new Mock<IFileStorage>(MockBehavior.Strict);
        Assert.Empty(await new ChatbotPublicImages(storage.Object).ReadAsync([new(url, "candidate")], default));
        storage.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Only_reads_three_unique_public_uploads_with_image_signature_and_size_limits()
    {
        var storage = new Mock<IFileStorage>();
        byte[] bytes = [137,80,78,71,13,10,26,10,0,0,0,0];
        storage.Setup(s => s.OpenReadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(bytes));
        var urls = Enumerable.Range(1, 6).Select(i => new ChatbotPublicImage($"/api/uploads/menu-images/1/{i:x32}.png", $"candidate {i}")).ToArray();
        var result = await new ChatbotPublicImages(storage.Object).ReadAsync(urls, default);
        Assert.Equal(3, result.Count); Assert.Equal("candidate 1", result[0].Label);
        storage.Verify(s => s.OpenReadAsync(It.Is<string>(p => p.StartsWith("menu/1/")), It.IsAny<CancellationToken>()), Times.Exactly(3));
        storage.Setup(s => s.OpenReadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(new byte[2_097_153]));
        Assert.Empty(await new ChatbotPublicImages(storage.Object).ReadAsync(urls.Take(1).ToArray(), default));
    }
}
