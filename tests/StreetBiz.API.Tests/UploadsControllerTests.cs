using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Moq;
using StreetBiz.API.Controllers;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;

namespace StreetBiz.API.Tests;

public sealed class UploadsControllerTests
{
    [Fact]
    public async Task Only_vendor_accounts_may_upload_evidence()
    {
        var currentUser = new Mock<ICurrentUser>();
        currentUser.SetupGet(c => c.UserId).Returns(1);
        currentUser.SetupGet(c => c.RoleCode).Returns(RoleCodes.Customer);
        var controller = new UploadsController(Mock.Of<IFileStorage>(), currentUser.Object);

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
        var controller = new UploadsController(Mock.Of<IFileStorage>(), currentUser.Object);

        var act = () => controller.Upload(file: null, CancellationToken.None);

        await act.Should().ThrowAsync<ValidationAppException>();
    }
}
