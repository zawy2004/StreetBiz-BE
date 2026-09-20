using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Moq;
using StreetBiz.API.Controllers;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Features.WardSlots;

namespace StreetBiz.API.Tests;

public sealed class UploadsControllerTests
{
    private const string FileName = "0123456789abcdef0123456789abcdef.jpg";

    private static UploadsController Build(
        ICurrentUser currentUser,
        IWardActorResolver? wardActors = null,
        IBusinessRegistrationRepository? registrations = null,
        IFileStorage? storage = null) =>
        new(storage ?? Mock.Of<IFileStorage>(),
            currentUser,
            wardActors ?? Mock.Of<IWardActorResolver>(),
            registrations ?? Mock.Of<IBusinessRegistrationRepository>());

    private static Mock<ICurrentUser> User(long userId, string roleCode)
    {
        var currentUser = new Mock<ICurrentUser>();
        currentUser.SetupGet(c => c.UserId).Returns(userId);
        currentUser.SetupGet(c => c.RoleCode).Returns(roleCode);
        return currentUser;
    }

    [Fact]
    public async Task Only_vendor_accounts_may_upload_evidence()
    {
        var currentUser = new Mock<ICurrentUser>();
        currentUser.SetupGet(c => c.UserId).Returns(1);
        currentUser.SetupGet(c => c.RoleCode).Returns(RoleCodes.Customer);
        var controller = Build(currentUser.Object);

        // A CUSTOMER account has no legitimate use for REG-02 evidence, so the
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
}
