using Azure;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Options;
using StreetBiz.Application.Common.Interfaces;

namespace StreetBiz.Infrastructure.Storage;

/// <summary>
/// Azure Blob Storage implementation of <see cref="IFileStorage"/>. The container stays private:
/// every file is still served through the API (which checks who may see it), never by a blob URL.
/// A relative path such as <c>slots/5/abc.jpg</c> becomes the blob name as is.
/// </summary>
public sealed class AzureBlobFileStorage : IFileStorage
{
    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".png"] = "image/png",
        [".webp"] = "image/webp",
        [".pdf"] = "application/pdf",
        [".wav"] = "audio/wav",
        [".mp3"] = "audio/mpeg",
        [".webm"] = "audio/webm",
    };

    private readonly BlobContainerClient container;
    private readonly SemaphoreSlim containerLock = new(1, 1);
    private volatile bool containerReady;

    public AzureBlobFileStorage(IOptions<StorageSettings> options)
        : this(CreateContainerClient(options.Value.AzureBlob))
    {
    }

    public AzureBlobFileStorage(BlobContainerClient container) => this.container = container;

    /// <summary>
    /// Connection string wins (local Azurite, or an account key kept in user-secrets); otherwise the
    /// account URL is used with <see cref="DefaultAzureCredential"/> (managed identity in Azure).
    /// </summary>
    public static BlobContainerClient CreateContainerClient(AzureBlobSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.ContainerName))
        {
            throw new InvalidOperationException("Storage:AzureBlob:ContainerName must be set.");
        }

        if (!string.IsNullOrWhiteSpace(settings.ConnectionString))
        {
            return new BlobContainerClient(settings.ConnectionString, settings.ContainerName);
        }

        if (!string.IsNullOrWhiteSpace(settings.AccountUrl))
        {
            var containerUri = new Uri($"{settings.AccountUrl.TrimEnd('/')}/{settings.ContainerName}");
            return new BlobContainerClient(containerUri, new DefaultAzureCredential());
        }

        throw new InvalidOperationException(
            "Storage:AzureBlob needs either ConnectionString or AccountUrl (user-secrets / env var).");
    }

    public async Task SaveAsync(string relativePath, Stream content, CancellationToken cancellationToken)
    {
        var blob = Blob(relativePath);
        await EnsureContainerAsync(cancellationToken);

        // Like the disk store's CreateNew: never silently replace an existing file.
        var options = new BlobUploadOptions
        {
            Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
            HttpHeaders = new BlobHttpHeaders { ContentType = ContentTypeOf(relativePath) },
        };
        await blob.UploadAsync(content, options, cancellationToken);
    }

    public async Task<Stream?> OpenReadAsync(string relativePath, CancellationToken cancellationToken)
    {
        try
        {
            var download = await Blob(relativePath).DownloadStreamingAsync(cancellationToken: cancellationToken);
            return download.Value.Content;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task<bool> ExistsAsync(string relativePath, CancellationToken cancellationToken) =>
        (await Blob(relativePath).ExistsAsync(cancellationToken)).Value;

    private BlobClient Blob(string relativePath) => container.GetBlobClient(BlobName(relativePath));

    /// <summary>Same guard as the disk store: refuse any path that could leave its folder.</summary>
    public static string BlobName(string relativePath)
    {
        var segments = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
        {
            throw new InvalidOperationException("Storage path escapes the storage root.");
        }

        return string.Join('/', segments);
    }

    private static string ContentTypeOf(string relativePath) =>
        ContentTypes.TryGetValue(Path.GetExtension(relativePath), out var type)
            ? type
            : "application/octet-stream";

    private async Task EnsureContainerAsync(CancellationToken cancellationToken)
    {
        if (containerReady)
        {
            return;
        }

        await containerLock.WaitAsync(cancellationToken);
        try
        {
            if (!containerReady)
            {
                try
                {
                    await container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: cancellationToken);
                }
                catch (RequestFailedException ex) when (ex.Status == 403)
                {
                    // An identity that may write blobs but not create containers: the container was
                    // provisioned beforehand, so carry on and let the upload report any real problem.
                }

                containerReady = true;
            }
        }
        finally
        {
            containerLock.Release();
        }
    }
}
