namespace StreetBiz.Application.Common.Interfaces;

/// <summary>Stores uploaded files by a relative, storage-agnostic path.</summary>
public interface IFileStorage
{
    Task SaveAsync(string relativePath, Stream content, CancellationToken cancellationToken);

    /// <summary>Opens the file for reading, or returns null when it does not exist.</summary>
    Task<Stream?> OpenReadAsync(string relativePath, CancellationToken cancellationToken);

    Task<bool> ExistsAsync(string relativePath, CancellationToken cancellationToken);

    /// <summary>Deletes the file if it exists; a missing file is not an error.</summary>
    Task DeleteAsync(string relativePath, CancellationToken cancellationToken);

    /// <summary>Every file under a folder (for example <c>menu</c>), with paths relative to the storage root.</summary>
    Task<IReadOnlyList<StoredFile>> ListAsync(string folder, CancellationToken cancellationToken);
}

/// <summary>A stored file: its path relative to the storage root ('/' separated) and when it was written.</summary>
public sealed record StoredFile(string RelativePath, DateTime LastWriteUtc);
