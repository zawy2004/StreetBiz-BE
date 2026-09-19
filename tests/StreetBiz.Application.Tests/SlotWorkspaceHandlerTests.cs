using FluentAssertions;
using Moq;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.Sidewalk;
using StreetBiz.Application.Features.RentalApplications.SubmitOpenSlotApplication;
using StreetBiz.Application.Features.SidewalkSlots.GetSlotQuote;
using StreetBiz.Application.Features.SlotHolds.CreateSlotHold;
using StreetBiz.Application.Features.SlotHolds.ReleaseSlotHold;

namespace StreetBiz.Application.Tests;

public sealed class SlotWorkspaceHandlerTests
{
    private const long RegistrationId = 700;
    private const long OtherRegistrationId = 701;
    private const long SlotId = 42;
    private static readonly DateTime Now = new(2026, 9, 19, 8, 0, 0, DateTimeKind.Utc);

    private readonly Mock<IVendorContext> vendorContext = new();
    private readonly Mock<ISidewalkSlotRepository> slots = new();
    private readonly Mock<IRentalApplicationRepository> applications = new();
    private readonly Mock<ISlotHoldRepository> holds = new();
    private readonly Mock<ISidewalkPolicy> policy = new();
    private readonly Mock<IDateTimeProvider> clock = new();

    public SlotWorkspaceHandlerTests()
    {
        clock.Setup(c => c.UtcNow).Returns(Now);
        policy.Setup(p => p.SlotHoldTtlMinutes).Returns(15);
        policy.Setup(p => p.MaxSlotHoldsPerRegistration).Returns(3);
        slots.Setup(s => s.GetByIdAsync(SlotId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Slot(SlotStatuses.Available));
        holds.Setup(h => h.ListActiveByRegistrationAsync(RegistrationId, Now, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
    }

    private static SlotRow Slot(string status) => new(
        SlotId, "NVL-01", 1, "Zone", 3, 16.06m, 108.21m, 2, 3, status, SlotSources.WardDefined,
        25000, null, null, null, false, false, false, null, null, null);

    private CreateSlotHoldCommandHandler CreateHandler() =>
        new(vendorContext.Object, slots.Object, applications.Object, holds.Object, policy.Object, clock.Object);

    private SubmitOpenSlotApplicationCommandHandler OpenSlotHandler() =>
        new(vendorContext.Object, slots.Object, applications.Object, holds.Object, clock.Object);

    private static SlotHoldRow Hold(long registrationId) =>
        new(SlotId, registrationId, Now.AddMinutes(-5), Now.AddMinutes(10));

    // ---- fee quote ----

    [Fact]
    public void A_quote_multiplies_per_day_lines_by_the_term_and_charges_per_term_lines_once()
    {
        var components = new List<FeeComponentRow>
        {
            new(1, "Cleaning", FeeBases.PerDay, 3000, 1),
            new(2, "Deposit", FeeBases.PerTerm, 500000, 2),
        };

        var quote = FeeQuoteCalculator.Calculate(SlotId, 25000, 90, components);

        quote.Lines.Should().HaveCount(3);
        quote.Lines[0].Should().Be(new FeeQuoteLineDto("RENT", null, FeeBases.PerDay, 25000, 90, 2_250_000));
        quote.Lines[1].Amount.Should().Be(270_000);
        quote.Lines[2].Amount.Should().Be(500_000);
        quote.Total.Should().Be(3_020_000);
    }

    [Fact]
    public void A_zone_without_fee_components_quotes_the_rent_alone()
    {
        var quote = FeeQuoteCalculator.Calculate(SlotId, 25000, 10, []);

        quote.Lines.Should().ContainSingle();
        quote.Total.Should().Be(250_000);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(366)]
    public void A_quote_term_outside_1_to_365_days_is_rejected(int termDays)
    {
        new GetSlotQuoteQueryValidator().Validate(new GetSlotQuoteQuery(SlotId, termDays)).IsValid.Should().BeFalse();
    }

    // ---- holds ----

    [Fact]
    public async Task Holding_an_available_slot_reserves_it_for_the_configured_minutes()
    {
        var result = await CreateHandler().Handle(new CreateSlotHoldCommand(RegistrationId, SlotId), CancellationToken.None);

        result.ExpiresAt.Should().Be(Now.AddMinutes(15));
        holds.Verify(h => h.UpsertAsync(SlotId, RegistrationId, Now, Now.AddMinutes(15), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_slot_that_is_not_available_cannot_be_held()
    {
        slots.Setup(s => s.GetByIdAsync(SlotId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Slot(SlotStatuses.Active));

        await FluentActions.Awaiting(() => CreateHandler().Handle(new CreateSlotHoldCommand(RegistrationId, SlotId), default))
            .Should().ThrowAsync<ConflictException>().WithMessage(SideMessages.SlotNotAvailable);
    }

    [Fact]
    public async Task A_slot_with_an_open_application_cannot_be_held()
    {
        applications.Setup(a => a.HasOpenApplicationForSlotAsync(SlotId, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        await FluentActions.Awaiting(() => CreateHandler().Handle(new CreateSlotHoldCommand(RegistrationId, SlotId), default))
            .Should().ThrowAsync<ConflictException>().WithMessage(SideMessages.ApplicationAlreadyOpenForSlot);
    }

    [Fact]
    public async Task A_slot_held_by_another_registration_cannot_be_held()
    {
        holds.Setup(h => h.GetActiveBySlotAsync(SlotId, Now, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Hold(OtherRegistrationId));

        await FluentActions.Awaiting(() => CreateHandler().Handle(new CreateSlotHoldCommand(RegistrationId, SlotId), default))
            .Should().ThrowAsync<ConflictException>().WithMessage(SideMessages.SlotHeldByAnother);

        holds.Verify(h => h.UpsertAsync(
            It.IsAny<long>(), It.IsAny<long>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_fourth_hold_is_refused_but_renewing_one_of_the_three_is_not()
    {
        holds.Setup(h => h.ListActiveByRegistrationAsync(RegistrationId, Now, It.IsAny<CancellationToken>()))
            .ReturnsAsync([Hold(RegistrationId), Hold(RegistrationId), Hold(RegistrationId)]);

        await FluentActions.Awaiting(() => CreateHandler().Handle(new CreateSlotHoldCommand(RegistrationId, SlotId), default))
            .Should().ThrowAsync<ConflictException>().WithMessage(SideMessages.SlotHoldLimitReached);

        holds.Setup(h => h.GetActiveBySlotAsync(SlotId, Now, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Hold(RegistrationId));

        var renewed = await CreateHandler().Handle(new CreateSlotHoldCommand(RegistrationId, SlotId), default);

        renewed.ExpiresAt.Should().Be(Now.AddMinutes(15));
    }

    [Fact]
    public async Task Releasing_a_hold_the_caller_does_not_own_is_not_found()
    {
        holds.Setup(h => h.ReleaseAsync(SlotId, RegistrationId, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var handler = new ReleaseSlotHoldCommandHandler(vendorContext.Object, holds.Object);

        await FluentActions.Awaiting(() => handler.Handle(new ReleaseSlotHoldCommand(RegistrationId, SlotId), default))
            .Should().ThrowAsync<NotFoundException>().WithMessage(SideMessages.SlotHoldNotFound);
    }

    [Fact]
    public void Hold_times_are_serialized_as_utc_even_when_the_database_returns_unspecified_kind()
    {
        var unspecified = DateTime.SpecifyKind(Now.AddMinutes(10), DateTimeKind.Unspecified);

        new SlotHoldRow(SlotId, RegistrationId, unspecified, unspecified).ToDto().ExpiresAt.Kind.Should().Be(DateTimeKind.Utc);
        (Slot(SlotStatuses.Available) with { HoldExpiresAt = unspecified }).ToDto(null).HoldExpiresAt!.Value.Kind
            .Should().Be(DateTimeKind.Utc);
    }

    // ---- open-slot application: commitments and holds ----

    [Fact]
    public void An_application_without_accepted_commitments_is_invalid()
    {
        var validator = new SubmitOpenSlotApplicationCommandValidator();

        validator.Validate(new SubmitOpenSlotApplicationCommand(RegistrationId, SlotId, 30, false)).IsValid.Should().BeFalse();
        validator.Validate(new SubmitOpenSlotApplicationCommand(RegistrationId, SlotId, 30, true)).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task An_application_for_a_slot_held_by_another_registration_is_a_conflict()
    {
        holds.Setup(h => h.GetActiveBySlotAsync(SlotId, Now, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Hold(OtherRegistrationId));

        await FluentActions.Awaiting(() => OpenSlotHandler().Handle(new SubmitOpenSlotApplicationCommand(RegistrationId, SlotId, 30, true), default))
            .Should().ThrowAsync<ConflictException>().WithMessage(SideMessages.SlotHeldByAnother);

        applications.Verify(a => a.CreateAsync(
            It.IsAny<long>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Submitting_stores_the_commitment_time_and_releases_the_own_hold()
    {
        holds.Setup(h => h.GetActiveBySlotAsync(SlotId, Now, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Hold(RegistrationId));
        applications.Setup(a => a.CreateAsync(RegistrationId, SlotId, ApplicationMethods.ManualSelected, 30, Now, It.IsAny<CancellationToken>()))
            .ReturnsAsync(900);
        applications.Setup(a => a.GetByIdAsync(900, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RentalApplicationRow(900, RegistrationId, SlotId, ApplicationMethods.ManualSelected, 30,
                ApplicationStatuses.Pending, null, null, Now, VendorId: 70));

        await OpenSlotHandler().Handle(new SubmitOpenSlotApplicationCommand(RegistrationId, SlotId, 30, true), default);

        applications.Verify(a => a.CreateAsync(RegistrationId, SlotId, ApplicationMethods.ManualSelected, 30, Now, It.IsAny<CancellationToken>()), Times.Once);
        holds.Verify(h => h.ReleaseAsync(SlotId, RegistrationId, It.IsAny<CancellationToken>()), Times.Once);
    }
}
