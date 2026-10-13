using FluentAssertions;
using Moq;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.Authentication;
using StreetBiz.Application.Features.Authentication.Login;
using StreetBiz.Application.Features.Authentication.RefreshToken;

namespace StreetBiz.Application.Tests;

/// <summary>Brute-force lockout on sign-in and rotation / reuse detection on refresh.</summary>
public sealed class LoginLockoutAndRefreshTests
{
    private const string Phone = "0905000001";
    private static readonly DateTime Now = new(2026, 10, 5, 8, 0, 0, DateTimeKind.Utc);

    private readonly Mock<IUserAccountRepository> users = new();
    private readonly Mock<IPasswordHasher> hasher = new();
    private readonly Mock<IAuthTokenIssuer> tokens = new();
    private readonly Mock<IDateTimeProvider> clock = new();
    private readonly Mock<ISessionRepository> sessions = new();
    private readonly Mock<IJwtTokenService> jwt = new();
    private readonly Mock<ISecurityEvents> events = new();

    public LoginLockoutAndRefreshTests()
    {
        clock.SetupGet(c => c.UtcNow).Returns(Now);
        var result = new AuthResultDto(
            "a", Now, "r", new UserDto(1, Phone, null, RoleCodes.Vendor, null, AccountStatuses.Active));
        tokens.Setup(t => t.IssueAsync(It.IsAny<AppUser>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);
        tokens.Setup(t => t.RotateAsync(It.IsAny<AppUser>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);
        jwt.Setup(j => j.HashRefreshToken(It.IsAny<string>())).Returns([1, 2, 3]);
    }

    private static AppUser User(DateTime? lockedUntil = null) =>
        new(1, Phone, "hash", "A", RoleCodes.Vendor, null, AccountStatuses.Active, null, lockedUntil);

    private LoginCommandHandler Login() => new(users.Object, hasher.Object, tokens.Object, clock.Object, events.Object);

    private RefreshTokenCommandHandler Refresh() =>
        new(jwt.Object, sessions.Object, users.Object, clock.Object, tokens.Object);

    [Fact]
    public async Task A_wrong_password_is_counted_against_the_account()
    {
        users.Setup(u => u.GetByPhoneAsync(Phone, default)).ReturnsAsync(User());
        hasher.Setup(h => h.Verify("bad", "hash")).Returns(false);

        var act = () => Login().Handle(new LoginCommand(Phone, "bad", null, null), default);

        await act.Should().ThrowAsync<AuthenticationException>();
        users.Verify(u => u.RecordFailedLoginAsync(1, 5, TimeSpan.FromMinutes(15), default), Times.Once);
    }

    [Fact]
    public async Task A_locked_account_is_refused_before_the_password_is_checked()
    {
        users.Setup(u => u.GetByPhoneAsync(Phone, default)).ReturnsAsync(User(Now.AddMinutes(10)));

        var act = () => Login().Handle(new LoginCommand(Phone, "right", null, null), default);

        (await act.Should().ThrowAsync<TooManyRequestsException>()).Which.RetryAfterSeconds.Should().Be(600);
        hasher.Verify(h => h.Verify(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task An_unknown_phone_still_spends_hashing_time_and_fails_uniformly()
    {
        users.Setup(u => u.GetByPhoneAsync(Phone, default)).ReturnsAsync((AppUser?)null);

        var act = () => Login().Handle(new LoginCommand(Phone, "x", null, null), default);

        (await act.Should().ThrowAsync<AuthenticationException>()).Which.Message.Should().Be(AppMessages.InvalidCredentials);
        hasher.Verify(h => h.Hash("x"), Times.Once);
    }

    [Fact]
    public async Task A_good_login_clears_earlier_failures()
    {
        users.Setup(u => u.GetByPhoneAsync(Phone, default)).ReturnsAsync(User());
        hasher.Setup(h => h.Verify("ok", "hash")).Returns(true);

        await Login().Handle(new LoginCommand(Phone, "ok", null, null), default);

        users.Verify(u => u.ClearFailedLoginsAsync(1, default), Times.Once);
    }

    [Fact]
    public async Task Refresh_rotates_and_keeps_the_original_expiry()
    {
        var expiry = Now.AddDays(20);
        sessions.Setup(s => s.GetActiveByRefreshHashAsync(It.IsAny<byte[]>(), default))
            .ReturnsAsync(new AppSession(9, 1, null, null, Now, Now, expiry, null));
        users.Setup(u => u.GetByIdAsync(1, default)).ReturnsAsync(User());
        sessions.Setup(s => s.TryRevokeAsync(9, default)).ReturnsAsync(true);

        await Refresh().Handle(new RefreshTokenCommand("tok", null, null), default);

        tokens.Verify(t => t.RotateAsync(It.IsAny<AppUser>(), null, null, expiry, default), Times.Once);
    }

    [Fact]
    public async Task Losing_the_rotation_race_fails_without_issuing_a_session()
    {
        sessions.Setup(s => s.GetActiveByRefreshHashAsync(It.IsAny<byte[]>(), default))
            .ReturnsAsync(new AppSession(9, 1, null, null, Now, Now, Now.AddDays(5), null));
        users.Setup(u => u.GetByIdAsync(1, default)).ReturnsAsync(User());
        sessions.Setup(s => s.TryRevokeAsync(9, default)).ReturnsAsync(false);

        var act = () => Refresh().Handle(new RefreshTokenCommand("tok", null, null), default);

        await act.Should().ThrowAsync<AuthenticationException>();
        tokens.Verify(
            t => t.RotateAsync(It.IsAny<AppUser>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<DateTime>(), default),
            Times.Never);
    }

    [Fact]
    public async Task Replaying_an_old_rotated_token_signs_the_user_out_everywhere()
    {
        sessions.Setup(s => s.GetActiveByRefreshHashAsync(It.IsAny<byte[]>(), default))
            .ReturnsAsync(new AppSession(9, 1, null, null, Now.AddDays(-1), Now, Now.AddDays(5), Now.AddHours(-2)));

        var act = () => Refresh().Handle(new RefreshTokenCommand("tok", null, null), default);

        await act.Should().ThrowAsync<AuthenticationException>();
        sessions.Verify(s => s.RevokeAllForUserAsync(1, default), Times.Once);
    }

    [Fact]
    public async Task A_just_rotated_token_seen_again_is_a_race_not_theft()
    {
        sessions.Setup(s => s.GetActiveByRefreshHashAsync(It.IsAny<byte[]>(), default))
            .ReturnsAsync(new AppSession(9, 1, null, null, Now.AddDays(-1), Now, Now.AddDays(5), Now.AddSeconds(-5)));

        var act = () => Refresh().Handle(new RefreshTokenCommand("tok", null, null), default);

        await act.Should().ThrowAsync<AuthenticationException>();
        sessions.Verify(s => s.RevokeAllForUserAsync(It.IsAny<long>(), default), Times.Never);
    }
}

public sealed class SecurityEventTests
{
    private const string Phone = "0905000001";
    private static readonly DateTime Now = new(2026, 10, 5, 8, 0, 0, DateTimeKind.Utc);

    private readonly Mock<IUserAccountRepository> users = new();
    private readonly Mock<IPasswordHasher> hasher = new();
    private readonly Mock<IAuthTokenIssuer> tokens = new();
    private readonly Mock<IDateTimeProvider> clock = new();
    private readonly Mock<ISecurityEvents> events = new();

    public SecurityEventTests()
    {
        clock.SetupGet(c => c.UtcNow).Returns(Now);
        users.Setup(u => u.GetByPhoneAsync(Phone, default)).ReturnsAsync(
            new AppUser(1, Phone, "hash", "A", RoleCodes.Vendor, null, AccountStatuses.Active, null));
        tokens.Setup(t => t.IssueAsync(It.IsAny<AppUser>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuthResultDto("a", Now, "r", new UserDto(1, Phone, null, RoleCodes.Vendor, null, AccountStatuses.Active)));
    }

    private LoginCommandHandler Login() => new(users.Object, hasher.Object, tokens.Object, clock.Object, events.Object);

    [Fact]
    public async Task A_good_sign_in_is_recorded_with_where_it_came_from_and_does_not_notify()
    {
        hasher.Setup(h => h.Verify("ok", "hash")).Returns(true);

        await Login().Handle(new LoginCommand(Phone, "ok", "Firefox", "10.0.0.1"), default);

        events.Verify(e => e.RecordAsync(
            1, SecurityActions.LoginSuccess, It.Is<string?>(d => d!.Contains("10.0.0.1") && d.Contains("Firefox")),
            null, default), Times.Once);
    }

    [Fact]
    public async Task A_wrong_password_is_recorded_without_notifying_the_owner()
    {
        hasher.Setup(h => h.Verify("bad", "hash")).Returns(false);
        users.Setup(u => u.RecordFailedLoginAsync(1, 5, TimeSpan.FromMinutes(15), default)).ReturnsAsync(false);

        var act = () => Login().Handle(new LoginCommand(Phone, "bad", null, "10.0.0.1"), default);

        await act.Should().ThrowAsync<AuthenticationException>();
        events.Verify(e => e.RecordAsync(
            1, SecurityActions.LoginFailed, It.IsAny<string?>(), null, default), Times.Once);
    }

    [Fact]
    public async Task The_attempt_that_locks_the_account_notifies_the_owner()
    {
        hasher.Setup(h => h.Verify("bad", "hash")).Returns(false);
        users.Setup(u => u.RecordFailedLoginAsync(1, 5, TimeSpan.FromMinutes(15), default)).ReturnsAsync(true);

        var act = () => Login().Handle(new LoginCommand(Phone, "bad", null, null), default);

        await act.Should().ThrowAsync<AuthenticationException>();
        events.Verify(e => e.RecordAsync(
            1, SecurityActions.AccountLocked, It.IsAny<string?>(),
            It.Is<(string Title, string Body)?>(n => n != null), default), Times.Once);
    }

    [Fact]
    public async Task Signing_out_other_devices_revokes_all_but_the_current_session_and_is_recorded()
    {
        var currentUser = new Mock<ICurrentUser>();
        currentUser.SetupGet(c => c.UserId).Returns(1);
        currentUser.SetupGet(c => c.SessionId).Returns(9);
        var sessions = new Mock<ISessionRepository>();
        var handler = new StreetBiz.Application.Features.Authentication.Sessions.RevokeOtherSessionsCommandHandler(
            currentUser.Object, sessions.Object, events.Object);

        await handler.Handle(new StreetBiz.Application.Features.Authentication.Sessions.RevokeOtherSessionsCommand(), default);

        sessions.Verify(s => s.RevokeAllForUserExceptAsync(1, 9, default), Times.Once);
        events.Verify(e => e.RecordAsync(1, SecurityActions.OtherSessionsRevoked, null, null, default), Times.Once);
    }

    [Fact]
    public async Task Changing_the_password_notifies_the_owner()
    {
        var currentUser = new Mock<ICurrentUser>();
        currentUser.SetupGet(c => c.UserId).Returns(1);
        currentUser.SetupGet(c => c.SessionId).Returns(9);
        hasher.Setup(h => h.Verify("old", "hash")).Returns(true);
        hasher.Setup(h => h.Hash("New#Pass123")).Returns("newhash");
        var handler = new StreetBiz.Application.Features.Authentication.ChangePassword.ChangePasswordCommandHandler(
            currentUser.Object, users.Object, hasher.Object, new Mock<ISessionRepository>().Object, events.Object);
        users.Setup(u => u.GetByIdAsync(1, default)).ReturnsAsync(
            new AppUser(1, Phone, "hash", "A", RoleCodes.Vendor, null, AccountStatuses.Active, null));

        await handler.Handle(
            new StreetBiz.Application.Features.Authentication.ChangePassword.ChangePasswordCommand("old", "New#Pass123"), default);

        events.Verify(e => e.RecordAsync(
            1, SecurityActions.PasswordChanged, null, It.Is<(string Title, string Body)?>(n => n != null), default), Times.Once);
    }
}
