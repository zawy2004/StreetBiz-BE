using FluentAssertions;
using Moq;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Features.RentalApplications.SubmitAdjacentApplication;
using StreetBiz.Application.Features.RentalApplications.SubmitOpenSlotApplication;

namespace StreetBiz.Application.Tests;

public sealed class RentalApplicationHandlerTests
{
    private const long RegistrationId = 700;
    private const long SlotId = 42;
    private const int WardId = 3;

    private readonly Mock<IVendorContext> vendorContext = new();
    private readonly Mock<ISidewalkSlotRepository> slots = new();
    private readonly Mock<IRentalApplicationRepository> applications = new();
    private readonly Mock<IRentalContractRepository> contracts = new();
    private readonly Mock<ISidewalkPolicy> sidewalkPolicy = new();

    public RentalApplicationHandlerTests()
    {
        sidewalkPolicy.Setup(p => p.AdjacentRadiusMeters).Returns(150);
    }

    private static SlotRow Slot(string status, decimal lat = 16.0130m, decimal lon = 108.2400m) => new(
        SlotId, "HQ-DH-01", 1, "Khu vuc gan truong dai hoc", WardId, lat, lon,
        2, 3, status, SlotSources.WardDefined, 25000, null, null);

    private static BizRegistration Registration(
        string vendorType, decimal? lat = 16.0130m, decimal? lon = 108.2400m) => new(
        RegistrationId, 70, vendorType, "Banh mi", "123 Le Van Hien", lat, lon, WardId,
        RegistrationStatuses.UnderReview, false, null, null, DateTime.UtcNow, null);

    private void Owns(BizRegistration registration) =>
        vendorContext.Setup(v => v.RequireOwnedRegistrationAsync(RegistrationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(registration);

    // ---- SIDE-03B: open-slot application ----

    [Fact]
    public async Task Submitting_for_a_slot_that_is_not_available_is_a_conflict()
    {
        Owns(Registration(VendorTypes.Itinerant));
        slots.Setup(s => s.GetByIdAsync(SlotId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Slot(SlotStatuses.PendingApplication));

        var handler = new SubmitOpenSlotApplicationCommandHandler(vendorContext.Object, slots.Object, applications.Object);
        var command = new SubmitOpenSlotApplicationCommand(RegistrationId, SlotId, 30);

        await FluentActions.Awaiting(() => handler.Handle(command, CancellationToken.None))
            .Should().ThrowAsync<ConflictException>().WithMessage(SideMessages.SlotNotAvailable);

        applications.Verify(a => a.CreateAsync(
            It.IsAny<long>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Submitting_for_a_slot_with_an_open_application_is_a_conflict()
    {
        Owns(Registration(VendorTypes.Itinerant));
        slots.Setup(s => s.GetByIdAsync(SlotId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Slot(SlotStatuses.Available));
        applications.Setup(a => a.HasOpenApplicationForSlotAsync(SlotId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var handler = new SubmitOpenSlotApplicationCommandHandler(vendorContext.Object, slots.Object, applications.Object);
        var command = new SubmitOpenSlotApplicationCommand(RegistrationId, SlotId, 30);

        await FluentActions.Awaiting(() => handler.Handle(command, CancellationToken.None))
            .Should().ThrowAsync<ConflictException>().WithMessage(SideMessages.ApplicationAlreadyOpenForSlot);
    }

    [Fact]
    public async Task A_fixed_storefront_vendor_may_still_apply_for_an_open_slot()
    {
        // Only SIDE-03A restricts by vendor type; SIDE-03B (open-slot) does not.
        Owns(Registration(VendorTypes.FixedStorefront));
        slots.Setup(s => s.GetByIdAsync(SlotId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Slot(SlotStatuses.Available));
        applications.Setup(a => a.CreateAsync(RegistrationId, SlotId, ApplicationMethods.ManualSelected, 30, It.IsAny<CancellationToken>()))
            .ReturnsAsync(900);
        applications.Setup(a => a.GetByIdAsync(900, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RentalApplicationRow(900, RegistrationId, SlotId, ApplicationMethods.ManualSelected, 30,
                ApplicationStatuses.Pending, null, null, DateTime.UtcNow, VendorId: 70));

        var handler = new SubmitOpenSlotApplicationCommandHandler(vendorContext.Object, slots.Object, applications.Object);
        var command = new SubmitOpenSlotApplicationCommand(RegistrationId, SlotId, 30);

        var result = await handler.Handle(command, CancellationToken.None);

        result.ApplicationMethod.Should().Be(ApplicationMethods.ManualSelected);
    }

    // ---- SIDE-03A: storefront-adjacent application ----

    [Fact]
    public async Task An_itinerant_vendor_cannot_apply_for_an_adjacent_slot()
    {
        Owns(Registration(VendorTypes.Itinerant));

        var handler = new SubmitAdjacentApplicationCommandHandler(
            vendorContext.Object, slots.Object, applications.Object, contracts.Object, sidewalkPolicy.Object);
        var command = new SubmitAdjacentApplicationCommand(RegistrationId, SlotId, 30);

        await FluentActions.Awaiting(() => handler.Handle(command, CancellationToken.None))
            .Should().ThrowAsync<DomainRuleException>().WithMessage(SideMessages.NotEligibleForAdjacent);

        slots.Verify(s => s.GetByIdAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_fixed_storefront_registration_with_no_address_on_file_cannot_measure_BR11()
    {
        Owns(Registration(VendorTypes.FixedStorefront, lat: null, lon: null));

        var handler = new SubmitAdjacentApplicationCommandHandler(
            vendorContext.Object, slots.Object, applications.Object, contracts.Object, sidewalkPolicy.Object);
        var command = new SubmitAdjacentApplicationCommand(RegistrationId, SlotId, 30);

        await FluentActions.Awaiting(() => handler.Handle(command, CancellationToken.None))
            .Should().ThrowAsync<DomainRuleException>().WithMessage(SideMessages.RegistrationMissingAddress);
    }

    [Fact]
    public async Task A_slot_outside_the_adjacency_radius_violates_BR11()
    {
        // Registration address vs. a slot roughly 480m away (see GeoMathTests) — well outside
        // the default 150m policy.
        Owns(Registration(VendorTypes.FixedStorefront, lat: 16.0132m, lon: 108.2398m));
        slots.Setup(s => s.GetByIdAsync(SlotId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Slot(SlotStatuses.Available, lat: 16.0155m, lon: 108.2437m));

        var handler = new SubmitAdjacentApplicationCommandHandler(
            vendorContext.Object, slots.Object, applications.Object, contracts.Object, sidewalkPolicy.Object);
        var command = new SubmitAdjacentApplicationCommand(RegistrationId, SlotId, 30);

        await FluentActions.Awaiting(() => handler.Handle(command, CancellationToken.None))
            .Should().ThrowAsync<DomainRuleException>().WithMessage(SideMessages.OutsideAdjacentRadius);
    }

    [Fact]
    public async Task A_registration_with_an_active_adjacent_contract_violates_BR12()
    {
        Owns(Registration(VendorTypes.FixedStorefront));
        slots.Setup(s => s.GetByIdAsync(SlotId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Slot(SlotStatuses.Available));
        contracts.Setup(c => c.HasActiveAdjacentContractAsync(RegistrationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var handler = new SubmitAdjacentApplicationCommandHandler(
            vendorContext.Object, slots.Object, applications.Object, contracts.Object, sidewalkPolicy.Object);
        var command = new SubmitAdjacentApplicationCommand(RegistrationId, SlotId, 30);

        await FluentActions.Awaiting(() => handler.Handle(command, CancellationToken.None))
            .Should().ThrowAsync<ConflictException>().WithMessage(SideMessages.AlreadyHasActiveAdjacentContract);

        applications.Verify(a => a.CreateAsync(
            It.IsAny<long>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_fixed_storefront_vendor_within_radius_can_submit_an_adjacent_application()
    {
        Owns(Registration(VendorTypes.FixedStorefront));
        slots.Setup(s => s.GetByIdAsync(SlotId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Slot(SlotStatuses.Available));
        contracts.Setup(c => c.HasActiveAdjacentContractAsync(RegistrationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        applications.Setup(a => a.CreateAsync(RegistrationId, SlotId, ApplicationMethods.AutoAdjacent, 30, It.IsAny<CancellationToken>()))
            .ReturnsAsync(901);
        applications.Setup(a => a.GetByIdAsync(901, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RentalApplicationRow(901, RegistrationId, SlotId, ApplicationMethods.AutoAdjacent, 30,
                ApplicationStatuses.Pending, null, null, DateTime.UtcNow, VendorId: 70));

        var handler = new SubmitAdjacentApplicationCommandHandler(
            vendorContext.Object, slots.Object, applications.Object, contracts.Object, sidewalkPolicy.Object);
        var command = new SubmitAdjacentApplicationCommand(RegistrationId, SlotId, 30);

        var result = await handler.Handle(command, CancellationToken.None);

        result.ApplicationMethod.Should().Be(ApplicationMethods.AutoAdjacent);
    }
}
