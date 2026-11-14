using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;
using StreetBiz.Infrastructure.Persistence.Repositories;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;
using StreetBiz.Infrastructure.Storage;
using StreetBiz.Infrastructure.Tests.Support;

namespace StreetBiz.Infrastructure.Tests;

/// <summary>Re-encoding of public photos: no EXIF/GPS survives, sizes are capped, junk is refused.</summary>
public sealed class ImageSharpImageProcessorTests
{
    private readonly ImageSharpImageProcessor processor = new();

    private static MemoryStream JpegWithGps(int width, int height)
    {
        using var image = new Image<Rgba32>(width, height, Color.OrangeRed);
        image.Metadata.ExifProfile = new ExifProfile();
        image.Metadata.ExifProfile.SetValue(ExifTag.GPSLatitudeRef, "N");
        image.Metadata.ExifProfile.SetValue(ExifTag.GPSLatitude, [new Rational(16), new Rational(4), new Rational(0)]);
        image.Metadata.ExifProfile.SetValue(ExifTag.Make, "PhoneMaker");
        var stream = new MemoryStream();
        image.SaveAsJpeg(stream);
        stream.Position = 0;
        return stream;
    }

    [Fact]
    public async Task A_dish_photo_loses_its_exif_and_gps()
    {
        await using var input = JpegWithGps(400, 300);
        Assert.NotNull(Image.Identify(input).Metadata.ExifProfile);
        input.Position = 0;

        await using var output = await processor.SanitizePhotoAsync(input, ".jpg", 1600, default);

        var info = Image.Identify(output);
        Assert.Null(info.Metadata.ExifProfile);
        Assert.Equal((400, 300), (info.Width, info.Height));
        Assert.IsType<JpegFormat>(info.Metadata.DecodedImageFormat);
    }

    [Fact]
    public async Task A_large_photo_is_scaled_down_to_the_longest_edge_and_keeps_its_format()
    {
        using var image = new Image<Rgba32>(3000, 1500, Color.Teal);
        await using var input = new MemoryStream();
        await image.SaveAsPngAsync(input);
        input.Position = 0;

        await using var output = await processor.SanitizePhotoAsync(input, ".png", 1600, default);

        var info = Image.Identify(output);
        Assert.Equal((1600, 800), (info.Width, info.Height));
        Assert.IsType<PngFormat>(info.Metadata.DecodedImageFormat);
    }

    [Fact]
    public async Task An_avatar_becomes_a_square_jpeg_without_metadata()
    {
        await using var input = JpegWithGps(900, 600);

        await using var output = await processor.SquareJpegAsync(input, AvatarFiles.SizePixels, default);

        var info = Image.Identify(output);
        Assert.Equal((256, 256), (info.Width, info.Height));
        Assert.IsType<JpegFormat>(info.Metadata.DecodedImageFormat);
        Assert.Null(info.Metadata.ExifProfile);
    }

    [Fact]
    public async Task Bytes_that_only_look_like_an_image_are_refused()
    {
        // A valid JPEG signature followed by garbage passes the header sniff but cannot be decoded.
        await using var input = new MemoryStream([0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10]);

        var error = await Assert.ThrowsAsync<InvalidImageException>(() =>
            processor.SanitizePhotoAsync(input, ".jpg", 1600, default));
        Assert.Equal(ImageSharpImageProcessor.UnreadableImage, error.Message);
    }

    [Fact]
    public async Task A_tiny_file_describing_a_huge_canvas_is_refused_before_decoding()
    {
        // Only the PNG header is read: 20000 x 20000 = 400 MP, far over the limit.
        await using var input = new MemoryStream(PngHeader(20_000, 20_000));

        var error = await Assert.ThrowsAsync<InvalidImageException>(() =>
            processor.SquareJpegAsync(input, 256, default));
        Assert.Equal(ImageSharpImageProcessor.ImageTooLarge, error.Message);
    }

    private static byte[] PngHeader(int width, int height)
    {
        var ihdr = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8; // bit depth
        ihdr[9] = 2; // truecolour
        var type = "IHDR"u8.ToArray();
        var crc = Crc32([.. type, .. ihdr]);
        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        png.Write([0, 0, 0, 13]);
        png.Write(type);
        png.Write(ihdr);
        var crcBytes = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        png.Write(crcBytes);
        return png.ToArray();
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }

        return ~crc;
    }
}

/// <summary>The sweep deletes only old, unreferenced public photos.</summary>
public sealed class OrphanImageSweeperTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "sb-orphans-" + Guid.NewGuid().ToString("N"));
    private readonly LocalFileStorage storage;
    private readonly FakeTimeProvider clock = new(new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero));

    public OrphanImageSweeperTests() =>
        storage = new LocalFileStorage(Options.Create(new StorageSettings { RootPath = root }));

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private static string Name(char c) => new string(c, 32);

    private async Task<string> Put(string relativePath, TimeSpan age)
    {
        await storage.SaveAsync(relativePath, new MemoryStream([1, 2, 3]), default);
        File.SetLastWriteTimeUtc(Path.Combine(root, relativePath), clock.GetUtcNow().UtcDateTime - age);
        return relativePath;
    }

    [Fact]
    public async Task Deletes_old_orphans_and_keeps_recent_referenced_and_private_files()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        var db = database.Db;
        var old = TimeSpan.FromDays(3);

        var orphan = await Put($"menu/10/{Name('a')}.jpg", old);
        var recent = await Put($"menu/10/{Name('b')}.jpg", TimeSpan.FromHours(2));
        var avatar = await Put($"avatars/10/{Name('c')}.jpg", old);
        var oldAvatar = await Put($"avatars/10/{Name('d')}.jpg", old);
        var audited = await Put($"menu/11/{Name('e')}.png", old);
        var evidence = await Put($"evidence/10/{Name('f')}.jpg", old);

        var user = await db.UserAccounts.FindAsync(SqliteTestDatabase.VendorA);
        user!.avatar_url = AvatarFiles.BuildUrl(10, $"{Name('c')}.jpg");
        db.AuditLogs.Add(new AuditLog
        {
            actor_user_id = SqliteTestDatabase.VendorB,
            action = "MENU_UPDATE",
            entity_type = "MenuItem",
            entity_id = 1,
            details = $"{{\"before\":\"{MenuImageFiles.BuildUrl(11, Name('e') + ".png")}\"}}",
            created_at = clock.GetUtcNow().UtcDateTime,
        });
        await db.SaveChangesAsync();

        var deleted = await new OrphanImageSweeper(db, storage, clock).SweepAsync(TimeSpan.FromHours(24), default);

        Assert.Equal(2, deleted);
        Assert.False(await storage.ExistsAsync(orphan, default));
        Assert.False(await storage.ExistsAsync(oldAvatar, default));
        Assert.True(await storage.ExistsAsync(recent, default));
        Assert.True(await storage.ExistsAsync(avatar, default));
        Assert.True(await storage.ExistsAsync(audited, default));
        Assert.True(await storage.ExistsAsync(evidence, default));
    }

    [Fact]
    public async Task Lists_files_with_forward_slashes_and_deletes_missing_files_quietly()
    {
        await Put($"menu/7/{Name('a')}.jpg", TimeSpan.Zero);

        var files = await storage.ListAsync("menu", default);

        Assert.Equal($"menu/7/{Name('a')}.jpg", Assert.Single(files).RelativePath);
        Assert.Empty(await storage.ListAsync("avatars", default));
        await storage.DeleteAsync($"menu/7/{Name('z')}.jpg", default);
        await storage.DeleteAsync($"menu/7/{Name('a')}.jpg", default);
        Assert.Empty(await storage.ListAsync("menu", default));
    }

    [Fact]
    public async Task Setting_an_avatar_returns_the_previous_one_and_reaches_the_sign_in_projection()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        var repository = new UserAccountRepository(database.Db, new FixedClock(clock));
        var first = AvatarFiles.BuildUrl(SqliteTestDatabase.VendorA, Name('a') + ".jpg");
        var second = AvatarFiles.BuildUrl(SqliteTestDatabase.VendorA, Name('b') + ".jpg");

        Assert.Null(await repository.SetAvatarUrlAsync(SqliteTestDatabase.VendorA, first, default));
        Assert.Equal(first, await repository.SetAvatarUrlAsync(SqliteTestDatabase.VendorA, second, default));
        Assert.Equal(second, (await repository.GetByIdAsync(SqliteTestDatabase.VendorA, default))!.AvatarUrl);
        Assert.Equal(second, await repository.SetAvatarUrlAsync(SqliteTestDatabase.VendorA, null, default));
        Assert.Null((await repository.GetByIdAsync(SqliteTestDatabase.VendorA, default))!.AvatarUrl);
    }

    private sealed class FixedClock(TimeProvider time) : IDateTimeProvider
    {
        public DateTime UtcNow => time.GetUtcNow().UtcDateTime;
    }
}
