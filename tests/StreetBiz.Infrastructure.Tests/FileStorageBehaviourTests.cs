using Microsoft.Extensions.Options;
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
