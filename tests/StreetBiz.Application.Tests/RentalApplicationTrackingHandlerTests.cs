using FluentAssertions;
using Moq;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Features.RentalApplications.GetApplication;
using StreetBiz.Application.Features.RentalApplications.WithdrawApplication;

namespace StreetBiz.Application.Tests;

public sealed class RentalApplicationTrackingHandlerTests
{
    private const long ApplicationId = 900;
    private const long OwnerVendorId = 70;
    private const long OtherVendorId = 71;

    private readonly Mock<IVendorContext> vendorContext = new();
    private readonly Mock<IRentalApplicationRepository> applications = new();

    private static RentalApplicationRow Application(string status, long vendorId) => new(
        ApplicationId, 700, 1, ApplicationMethods.ManualSelected, 30, status, null, null, DateTime.UtcNow, vendorId);

    [Fact]
    public async Task Viewing_another_vendors_application_is_forbidden_not_not_found()
    {
        vendorContext.Setup(v => v.RequireVendorIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(OtherVendorId);
        applications.Setup(a => a.GetByIdAsync(ApplicationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Application(ApplicationStatuses.Pending, OwnerVendorId));

        var handler = new GetApplicationQueryHandler(vendorContext.Object, applications.Object);

        await FluentActions.Awaiting(() => handler.Handle(new GetApplicationQuery(ApplicationId), CancellationToken.None))
            .Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task Withdrawing_another_vendors_application_is_forbidden()
    {
        vendorContext.Setup(v => v.RequireVendorIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(OtherVendorId);
        applications.Setup(a => a.GetByIdAsync(ApplicationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Application(ApplicationStatuses.Pending, OwnerVendorId));

        var handler = new WithdrawApplicationCommandHandler(vendorContext.Object, applications.Object);

        await FluentActions.Awaiting(() => handler.Handle(new WithdrawApplicationCommand(ApplicationId), CancellationToken.None))
            .Should().ThrowAsync<ForbiddenException>();

        applications.Verify(a => a.SetStatusAsync(It.IsAny<long>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Withdrawing_an_already_approved_application_is_a_conflict()
    {
        vendorContext.Setup(v => v.RequireVendorIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(OwnerVendorId);
        applications.Setup(a => a.GetByIdAsync(ApplicationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Application(ApplicationStatuses.Approved, OwnerVendorId));

        var handler = new WithdrawApplicationCommandHandler(vendorContext.Object, applications.Object);

        await FluentActions.Awaiting(() => handler.Handle(new WithdrawApplicationCommand(ApplicationId), CancellationToken.None))
            .Should().ThrowAsync<ConflictException>().WithMessage(SideMessages.ApplicationNotWithdrawable);
    }

    [Fact]
    public async Task Owner_can_withdraw_a_pending_application()
    {
        vendorContext.Setup(v => v.RequireVendorIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(OwnerVendorId);
        applications.Setup(a => a.GetByIdAsync(ApplicationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Application(ApplicationStatuses.Pending, OwnerVendorId));

        var handler = new WithdrawApplicationCommandHandler(vendorContext.Object, applications.Object);
        await handler.Handle(new WithdrawApplicationCommand(ApplicationId), CancellationToken.None);

        applications.Verify(a => a.SetStatusAsync(ApplicationId, ApplicationStatuses.Withdrawn, It.IsAny<CancellationToken>()), Times.Once);
    }
}
