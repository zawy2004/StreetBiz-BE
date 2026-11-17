using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;
using StreetBiz.API.Controllers;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;
using StreetBiz.Infrastructure.Storage;

namespace StreetBiz.API.Tests;

/// <summary>
/// Public photos: dish/store photos (vendors upload) and sidewalk-slot photos (ward officers upload).
/// Who may upload, that the stored file is re-encoded and scaled without metadata, and how it is served.
/// </summary>
public sealed class PhotoUploadControllerTests
{
    private const string FileName = "0123456789abcdef0123456789abcdef.jpg";

    private sealed class RecordingStorage : IFileStorage
    {
        public Dictionary<string, byte[]> Saved { get; } = [];

        public async Task SaveAsync(string relativePath, Stream content, CancellationToken cancellationToken)
        {
            using var copy = new MemoryStream();
            await content.CopyToAsync(copy, cancellationToken);
            Saved[relativePath] = copy.ToArray();
        }

        public Task<Stream?> OpenReadAsync(string relativePath, CancellationToken cancellationToken) =>
            Task.FromResult<Stream?>(Saved.TryGetValue(relativePath, out var bytes) ? new MemoryStream(bytes) : null);

        public Task<bool> ExistsAsync(string relativePath, CancellationToken cancellationToken) =>
            Task.FromResult(Saved.ContainsKey(relativePath));

        public Task DeleteAsync(string relativePath, CancellationToken cancellationToken)
        {
            Saved.Remove(relativePath);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<StoredFile>> ListAsync(string folder, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private static ICurrentUser User(long userId, string roleCode)
    {
        var user = new Mock<ICurrentUser>();
        user.SetupGet(x => x.UserId).Returns(userId);
        user.SetupGet(x => x.RoleCode).Returns(roleCode);
        return user.Object;
    }

    private static T WithHttp<T>(T controller) where T : ControllerBase
    {
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }

    private static MenuImagesController Menu(ICurrentUser user, RecordingStorage storage) =>
        WithHttp(new MenuImagesController(storage, user, new ImageSharpImageProcessor()));

    private static SlotImagesController Slot(ICurrentUser user, RecordingStorage storage) =>
        WithHttp(new SlotImagesController(storage, user, new ImageSharpImageProcessor()));

    /// <summary>A phone-sized JPEG carrying a GPS position in its EXIF, as a camera writes it.</summary>
    private static async Task<byte[]> PhonePhoto(int width = 4000, int height = 3000)
    {
        using var image = new Image<Rgb24>(width, height, Color.Orange);
        image.Metadata.ExifProfile = new ExifProfile();
        image.Metadata.ExifProfile.SetValue(ExifTag.GPSLatitudeRef, "N");
        image.Metadata.ExifProfile.SetValue(ExifTag.Make, "PhoneMaker");
        using var buffer = new MemoryStream();
        await image.SaveAsJpegAsync(buffer);
        return buffer.ToArray();
    }

    private static IFormFile Upload(byte[] bytes, string name = "photo.jpg") =>
        new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", name);

    private static byte[] FakeJpeg() => [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12];

    private static UploadedFileResponse Body(ActionResult<UploadedFileResponse> result) =>
        result.Result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<UploadedFileResponse>().Subject;

    // ---- dish and store photos ----------------------------------------------------------

    [Theory]
    [InlineData(RoleCodes.Customer)]
    [InlineData(RoleCodes.WardAuthority)]
    [InlineData(RoleCodes.PlatformAdmin)]
    public async Task Only_a_vendor_uploads_a_dish_photo(string role)
    {
        var storage = new RecordingStorage();

        var act = () => Menu(User(5, role), storage).Upload(Upload([1, 2, 3]), CancellationToken.None);

        await act.Should().ThrowAsync<ForbiddenException>();
        storage.Saved.Should().BeEmpty();
    }

    [Fact]
    public async Task A_dish_photo_is_scaled_to_1600_px_and_loses_its_exif()
    {
        var storage = new RecordingStorage();

        var result = await Menu(User(5, RoleCodes.Vendor), storage).Upload(Upload(await PhonePhoto()), CancellationToken.None);

        var (path, stored) = storage.Saved.Should().ContainSingle().Subject;
        path.Should().StartWith("menu/5/");
        var info = Image.Identify(stored);
        (info.Width, info.Height).Should().Be((MenuImagesController.MaxEdgePixels, 1200));
        info.Metadata.ExifProfile.Should().BeNull();
        var body = Body(result);
        body.FileUrl.Should().StartWith(MenuImageFiles.UrlPrefix + "5/").And.EndWith(".jpg");
        body.SizeBytes.Should().Be(stored.Length);
    }

    [Fact]
    public async Task A_small_dish_photo_keeps_its_size()
    {
        var storage = new RecordingStorage();

        await Menu(User(5, RoleCodes.Vendor), storage).Upload(Upload(await PhonePhoto(800, 600)), CancellationToken.None);

        var info = Image.Identify(storage.Saved.Values.Single());
        (info.Width, info.Height).Should().Be((800, 600));
    }

    [Fact]
    public async Task A_file_that_only_looks_like_a_dish_photo_is_refused()
    {
        var storage = new RecordingStorage();

        var act = () => Menu(User(5, RoleCodes.Vendor), storage).Upload(Upload(FakeJpeg()), CancellationToken.None);

        (await act.Should().ThrowAsync<ValidationAppException>()).Which.Errors.Should().ContainKey("File");
        storage.Saved.Should().BeEmpty();
    }

    [Fact]
    public async Task A_pdf_is_not_a_dish_photo()
    {
        var act = () => Menu(User(5, RoleCodes.Vendor), new RecordingStorage())
            .Upload(Upload("%PDF-1.4 menu"u8.ToArray(), "menu.pdf"), CancellationToken.None);

        (await act.Should().ThrowAsync<ValidationAppException>()).Which.Errors["File"].Should().Equal(MenuImageFiles.InvalidType);
    }

    [Fact]
    public async Task A_missing_dish_photo_is_a_validation_error()
    {
        var act = () => Menu(User(5, RoleCodes.Vendor), new RecordingStorage()).Upload(null, CancellationToken.None);

        await act.Should().ThrowAsync<ValidationAppException>();
    }

    [Fact]
    public async Task A_dish_photo_is_served_publicly_and_cached_and_a_bad_name_is_not_found()
    {
        var storage = new RecordingStorage();
        storage.Saved[MenuImageFiles.StoragePath(5, FileName)] = await PhonePhoto(10, 10);
        var controller = Menu(User(0, RoleCodes.Customer), storage);

        var served = await controller.Download(5, FileName, CancellationToken.None);

        served.Should().BeOfType<FileStreamResult>().Which.ContentType.Should().Be("image/jpeg");
        controller.Response.Headers.CacheControl.ToString().Should().Contain("public");
        (await controller.Download(5, "../../evidence/5/" + FileName, CancellationToken.None)).Should().BeOfType<NotFoundResult>();
        (await controller.Download(6, FileName, CancellationToken.None)).Should().BeOfType<NotFoundResult>();
    }

    // ---- sidewalk-slot photos -----------------------------------------------------------

    [Theory]
    [InlineData(RoleCodes.Vendor)]
    [InlineData(RoleCodes.Customer)]
    [InlineData(RoleCodes.PlatformAdmin)]
    public async Task Only_a_ward_officer_uploads_a_slot_photo(string role)
    {
        var storage = new RecordingStorage();

        var act = () => Slot(User(9, role), storage).Upload(Upload([1, 2, 3]), CancellationToken.None);

        await act.Should().ThrowAsync<ForbiddenException>();
        storage.Saved.Should().BeEmpty();
    }

    [Fact]
    public async Task A_slot_photo_is_scaled_like_a_dish_photo_and_loses_its_exif()
    {
        var storage = new RecordingStorage();

        var result = await Slot(User(9, RoleCodes.WardAuthority), storage).Upload(Upload(await PhonePhoto(3000, 4000)), CancellationToken.None);

        var (path, stored) = storage.Saved.Should().ContainSingle().Subject;
        path.Should().StartWith("slots/9/");
        var info = Image.Identify(stored);
        (info.Width, info.Height).Should().Be((1200, MenuImagesController.MaxEdgePixels));
        info.Metadata.ExifProfile.Should().BeNull();
        Body(result).SizeBytes.Should().Be(stored.Length);
    }

    [Fact]
    public async Task A_file_that_only_looks_like_a_slot_photo_is_refused()
    {
        var storage = new RecordingStorage();

        var act = () => Slot(User(9, RoleCodes.WardAuthority), storage).Upload(Upload(FakeJpeg()), CancellationToken.None);

        await act.Should().ThrowAsync<ValidationAppException>();
        storage.Saved.Should().BeEmpty();
    }

    [Fact]
    public async Task A_slot_photo_is_served_publicly_and_a_bad_name_is_not_found()
    {
        var storage = new RecordingStorage();
        storage.Saved[SlotImageFiles.StoragePath(9, FileName)] = await PhonePhoto(10, 10);
        var controller = Slot(User(0, RoleCodes.Customer), storage);

        (await controller.Download(9, FileName, CancellationToken.None)).Should().BeOfType<FileStreamResult>();
        (await controller.Download(9, "not-a-photo.exe", CancellationToken.None)).Should().BeOfType<NotFoundResult>();
    }
}
