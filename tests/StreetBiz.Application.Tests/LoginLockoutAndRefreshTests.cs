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

    private LoginCommandHandler Login() => new(users.Object, hasher.Object, tokens.Object, clock.Object);

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
