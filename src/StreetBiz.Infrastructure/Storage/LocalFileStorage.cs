using StreetBiz.Application.Common.Interfaces;

namespace StreetBiz.Infrastructure.Storage;

public sealed class StorageSettings
{
    public const string SectionName = "Storage";
    public const string LocalProvider = "Local";
    public const string AzureBlobProvider = "AzureBlob";

    /// <summary>Where uploads live: <c>Local</c> (disk, the default) or <c>AzureBlob</c>.</summary>
    public string Provider { get; set; } = LocalProvider;

    /// <summary>Root folder for uploads; relative paths are resolved against the content root.</summary>
    public string RootPath { get; set; } = "App_Data/uploads";

    /// <summary>Used only when <see cref="Provider"/> is <c>AzureBlob</c>.</summary>
    public AzureBlobSettings AzureBlob { get; set; } = new();

    public bool UsesAzureBlob => string.Equals(Provider, AzureBlobProvider, StringComparison.OrdinalIgnoreCase);

    /// <summary>Fails at startup, not at the first upload, when the provider is unknown or half-configured.</summary>
    public void Validate()
    {
        if (UsesAzureBlob)
        {
            AzureBlobFileStorage.CreateContainerClient(AzureBlob);
        }
        else if (!string.Equals(Provider, LocalProvider, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Storage:Provider must be '{LocalProvider}' or '{AzureBlobProvider}', not '{Provider}'.");
        }
    }
}

public sealed class AzureBlobSettings
{
    /// <summary>Full connection string (Azurite for local runs). Keep it in user-secrets or an env var.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>Account URL such as https://name.blob.core.windows.net, used with managed identity.</summary>
    public string? AccountUrl { get; set; }

    /// <summary>Private container; created on the first upload when it is missing.</summary>
    public string ContainerName { get; set; } = "streetbiz-uploads";
}

/// <summary>
/// Disk-backed storage for development and single-server deployments. Set
/// <c>Storage:Provider</c> to <c>AzureBlob</c> (<see cref="AzureBlobFileStorage"/>) when scaling out.
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
