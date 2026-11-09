using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Options;
using Moq;
using StreetBiz.Infrastructure.Storage;

namespace StreetBiz.Infrastructure.Tests;

/// <summary>Disk store on a temporary folder.</summary>
public sealed class LocalFileStorageTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "sb-storage-" + Guid.NewGuid().ToString("N"));
    private readonly LocalFileStorage storage;

    public LocalFileStorageTests() =>
        storage = new LocalFileStorage(Options.Create(new StorageSettings { RootPath = root }));

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task A_saved_file_creates_its_folder_and_reads_back()
    {
        await storage.SaveAsync("evidence/5/a.jpg", new MemoryStream([1, 2, 3]), default);

        Assert.True(await storage.ExistsAsync("evidence/5/a.jpg", default));
        await using var read = await storage.OpenReadAsync("evidence/5/a.jpg", default);
        using var copy = new MemoryStream();
        await read!.CopyToAsync(copy);
        Assert.Equal([1, 2, 3], copy.ToArray());
    }

    [Fact]
    public async Task A_missing_file_is_null_and_does_not_exist()
    {
        Assert.Null(await storage.OpenReadAsync("evidence/5/none.jpg", default));
        Assert.False(await storage.ExistsAsync("evidence/5/none.jpg", default));
    }

    [Fact]
    public async Task Saving_the_same_path_twice_never_overwrites()
    {
        await storage.SaveAsync("menu/1/x.png", new MemoryStream([1]), default);

        await Assert.ThrowsAsync<IOException>(() => storage.SaveAsync("menu/1/x.png", new MemoryStream([2]), default));
        await using var read = await storage.OpenReadAsync("menu/1/x.png", default);
        Assert.Equal(1, read!.ReadByte());
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("evidence/../../outside.txt")]
    [InlineData("")]
    public async Task Paths_that_leave_the_root_are_refused(string path)
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => storage.SaveAsync(path, new MemoryStream([1]), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => storage.ExistsAsync(path, default));
    }
}

/// <summary>Blob store against a mocked container: upload conditions, content types, 404 and 403 handling.</summary>
public sealed class AzureBlobFileStorageBehaviourTests
{
    private readonly Mock<BlobContainerClient> container = new();
    private readonly Mock<BlobClient> blob = new();
    private readonly List<string> names = [];
    private readonly List<BlobUploadOptions> uploads = [];

    public AzureBlobFileStorageBehaviourTests()
    {
        container.Setup(c => c.GetBlobClient(It.IsAny<string>()))
            .Callback<string>(names.Add)
            .Returns(blob.Object);
        container.Setup(c => c.CreateIfNotExistsAsync(PublicAccessType.None, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(BlobsModelFactory.BlobContainerInfo(ETag.All, DateTimeOffset.UtcNow), Mock.Of<Response>()));
        blob.Setup(b => b.UploadAsync(It.IsAny<Stream>(), It.IsAny<BlobUploadOptions>(), It.IsAny<CancellationToken>()))
            .Callback<Stream, BlobUploadOptions, CancellationToken>((_, options, _) => uploads.Add(options))
            .ReturnsAsync(Response.FromValue(BlobsModelFactory.BlobContentInfo(ETag.All, DateTimeOffset.UtcNow, null, null, null, null, 0), Mock.Of<Response>()));
    }

    [Theory]
    [InlineData("evidence/5/a.JPG", "image/jpeg")]
    [InlineData("menu/1/x.webp", "image/webp")]
    [InlineData("docs/1/f.pdf", "application/pdf")]
    [InlineData("voice/1/v.webm", "audio/webm")]
    [InlineData("misc/1/blob.bin", "application/octet-stream")]
    public async Task Uploads_never_replace_a_blob_and_carry_the_content_type(string path, string contentType)
    {
        var storage = new AzureBlobFileStorage(container.Object);

        await storage.SaveAsync(path, new MemoryStream([1]), default);

        var options = Assert.Single(uploads);
        Assert.Equal(ETag.All, options.Conditions.IfNoneMatch);
        Assert.Equal(contentType, options.HttpHeaders.ContentType);
        Assert.Equal(path, Assert.Single(names));
    }

    [Fact]
    public async Task The_container_is_created_once_and_a_403_on_creation_is_tolerated()
    {
        container.Setup(c => c.CreateIfNotExistsAsync(PublicAccessType.None, null, null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(403, "no container rights"));
        var storage = new AzureBlobFileStorage(container.Object);

        await storage.SaveAsync("a/1/x.jpg", new MemoryStream([1]), default);
        await storage.SaveAsync("a/1/y.jpg", new MemoryStream([1]), default);

        container.Verify(c => c.CreateIfNotExistsAsync(PublicAccessType.None, null, null, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(2, uploads.Count);
    }

    [Fact]
    public async Task Other_container_errors_reach_the_caller()
    {
        container.Setup(c => c.CreateIfNotExistsAsync(PublicAccessType.None, null, null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(500, "down"));
        var storage = new AzureBlobFileStorage(container.Object);

        await Assert.ThrowsAsync<RequestFailedException>(() => storage.SaveAsync("a/1/x.jpg", new MemoryStream([1]), default));
        blob.Verify(b => b.UploadAsync(It.IsAny<Stream>(), It.IsAny<BlobUploadOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Reading_streams_the_blob_and_a_404_is_null()
    {
        var content = new MemoryStream([7]);
        blob.SetupSequence(b => b.DownloadStreamingAsync(It.IsAny<BlobDownloadOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(BlobsModelFactory.BlobDownloadStreamingResult(content, BlobsModelFactory.BlobDownloadDetails()), Mock.Of<Response>()))
            .ThrowsAsync(new RequestFailedException(404, "missing"));
        var storage = new AzureBlobFileStorage(container.Object);

        Assert.Same(content, await storage.OpenReadAsync("a/1/x.jpg", default));
        Assert.Null(await storage.OpenReadAsync("a/1/gone.jpg", default));
    }

    [Fact]
    public async Task A_read_failure_other_than_404_is_not_hidden()
    {
        blob.Setup(b => b.DownloadStreamingAsync(It.IsAny<BlobDownloadOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(403, "denied"));
        var storage = new AzureBlobFileStorage(container.Object);

        await Assert.ThrowsAsync<RequestFailedException>(() => storage.OpenReadAsync("a/1/x.jpg", default));
    }

    [Fact]
    public async Task Exists_asks_the_blob()
    {
        blob.Setup(b => b.ExistsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Response.FromValue(true, Mock.Of<Response>()));
        var storage = new AzureBlobFileStorage(container.Object);

        Assert.True(await storage.ExistsAsync(@"a\1\x.jpg", default));
        Assert.Equal("a/1/x.jpg", Assert.Single(names));
    }
}
