using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using StreetBiz.Application.Common.Interfaces;

namespace StreetBiz.Infrastructure.Storage;

/// <summary>
/// <see cref="IImageProcessor"/> on ImageSharp. Each photo is decoded and saved again, which keeps
/// only the pixels (plus the colour profile) and drops EXIF/IPTC/XMP. A 5 MB upload can still
/// describe a huge canvas, so the header is read first and anything above
/// <see cref="MaxSourcePixels"/> is refused before any pixel memory is allocated.
/// </summary>
public sealed class ImageSharpImageProcessor : IImageProcessor
{
    /// <summary>About 50 megapixels: more than any phone camera, far below what would exhaust memory.</summary>
    public const long MaxSourcePixels = 50_000_000;

    public const string UnreadableImage = "Ảnh bị lỗi hoặc không đọc được. Vui lòng chọn ảnh khác.";
    public const string ImageTooLarge = "Ảnh có kích thước quá lớn. Vui lòng chọn ảnh nhỏ hơn.";

    private static readonly DecoderOptions Decoder = new() { MaxFrames = 1 };

    public async Task<Stream> SanitizePhotoAsync(
        Stream input, string extension, int maxEdgePixels, CancellationToken cancellationToken)
    {
        using var image = await LoadAsync(input, cancellationToken);
        image.Mutate(x =>
        {
            x.AutoOrient();
            if (image.Width > maxEdgePixels || image.Height > maxEdgePixels)
            {
                x.Resize(new ResizeOptions { Mode = ResizeMode.Max, Size = new Size(maxEdgePixels, maxEdgePixels) });
            }
        });
        StripMetadata(image);

        IImageEncoder encoder = extension switch
        {
            ".jpg" => new JpegEncoder { Quality = 85 },
            ".png" => new PngEncoder(),
            ".webp" => new WebpEncoder { Quality = 85 },
            _ => throw new InvalidImageException(UnreadableImage),
        };
        return await SaveAsync(image, encoder, cancellationToken);
    }

    public async Task<Stream> SquareJpegAsync(Stream input, int sizePixels, CancellationToken cancellationToken)
    {
        using var image = await LoadAsync(input, cancellationToken);
        image.Mutate(x => x
            .AutoOrient()
            .Resize(new ResizeOptions { Mode = ResizeMode.Crop, Position = AnchorPositionMode.Center, Size = new Size(sizePixels, sizePixels) })
            // JPEG has no transparency: a transparent PNG becomes white instead of black.
            .BackgroundColor(Color.White));
        StripMetadata(image);
        return await SaveAsync(image, new JpegEncoder { Quality = 85 }, cancellationToken);
    }

    private static async Task<Image<Rgba32>> LoadAsync(Stream input, CancellationToken cancellationToken)
    {
        // Identify and decode both need a seekable stream; uploads are capped at 5 MB.
        var buffer = new MemoryStream();
        await input.CopyToAsync(buffer, cancellationToken);
        buffer.Position = 0;

        try
        {
            var info = await Image.IdentifyAsync(Decoder, buffer, cancellationToken);
            if ((long)info.Width * info.Height > MaxSourcePixels)
            {
                throw new InvalidImageException(ImageTooLarge);
            }

            buffer.Position = 0;
            return await Image.LoadAsync<Rgba32>(Decoder, buffer, cancellationToken);
        }
        catch (Exception ex) when (ex is UnknownImageFormatException or InvalidImageContentException or NotSupportedException)
        {
            throw new InvalidImageException(UnreadableImage, ex);
        }
    }

    private static void StripMetadata(Image image)
    {
        image.Metadata.ExifProfile = null;
        image.Metadata.IptcProfile = null;
        image.Metadata.XmpProfile = null;
        foreach (var frame in image.Frames)
        {
            frame.Metadata.ExifProfile = null;
            frame.Metadata.IptcProfile = null;
            frame.Metadata.XmpProfile = null;
        }

        // PNG tEXt/iTXt chunks can carry the same data as EXIF.
        image.Metadata.GetPngMetadata().TextData.Clear();
    }

    private static async Task<Stream> SaveAsync(Image image, IImageEncoder encoder, CancellationToken cancellationToken)
    {
        var output = new MemoryStream();
        await image.SaveAsync(output, encoder, cancellationToken);
        output.Position = 0;
        return output;
    }
}
