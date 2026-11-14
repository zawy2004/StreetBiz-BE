namespace StreetBiz.Application.Common.Interfaces;

/// <summary>
/// Re-encodes uploaded photos before they are stored for public viewing. Decoding and saving again
/// drops every metadata block (EXIF with the phone's GPS position, IPTC, XMP), applies the EXIF
/// rotation to the pixels, and caps the size, so a public URL never leaks where a photo was taken.
/// </summary>
public interface IImageProcessor
{
    /// <summary>
    /// A dish or storefront photo: same format as the upload (.jpg/.png/.webp), longest edge at most
    /// <paramref name="maxEdgePixels"/>. Throws <see cref="InvalidImageException"/> for a file that
    /// is not a decodable image.
    /// </summary>
    Task<Stream> SanitizePhotoAsync(Stream input, string extension, int maxEdgePixels, CancellationToken cancellationToken);

    /// <summary>A profile photo: centre-cropped to a square of <paramref name="sizePixels"/>, saved as JPEG.</summary>
    Task<Stream> SquareJpegAsync(Stream input, int sizePixels, CancellationToken cancellationToken);
}

/// <summary>The bytes passed the format sniff but are not an image the server can decode.</summary>
public sealed class InvalidImageException(string message, Exception? inner = null) : Exception(message, inner);
