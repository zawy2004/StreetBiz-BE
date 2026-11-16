using FluentAssertions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Microsoft.AspNetCore.Http;
using Moq;
using StreetBiz.API.Controllers;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Features.AiMarketplace;
using StreetBiz.Application.Features.FoodSafety;
using StreetBiz.Application.Features.PenaltyReceipts;
using StreetBiz.Application.Features.WardSlots;
using StreetBiz.Infrastructure.Storage;

namespace StreetBiz.API.Tests;

public sealed class UploadsControllerTests
{
    private const string FileName = "0123456789abcdef0123456789abcdef.jpg";

    private static UploadsController Build(
        ICurrentUser currentUser,
        IWardActorResolver? wardActors = null,
        IBusinessRegistrationRepository? registrations = null,
        IFileStorage? storage = null,
        IAiMarketplaceRepository? vendorReports = null,
        IPenaltyReceiptRepository? penaltyReceipts = null) =>
        new(storage ?? Mock.Of<IFileStorage>(),
            currentUser,
            new ImageSharpImageProcessor(),
            wardActors ?? Mock.Of<IWardActorResolver>(),
            registrations ?? Mock.Of<IBusinessRegistrationRepository>(),
            Mock.Of<IFoodSafetyService>(),
            vendorReports ?? Mock.Of<IAiMarketplaceRepository>(),
            penaltyReceipts ?? Mock.Of<IPenaltyReceiptRepository>());

    private static Mock<ICurrentUser> User(long userId, string roleCode)
    {
        var currentUser = new Mock<ICurrentUser>();
        currentUser.SetupGet(c => c.UserId).Returns(userId);
        currentUser.SetupGet(c => c.RoleCode).Returns(roleCode);
        return currentUser;
    }

    [Fact]
    public async Task Only_vendors_ward_officers_and_customers_may_upload_evidence()
    {
        var currentUser = new Mock<ICurrentUser>();
        currentUser.SetupGet(c => c.UserId).Returns(1);
        currentUser.SetupGet(c => c.RoleCode).Returns(RoleCodes.PlatformAdmin);
        var controller = Build(currentUser.Object);

        // A platform administrator has no legitimate use for evidence files, so the
        // endpoint should refuse it before ever touching the request body.
        var act = () => controller.Upload(file: null, CancellationToken.None);

        await act.Should().ThrowAsync<ForbiddenException>().WithMessage(RegMessages.NotAVendor);
    }

    [Fact]
    public async Task A_missing_file_from_a_vendor_is_a_validation_error_not_a_role_error()
    {
        var currentUser = new Mock<ICurrentUser>();
        currentUser.SetupGet(c => c.UserId).Returns(1);
        currentUser.SetupGet(c => c.RoleCode).Returns(RoleCodes.Vendor);
        var controller = Build(currentUser.Object);

        var act = () => controller.Upload(file: null, CancellationToken.None);

        await act.Should().ThrowAsync<ValidationAppException>();
    }

    [Fact]
    public async Task A_platform_admin_cannot_open_registration_evidence()
    {
        // PRI-07 / BR-44: Platform Administrator has no authority over registration
        // identity evidence, even though it is the most privileged role elsewhere.
        var controller = Build(User(99, RoleCodes.PlatformAdmin).Object);

        var act = () => controller.Download(ownerUserId: 7, FileName, CancellationToken.None);

        await act.Should().ThrowAsync<ForbiddenException>().WithMessage(AppMessages.Forbidden);
    }

    [Fact]
    public async Task A_ward_officer_cannot_open_evidence_from_another_ward()
    {
        var wardActors = new Mock<IWardActorResolver>();
        wardActors.Setup(a => a.ResolveAsync(50, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WardActor(50, WardId: 11, "Officer"));
        var registrations = new Mock<IBusinessRegistrationRepository>();
        registrations.Setup(r => r.EvidenceBelongsToWardAsync(
                7, It.IsAny<string>(), 11, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var controller = Build(User(50, RoleCodes.WardAuthority).Object, wardActors.Object, registrations.Object);

        var act = () => controller.Download(ownerUserId: 7, FileName, CancellationToken.None);

        await act.Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task A_ward_officer_reviewing_the_owning_ward_gets_past_the_access_check()
    {
        var wardActors = new Mock<IWardActorResolver>();
        wardActors.Setup(a => a.ResolveAsync(50, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WardActor(50, WardId: 10, "Officer"));
        var registrations = new Mock<IBusinessRegistrationRepository>();
        registrations.Setup(r => r.EvidenceBelongsToWardAsync(
                7, It.IsAny<string>(), 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var controller = Build(User(50, RoleCodes.WardAuthority).Object, wardActors.Object, registrations.Object);

        // Storage has no such file, so reaching a 404 proves the access check passed.
        var result = await controller.Download(ownerUserId: 7, FileName, CancellationToken.None);

        result.Should().BeOfType<Microsoft.AspNetCore.Mvc.NotFoundResult>();
    }

    [Fact]
    public async Task A_customer_may_attach_a_photo_to_a_report_about_a_vendor()
    {
        // BUY-05: the photo goes through this endpoint too, so a missing file is a validation error, not a role error.
        var controller = Build(User(8, RoleCodes.Customer).Object);

        var act = () => controller.Upload(file: null, CancellationToken.None);

        await act.Should().ThrowAsync<ValidationAppException>();
    }

    [Fact]
    public async Task A_ward_officer_may_open_the_photo_of_a_report_in_their_own_ward()
    {
        var wardActors = new Mock<IWardActorResolver>();
        wardActors.Setup(a => a.ResolveAsync(50, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WardActor(50, WardId: 10, "Officer"));
        var reports = new Mock<IAiMarketplaceRepository>();
        reports.Setup(r => r.ReportEvidenceBelongsToWardAsync(
                EvidenceFiles.BuildUrl(8, FileName), 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var controller = Build(User(50, RoleCodes.WardAuthority).Object, wardActors.Object, vendorReports: reports.Object);

        // Storage has no such file, so reaching a 404 proves the access check passed.
        var result = await controller.Download(ownerUserId: 8, FileName, CancellationToken.None);

        result.Should().BeOfType<Microsoft.AspNetCore.Mvc.NotFoundResult>();
    }

    [Fact]
    public async Task A_ward_officer_cannot_open_the_photo_of_a_report_in_another_ward()
    {
        var wardActors = new Mock<IWardActorResolver>();
        wardActors.Setup(a => a.ResolveAsync(50, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WardActor(50, WardId: 11, "Officer"));
        var reports = new Mock<IAiMarketplaceRepository>();
        reports.Setup(r => r.ReportEvidenceBelongsToWardAsync(It.IsAny<string>(), 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var controller = Build(User(50, RoleCodes.WardAuthority).Object, wardActors.Object, vendorReports: reports.Object);

        var act = () => controller.Download(ownerUserId: 8, FileName, CancellationToken.None);

        await act.Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task A_ward_officer_may_open_the_treasury_receipt_of_a_fine_in_their_own_ward()
    {
        var wardActors = new Mock<IWardActorResolver>();
        wardActors.Setup(a => a.ResolveAsync(50, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WardActor(50, WardId: 10, "Officer"));
        var receipts = new Mock<IPenaltyReceiptRepository>();
        receipts.Setup(r => r.ReceiptBelongsToWardAsync(
                EvidenceFiles.BuildUrl(7, FileName), 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var controller = Build(User(50, RoleCodes.WardAuthority).Object, wardActors.Object, penaltyReceipts: receipts.Object);

        // Storage has no such file, so reaching a 404 proves the access check passed.
        var result = await controller.Download(ownerUserId: 7, FileName, CancellationToken.None);

        result.Should().BeOfType<Microsoft.AspNetCore.Mvc.NotFoundResult>();
    }

    [Fact]
    public async Task A_ward_officer_cannot_open_the_treasury_receipt_of_another_wards_fine()
    {
        var wardActors = new Mock<IWardActorResolver>();
        wardActors.Setup(a => a.ResolveAsync(50, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WardActor(50, WardId: 11, "Officer"));
        var receipts = new Mock<IPenaltyReceiptRepository>();
        receipts.Setup(r => r.ReceiptBelongsToWardAsync(It.IsAny<string>(), 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var controller = Build(User(50, RoleCodes.WardAuthority).Object, wardActors.Object, penaltyReceipts: receipts.Object);

        var act = () => controller.Download(ownerUserId: 7, FileName, CancellationToken.None);

        await act.Should().ThrowAsync<ForbiddenException>();
    }

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

        public Task<IReadOnlyList<StoredFile>> ListAsync(string folder, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> ExistsAsync(string relativePath, CancellationToken cancellationToken) =>
            Task.FromResult(Saved.ContainsKey(relativePath));

        public Task DeleteAsync(string relativePath, CancellationToken cancellationToken)
        {
            Saved.Remove(relativePath);
            return Task.CompletedTask;
        }
    }

    private static IFormFile Upload(byte[] bytes, string name) =>
        new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", name);

    [Fact]
    public async Task An_evidence_photo_is_stored_scaled_down_and_re_encoded()
    {
        // A phone photo (4000x3000 here) must not be kept at full size: it is re-encoded, its longest
        // edge capped, and the size in the answer is what was actually stored.
        byte[] original;
        using (var image = new Image<Rgb24>(4000, 3000, Color.SkyBlue))
        using (var buffer = new MemoryStream())
        {
            await image.SaveAsJpegAsync(buffer, new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder { Quality = 100 });
            original = buffer.ToArray();
        }

        var storage = new RecordingStorage();
        var controller = Build(User(8, RoleCodes.Customer).Object, storage: storage);

        var result = await controller.Upload(Upload(original, "photo.jpg"), CancellationToken.None);

        var stored = storage.Saved.Values.Should().ContainSingle().Subject;
        var info = Image.Identify(stored);
        info.Width.Should().Be(UploadsController.MaxEdgePixels);
        info.Height.Should().Be(1920);
        var body = result.Result.Should().BeOfType<Microsoft.AspNetCore.Mvc.OkObjectResult>().Subject.Value
            .Should().BeOfType<UploadedFileResponse>().Subject;
        body.SizeBytes.Should().Be(stored.Length);
        body.FileUrl.Should().EndWith(".jpg");
    }

    [Fact]
    public async Task An_evidence_pdf_is_stored_as_uploaded()
    {
        var pdf = "%PDF-1.4 test document"u8.ToArray();
        var storage = new RecordingStorage();
        var controller = Build(User(7, RoleCodes.Vendor).Object, storage: storage);

        await controller.Upload(Upload(pdf, "paper.pdf"), CancellationToken.None);

        storage.Saved.Values.Should().ContainSingle().Which.Should().Equal(pdf);
    }

    [Fact]
    public async Task A_file_that_only_looks_like_an_image_is_refused()
    {
        // JPEG magic bytes followed by garbage: passes the sniff, fails the decode, never stored.
        byte[] fake = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12];
        var storage = new RecordingStorage();
        var controller = Build(User(7, RoleCodes.Vendor).Object, storage: storage);

        var act = () => controller.Upload(Upload(fake, "photo.jpg"), CancellationToken.None);

        await act.Should().ThrowAsync<ValidationAppException>();
        storage.Saved.Should().BeEmpty();
    }
}
