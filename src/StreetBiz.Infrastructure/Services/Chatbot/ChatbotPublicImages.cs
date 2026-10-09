using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Features.Chatbot;

namespace StreetBiz.Infrastructure.Services.Chatbot;

/// <summary>Only server-returned public menu uploads. No HTTP fetches, private evidence, or arbitrary paths.</summary>
public sealed class ChatbotPublicImages(IFileStorage storage) : IChatbotPublicImages
{
    public async Task<IReadOnlyList<ChatbotImage>> ReadAsync(IReadOnlyList<ChatbotPublicImage> candidates, CancellationToken ct)
    {
        var images = new List<ChatbotImage>();
        try
        {
            foreach (var candidate in candidates.DistinctBy(c => c.Url).Take(3))
            {
                if (!MenuImageFiles.TryParseUrl(candidate.Url, out var owner, out var file) || owner <= 0) continue;
                try
                {
                    await using var stream = await storage.OpenReadAsync(MenuImageFiles.StoragePath(owner, file), ct);
                    if (stream is null) continue;
                    using var buffer = new MemoryStream();
                    var chunk = new byte[8192]; int read;
                    while (buffer.Length <= 2_097_152 && (read = await stream.ReadAsync(chunk, ct)) > 0) buffer.Write(chunk, 0, read);
                    if (buffer.Length is < 12 or > 2_097_152) continue;
                    var bytes = buffer.ToArray();
                    var extension = MenuImageFiles.DetectExtension(bytes.AsSpan(0, 12));
                    if (extension != Path.GetExtension(file)) continue;
                    images.Add(new(bytes, EvidenceFiles.ContentTypes[extension!], candidate.Label));
                }
                catch (IOException) { /* A missing/unreadable candidate is not an image-match claim. */ }
            }
            return images;
        }
        catch
        {
            foreach (var image in images) Array.Clear(image.Bytes);
            throw;
        }
    }
}
