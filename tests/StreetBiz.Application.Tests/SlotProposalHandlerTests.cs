using FluentAssertions;
using Moq;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Features.SlotProposals.ProposeSlot;

namespace StreetBiz.Application.Tests;

public sealed class SlotProposalHandlerTests
{
    private const long RegistrationId = 700;
    private const int ZoneId = 1;

    private readonly Mock<IVendorContext> vendorContext = new();
    private readonly Mock<ISidewalkSlotRepository> slots = new();

    private static ProposeSlotCommand ValidCommand() => new(
        RegistrationId, ZoneId, 16.0130m, 108.2400m, 2, 3, "https://example.test/evidence/photo.jpg");

    [Fact]
    public void Proposing_without_a_photo_is_a_validation_error()
    {
        var command = ValidCommand() with { ProposalPhotoUrl = "" };
        var validator = new ProposeSlotCommandValidator();

        var result = validator.Validate(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage == SideMessages.ProposalPhotoRequired);
    }

    [Fact]
    public async Task Proposing_for_an_unknown_zone_is_a_400_not_a_foreign_key_500()
    {
        slots.Setup(s => s.ZoneExistsAsync(ZoneId, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var handler = new ProposeSlotCommandHandler(vendorContext.Object, slots.Object);

        var error = await FluentActions.Awaiting(() => handler.Handle(ValidCommand(), CancellationToken.None))
            .Should().ThrowAsync<ValidationAppException>();

        error.Which.Errors.Should().ContainKey("ZoneId");
        slots.Verify(s => s.ProposeAsync(It.IsAny<long>(), It.IsAny<NewSlotProposal>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_slot_code_collision_is_retried_and_then_succeeds()
    {
        slots.Setup(s => s.ZoneExistsAsync(ZoneId, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        slots.SetupSequence(s => s.ProposeAsync(RegistrationId, It.IsAny<NewSlotProposal>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ConflictException(SideMessages.SlotCodeGenerationFailed))
            .ReturnsAsync(55);
        slots.Setup(s => s.GetProposalByIdAsync(55, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SlotProposalRow(55, "VP-1-x", 16.0130m, 108.2400m,
                ProposalReviewStatuses.Pending, "https://example.test/evidence/photo.jpg", null, DateTime.UtcNow));

        var handler = new ProposeSlotCommandHandler(vendorContext.Object, slots.Object);
        var result = await handler.Handle(ValidCommand(), CancellationToken.None);

        result.SlotId.Should().Be(55);
        slots.Verify(s => s.ProposeAsync(RegistrationId, It.IsAny<NewSlotProposal>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Exhausting_all_retries_surfaces_as_a_conflict()
    {
        slots.Setup(s => s.ZoneExistsAsync(ZoneId, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        slots.Setup(s => s.ProposeAsync(RegistrationId, It.IsAny<NewSlotProposal>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ConflictException(SideMessages.SlotCodeGenerationFailed));

        var handler = new ProposeSlotCommandHandler(vendorContext.Object, slots.Object);

        await FluentActions.Awaiting(() => handler.Handle(ValidCommand(), CancellationToken.None))
            .Should().ThrowAsync<ConflictException>().WithMessage(SideMessages.SlotCodeGenerationFailed);
    }

    [Fact]
    public async Task A_valid_proposal_succeeds_on_the_first_attempt()
    {
        slots.Setup(s => s.ZoneExistsAsync(ZoneId, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        slots.Setup(s => s.ProposeAsync(RegistrationId, It.IsAny<NewSlotProposal>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(56);
        slots.Setup(s => s.GetProposalByIdAsync(56, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SlotProposalRow(56, "VP-1-x", 16.0130m, 108.2400m,
                ProposalReviewStatuses.Pending, "https://example.test/evidence/photo.jpg", null, DateTime.UtcNow));

        var handler = new ProposeSlotCommandHandler(vendorContext.Object, slots.Object);
        var result = await handler.Handle(ValidCommand(), CancellationToken.None);

        result.ProposalReviewStatus.Should().Be(ProposalReviewStatuses.Pending);
        slots.Verify(s => s.ProposeAsync(RegistrationId, It.IsAny<NewSlotProposal>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
