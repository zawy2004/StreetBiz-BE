using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using StreetBiz.Application.Features.Chatbot;

namespace StreetBiz.Infrastructure.Services.Chatbot;

/// <summary>Single-node, short-lived image vault. No public URL, disk, DB, or provider Files API.</summary>
public sealed class ChatbotAttachmentService(ChatbotSettings settings, TimeProvider clock) : IChatbotAttachments, IDisposable
{
    private sealed record Entry(ChatbotActor Owner, byte[] Bytes, DateTimeOffset ExpiresAt);
    private readonly Dictionary<string, Entry> entries = new();
    private readonly object gate = new();
    private ITimer? cleanup;

    public ChatbotAttachment Upload(ChatbotActor actor, ChatbotUploadRequest request)
    {
        if (!settings.Enabled || !settings.AttachmentsEnabled || actor.UserId == 0)
            throw new ChatbotException(403, "attachments_disabled", "Tính năng ảnh chưa được bật cho tài khoản này.");
        if (!request.Consent) throw new ChatbotException(400, "consent_required", "Cần xác nhận ảnh không chứa thông tin cá nhân và đồng ý gửi ảnh đến Gemini.");
        if (string.IsNullOrWhiteSpace(request.PngBase64) || request.PngBase64.Length > 2_800_000) throw Invalid();
        byte[] input;
        try { input = Convert.FromBase64String(request.PngBase64); } catch (FormatException) { throw Invalid(); }
        byte[] safe;
        try { safe = SanitizePng(input); } finally { CryptographicOperations.ZeroMemory(input); }
        lock (gate)
        {
            Expire();
            cleanup ??= clock.CreateTimer(_ => { lock (gate) Expire(); }, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
            if (entries.Values.Count(e => e.Owner.UserId == actor.UserId) >= 2 || entries.Values.Sum(e => (long)e.Bytes.Length) + safe.Length > 32_000_000)
            {
                CryptographicOperations.ZeroMemory(safe);
                throw new ChatbotException(429, "image_budget", "Bộ nhớ ảnh đang bận. Xóa ảnh đã chọn hoặc thử lại sau 5 phút.");
            }
            var id = Guid.NewGuid().ToString("N");
            var expires = clock.GetUtcNow().AddMinutes(5);
            entries[id] = new(actor, safe, expires);
            return new(id, expires.ToOffset(TimeSpan.FromHours(7)));
        }
    }

    public Task<IReadOnlyList<ChatbotImage>> ReadAsync(ChatbotActor actor, string[] ids, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (gate)
        {
            Expire();
            if (ids.Length > 2 || ids.Distinct().Count() != ids.Length) throw Invalid();
            var found = ids.Select(id => entries.TryGetValue(id, out var e) && e.Owner == actor ? e : throw new ChatbotException(404, "image_unavailable", "Ảnh không còn khả dụng. Hãy chọn và gửi lại ảnh.")).ToArray();
            // Consume once. The request owns these bytes and clears them on completion.
            foreach (var id in ids) entries.Remove(id);
            return Task.FromResult<IReadOnlyList<ChatbotImage>>(found.Select(e => new ChatbotImage(e.Bytes, "image/png")).ToArray());
        }
    }

    public void Remove(ChatbotActor actor, string id)
    {
        lock (gate)
            if (entries.TryGetValue(id, out var e) && e.Owner == actor) { CryptographicOperations.ZeroMemory(e.Bytes); entries.Remove(id); }
    }
    private void Expire()
    {
        foreach (var pair in entries.Where(e => e.Value.ExpiresAt <= clock.GetUtcNow()).ToArray())
        { CryptographicOperations.ZeroMemory(pair.Value.Bytes); entries.Remove(pair.Key); }
    }
    public void Dispose()
    {
        cleanup?.Dispose();
        lock (gate) { foreach (var entry in entries.Values) CryptographicOperations.ZeroMemory(entry.Bytes); entries.Clear(); }
    }
    private static ChatbotException Invalid() => new(400, "invalid_image", "Ảnh không hợp lệ. Chọn ảnh PNG/JPEG/WebP, tối đa 5 MB; không gửi giấy tờ định danh.");

    // Accept only the narrow PNG profile emitted by a browser canvas: 8-bit RGB/RGBA,
    // non-interlaced, <=1280x1280. Validate CRC and bounded decompressed scanlines;
    // discard every ancillary chunk (EXIF/text/location/ICC/APNG). No native image decoder.
    public static byte[] SanitizePng(byte[] bytes)
    {
        if (bytes.Length is < 45 or > 2_097_152 || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] {137,80,78,71,13,10,26,10})) throw Invalid();
        using var output = new MemoryStream(); output.Write(bytes, 0, 8);
        using var compressed = new MemoryStream();
        var offset = 8; var width = 0; var height = 0; var channels = 0; var ended = false; var seenData = false;
        while (offset + 12 <= bytes.Length)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));
            if (length > bytes.Length - offset - 12) throw Invalid();
            var n = (int)length;
            var type = System.Text.Encoding.ASCII.GetString(bytes, offset + 4, 4);
            var chunk = bytes.AsSpan(offset + 4, n + 4);
            var crc = uint.MaxValue;
            foreach (var b in chunk) { crc ^= b; for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xedb88320u); }
            if ((crc ^ uint.MaxValue) != BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset + 8 + n, 4))) throw Invalid();
            if (type == "IHDR")
            {
                if (offset != 8 || n != 13) throw Invalid();
                width = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset + 8, 4));
                height = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset + 12, 4));
                channels = bytes[offset + 17] == 2 ? 3 : bytes[offset + 17] == 6 ? 4 : 0;
                if (width is < 1 or > 1280 || height is < 1 or > 1280 || channels == 0 || bytes[offset + 16] != 8
                    || bytes[offset + 18] != 0 || bytes[offset + 19] != 0 || bytes[offset + 20] != 0) throw Invalid();
            }
            else if (width == 0) throw Invalid();
            else if (type == "IDAT") { compressed.Write(bytes, offset + 8, n); seenData = true; }
            else if (type == "IEND") { if (n != 0 || !seenData || offset + 12 != bytes.Length) throw Invalid(); ended = true; }
            else if (type.Length != 4 || (type[0] & 32) == 0) throw Invalid(); // Unknown critical chunk.
            if (type is "IHDR" or "IDAT" or "IEND") output.Write(bytes, offset, n + 12);
            offset += n + 12;
            if (ended) break;
        }
        if (!ended) throw Invalid();
        compressed.Position = 0;
        try
        {
            using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
            var row = new byte[width * channels];
            for (var y = 0; y < height; y++) { var filter = zlib.ReadByte(); if (filter is < 0 or > 4) throw Invalid(); zlib.ReadExactly(row); }
            if (zlib.ReadByte() != -1) throw Invalid();
        }
        catch (InvalidDataException) { throw Invalid(); }
        catch (EndOfStreamException) { throw Invalid(); }
        return output.ToArray();
    }
}
