using FluentAssertions;
using Moq;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Features.VendorKyc;
using StreetBiz.Application.Features.VendorRegistration.SubmitEvidence;
using StreetBiz.Application.Features.VendorRegistration.SubmitRegistration;
using StreetBiz.Application.Features.VendorRegistration.UpdateRegistration;

namespace StreetBiz.Application.Tests;

public sealed class VendorRegistrationHandlerTests
{
    private const long UserId = 7;
    private const long VendorId = 70;
    private const long RegistrationId = 700;
    private const int WardId = 10;

    private readonly Mock<IVendorContext> vendorContext = new();
    private readonly Mock<ICurrentUser> submitCurrentUser = new();
    private readonly Mock<IBusinessRegistrationRepository> registrations = new();
    private readonly Mock<IKycResultRepository> kycResults = new();
    private readonly Mock<IAdministrativeUnitRepository> units = new();

    public VendorRegistrationHandlerTests()
    {
        vendorContext.Setup(v => v.RequireVendorIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(VendorId);
        units.Setup(u => u.IsWardAsync(WardId, It.IsAny<CancellationToken>())).ReturnsAsync(true);
    }

    private static BizRegistration Registration(string status) => new(
        RegistrationId, VendorId, VendorTypes.Itinerant, "Banh mi", null, null, null, WardId,
        status, false, null, null, DateTime.UtcNow, null);

    private void Owns(string status) =>
        vendorContext.Setup(v => v.RequireOwnedRegistrationAsync(RegistrationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Registration(status));

    [Fact]
    public async Task Submitting_with_an_unknown_ward_is_a_400_not_a_foreign_key_500()
    {
        var handler = new SubmitRegistrationCommandHandler(
            vendorContext.Object, submitCurrentUser.Object, registrations.Object, kycResults.Object, units.Object);
        var command = new SubmitRegistrationCommand(VendorTypes.Itinerant, "Banh mi", null, null, null, WardUnitId: 999);

        var error = await FluentActions.Awaiting(() => handler.Handle(command, CancellationToken.None))
            .Should().ThrowAsync<ValidationAppException>();

        error.Which.Errors.Should().ContainKey("WardUnitId");
        registrations.Verify(r => r.CreateAsync(It.IsAny<long>(), It.IsAny<NewBizRegistration>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Resubmitting_while_another_application_is_in_review_violates_BR09()
    {
        Owns(RegistrationStatuses.MoreInformationRequired);
        registrations.Setup(r => r.HasActivePendingAsync(VendorId, It.IsAny<CancellationToken>(), RegistrationId))
            .ReturnsAsync(true);
        var handler = new UpdateRegistrationCommandHandler(vendorContext.Object, registrations.Object, units.Object);
        var command = new UpdateRegistrationCommand(RegistrationId, VendorTypes.Itinerant, "Banh mi", null, null, null, WardId);

        await FluentActions.Awaiting(() => handler.Handle(command, CancellationToken.None))
            .Should().ThrowAsync<ConflictException>().WithMessage(RegMessages.DuplicatePending);

        registrations.Verify(r => r.UpdateAndResubmitAsync(It.IsAny<long>(), It.IsAny<NewBizRegistration>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Updating_a_submitted_registration_does_not_count_itself_as_a_duplicate()
    {
        Owns(RegistrationStatuses.Submitted);
        registrations.Setup(r => r.GetByIdAsync(RegistrationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Registration(RegistrationStatuses.Submitted));
        var handler = new UpdateRegistrationCommandHandler(vendorContext.Object, registrations.Object, units.Object);
        var command = new UpdateRegistrationCommand(RegistrationId, VendorTypes.Itinerant, "Banh mi 2", null, null, null, WardId);

        await handler.Handle(command, CancellationToken.None);

        registrations.Verify(r => r.HasActivePendingAsync(VendorId, It.IsAny<CancellationToken>(), RegistrationId), Times.Once);
        registrations.Verify(r => r.UpdateAndResubmitAsync(RegistrationId, It.IsAny<NewBizRegistration>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(RegistrationStatuses.Approved)]
    [InlineData(RegistrationStatuses.Withdrawn)]
    [InlineData(RegistrationStatuses.UnderReview)]
    public async Task Evidence_cannot_be_added_once_the_registration_is_not_editable(string status)
    {
        Owns(status);
        var handler = EvidenceHandler(fileExists: true);

        await FluentActions.Awaiting(() => handler.Handle(Evidence(UserId), CancellationToken.None))
            .Should().ThrowAsync<DomainRuleException>();
    }

    [Fact]
    public async Task Evidence_must_be_a_file_the_caller_uploaded()
    {
        Owns(RegistrationStatuses.Submitted);
        var handler = EvidenceHandler(fileExists: true);

        await FluentActions.Awaiting(() => handler.Handle(Evidence(ownerUserId: 99), CancellationToken.None))
            .Should().ThrowAsync<ValidationAppException>();

        registrations.Verify(r => r.AddEvidenceAsync(It.IsAny<long>(), It.IsAny<NewRegistrationEvidence>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Evidence_is_attached_for_an_editable_registration_and_an_owned_file()
    {
        Owns(RegistrationStatuses.MoreInformationRequired);
        registrations.Setup(r => r.AddEvidenceAsync(RegistrationId, It.IsAny<NewRegistrationEvidence>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        var handler = EvidenceHandler(fileExists: true);

        var result = await handler.Handle(Evidence(UserId), CancellationToken.None);

        result.EvidenceId.Should().Be(1);
    }

    [Fact]
    public void Evidence_validator_rejects_browser_blob_urls()
    {
        var validator = new SubmitEvidenceCommandValidator();

        validator.Validate(new SubmitEvidenceCommand(RegistrationId, EvidenceTypes.IdentityDocument, "blob:http://localhost:5173/x", null))
            .IsValid.Should().BeFalse();
        validator.Validate(Evidence(UserId)).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Submit_validator_gives_a_readable_message_for_a_non_positive_ward_id(int wardUnitId)
    {
        var validator = new SubmitRegistrationCommandValidator();
        var command = new SubmitRegistrationCommand(VendorTypes.Itinerant, "Banh mi", null, null, null, wardUnitId);

        var result = validator.Validate(command);

        result.Errors.Should().ContainSingle(e => e.PropertyName == nameof(SubmitRegistrationCommand.WardUnitId))
            .Which.ErrorMessage.Should().Be(AppMessages.InvalidWard);
    }

    [Fact]
    public void Update_validator_matches_submit_validator_on_an_empty_display_name()
    {
        var validator = new UpdateRegistrationCommandValidator();
        var command = new UpdateRegistrationCommand(RegistrationId, VendorTypes.Itinerant, "  ", null, null, null, WardId);

        var result = validator.Validate(command);

        result.Errors.Should().ContainSingle(e => e.PropertyName == nameof(UpdateRegistrationCommand.DisplayName))
            .Which.ErrorMessage.Should().Be(RegMessages.DisplayNameRequired);
    }

    [Fact]
    public void Submit_validator_requires_the_Mau_so_01_owner_and_business_fields()
    {
        // TT 68/2025/TT-BTC Mẫu số 01: chủ hộ kinh doanh + ngành nghề + cam kết ATTP are all
        // required on a real submission, even though the record itself defaults them to null
        // so older callers (e.g. this file's other tests) keep compiling.
        var validator = new SubmitRegistrationCommandValidator();
        var command = new SubmitRegistrationCommand(VendorTypes.Itinerant, "Banh mi", null, null, null, WardId);

        var result = validator.Validate(command);

        Assert.False(result.IsValid);
        result.Errors.Should().Contain(e => e.PropertyName == nameof(SubmitRegistrationCommand.OwnerDateOfBirth));
        result.Errors.Should().Contain(e => e.PropertyName == nameof(SubmitRegistrationCommand.OwnerGender));
        result.Errors.Should().Contain(e => e.PropertyName == nameof(SubmitRegistrationCommand.IdType));
        result.Errors.Should().Contain(e => e.PropertyName == nameof(SubmitRegistrationCommand.PermanentAddress));
        result.Errors.Should().Contain(e => e.PropertyName == nameof(SubmitRegistrationCommand.BusinessLine));
        result.Errors.Should().Contain(e => e.PropertyName == nameof(SubmitRegistrationCommand.CapitalAmount));
        result.Errors.Should().Contain(e => e.PropertyName == nameof(SubmitRegistrationCommand.FoodSafetyCommitment));
    }

    [Fact]
    public void Submit_validator_passes_once_every_Mau_so_01_field_is_filled()
    {
        var validator = new SubmitRegistrationCommandValidator();
        var command = new SubmitRegistrationCommand(
            VendorTypes.Itinerant, "Banh mi", null, null, null, WardId,
            OwnerDateOfBirth: new DateOnly(1985, 1, 1),
            OwnerGender: OwnerGenders.Female,
            OwnerEthnicity: "Kinh",
            OwnerNationality: "Việt Nam",
            IdType: OwnerIdTypes.CitizenId,
            IdIssuedDate: new DateOnly(2021, 1, 1),
            IdIssuedPlace: "Cục Cảnh sát QLHC về TTXH",
            PermanentAddress: "12 Le Duan, Da Nang",
            ContactAddress: null,
            BusinessLine: "Bán đồ ăn lưu động",
            BusinessLineCode: null,
            CapitalAmount: 20_000_000m,
            LaborCount: 1,
            PlannedStartDate: new DateOnly(2026, 10, 1),
            FoodSafetyCommitment: true);

        validator.Validate(command).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Update_validator_gives_a_readable_message_for_a_non_positive_ward_id()
    {
        var validator = new UpdateRegistrationCommandValidator();
        var command = new UpdateRegistrationCommand(RegistrationId, VendorTypes.Itinerant, "Banh mi", null, null, null, WardUnitId: 0);

        var result = validator.Validate(command);

        result.Errors.Should().ContainSingle(e => e.PropertyName == nameof(UpdateRegistrationCommand.WardUnitId))
            .Which.ErrorMessage.Should().Be(AppMessages.InvalidWard);
    }

    private SubmitEvidenceCommandHandler EvidenceHandler(bool fileExists)
    {
        var currentUser = new Mock<ICurrentUser>();
        currentUser.SetupGet(c => c.UserId).Returns(UserId);
        var storage = new Mock<IFileStorage>();
        storage.Setup(s => s.ExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(fileExists);
        return new SubmitEvidenceCommandHandler(vendorContext.Object, currentUser.Object, storage.Object, registrations.Object);
    }

    private static SubmitEvidenceCommand Evidence(long ownerUserId) => new(
        RegistrationId,
        EvidenceTypes.IdentityDocument,
        EvidenceFiles.BuildUrl(ownerUserId, "0123456789abcdef0123456789abcdef.jpg"),
        null);
}
