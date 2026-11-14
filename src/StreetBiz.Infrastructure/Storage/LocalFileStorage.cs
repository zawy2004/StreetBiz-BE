using StreetBiz.Application.Common.Interfaces;

namespace StreetBiz.Infrastructure.Storage;

public sealed class StorageSettings
{
    public const string SectionName = "Storage";

    /// <summary>Root folder for uploads; relative paths are resolved against the content root.</summary>
    public string RootPath { get; set; } = "App_Data/uploads";
}

/// <summary>
/// Disk-backed storage. On the VPS the root is the <c>streetbiz_uploads</c> Docker volume, so files
/// survive deploys and restarts; <c>deploy/vps/backup.sh</c> archives it.
/// </summary>
public sealed class LocalFileStorage(Microsoft.Extensions.Options.IOptions<StorageSettings> options) : IFileStorage
{
    private readonly string root = Path.GetFullPath(options.Value.RootPath);

    public async Task SaveAsync(string relativePath, Stream content, CancellationToken cancellationToken)
    {
        var path = Resolve(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await content.CopyToAsync(file, cancellationToken);
    }

    public Task<Stream?> OpenReadAsync(string relativePath, CancellationToken cancellationToken)
    {
        var path = Resolve(relativePath);
        Stream? stream = File.Exists(path)
            ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true)
            : null;
        return Task.FromResult(stream);
    }

    public Task<bool> ExistsAsync(string relativePath, CancellationToken cancellationToken) =>
        Task.FromResult(File.Exists(Resolve(relativePath)));

    public Task DeleteAsync(string relativePath, CancellationToken cancellationToken)
    {
        // File.Delete does nothing for a missing file.
        File.Delete(Resolve(relativePath));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<StoredFile>> ListAsync(string folder, CancellationToken cancellationToken)
    {
        var directory = Resolve(folder);
        IReadOnlyList<StoredFile> files = Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Select(path => new StoredFile(
                    Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'),
                    File.GetLastWriteTimeUtc(path)))
                .ToList()
            : [];
        return Task.FromResult(files);
    }

    // Callers already validate names, but refuse anything that escapes the root anyway.
    private string Resolve(string relativePath)
    {
        var full = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Storage path escapes the storage root.");
        }

        return full;
    }
}
