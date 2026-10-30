using FluentAssertions;
using Moq;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Features.VendorKyc;

namespace StreetBiz.Application.Tests;

/// <summary>
/// REG-02 eKYC: the two use cases spend real FPT.AI credits, so they are vendor-only, need an
/// explicit biometric consent, and only ever read files the caller uploaded.
/// </summary>
public sealed class VendorKycUseCaseTests
{
    private const long VendorId = 30;
    private const long UserId = 7;
    private const string Card = "0a1b2c3d4e5f60718293a4b5c6d7e8f9.jpg";
    private const string BackCard = "ffeeddccbbaa99887766554433221100.png";
    private const string Selfie = "1111222233334444555566667777aaaa.jpg";

    private readonly Mock<IVendorContext> vendorContext = new();
    private readonly Mock<ICurrentUser> currentUser = new();
    private readonly Mock<IFileStorage> storage = new();
    private readonly Mock<IKycVerificationService> kyc = new();
    private readonly Mock<IKycResultRepository> results = new();

    public VendorKycUseCaseTests()
    {
        vendorContext.Setup(v => v.RequireVendorIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(VendorId);
        currentUser.SetupGet(u => u.UserId).Returns(UserId);
        storage.Setup(s => s.ExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
    }

    private static string Url(long owner, string file) => EvidenceFiles.BuildUrl(owner, file);

    private static KycIdCardExtraction Extraction() => new(
        "012345678901", "NGUYỄN VĂN A", new DateOnly(1990, 5, 20), "Nam", "Việt Nam", "Kinh",
        "Hải Châu, Đà Nẵng", new DateOnly(2021, 4, 1), "Cục CSQLHC", 92, false, [], true, "Đọc được CCCD");

    private static KycFaceMatchResult Match() => new(true, 87.5, false, false, [], true, "Khớp");

    private ExtractKycIdCardCommandHandler ExtractHandler() =>
        new(vendorContext.Object, currentUser.Object, storage.Object, kyc.Object, results.Object);

    private MatchKycFaceCommandHandler MatchHandler() =>
        new(vendorContext.Object, currentUser.Object, storage.Object, kyc.Object, results.Object);

    // ---- ExtractKycIdCardCommand --------------------------------------------------------

    [Fact]
    public async Task Reads_the_card_and_records_the_check_for_the_caller()
    {
        var front = Url(UserId, Card);
        var back = Url(UserId, BackCard);
        var extraction = Extraction();
        kyc.Setup(k => k.ExtractIdCardAsync(front, back, It.IsAny<CancellationToken>())).ReturnsAsync(extraction);

        var result = await ExtractHandler().Handle(new ExtractKycIdCardCommand(front, back, true), default);

        result.Should().BeSameAs(extraction);
        results.Verify(r => r.RecordIdCardCheckAsync(UserId, extraction, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task The_back_photo_is_optional_and_is_not_checked_when_absent()
    {
        var front = Url(UserId, Card);
        kyc.Setup(k => k.ExtractIdCardAsync(front, null, It.IsAny<CancellationToken>())).ReturnsAsync(Extraction());

        await ExtractHandler().Handle(new ExtractKycIdCardCommand(front, null, true), default);

        storage.Verify(s => s.ExistsAsync(EvidenceFiles.StoragePath(UserId, Card), It.IsAny<CancellationToken>()), Times.Once);
        storage.Verify(s => s.ExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_front_photo_uploaded_by_someone_else_is_refused_before_the_provider_is_called()
    {
        var act = () => ExtractHandler().Handle(
            new ExtractKycIdCardCommand(Url(UserId + 1, Card), null, true), default);

        await act.Should().ThrowAsync<ValidationAppException>();
        kyc.VerifyNoOtherCalls();
        results.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task A_back_photo_uploaded_by_someone_else_is_refused_too()
    {
        var act = () => ExtractHandler().Handle(
            new ExtractKycIdCardCommand(Url(UserId, Card), Url(UserId + 1, BackCard), true), default);

        await act.Should().ThrowAsync<ValidationAppException>();
        kyc.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task A_file_that_is_no_longer_in_storage_is_refused()
    {
        storage.Setup(s => s.ExistsAsync(EvidenceFiles.StoragePath(UserId, Card), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var act = () => ExtractHandler().Handle(new ExtractKycIdCardCommand(Url(UserId, Card), null, true), default);

        await act.Should().ThrowAsync<ValidationAppException>();
        kyc.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Only_a_vendor_may_spend_the_provider_credits()
    {
        vendorContext.Setup(v => v.RequireVendorIdAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ForbiddenException("Vendors only."));

        var act = () => ExtractHandler().Handle(new ExtractKycIdCardCommand(Url(UserId, Card), null, true), default);

        await act.Should().ThrowAsync<ForbiddenException>();
        kyc.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task A_missing_session_is_a_401()
    {
        currentUser.SetupGet(u => u.UserId).Returns((long?)null);

        var act = () => ExtractHandler().Handle(new ExtractKycIdCardCommand(Url(UserId, Card), null, true), default);

        await act.Should().ThrowAsync<AuthenticationException>();
        kyc.VerifyNoOtherCalls();
    }

    [Fact]
    public void The_extract_command_needs_a_valid_front_url_and_explicit_consent()
    {
        var validator = new ExtractKycIdCardCommandValidator();

        validator.Validate(new ExtractKycIdCardCommand(Url(UserId, Card), null, true)).IsValid.Should().BeTrue();
        validator.Validate(new ExtractKycIdCardCommand(Url(UserId, Card), Url(UserId, BackCard), true)).IsValid.Should().BeTrue();

        validator.Validate(new ExtractKycIdCardCommand("", null, true)).Errors
            .Should().Contain(e => e.ErrorMessage == KycMessages.FrontPhotoRequired);
        validator.Validate(new ExtractKycIdCardCommand("https://evil.test/a.jpg", null, true)).Errors
            .Should().Contain(e => e.ErrorMessage == RegMessages.UploadValidDocument);
        validator.Validate(new ExtractKycIdCardCommand(Url(UserId, Card), "not-a-url", true)).Errors
            .Should().Contain(e => e.ErrorMessage == RegMessages.UploadValidDocument);
        validator.Validate(new ExtractKycIdCardCommand(Url(UserId, Card), null, false)).Errors
            .Should().Contain(e => e.ErrorMessage == KycMessages.ConsentRequired);
    }

    [Fact]
    public void A_blank_back_url_is_treated_as_no_back_photo()
    {
        var validator = new ExtractKycIdCardCommandValidator();

        validator.Validate(new ExtractKycIdCardCommand(Url(UserId, Card), "  ", true)).IsValid.Should().BeTrue();
    }

    // ---- MatchKycFaceCommand ------------------------------------------------------------

    [Fact]
    public async Task Compares_the_portrait_with_the_card_and_records_the_match()
    {
        var selfie = Url(UserId, Selfie);
        var card = Url(UserId, Card);
        var match = Match();
        kyc.Setup(k => k.MatchFaceAsync(selfie, card, It.IsAny<CancellationToken>())).ReturnsAsync(match);

        var result = await MatchHandler().Handle(new MatchKycFaceCommand(selfie, card, true), default);

        result.Should().BeSameAs(match);
        results.Verify(r => r.RecordFaceMatchAsync(UserId, match, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Both_photos_must_belong_to_the_caller(bool selfieIsForeign, bool cardIsForeign)
    {
        var selfie = Url(selfieIsForeign ? UserId + 1 : UserId, Selfie);
        var card = Url(cardIsForeign ? UserId + 1 : UserId, Card);

        var act = () => MatchHandler().Handle(new MatchKycFaceCommand(selfie, card, true), default);

        await act.Should().ThrowAsync<ValidationAppException>();
        kyc.VerifyNoOtherCalls();
        results.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Only_a_vendor_may_compare_faces()
    {
        vendorContext.Setup(v => v.RequireVendorIdAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ForbiddenException("Vendors only."));

        var act = () => MatchHandler().Handle(
            new MatchKycFaceCommand(Url(UserId, Selfie), Url(UserId, Card), true), default);

        await act.Should().ThrowAsync<ForbiddenException>();
        kyc.VerifyNoOtherCalls();
    }

    [Fact]
    public void The_match_command_needs_both_photos_and_explicit_consent()
    {
        var validator = new MatchKycFaceCommandValidator();

        validator.Validate(new MatchKycFaceCommand(Url(UserId, Selfie), Url(UserId, Card), true)).IsValid.Should().BeTrue();

        validator.Validate(new MatchKycFaceCommand("", Url(UserId, Card), true)).Errors
            .Should().Contain(e => e.ErrorMessage == KycMessages.SelfiePhotoRequired);
        validator.Validate(new MatchKycFaceCommand(Url(UserId, Selfie), "", true)).Errors
            .Should().Contain(e => e.ErrorMessage == KycMessages.FrontPhotoRequired);
        validator.Validate(new MatchKycFaceCommand("/api/uploads/evidence/x/y", Url(UserId, Card), true)).Errors
            .Should().Contain(e => e.ErrorMessage == RegMessages.UploadValidDocument);
        validator.Validate(new MatchKycFaceCommand(Url(UserId, Selfie), Url(UserId, Card), false)).Errors
            .Should().Contain(e => e.ErrorMessage == KycMessages.ConsentRequired);
    }
}
