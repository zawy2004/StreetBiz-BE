using StreetBiz.Application.Common.Interfaces;

namespace StreetBiz.Infrastructure.Storage;

public sealed class StorageSettings
{
    public const string SectionName = "Storage";

    /// <summary>Root folder for uploads; relative paths are resolved against the content root.</summary>
    public string RootPath { get; set; } = "App_Data/uploads";
}

/// <summary>
/// Disk-backed storage for development and single-server deployments. Swap for a
/// blob-storage implementation of <see cref="IFileStorage"/> when scaling out.
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
