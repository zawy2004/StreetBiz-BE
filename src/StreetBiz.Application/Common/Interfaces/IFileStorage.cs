namespace StreetBiz.Application.Common.Interfaces;

/// <summary>Stores uploaded files by a relative, storage-agnostic path.</summary>
public interface IFileStorage
{
    Task SaveAsync(string relativePath, Stream content, CancellationToken cancellationToken);

    /// <summary>Opens the file for reading, or returns null when it does not exist.</summary>
    Task<Stream?> OpenReadAsync(string relativePath, CancellationToken cancellationToken);

    Task<bool> ExistsAsync(string relativePath, CancellationToken cancellationToken);
}
