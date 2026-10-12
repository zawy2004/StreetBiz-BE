using FluentAssertions;
using Moq;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Features.VendorRegistration.SubmitRegistration;
using StreetBiz.Application.Features.VendorRegistration.UpdateRegistration;
using StreetBiz.Application.Features.VendorRegistration.WithdrawRegistration;

namespace StreetBiz.Application.Tests;

public sealed class RegistrationRulesTests
{
    [Theory]
    [InlineData("SUBMITTED", "UNDER_REVIEW", true)]
    [InlineData("SUBMITTED", "APPROVED", true)]
    [InlineData("UNDER_REVIEW", "MORE_INFORMATION_REQUIRED", true)]
    [InlineData("MORE_INFORMATION_REQUIRED", "SUBMITTED", true)]
    [InlineData("APPROVED", "WITHDRAWN", true)]
    [InlineData("WITHDRAWN", "APPROVED", false)]
    [InlineData("REJECTED", "APPROVED", false)]
    [InlineData("APPROVED", "REJECTED", false)]
    [InlineData("MORE_INFORMATION_REQUIRED", "APPROVED", false)]
    [InlineData("DRAFT", "APPROVED", false)]
    [InlineData("UNDER_REVIEW", "SUBMITTED", false)]
    public void Transition_table_allows_only_the_documented_moves(string from, string to, bool allowed) =>
        RegistrationStatuses.CanTransition(from, to).Should().Be(allowed);

    [Fact]
    public void Terminal_statuses_have_no_way_out()
    {
        foreach (var terminal in new[] { RegistrationStatuses.Rejected, RegistrationStatuses.Withdrawn })
        {
            RegistrationStatuses.SourcesOf(terminal).Should().NotContain(terminal);
            foreach (var to in new[] { "SUBMITTED", "UNDER_REVIEW", "APPROVED", "MORE_INFORMATION_REQUIRED" })
            {
                RegistrationStatuses.CanTransition(terminal, to).Should().BeFalse();
            }
        }
    }

    private static UpdateRegistrationCommand ValidUpdate() => new(
        1, VendorTypes.Itinerant, "Hộ A", null, 16.05m, 108.2m, 1,
        OwnerDateOfBirth: new DateOnly(1990, 1, 1), OwnerGender: OwnerGenders.Male,
        OwnerNationality: "Việt Nam", IdType: OwnerIdTypes.All[0], IdIssuedDate: new DateOnly(2021, 1, 1),
        IdIssuedPlace: "Cục CSQLHC", PermanentAddress: "1 Lê Lợi", BusinessLine: "Bán nước",
        CapitalAmount: 1000, LaborCount: 1, PlannedStartDate: new DateOnly(2026, 11, 1),
        FoodSafetyCommitment: true);

    [Fact]
    public void A_complete_update_is_valid()
        => new UpdateRegistrationCommandValidator().Validate(ValidUpdate()).IsValid.Should().BeTrue();

    [Fact]
    public void An_update_needs_the_food_safety_commitment_just_like_a_submission()
        => new UpdateRegistrationCommandValidator().Validate(ValidUpdate() with { FoodSafetyCommitment = false })
            .IsValid.Should().BeFalse();

    [Fact]
    public void An_owner_under_eighteen_is_refused()
        => new UpdateRegistrationCommandValidator()
            .Validate(ValidUpdate() with { OwnerDateOfBirth = DateOnly.FromDateTime(DateTime.UtcNow).AddYears(-17) })
            .Errors.Should().Contain(e => e.ErrorMessage == RegMessages.OwnerTooYoung);

    [Fact]
    public void A_future_id_issue_date_is_refused()
        => new UpdateRegistrationCommandValidator()
            .Validate(ValidUpdate() with { IdIssuedDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30) })
            .Errors.Should().Contain(e => e.ErrorMessage == RegMessages.DateMustBePast);

    [Fact]
    public void Out_of_range_coordinates_are_refused()
        => new UpdateRegistrationCommandValidator()
            .Validate(ValidUpdate() with { AddressLatitude = 120m })
            .Errors.Should().Contain(e => e.ErrorMessage == RegMessages.CoordinatesInvalid);

    [Fact]
    public void Over_long_text_is_a_validation_error_not_a_database_error()
        => new UpdateRegistrationCommandValidator()
            .Validate(ValidUpdate() with { PermanentAddress = new string('a', 301) })
            .Errors.Should().Contain(e => e.ErrorMessage == RegMessages.FieldTooLong);

    [Fact]
    public void Submit_and_update_share_the_same_rules()
    {
        var submit = new SubmitRegistrationCommand(
            VendorTypes.Itinerant, "Hộ A", null, null, null, 1,
            OwnerDateOfBirth: DateOnly.FromDateTime(DateTime.UtcNow).AddYears(-10));
        new SubmitRegistrationCommandValidator().Validate(submit).Errors
            .Should().Contain(e => e.ErrorMessage == RegMessages.OwnerTooYoung);
    }

    [Fact]
    public async Task Withdrawing_loses_gracefully_when_an_officer_decided_first()
    {
        var vendorContext = new Mock<IVendorContext>();
        var repository = new Mock<IBusinessRegistrationRepository>();
        vendorContext.Setup(v => v.RequireOwnedRegistrationAsync(5, default)).ReturnsAsync(
            new StreetBiz.Application.Common.Models.BizRegistration(
                5, 1, VendorTypes.Itinerant, "Hộ A", null, null, null, 1, RegistrationStatuses.Submitted,
                false, null, null, DateTime.UtcNow, null));
        repository.Setup(r => r.TryTransitionAsync(5, RegistrationStatuses.Withdrawn, default)).ReturnsAsync(false);

        var handler = new WithdrawRegistrationCommandHandler(vendorContext.Object, repository.Object);
        var act = () => handler.Handle(new WithdrawRegistrationCommand(5), default);

        await act.Should().ThrowAsync<ConflictException>();
    }
}

public sealed class FileRegistrationTests
{
    private static StreetBiz.Application.Common.Models.BizRegistration Draft(string vendorType = VendorTypes.Itinerant) =>
        new(5, 1, vendorType, "Hộ A", null, null, null, 1, RegistrationStatuses.Draft,
            false, null, null, DateTime.UtcNow, null);

    private readonly Mock<IVendorContext> vendorContext = new();
    private readonly Mock<IBusinessRegistrationRepository> repository = new();

    private StreetBiz.Application.Features.VendorRegistration.FileRegistration.FileRegistrationCommandHandler Handler() =>
        new(vendorContext.Object, repository.Object);

    private static StreetBiz.Application.Common.Models.BizRegistrationEvidence Evidence(string type) =>
        new(1, 5, type, "/x", DateTime.UtcNow);

    [Fact]
    public async Task A_draft_missing_required_documents_cannot_be_filed()
    {
        vendorContext.Setup(v => v.RequireOwnedRegistrationAsync(5, default)).ReturnsAsync(Draft());
        repository.Setup(r => r.ListEvidenceAsync(5, default)).ReturnsAsync([Evidence(EvidenceTypes.IdentityDocument)]);

        var act = () => Handler().Handle(
            new StreetBiz.Application.Features.VendorRegistration.FileRegistration.FileRegistrationCommand(5), default);

        (await act.Should().ThrowAsync<DomainRuleException>()).Which.Message.Should().Contain("CCCD mặt sau");
        repository.Verify(r => r.TryTransitionAsync(It.IsAny<long>(), It.IsAny<string>(), default), Times.Never);
    }

    [Fact]
    public async Task A_fixed_storefront_also_needs_its_business_licence()
    {
        vendorContext.Setup(v => v.RequireOwnedRegistrationAsync(5, default)).ReturnsAsync(Draft(VendorTypes.FixedStorefront));
        repository.Setup(r => r.ListEvidenceAsync(5, default)).ReturnsAsync(
            [Evidence(EvidenceTypes.IdentityDocument), Evidence(EvidenceTypes.IdentityDocumentBack)]);

        var act = () => Handler().Handle(
            new StreetBiz.Application.Features.VendorRegistration.FileRegistration.FileRegistrationCommand(5), default);

        (await act.Should().ThrowAsync<DomainRuleException>()).Which.Message.Should().Contain("Giấy phép kinh doanh");
    }

    [Fact]
    public async Task A_complete_draft_is_filed_and_the_ward_is_told()
    {
        vendorContext.Setup(v => v.RequireOwnedRegistrationAsync(5, default)).ReturnsAsync(Draft());
        repository.Setup(r => r.ListEvidenceAsync(5, default)).ReturnsAsync(
            [Evidence(EvidenceTypes.IdentityDocument), Evidence(EvidenceTypes.IdentityDocumentBack)]);
        repository.Setup(r => r.TryTransitionAsync(5, RegistrationStatuses.Submitted, default)).ReturnsAsync(true);
        repository.Setup(r => r.GetByIdAsync(5, default)).ReturnsAsync(Draft() with { RegistrationStatus = RegistrationStatuses.Submitted });

        var result = await Handler().Handle(
            new StreetBiz.Application.Features.VendorRegistration.FileRegistration.FileRegistrationCommand(5), default);

        result.RegistrationStatus.Should().Be(RegistrationStatuses.Submitted);
        repository.Verify(r => r.RecordSubmittedAsync(5, default), Times.Once);
    }

    [Fact]
    public async Task Only_a_draft_can_be_filed()
    {
        vendorContext.Setup(v => v.RequireOwnedRegistrationAsync(5, default))
            .ReturnsAsync(Draft() with { RegistrationStatus = RegistrationStatuses.Approved });

        var act = () => Handler().Handle(
            new StreetBiz.Application.Features.VendorRegistration.FileRegistration.FileRegistrationCommand(5), default);

        await act.Should().ThrowAsync<DomainRuleException>();
    }

    [Fact]
    public async Task A_second_filed_application_is_a_conflict()
    {
        vendorContext.Setup(v => v.RequireOwnedRegistrationAsync(5, default)).ReturnsAsync(Draft());
        repository.Setup(r => r.HasActivePendingAsync(1, default, 5)).ReturnsAsync(true);

        var act = () => Handler().Handle(
            new StreetBiz.Application.Features.VendorRegistration.FileRegistration.FileRegistrationCommand(5), default);

        await act.Should().ThrowAsync<ConflictException>();
    }
}
