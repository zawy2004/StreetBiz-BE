using FluentAssertions;
using Moq;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Features.DigitalPermits.GetPermit;

namespace StreetBiz.Application.Tests;

public sealed class DigitalPermitHandlerTests
{
    private const long ContractId = 500;
    private const long OwnerVendorId = 70;
    private const long OtherVendorId = 71;

    private readonly Mock<IVendorContext> vendorContext = new();
    private readonly Mock<IDigitalPermitRepository> permits = new();

    private static PermitValidityRow Permit(string effectiveStatus, long vendorId = OwnerVendorId) => new(
        1, ContractId, "SBP1.abc.def", 2, vendorId,
        DateOnly.FromDateTime(DateTime.UtcNow), DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)),
        PermitStatuses.Active, ContractStatuses.Active, effectiveStatus);

    [Fact]
    public async Task No_permit_issued_yet_is_a_404_not_an_empty_success()
    {
        vendorContext.Setup(v => v.RequireVendorIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(OwnerVendorId);
        permits.Setup(p => p.GetValidityByContractAsync(ContractId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((PermitValidityRow?)null);

        var handler = new GetPermitQueryHandler(vendorContext.Object, permits.Object);

        await FluentActions.Awaiting(() => handler.Handle(new GetPermitQuery(ContractId), CancellationToken.None))
            .Should().ThrowAsync<NotFoundException>().WithMessage(SideMessages.PermitNotFound);
    }

    [Fact]
    public async Task Viewing_another_vendors_permit_is_forbidden()
    {
        vendorContext.Setup(v => v.RequireVendorIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(OtherVendorId);
        permits.Setup(p => p.GetValidityByContractAsync(ContractId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Permit(PermitEffectiveStatuses.Valid));

        var handler = new GetPermitQueryHandler(vendorContext.Object, permits.Object);

        await FluentActions.Awaiting(() => handler.Handle(new GetPermitQuery(ContractId), CancellationToken.None))
            .Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task Owner_sees_the_effective_status_not_just_permit_status()
    {
        // A vendor who returned their slot: permit_status still ACTIVE, but the view already
        // derived SUSPENDED/REVOKED/EXPIRED from the contract underneath. The handler must pass
        // that through untouched rather than re-deriving it from permit_status.
        vendorContext.Setup(v => v.RequireVendorIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(OwnerVendorId);
        permits.Setup(p => p.GetValidityByContractAsync(ContractId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Permit(PermitEffectiveStatuses.Expired));

        var handler = new GetPermitQueryHandler(vendorContext.Object, permits.Object);
        var result = await handler.Handle(new GetPermitQuery(ContractId), CancellationToken.None);

        result.EffectiveStatus.Should().Be(PermitEffectiveStatuses.Expired);
        result.PermitStatus.Should().Be(PermitStatuses.Active);
    }
}
