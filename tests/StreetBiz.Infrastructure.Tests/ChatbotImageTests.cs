using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using StreetBiz.Application.Features.Chatbot;
using StreetBiz.Infrastructure.Services.Chatbot;

namespace StreetBiz.Infrastructure.Tests;

public sealed class ChatbotImageTests
{
    [Fact]
    public void Removes_metadata_and_rejects_corruption_or_non_images()
    {
        var png = Png(); var sanitized = ChatbotAttachmentService.SanitizePng(png);
        Assert.DoesNotContain("private-location", Encoding.ASCII.GetString(sanitized));
        Assert.True(sanitized.Length < png.Length);
        Assert.Equal(sanitized, ChatbotAttachmentService.SanitizePng(sanitized));
        png[^1] ^= 1;
        Assert.Throws<ChatbotException>(() => ChatbotAttachmentService.SanitizePng(png));
        Assert.Throws<ChatbotException>(() => ChatbotAttachmentService.SanitizePng(Encoding.UTF8.GetBytes("<svg onload='bad'>")));
    }

    [Fact]
    public async Task Image_is_owner_session_scoped_consumed_once_and_requires_consent()
    {
        using var vault = new ChatbotAttachmentService(new() { AttachmentsEnabled = true }, TimeProvider.System);
        var actor = new ChatbotActor(1, 2, "VENDOR", null, 3);
        var request = new ChatbotUploadRequest(Convert.ToBase64String(Png()), true);
        Assert.Throws<ChatbotException>(() => vault.Upload(actor, request with { Consent = false }));
        var image = vault.Upload(actor, request);
        await Assert.ThrowsAsync<ChatbotException>(async () => await vault.ReadAsync(actor with { UserId = 2 }, [image.Id], default));
        await Assert.ThrowsAsync<ChatbotException>(async () => await vault.ReadAsync(actor with { SessionId = 5 }, [image.Id], default));
        Assert.Single(await vault.ReadAsync(actor, [image.Id], default));
        await Assert.ThrowsAsync<ChatbotException>(async () => await vault.ReadAsync(actor, [image.Id], default));
    }

    [Fact]
    public void Flag_and_guest_and_per_user_limit_are_enforced()
    {
        var actor = new ChatbotActor(1, 2, "CUSTOMER", null, null);
        var request = new ChatbotUploadRequest(Convert.ToBase64String(Png()), true);
        using var disabled = new ChatbotAttachmentService(new(), TimeProvider.System);
        Assert.Throws<ChatbotException>(() => disabled.Upload(actor, request));
        using var enabled = new ChatbotAttachmentService(new() { AttachmentsEnabled = true }, TimeProvider.System);
        Assert.Throws<ChatbotException>(() => enabled.Upload(ChatbotActor.Guest, request));
        var first = enabled.Upload(actor, request); enabled.Upload(actor, request);
        Assert.Equal(429, Assert.Throws<ChatbotException>(() => enabled.Upload(actor, request)).Status);
        enabled.Remove(actor, first.Id); enabled.Upload(actor, request);
    }

    private static byte[] Png()
    {
        using var output = new MemoryStream(); output.Write(new byte[] {137,80,78,71,13,10,26,10});
        Chunk("IHDR", [0,0,0,1,0,0,0,1,8,6,0,0,0]);
        Chunk("tEXt", Encoding.ASCII.GetBytes("location\0private-location"));
        using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Fastest, true)) z.Write(new byte[] {0,255,0,0,255});
        Chunk("IDAT", compressed.ToArray()); Chunk("IEND", []); return output.ToArray();
        void Chunk(string name, byte[] bytes)
        {
            var length = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length); output.Write(length);
            var data = Encoding.ASCII.GetBytes(name).Concat(bytes).ToArray(); output.Write(data);
            var crc = uint.MaxValue; foreach (var b in data) { crc ^= b; for (var i = 0; i < 8; i++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xedb88320u); }
            BinaryPrimitives.WriteUInt32BigEndian(length, crc ^ uint.MaxValue); output.Write(length);
        }
    }
}
