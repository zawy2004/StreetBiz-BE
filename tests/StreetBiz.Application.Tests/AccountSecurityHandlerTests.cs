using FluentAssertions;
using Moq;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.Authentication;
using StreetBiz.Application.Features.Authentication.ChangePassword;
using StreetBiz.Application.Features.Authentication.LoginWithOtp;
using StreetBiz.Application.Features.Authentication.Logout;
using StreetBiz.Application.Features.Authentication.Register;
using StreetBiz.Application.Features.Authentication.ResetPassword;
using StreetBiz.Application.Features.Authentication.Sessions;

namespace StreetBiz.Application.Tests;

/// <summary>AUTH-01 / AUTH-02: self-registration proves the phone with an OTP and signs straight in.</summary>
public sealed class RegisterCommandHandlerTests
{
    private const string Phone = "0905123456";
    private static readonly DateTime Now = new(2026, 11, 1, 3, 0, 0, DateTimeKind.Utc);
    private readonly Mock<IOtpService> otp = new();
    private readonly Mock<IUserAccountRepository> users = new();
    private readonly Mock<IPasswordHasher> hasher = new();
    private readonly Mock<IDateTimeProvider> clock = new();
    private readonly Mock<IAuthTokenIssuer> tokens = new();
    private readonly Mock<IAdministrativeUnitRepository> units = new();

    public RegisterCommandHandlerTests()
    {
        clock.Setup(c => c.UtcNow).Returns(Now);
        hasher.Setup(h => h.Hash(It.IsAny<string>())).Returns<string>(p => "hash:" + p);
        units.Setup(u => u.IsWardAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        users.Setup(u => u.CreateAsync(It.IsAny<NewUser>(), It.IsAny<CancellationToken>())).ReturnsAsync(77);
    }

    private RegisterCommandHandler Handler() => new(otp.Object, users.Object, hasher.Object, clock.Object, tokens.Object, units.Object, new FakeUnitOfWork());

    private static RegisterCommand Command(string role = RoleCodes.Vendor, int? ward = 10) =>
        new(Phone, "Str0ng!Pass", "Chị Lan", role, ward, "123456", "Chrome", "127.0.0.1");

    [Fact]
    public async Task A_valid_registration_spends_the_signup_otp_creates_an_active_verified_account_and_signs_in()
    {
        await Handler().Handle(Command(), default);

        otp.Verify(o => o.ConsumeAsync(Phone, OtpPurposes.Signup, "123456", It.IsAny<CancellationToken>()));
        users.Verify(u => u.CreateAsync(It.Is<NewUser>(n =>
            n.PhoneNumber == Phone && n.PasswordHash == "hash:Str0ng!Pass" && n.RoleCode == RoleCodes.Vendor &&
            n.WardUnitId == 10 && n.AccountStatus == AccountStatuses.Active && n.PhoneVerifiedAtUtc == Now), It.IsAny<CancellationToken>()));
        tokens.Verify(t => t.IssueAsync(It.Is<AppUser>(u => u.Id == 77 && u.PhoneNumber == Phone && u.RoleCode == RoleCodes.Vendor), "Chrome", "127.0.0.1", It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task An_unknown_ward_is_refused_before_the_otp_is_spent()
    {
        var act = () => Handler().Handle(Command(ward: 999), default);

        await act.Should().ThrowAsync<ValidationAppException>();
        otp.VerifyNoOtherCalls();
        users.Verify(u => u.CreateAsync(It.IsAny<NewUser>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_registered_phone_is_a_conflict_and_does_not_burn_the_code()
    {
        users.Setup(u => u.PhoneExistsAsync(Phone, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var act = () => Handler().Handle(Command(), default);

        await act.Should().ThrowAsync<ConflictException>().WithMessage(AppMessages.PhoneAlreadyRegistered);
        otp.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task A_wrong_otp_creates_no_account()
    {
        otp.Setup(o => o.ConsumeAsync(Phone, OtpPurposes.Signup, "123456", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AuthenticationException(AppMessages.OtpIncorrect));

        var act = () => Handler().Handle(Command(), default);

        await act.Should().ThrowAsync<AuthenticationException>();
        users.Verify(u => u.CreateAsync(It.IsAny<NewUser>(), It.IsAny<CancellationToken>()), Times.Never);
        tokens.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task A_customer_may_register_without_a_ward()
    {
        await Handler().Handle(Command(RoleCodes.Customer, ward: null), default);

        units.VerifyNoOtherCalls();
        users.Verify(u => u.CreateAsync(It.Is<NewUser>(n => n.WardUnitId == null && n.RoleCode == RoleCodes.Customer), It.IsAny<CancellationToken>()));
    }

    [Theory]
    [InlineData(RoleCodes.Customer, true)]
    [InlineData(RoleCodes.Vendor, true)]
    [InlineData("WARD_AUTHORITY", false)]
    [InlineData("PLATFORM_ADMIN", false)]
    public void Only_customer_and_vendor_can_self_register(string role, bool valid) =>
        new RegisterCommandValidator().Validate(Command(role)).IsValid.Should().Be(valid);

    [Theory]
    [InlineData("weakpass")]
    [InlineData("NoDigits!!")]
    [InlineData("n0upper!x")]
    [InlineData("Sp ace1!aa")]
    public void A_password_must_meet_the_policy(string password) =>
        new RegisterCommandValidator().Validate(Command() with { Password = password }).IsValid.Should().BeFalse();

    [Theory]
    [InlineData("12345")]
    [InlineData("abcdef")]
    public void The_otp_is_six_digits(string code) =>
        new RegisterCommandValidator().Validate(Command() with { Otp = code }).IsValid.Should().BeFalse();
}

/// <summary>AUTH-03 (OTP), AUTH-06, AUTH-07: OTP sign-in, password reset and password change.</summary>
public sealed class PasswordAndOtpSignInHandlerTests
{
    private const string Phone = "0905123456";
    private static readonly AppUser User = new(5, Phone, "old-hash", "Lan", RoleCodes.Vendor, 10, AccountStatuses.Active, DateTime.UtcNow);
    private readonly Mock<IOtpService> otp = new();
    private readonly Mock<IUserAccountRepository> users = new();
    private readonly Mock<IPasswordHasher> hasher = new();
    private readonly Mock<ISessionRepository> sessions = new();
    private readonly Mock<ISecurityEvents> events = new();
    private readonly Mock<IAuthTokenIssuer> tokens = new();
    private readonly Mock<ICurrentUser> current = new();

    public PasswordAndOtpSignInHandlerTests()
    {
        hasher.Setup(h => h.Hash(It.IsAny<string>())).Returns<string>(p => "hash:" + p);
        users.Setup(u => u.GetByPhoneAsync(Phone, It.IsAny<CancellationToken>())).ReturnsAsync(User);
        users.Setup(u => u.GetByIdAsync(5, It.IsAny<CancellationToken>())).ReturnsAsync(User);
    }

    [Fact]
    public async Task Otp_sign_in_spends_the_login_code_then_issues_a_session()
    {
        await new LoginWithOtpCommandHandler(users.Object, otp.Object, tokens.Object).Handle(new LoginWithOtpCommand(Phone, "654321", "Safari", "10.0.0.1"), default);

        otp.Verify(o => o.ConsumeAsync(Phone, OtpPurposes.Login, "654321", It.IsAny<CancellationToken>()));
        tokens.Verify(t => t.IssueAsync(User, "Safari", "10.0.0.1", It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task Otp_sign_in_to_a_suspended_account_is_refused()
    {
        users.Setup(u => u.GetByPhoneAsync(Phone, It.IsAny<CancellationToken>())).ReturnsAsync(User with { AccountStatus = "SUSPENDED" });

        var act = () => new LoginWithOtpCommandHandler(users.Object, otp.Object, tokens.Object).Handle(new LoginWithOtpCommand(Phone, "654321", null, null), default);

        await act.Should().ThrowAsync<AuthenticationException>().WithMessage(AppMessages.AccountSuspended);
        tokens.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Otp_sign_in_for_a_phone_without_an_account_gives_the_generic_credentials_error()
    {
        users.Setup(u => u.GetByPhoneAsync(Phone, It.IsAny<CancellationToken>())).ReturnsAsync((AppUser?)null);

        var act = () => new LoginWithOtpCommandHandler(users.Object, otp.Object, tokens.Object).Handle(new LoginWithOtpCommand(Phone, "654321", null, null), default);

        await act.Should().ThrowAsync<AuthenticationException>().WithMessage(AppMessages.InvalidCredentials);
    }

    [Theory]
    [InlineData("0905123456", "123456", true)]
    [InlineData("+84905123456", "123456", true)]
    [InlineData("905123456", "123456", false)]
    [InlineData("0905123456", "12345a", false)]
    public void Otp_sign_in_input_rules(string phone, string code, bool valid) =>
        new LoginWithOtpCommandValidator().Validate(new LoginWithOtpCommand(phone, code, null, null)).IsValid.Should().Be(valid);

    [Fact]
    public async Task A_reset_sets_the_new_hash_signs_out_every_device_and_tells_the_user()
    {
        await new ResetPasswordCommandHandler(otp.Object, users.Object, hasher.Object, sessions.Object, events.Object, new FakeUnitOfWork())
            .Handle(new ResetPasswordCommand(Phone, "111222", "N3w!Passw0rd"), default);

        otp.Verify(o => o.ConsumeAsync(Phone, OtpPurposes.PasswordReset, "111222", It.IsAny<CancellationToken>()));
        users.Verify(u => u.UpdatePasswordHashAsync(5, "hash:N3w!Passw0rd", It.IsAny<CancellationToken>()));
        sessions.Verify(s => s.RevokeAllForUserAsync(5, It.IsAny<CancellationToken>()));
        events.Verify(e => e.RecordAsync(5, SecurityActions.PasswordReset, null, It.IsNotNull<(string, string)?>(), It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task A_reset_with_a_wrong_code_changes_nothing()
    {
        otp.Setup(o => o.ConsumeAsync(Phone, OtpPurposes.PasswordReset, "000000", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AuthenticationException(AppMessages.OtpIncorrect));

        var act = () => new ResetPasswordCommandHandler(otp.Object, users.Object, hasher.Object, sessions.Object, events.Object, new FakeUnitOfWork())
            .Handle(new ResetPasswordCommand(Phone, "000000", "N3w!Passw0rd"), default);

        await act.Should().ThrowAsync<AuthenticationException>();
        users.Verify(u => u.UpdatePasswordHashAsync(It.IsAny<long>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        sessions.VerifyNoOtherCalls();
        events.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("0905123456", "111222", "N3w!Passw0rd", true)]
    [InlineData("0905123456", "111222", "weak", false)]
    [InlineData("0905123456", "11122", "N3w!Passw0rd", false)]
    [InlineData("12", "111222", "N3w!Passw0rd", false)]
    public void Reset_input_rules(string phone, string code, string password, bool valid) =>
        new ResetPasswordCommandValidator().Validate(new ResetPasswordCommand(phone, code, password)).IsValid.Should().Be(valid);

    [Fact]
    public async Task Changing_the_password_keeps_this_device_and_signs_out_the_others()
    {
        current.Setup(c => c.UserId).Returns(5);
        current.Setup(c => c.SessionId).Returns(40);
        hasher.Setup(h => h.Verify("Old!Passw0rd", "old-hash")).Returns(true);

        await new ChangePasswordCommandHandler(current.Object, users.Object, hasher.Object, sessions.Object, events.Object, new FakeUnitOfWork())
            .Handle(new ChangePasswordCommand("Old!Passw0rd", "N3w!Passw0rd"), default);

        users.Verify(u => u.UpdatePasswordHashAsync(5, "hash:N3w!Passw0rd", It.IsAny<CancellationToken>()));
        sessions.Verify(s => s.RevokeAllForUserExceptAsync(5, 40, It.IsAny<CancellationToken>()));
        events.Verify(e => e.RecordAsync(5, SecurityActions.PasswordChanged, null, It.IsNotNull<(string, string)?>(), It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task Changing_the_password_without_a_session_is_a_session_expired_error()
    {
        current.Setup(c => c.UserId).Returns((long?)null);

        var act = () => new ChangePasswordCommandHandler(current.Object, users.Object, hasher.Object, sessions.Object, events.Object, new FakeUnitOfWork())
            .Handle(new ChangePasswordCommand("Old!Passw0rd", "N3w!Passw0rd"), default);

        await act.Should().ThrowAsync<AuthenticationException>().WithMessage(AppMessages.SessionExpired);
    }

    [Theory]
    [InlineData("Old!Passw0rd", "N3w!Passw0rd", true)]
    [InlineData("", "N3w!Passw0rd", false)]
    [InlineData("Same!Passw0rd", "Same!Passw0rd", false)]
    [InlineData("Old!Passw0rd", "short", false)]
    public void Change_password_input_rules(string currentPassword, string newPassword, bool valid) =>
        new ChangePasswordCommandValidator().Validate(new ChangePasswordCommand(currentPassword, newPassword)).IsValid.Should().Be(valid);
}

/// <summary>AUTH-04 / AUTH-08 / AUTH-09: signing out, listing devices and revoking one.</summary>
public sealed class SessionHandlerTests
{
    private static readonly DateTime T = new(2026, 11, 1, 3, 0, 0, DateTimeKind.Utc);
    private readonly Mock<ICurrentUser> current = new();
    private readonly Mock<ISessionRepository> sessions = new();
    private readonly Mock<ISecurityEvents> events = new();

    public SessionHandlerTests()
    {
        current.Setup(c => c.UserId).Returns(5);
        current.Setup(c => c.SessionId).Returns(40);
    }

    private static AppSession Session(long id, long user, string device = "Chrome") => new(id, user, device, "1.1.1.1", T, T, T.AddDays(30), null);

    [Fact]
    public async Task Logout_revokes_the_current_session()
    {
        await new LogoutCommandHandler(current.Object, sessions.Object).Handle(new LogoutCommand(), default);

        sessions.Verify(s => s.RevokeAsync(40, It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task Logout_without_a_session_is_refused()
    {
        current.Setup(c => c.SessionId).Returns((long?)null);

        var act = () => new LogoutCommandHandler(current.Object, sessions.Object).Handle(new LogoutCommand(), default);

        await act.Should().ThrowAsync<AuthenticationException>();
        sessions.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task The_session_list_flags_the_current_device()
    {
        sessions.Setup(s => s.ListActiveByUserAsync(5, It.IsAny<CancellationToken>())).ReturnsAsync([Session(40, 5), Session(41, 5, "Android")]);

        var list = await new ListSessionsQueryHandler(current.Object, sessions.Object).Handle(new ListSessionsQuery(), default);

        list.Should().HaveCount(2);
        list.Single(s => s.IsCurrent).SessionId.Should().Be(40);
        list.Single(s => !s.IsCurrent).DeviceInfo.Should().Be("Android");
    }

    [Fact]
    public async Task Revoking_another_of_my_devices_is_recorded()
    {
        sessions.Setup(s => s.GetByIdAsync(41, It.IsAny<CancellationToken>())).ReturnsAsync(Session(41, 5, "Android"));

        await new RevokeSessionCommandHandler(current.Object, sessions.Object, events.Object).Handle(new RevokeSessionCommand(41), default);

        sessions.Verify(s => s.RevokeAsync(41, It.IsAny<CancellationToken>()));
        events.Verify(e => e.RecordAsync(5, SecurityActions.SessionRevoked, "Android", null, It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task The_current_session_cannot_be_revoked_from_the_device_list()
    {
        var act = () => new RevokeSessionCommandHandler(current.Object, sessions.Object, events.Object).Handle(new RevokeSessionCommand(40), default);

        await act.Should().ThrowAsync<DomainRuleException>().WithMessage(AppMessages.CannotRevokeCurrent);
        sessions.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Someone_elses_session_is_forbidden_and_an_unknown_one_is_not_found()
    {
        sessions.Setup(s => s.GetByIdAsync(50, It.IsAny<CancellationToken>())).ReturnsAsync(Session(50, 6));
        var handler = new RevokeSessionCommandHandler(current.Object, sessions.Object, events.Object);

        await FluentActions.Invoking(() => handler.Handle(new RevokeSessionCommand(50), default)).Should().ThrowAsync<ForbiddenException>();
        await FluentActions.Invoking(() => handler.Handle(new RevokeSessionCommand(99), default)).Should().ThrowAsync<NotFoundException>();
        sessions.Verify(s => s.RevokeAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
        events.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Signing_out_all_other_devices_keeps_this_one()
    {
        await new RevokeOtherSessionsCommandHandler(current.Object, sessions.Object, events.Object).Handle(new RevokeOtherSessionsCommand(), default);

        sessions.Verify(s => s.RevokeAllForUserExceptAsync(5, 40, It.IsAny<CancellationToken>()));
        events.Verify(e => e.RecordAsync(5, SecurityActions.OtherSessionsRevoked, null, null, It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task Login_history_pages_the_callers_own_events()
    {
        var history = new List<SecurityEvent> { new(9, SecurityActions.LoginSuccess, "Chrome", T) };
        events.Setup(e => e.ListAsync(5, 10, 100L, It.IsAny<CancellationToken>())).ReturnsAsync(history);

        var page = await new LoginHistoryQueryHandler(current.Object, events.Object).Handle(new LoginHistoryQuery(10, 100), default);

        page.Should().BeSameAs(history);
    }

    [Fact]
    public async Task Login_history_needs_a_signed_in_user()
    {
        current.Setup(c => c.UserId).Returns((long?)null);

        var act = () => new LoginHistoryQueryHandler(current.Object, events.Object).Handle(new LoginHistoryQuery(), default);

        await act.Should().ThrowAsync<AuthenticationException>();
    }
}
