using FluentAssertions;
using Moq;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Features.CommunityVendors;

namespace StreetBiz.Application.Tests;

public sealed class CommunityVendorUseCaseTests
{
    [Fact]
    public async Task Active_vendor_search_applies_the_exact_radius_after_the_database_box()
    {
        var repository = new Mock<ICommunityVendorRepository>();
        repository.Setup(x => x.SearchActiveAsync(
                It.IsAny<CommunityVendorSearchArea>(),
                500,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                Location(1, 10.0000m, 106.0000m),
                Location(2, 10.0200m, 106.0000m),
            ]);

        var result = await new SearchActiveVendorsQueryHandler(repository.Object)
            .Handle(new SearchActiveVendorsQuery(10m, 106m, 1_000), CancellationToken.None);

        result.Should().ContainSingle().Which.VendorId.Should().Be(1);
        result[0].DistanceMeters.Should().Be(0);
        repository.Verify(x => x.SearchActiveAsync(
            It.IsAny<CommunityVendorSearchArea>(),
            500,
            It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task Invalid_QR_is_logged_as_not_found_without_querying_a_permit()
    {
        var repository = new Mock<ICommunityVendorRepository>();
        var tokens = new Mock<IPermitTokenService>();
        PermitTokenClaims ignored;
        tokens.Setup(x => x.TryParse("tampered", out ignored!)).Returns(false);
        var currentUser = Mock.Of<ICurrentUser>();

        var result = await new VerifyPublicPermitCommandHandler(
                repository.Object,
                tokens.Object,
                currentUser)
            .Handle(new VerifyPublicPermitCommand("tampered", null, null, null), CancellationToken.None);

        result.IsValid.Should().BeFalse();
        result.Status.Should().Be(PermitScanResults.NotFound);
        repository.Verify(x => x.GetPermitVerificationAsync(
            It.IsAny<long>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        repository.Verify(x => x.AddPermitScanAsync(
            It.Is<PublicPermitScan>(scan =>
                scan.PermitId == null && scan.Result == PermitScanResults.NotFound),
            It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task Signed_QR_uses_the_live_effective_status_and_logs_the_scan()
    {
        var repository = new Mock<ICommunityVendorRepository>();
        var tokens = new Mock<IPermitTokenService>();
        var claims = new PermitTokenClaims(9, DateTime.UtcNow);
        tokens.Setup(x => x.TryParse("signed", out claims)).Returns(true);
        repository.Setup(x => x.GetPermitVerificationAsync(9, "signed", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PublicPermitVerificationRow(
                3, 9, 2, "Vendor", 4, "S-04", 10, 106,
                new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31),
                PermitEffectiveStatuses.Expired));

        var result = await new VerifyPublicPermitCommandHandler(
                repository.Object,
                tokens.Object,
                Mock.Of<ICurrentUser>())
            .Handle(new VerifyPublicPermitCommand("signed", 10, 106, null), CancellationToken.None);

        result.IsValid.Should().BeFalse();
        result.Status.Should().Be(PermitEffectiveStatuses.Expired);
        result.VendorId.Should().Be(2);
        repository.Verify(x => x.AddPermitScanAsync(
            It.Is<PublicPermitScan>(scan =>
                scan.PermitId == 3 && scan.Result == PermitEffectiveStatuses.Expired),
            It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task Comment_handler_upserts_for_the_database_verified_customer()
    {
        var customer = new Mock<ICustomerContext>();
        customer.Setup(x => x.RequireCustomerUserIdAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(7);
        var repository = new Mock<ICommunityVendorRepository>();
        repository.Setup(x => x.GetPublicProfileAsync(2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Profile(2));
        repository.Setup(x => x.UpsertCommentAsync(
                2,
                7,
                It.Is<CustomerVendorComment>(comment => comment.Rating == 5 && comment.CommentText == "Great"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PublicVendorCommentRow(1, 7, "Customer", 5, "Great", DateTime.UtcNow));

        var result = await new UpsertVendorCommentCommandHandler(customer.Object, repository.Object)
            .Handle(new UpsertVendorCommentCommand(2, 5, " Great "), CancellationToken.None);

        result.Rating.Should().Be(5);
        repository.VerifyAll();
    }

    [Fact]
    public async Task Report_handler_rejects_a_slot_or_permit_owned_by_another_vendor()
    {
        var customer = new Mock<ICustomerContext>();
        customer.Setup(x => x.RequireCustomerUserIdAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(7);
        var repository = new Mock<ICommunityVendorRepository>();
        repository.Setup(x => x.GetPublicProfileAsync(2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Profile(2));
        repository.Setup(x => x.ReportReferencesBelongToVendorAsync(
                2, 99, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var action = () => new ReportSuspiciousVendorCommandHandler(customer.Object, repository.Object)
            .Handle(new ReportSuspiciousVendorCommand(2, "Wrong location", null, 99, null), CancellationToken.None);

        await action.Should().ThrowAsync<DomainRuleException>()
            .WithMessage(CommunityMessages.ReportReferenceMismatch);
        repository.Verify(x => x.AddReportAsync(
            It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CustomerVendorReport>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Report_handler_creates_a_pending_report_for_verified_references()
    {
        var customer = new Mock<ICustomerContext>();
        customer.Setup(x => x.RequireCustomerUserIdAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(7);
        var repository = new Mock<ICommunityVendorRepository>();
        repository.Setup(x => x.GetPublicProfileAsync(2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Profile(2));
        repository.Setup(x => x.ReportReferencesBelongToVendorAsync(
                2, 1, 3, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        repository.Setup(x => x.AddReportAsync(
                2,
                7,
                It.Is<CustomerVendorReport>(report =>
                    report.Reason == "Wrong location"
                    && report.EvidenceUrl == "/uploads/evidence/photo.jpg"
                    && report.SlotId == 1
                    && report.ScannedPermitId == 3),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(41);

        var result = await new ReportSuspiciousVendorCommandHandler(customer.Object, repository.Object)
            .Handle(new ReportSuspiciousVendorCommand(
                2,
                " Wrong location ",
                " /uploads/evidence/photo.jpg ",
                1,
                3), CancellationToken.None);

        result.ReportId.Should().Be(41);
        result.Status.Should().Be(VendorReportStatuses.Pending);
        repository.VerifyAll();
    }

    [Theory]
    [InlineData(RoleCodes.Vendor, AccountStatuses.Active)]
    [InlineData(RoleCodes.Customer, AccountStatuses.Suspended)]
    public async Task Customer_context_rechecks_role_and_status_in_the_database(string role, string status)
    {
        var currentUser = new Mock<ICurrentUser>();
        currentUser.SetupGet(x => x.UserId).Returns(7);
        currentUser.SetupGet(x => x.RoleCode).Returns(RoleCodes.Customer);
        var users = new Mock<IUserAccountRepository>();
        users.Setup(x => x.GetByIdAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AppUser(7, "0900000007", "hash", "Customer", role, null, status, DateTime.UtcNow));

        var action = () => new CustomerContext(currentUser.Object, users.Object)
            .RequireCustomerUserIdAsync(CancellationToken.None);

        await action.Should().ThrowAsync<ForbiddenException>();
    }

    private static ActiveVendorLocationRow Location(long vendorId, decimal latitude, decimal longitude) => new(
        vendorId, vendorId, $"Vendor {vendorId}", VendorTypes.Itinerant, null,
        vendorId, new DateOnly(2026, 12, 31), vendorId, $"S-{vendorId}", "Zone",
        latitude, longitude, null, 0, null, 0);

    private static PublicVendorProfileRow Profile(long vendorId) => new(
        vendorId, 1, "Vendor", VendorTypes.Itinerant, null, 16, "Ward 16",
        1, PermitEffectiveStatuses.Valid, new DateOnly(2026, 12, 31),
        1, "S-01", "Zone", 10, 106, null, 0, null, 0);
}
