using FluentAssertions;
using Moq;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.Authentication;
using StreetBiz.Application.Features.Authentication.RefreshToken;
using StreetBiz.Application.Features.Authentication.Register;
using StreetBiz.Application.Features.Authentication.ResetPassword;

namespace StreetBiz.Application.Tests;

/// <summary>The auth use cases commit all their steps together, or none of them.</summary>
public sealed class AuthUnitOfWorkTests
{
    private const string Phone = "0905123456";
    private static readonly DateTime Now = new(2026, 11, 1, 3, 0, 0, DateTimeKind.Utc);
    private static readonly AppUser User = new(5, Phone, "old-hash", "Lan", RoleCodes.Vendor, 10, AccountStatuses.Active, Now);

    private readonly FakeUnitOfWork uow = new();
    private readonly Mock<IOtpService> otp = new();
    private readonly Mock<IUserAccountRepository> users = new();
    private readonly Mock<IPasswordHasher> hasher = new();
    private readonly Mock<ISessionRepository> sessions = new();
    private readonly Mock<ISecurityEvents> events = new();
    private readonly Mock<IAuthTokenIssuer> tokens = new();
    private readonly Mock<IDateTimeProvider> clock = new();
    private readonly Mock<IJwtTokenService> jwt = new();

    public AuthUnitOfWorkTests()
    {
        clock.Setup(c => c.UtcNow).Returns(Now);
        hasher.Setup(h => h.Hash(It.IsAny<string>())).Returns<string>(p => "hash:" + p);
        users.Setup(u => u.GetByPhoneAsync(Phone, It.IsAny<CancellationToken>())).ReturnsAsync(User);
        users.Setup(u => u.GetByIdAsync(5, It.IsAny<CancellationToken>())).ReturnsAsync(User);
        users.Setup(u => u.CreateAsync(It.IsAny<NewUser>(), It.IsAny<CancellationToken>())).ReturnsAsync(77);
    }

    private ResetPasswordCommandHandler Reset() =>
        new(otp.Object, users.Object, hasher.Object, sessions.Object, events.Object, uow);

    private static ResetPasswordCommand ResetCommand => new(Phone, "111222", "N3w!Passw0rd");

    [Fact]
    public async Task A_reset_commits_once_when_every_step_succeeds()
    {
        await Reset().Handle(ResetCommand, default);

        uow.Commits.Should().Be(1);
    }

    [Fact]
    public async Task A_reset_that_fails_after_the_password_change_commits_nothing()
    {
        events.Setup(e => e.RecordAsync(
                It.IsAny<long>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<(string, string)?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var act = () => Reset().Handle(ResetCommand, default);

        await act.Should().ThrowAsync<InvalidOperationException>();
        uow.Commits.Should().Be(0);
        // The code was already spent before the transaction, and stays spent.
        otp.Verify(o => o.ConsumeAsync(Phone, OtpPurposes.PasswordReset, "111222", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_reset_for_an_unknown_account_commits_nothing()
    {
        users.Setup(u => u.GetByPhoneAsync(Phone, It.IsAny<CancellationToken>())).ReturnsAsync((AppUser?)null);

        var act = () => Reset().Handle(ResetCommand, default);

        await act.Should().ThrowAsync<NotFoundException>();
        uow.Commits.Should().Be(0);
    }

    private RegisterCommandHandler Register()
    {
        var units = new Mock<IAdministrativeUnitRepository>();
        units.Setup(u => u.IsWardAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        return new(otp.Object, users.Object, hasher.Object, clock.Object, tokens.Object, units.Object, uow);
    }

    private static RegisterCommand RegisterCommandFor => new(Phone, "Str0ng!Pass", "Chị Lan", RoleCodes.Vendor, 10, "123456", "Chrome", "127.0.0.1");

    [Fact]
    public async Task Registration_commits_the_account_and_its_first_session_together()
    {
        await Register().Handle(RegisterCommandFor, default);

        uow.Commits.Should().Be(1);
    }

    [Fact]
    public async Task A_failed_sign_in_after_creating_the_account_commits_nothing()
    {
        tokens.Setup(t => t.IssueAsync(It.IsAny<AppUser>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var act = () => Register().Handle(RegisterCommandFor, default);

        await act.Should().ThrowAsync<InvalidOperationException>();
        uow.Commits.Should().Be(0);
    }

    private RefreshTokenCommandHandler Refresh() => new(jwt.Object, sessions.Object, users.Object, clock.Object, tokens.Object, uow);

    private void ActiveSession()
    {
        jwt.Setup(j => j.HashRefreshToken("raw")).Returns([1, 2, 3]);
        sessions.Setup(s => s.GetActiveByRefreshHashAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AppSession(9, 5, null, null, Now, Now, Now.AddDays(5), null));
    }

    [Fact]
    public async Task Rotating_a_session_commits_the_old_one_retired_with_the_new_one_issued()
    {
        ActiveSession();
        sessions.Setup(s => s.TryRevokeAsync(9, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        await Refresh().Handle(new RefreshTokenCommand("raw", null, null), default);

        uow.Commits.Should().Be(1);
    }

    [Fact]
    public async Task A_rotation_that_cannot_issue_the_new_session_commits_nothing()
    {
        ActiveSession();
        sessions.Setup(s => s.TryRevokeAsync(9, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        tokens.Setup(t => t.RotateAsync(
                It.IsAny<AppUser>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var act = () => Refresh().Handle(new RefreshTokenCommand("raw", null, null), default);

        await act.Should().ThrowAsync<InvalidOperationException>();
        uow.Commits.Should().Be(0);
    }

    [Fact]
    public async Task Losing_the_rotation_race_commits_nothing()
    {
        ActiveSession();
        sessions.Setup(s => s.TryRevokeAsync(9, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var act = () => Refresh().Handle(new RefreshTokenCommand("raw", null, null), default);

        await act.Should().ThrowAsync<AuthenticationException>();
        uow.Commits.Should().Be(0);
    }
}
