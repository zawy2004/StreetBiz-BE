using FluentAssertions;
using Moq;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Features.RentalContracts.GetContract;

namespace StreetBiz.Application.Tests;

public sealed class RentalContractTrackingHandlerTests
{
    private const long ContractId = 500;
    private const long OwnerVendorId = 70;
    private const long OtherVendorId = 71;

    private readonly Mock<IVendorContext> vendorContext = new();
    private readonly Mock<IRentalContractRepository> contracts = new();

    private static RentalContractRow Contract(long vendorId) => new(
        ContractId, 900, 1, "HQ-DH-01", "Khu vuc gan truong dai hoc", vendorId,
        DateOnly.FromDateTime(DateTime.UtcNow), DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)),
        ContractStatuses.Active, null, null, null, DateTime.UtcNow, null);

    [Fact]
    public async Task Viewing_another_vendors_contract_is_forbidden_not_not_found()
    {
        vendorContext.Setup(v => v.RequireVendorIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(OtherVendorId);
        contracts.Setup(c => c.GetByIdAsync(ContractId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Contract(OwnerVendorId));

        var handler = new GetContractQueryHandler(vendorContext.Object, contracts.Object);

        await FluentActions.Awaiting(() => handler.Handle(new GetContractQuery(ContractId), CancellationToken.None))
            .Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task Owner_can_view_their_own_contract()
    {
        vendorContext.Setup(v => v.RequireVendorIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(OwnerVendorId);
        contracts.Setup(c => c.GetByIdAsync(ContractId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Contract(OwnerVendorId));

        var handler = new GetContractQueryHandler(vendorContext.Object, contracts.Object);
        var result = await handler.Handle(new GetContractQuery(ContractId), CancellationToken.None);

        result.ContractId.Should().Be(ContractId);
    }

    [Fact]
    public async Task A_missing_contract_is_a_404()
    {
        vendorContext.Setup(v => v.RequireVendorIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(OwnerVendorId);
        contracts.Setup(c => c.GetByIdAsync(ContractId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((RentalContractRow?)null);

        var handler = new GetContractQueryHandler(vendorContext.Object, contracts.Object);

        await FluentActions.Awaiting(() => handler.Handle(new GetContractQuery(ContractId), CancellationToken.None))
            .Should().ThrowAsync<NotFoundException>();
    }
}
