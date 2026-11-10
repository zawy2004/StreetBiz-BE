using FluentAssertions;
using Moq;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Features.Commerce;

namespace StreetBiz.Application.Tests;

/// <summary>REV-03: who may report, what a report must say, and how the repository's answer becomes an HTTP error.</summary>
public sealed class ContentReportUseCaseTests
{
    private readonly Mock<IContentReportRepository> reports = new();
    private readonly Mock<IContentReporterContext> reporter = new();

    public ContentReportUseCaseTests() =>
        reporter.Setup(x => x.RequireAsync(It.IsAny<CancellationToken>())).ReturnsAsync(5);

    private ReportContentCommandHandler Handler() => new(reporter.Object, reports.Object);

    [Theory]
    [InlineData("STOREFRONT", 1, "MISLEADING", null, true)]
    [InlineData("MENU_ITEM", 1, "COUNTERFEIT", "Ảnh lấy từ trang khác", true)]
    [InlineData("REVIEW", 1, "MISLEADING", null, false)]           // reviews are not reported through here
    [InlineData("STOREFRONT", 0, "MISLEADING", null, false)]
    [InlineData("STOREFRONT", 1, "NOPE", null, false)]
    [InlineData("STOREFRONT", 1, "OTHER", null, false)]              // "other" must be explained
    [InlineData("STOREFRONT", 1, "OTHER", "ngắn", false)]
    [InlineData("STOREFRONT", 1, "OTHER", "Bán hàng quá giờ cho phép", true)]
    public void The_command_needs_a_known_target_and_reason(string type, long id, string code, string? detail, bool valid) =>
        new ReportContentCommandValidator()
            .Validate(new ReportContentCommand(type, id, code, detail))
            .IsValid.Should().Be(valid);

    [Fact]
    public void The_detail_is_capped_so_the_stored_reason_stays_within_500_characters()
    {
        var validator = new ReportContentCommandValidator();

        validator.Validate(new ReportContentCommand("STOREFRONT", 1, "MISLEADING", new string('x', 400))).IsValid.Should().BeTrue();
        validator.Validate(new ReportContentCommand("STOREFRONT", 1, "MISLEADING", new string('x', 401))).IsValid.Should().BeFalse();
        foreach (var code in ContentReportReasons.All)
        {
            ContentReportReasons.Compose(code, new string('x', 400)).Length.Should().BeLessThanOrEqualTo(500);
        }
    }

    [Fact]
    public async Task A_good_report_stores_the_label_and_the_reporters_words_and_comes_back_pending()
    {
        ContentReportDraft? stored = null;
        reports.Setup(x => x.CreateAsync(It.IsAny<ContentReportDraft>(), It.IsAny<CancellationToken>()))
            .Callback<ContentReportDraft, CancellationToken>((d, _) => stored = d)
            .ReturnsAsync(new ContentReportResult(ContentReportOutcome.Created, 42));

        var receipt = await Handler().Handle(new ReportContentCommand("MENU_ITEM", 9, "MISLEADING", "  Ảnh không đúng món  "), default);

        receipt.Should().Be(new ContentReportReceiptDto(42, ReportedContentStatuses.Pending));
        stored!.ContentType.Should().Be("MENU_ITEM");
        stored.ContentId.Should().Be(9);
        stored.ReporterUserId.Should().Be(5);
        stored.Reason.Should().Be("Thông tin hoặc hình ảnh sai sự thật, gây hiểu lầm: Ảnh không đúng món");
    }

    [Fact]
    public async Task Without_a_detail_the_reason_is_just_the_label()
    {
        ContentReportDraft? stored = null;
        reports.Setup(x => x.CreateAsync(It.IsAny<ContentReportDraft>(), It.IsAny<CancellationToken>()))
            .Callback<ContentReportDraft, CancellationToken>((d, _) => stored = d)
            .ReturnsAsync(new ContentReportResult(ContentReportOutcome.Created, 1));

        await Handler().Handle(new ReportContentCommand("STOREFRONT", 9, "COUNTERFEIT", " "), default);

        stored!.Reason.Should().Be("Hàng giả, gian lận");
    }

    [Theory]
    [InlineData(ContentReportOutcome.ContentNotFound, typeof(NotFoundException))]
    [InlineData(ContentReportOutcome.OwnContent, typeof(DomainRuleException))]
    [InlineData(ContentReportOutcome.Duplicate, typeof(ConflictException))]
    public async Task Refusals_become_the_matching_http_errors(ContentReportOutcome outcome, Type expected)
    {
        reports.Setup(x => x.CreateAsync(It.IsAny<ContentReportDraft>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContentReportResult(outcome));

        var act = () => Handler().Handle(new ReportContentCommand("STOREFRONT", 9, "MISLEADING", null), default);

        (await act.Should().ThrowAsync<Exception>()).And.GetType().Should().Be(expected);
    }

    [Fact]
    public async Task Hitting_the_hourly_limit_is_a_429_that_says_when_to_come_back()
    {
        reports.Setup(x => x.CreateAsync(It.IsAny<ContentReportDraft>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContentReportResult(ContentReportOutcome.RateLimited, RetryAfterSeconds: 1234));

        var act = () => Handler().Handle(new ReportContentCommand("STOREFRONT", 9, "MISLEADING", null), default);

        (await act.Should().ThrowAsync<TooManyRequestsException>()).Which.RetryAfterSeconds.Should().Be(1234);
    }

    [Fact]
    public async Task A_guest_or_the_wrong_role_never_reaches_the_repository()
    {
        reporter.Setup(x => x.RequireAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new AuthenticationException("login"));

        var act = () => Handler().Handle(new ReportContentCommand("STOREFRONT", 9, "MISLEADING", null), default);

        await act.Should().ThrowAsync<AuthenticationException>();
        reports.VerifyNoOtherCalls();
    }

    private static ContentReporterContext Context(string claimRole, string storedRole, string status)
    {
        var currentUser = new Mock<ICurrentUser>();
        currentUser.SetupGet(x => x.UserId).Returns(5);
        currentUser.SetupGet(x => x.RoleCode).Returns(claimRole);
        var users = new Mock<IUserAccountRepository>();
        users.Setup(x => x.GetByIdAsync(5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AppUser(5, "0900000005", "hash", "A", storedRole, null, status, DateTime.UtcNow));
        return new ContentReporterContext(currentUser.Object, users.Object);
    }

    [Theory]
    [InlineData(RoleCodes.Customer)]
    [InlineData(RoleCodes.Vendor)]
    public async Task Customers_and_vendors_may_report(string role) =>
        (await Context(role, role, AccountStatuses.Active).RequireAsync(default)).Should().Be(5);

    [Theory]
    [InlineData(RoleCodes.WardAuthority, RoleCodes.WardAuthority, AccountStatuses.Active)]
    [InlineData(RoleCodes.PlatformAdmin, RoleCodes.PlatformAdmin, AccountStatuses.Active)]
    [InlineData(RoleCodes.Customer, RoleCodes.Customer, AccountStatuses.Suspended)]
    [InlineData(RoleCodes.Customer, RoleCodes.Vendor, AccountStatuses.Active)] // a stale token: the database role wins
    public async Task Everyone_else_is_refused(string claimRole, string storedRole, string status)
    {
        var act = () => Context(claimRole, storedRole, status).RequireAsync(default);

        await act.Should().ThrowAsync<ForbiddenException>();
    }
}
