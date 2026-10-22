using FluentAssertions;
using Moq;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.Authentication;
using StreetBiz.Application.Features.Authentication.Login;
using StreetBiz.Application.Features.Authentication.RefreshToken;
using StreetBiz.Application.Features.PlatformAdministration;

namespace StreetBiz.Application.Tests;

/// <summary>ADM-02: suspend / reactivate Customer and Vendor accounts.</summary>
public sealed class PlatformAccountUseCaseTests
{
    private static readonly DateTime Now = new(2026, 10, 20, 8, 0, 0, DateTimeKind.Utc);
    private readonly Mock<IPlatformAdministrationRepository> repository = new();

    private static Mock<IPlatformAdminContext> AdminContext(long id = 7)
    {
        var actor = new Mock<IPlatformAdminContext>();
        actor.Setup(x => x.RequireAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlatformAdminActor(id, "Admin"));
        return actor;
    }

    private static PlatformAccountRow Row(string status = AccountStatuses.Suspended) => new(
        9, "Hộ bán", "0901234567", RoleCodes.Vendor, status, Now, Now, 0, 1);

    private static AccountStatusChangeResult Updated(string status) =>
        new(AccountStatusChangeOutcome.Updated, new PlatformAccountDetailRow(Row(status), []));

    [Fact]
    public async Task Suspend_trims_the_reason_and_masks_the_phone_in_the_answer()
    {
        repository.Setup(x => x.ChangeAccountStatusAsync(
                9, AccountStatuses.Suspended, AccountStatuses.Active, "Bán hàng giả", 7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Updated(AccountStatuses.Suspended));

        var result = await new SuspendPlatformAccountCommandHandler(AdminContext().Object, repository.Object)
            .Handle(new SuspendPlatformAccountCommand(9, "  Bán hàng giả ", AccountStatuses.Active), default);

        result.Account.AccountStatus.Should().Be(AccountStatuses.Suspended);
        result.Account.MaskedPhone.Should().Be("090****567");
        repository.VerifyAll();
    }

    [Fact]
    public async Task An_administrator_cannot_suspend_themselves()
    {
        var handler = new SuspendPlatformAccountCommandHandler(AdminContext(7).Object, repository.Object);

        var act = () => handler.Handle(new SuspendPlatformAccountCommand(7, "Vi phạm", AccountStatuses.Active), default);

        await act.Should().ThrowAsync<DomainRuleException>()
            .WithMessage(PlatformAdministrationMessages.AccountSelfAction);
        repository.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(AccountStatusChangeOutcome.NotFound, typeof(NotFoundException))]
    [InlineData(AccountStatusChangeOutcome.RoleNotManageable, typeof(ForbiddenException))]
    [InlineData(AccountStatusChangeOutcome.Conflict, typeof(ConflictException))]
    public async Task Repository_outcomes_become_the_matching_http_errors(
        AccountStatusChangeOutcome outcome,
        Type expected)
    {
        repository.Setup(x => x.ChangeAccountStatusAsync(
                It.IsAny<long>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccountStatusChangeResult(outcome, null));
        var handler = new SuspendPlatformAccountCommandHandler(AdminContext().Object, repository.Object);

        var act = () => handler.Handle(new SuspendPlatformAccountCommand(9, "Vi phạm", AccountStatuses.Active), default);

        (await act.Should().ThrowAsync<Exception>()).And.GetType().Should().Be(expected);
    }

    [Fact]
    public async Task Reactivate_passes_the_optional_note_through()
    {
        repository.Setup(x => x.ChangeAccountStatusAsync(
                9, AccountStatuses.Active, AccountStatuses.Suspended, null, 7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Updated(AccountStatuses.Active));

        var result = await new ReactivatePlatformAccountCommandHandler(AdminContext().Object, repository.Object)
            .Handle(new ReactivatePlatformAccountCommand(9, "   ", AccountStatuses.Suspended), default);

        result.Account.AccountStatus.Should().Be(AccountStatuses.Active);
        repository.VerifyAll();
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("ab", false)]
    [InlineData("  ab  ", false)]
    [InlineData("abc", true)]
    public void Suspend_needs_a_reason_of_three_to_five_hundred_characters(string reason, bool valid)
    {
        new SuspendPlatformAccountCommandValidator()
            .Validate(new SuspendPlatformAccountCommand(9, reason, AccountStatuses.Active))
            .IsValid.Should().Be(valid);
        new SuspendPlatformAccountCommandValidator()
            .Validate(new SuspendPlatformAccountCommand(9, new string('x', 501), AccountStatuses.Active))
            .IsValid.Should().BeFalse();
    }

    [Fact]
    public void The_expected_status_must_match_the_direction_of_the_change()
    {
        new SuspendPlatformAccountCommandValidator()
            .Validate(new SuspendPlatformAccountCommand(9, "Vi phạm", AccountStatuses.Suspended))
            .IsValid.Should().BeFalse();
        new ReactivatePlatformAccountCommandValidator()
            .Validate(new ReactivatePlatformAccountCommand(9, null, AccountStatuses.Active))
            .IsValid.Should().BeFalse();
    }

    [Fact]
    public void The_list_accepts_only_manageable_roles()
    {
        var validator = new ListPlatformAccountsQueryValidator();

        validator.Validate(new ListPlatformAccountsQuery(RoleCodes.Vendor, null, null)).IsValid.Should().BeTrue();
        validator.Validate(new ListPlatformAccountsQuery(RoleCodes.WardAuthority, null, null)).IsValid.Should().BeFalse();
        validator.Validate(new ListPlatformAccountsQuery(RoleCodes.PlatformAdmin, null, null)).IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Listing_normalises_filters_before_they_reach_the_repository()
    {
        repository.Setup(x => x.ListAccountsAsync(
                RoleCodes.Vendor, AccountStatuses.Suspended, "an", 2, 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlatformPage<PlatformAccountRow>([Row()], 2, 10, 11));

        var page = await new ListPlatformAccountsQueryHandler(AdminContext().Object, repository.Object)
            .Handle(new ListPlatformAccountsQuery(" vendor", "suspended ", " an ", 2, 10), default);

        page.TotalCount.Should().Be(11);
        page.Items.Should().ContainSingle().Which.MaskedPhone.Should().Be("090****567");
    }

    [Fact]
    public async Task A_non_admin_cannot_reach_any_account_action()
    {
        var denied = new Mock<IPlatformAdminContext>();
        denied.Setup(x => x.RequireAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ForbiddenException("no"));

        var list = () => new ListPlatformAccountsQueryHandler(denied.Object, repository.Object)
            .Handle(new ListPlatformAccountsQuery(null, null, null), default);
        var suspend = () => new SuspendPlatformAccountCommandHandler(denied.Object, repository.Object)
            .Handle(new SuspendPlatformAccountCommand(9, "Vi phạm", AccountStatuses.Active), default);

        await list.Should().ThrowAsync<ForbiddenException>();
        await suspend.Should().ThrowAsync<ForbiddenException>();
        repository.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("0901234567", "090****567")]
    [InlineData("12345", "*****")]
    public async Task Phone_numbers_keep_only_the_edges(string phone, string masked)
    {
        repository.Setup(x => x.GetAccountAsync(9, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlatformAccountDetailRow(Row() with { PhoneNumber = phone }, []));

        var detail = await new GetPlatformAccountQueryHandler(AdminContext().Object, repository.Object)
            .Handle(new GetPlatformAccountQuery(9), default);

        detail.Account.MaskedPhone.Should().Be(masked);
    }

    [Fact]
    public async Task A_suspended_account_cannot_sign_in_even_with_the_right_password()
    {
        var users = new Mock<IUserAccountRepository>();
        var hasher = new Mock<IPasswordHasher>();
        var tokens = new Mock<IAuthTokenIssuer>();
        var clock = new Mock<IDateTimeProvider>();
        clock.SetupGet(c => c.UtcNow).Returns(Now);
        users.Setup(u => u.GetByPhoneAsync("0905000001", default)).ReturnsAsync(Suspended());
        hasher.Setup(h => h.Verify("ok", "hash")).Returns(true);

        var act = () => new LoginCommandHandler(users.Object, hasher.Object, tokens.Object, clock.Object,
                Mock.Of<ISecurityEvents>())
            .Handle(new LoginCommand("0905000001", "ok", null, null), default);

        await act.Should().ThrowAsync<AuthenticationException>().WithMessage(AppMessages.AccountSuspended);
        tokens.Verify(t => t.IssueAsync(It.IsAny<AppUser>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_suspended_account_cannot_refresh_its_token()
    {
        var users = new Mock<IUserAccountRepository>();
        var sessions = new Mock<ISessionRepository>();
        var jwt = new Mock<IJwtTokenService>();
        var tokens = new Mock<IAuthTokenIssuer>();
        var clock = new Mock<IDateTimeProvider>();
        clock.SetupGet(c => c.UtcNow).Returns(Now);
        jwt.Setup(j => j.HashRefreshToken(It.IsAny<string>())).Returns([1]);
        sessions.Setup(s => s.GetActiveByRefreshHashAsync(It.IsAny<byte[]>(), default))
            .ReturnsAsync(new AppSession(9, 1, null, null, Now, Now, Now.AddDays(5), null));
        users.Setup(u => u.GetByIdAsync(1, default)).ReturnsAsync(Suspended());

        var act = () => new RefreshTokenCommandHandler(jwt.Object, sessions.Object, users.Object, clock.Object, tokens.Object)
            .Handle(new RefreshTokenCommand("refresh", null, null), default);

        await act.Should().ThrowAsync<AuthenticationException>().WithMessage(AppMessages.AccountSuspended);
        sessions.Verify(s => s.TryRevokeAsync(It.IsAny<long>(), default), Times.Never);
    }

    private static AppUser Suspended() => new(
        1, "0905000001", "hash", "A", RoleCodes.Vendor, null, AccountStatuses.Suspended, null);
}
