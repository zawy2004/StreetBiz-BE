using FluentAssertions;
using Moq;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Features.AddressChanges.RequestAddressChange;

namespace StreetBiz.Application.Tests;

public sealed class AddressChangeHandlerTests
{
    private const long RegistrationId = 700;
    private const long VendorId = 70;
    private const long OtherVendorId = 71;
    private const long ContractId = 500;

    private readonly Mock<IVendorContext> vendorContext = new();
    private readonly Mock<IAddressChangeRequestRepository> addressChanges = new();
    private readonly Mock<IRentalContractRepository> contracts = new();
    private readonly Mock<IGeocodingService> geocoding = new();

    public AddressChangeHandlerTests()
    {
        vendorContext.Setup(v => v.RequireVendorIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(VendorId);
    }

    private static BizRegistration Registration(string vendorType) => new(
        RegistrationId, VendorId, vendorType, "Banh mi", "123 Le Van Hien", 16.013m, 108.240m, 3,
        RegistrationStatuses.Approved, false, null, null, DateTime.UtcNow, null);

    private void Owns(string vendorType) =>
        vendorContext.Setup(v => v.RequireOwnedRegistrationAsync(RegistrationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Registration(vendorType));

    private static RequestAddressChangeCommand ValidCommand(long? releasedContractId = null) => new(
        RegistrationId, "456 New Street", 16.02m, 108.25m, releasedContractId, null);

    [Fact]
    public async Task An_itinerant_vendor_cannot_request_an_address_change()
    {
        Owns(VendorTypes.Itinerant);

        var handler = new RequestAddressChangeCommandHandler(
            vendorContext.Object, addressChanges.Object, contracts.Object, geocoding.Object);

        await FluentActions.Awaiting(() => handler.Handle(ValidCommand(), CancellationToken.None))
            .Should().ThrowAsync<DomainRuleException>().WithMessage(SideMessages.AddressChangeNotEligible);

        addressChanges.Verify(a => a.CreateAsync(It.IsAny<long>(), It.IsAny<NewAddressChangeRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_second_open_request_is_a_conflict()
    {
        Owns(VendorTypes.FixedStorefront);
        addressChanges.Setup(a => a.HasOpenAsync(RegistrationId, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var handler = new RequestAddressChangeCommandHandler(
            vendorContext.Object, addressChanges.Object, contracts.Object, geocoding.Object);

        await FluentActions.Awaiting(() => handler.Handle(ValidCommand(), CancellationToken.None))
            .Should().ThrowAsync<ConflictException>().WithMessage(SideMessages.AddressChangeAlreadyOpen);
    }

    [Fact]
    public async Task Releasing_a_contract_that_belongs_to_another_vendor_is_forbidden()
    {
        Owns(VendorTypes.FixedStorefront);
        addressChanges.Setup(a => a.HasOpenAsync(RegistrationId, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        contracts.Setup(c => c.GetByIdAsync(ContractId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RentalContractRow(ContractId, 900, 1, "HQ-DH-01", "Zone", OtherVendorId,
                DateOnly.FromDateTime(DateTime.UtcNow), DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)),
                ContractStatuses.Active, null, null, null, DateTime.UtcNow, null));

        var handler = new RequestAddressChangeCommandHandler(
            vendorContext.Object, addressChanges.Object, contracts.Object, geocoding.Object);

        await FluentActions.Awaiting(() => handler.Handle(ValidCommand(ContractId), CancellationToken.None))
            .Should().ThrowAsync<ForbiddenException>().WithMessage(SideMessages.ReleasedContractNotOwned);

        addressChanges.Verify(a => a.CreateAsync(It.IsAny<long>(), It.IsAny<NewAddressChangeRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Missing_coordinates_are_filled_in_from_geocoding_without_blocking_the_write()
    {
        Owns(VendorTypes.FixedStorefront);
        addressChanges.Setup(a => a.HasOpenAsync(RegistrationId, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        geocoding.Setup(g => g.ForwardAsync("456 New Street", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GeoPoint(16.05m, 108.30m, "456 New Street, Da Nang"));
        addressChanges.Setup(a => a.CreateAsync(RegistrationId,
                It.Is<NewAddressChangeRequest>(d => d.NewLatitude == 16.05m && d.NewLongitude == 108.30m),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        addressChanges.Setup(a => a.GetByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AddressChangeRequestRow(1, RegistrationId, "456 New Street", 16.05m, 108.30m,
                null, null, AddressChangeStatuses.Pending, null, null, DateTime.UtcNow));

        var command = new RequestAddressChangeCommand(RegistrationId, "456 New Street", null, null, null, null);
        var handler = new RequestAddressChangeCommandHandler(
            vendorContext.Object, addressChanges.Object, contracts.Object, geocoding.Object);

        var result = await handler.Handle(command, CancellationToken.None);

        result.NewLatitude.Should().Be(16.05m);
        result.NewLongitude.Should().Be(108.30m);
    }

    [Fact]
    public async Task A_failed_geocode_still_lets_the_request_through_with_null_coordinates()
    {
        Owns(VendorTypes.FixedStorefront);
        addressChanges.Setup(a => a.HasOpenAsync(RegistrationId, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        geocoding.Setup(g => g.ForwardAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((GeoPoint?)null);
        addressChanges.Setup(a => a.CreateAsync(RegistrationId,
                It.Is<NewAddressChangeRequest>(d => d.NewLatitude == null && d.NewLongitude == null),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(2);
        addressChanges.Setup(a => a.GetByIdAsync(2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AddressChangeRequestRow(2, RegistrationId, "456 New Street", null, null,
                null, null, AddressChangeStatuses.Pending, null, null, DateTime.UtcNow));

        var command = new RequestAddressChangeCommand(RegistrationId, "456 New Street", null, null, null, null);
        var handler = new RequestAddressChangeCommandHandler(
            vendorContext.Object, addressChanges.Object, contracts.Object, geocoding.Object);

        var result = await handler.Handle(command, CancellationToken.None);

        result.AddressChangeId.Should().Be(2);
    }
}
