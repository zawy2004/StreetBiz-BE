using FluentAssertions;
using MediatR;
using Moq;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Features.Authentication.ChangePassword;
using StreetBiz.Application.Features.Authentication.RequestPasswordReset;
using StreetBiz.Application.Features.Authentication.SendOtp;

namespace StreetBiz.Application.Tests;

public sealed class AuthenticationHandlerTests
{
    private const string Phone = "0905000001";

    private readonly Mock<IOtpService> otp = new();
    private readonly Mock<IUserAccountRepository> users = new();

    [Fact]
    public async Task Send_registration_otp_to_a_registered_phone_is_rejected_before_any_sms()
    {
        users.Setup(u => u.PhoneExistsAsync(Phone, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var handler = new SendOtpCommandHandler(otp.Object, users.Object, Mock.Of<ISender>());

        var act = () => handler.Handle(new SendOtpCommand(Phone, OtpPurposes.Signup), CancellationToken.None);

        await act.Should().ThrowAsync<ConflictException>().WithMessage(AppMessages.PhoneAlreadyRegistered);
        otp.Verify(o => o.IssueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Send_password_reset_otp_goes_through_the_uniform_reset_flow()
    {
        var sender = new Mock<ISender>();
        var handler = new SendOtpCommandHandler(otp.Object, users.Object, sender.Object);

        await handler.Handle(new SendOtpCommand(Phone, OtpPurposes.PasswordReset), CancellationToken.None);

        sender.Verify(s => s.Send(
            It.Is<RequestPasswordResetCommand>(c => c.PhoneNumber == Phone), It.IsAny<CancellationToken>()), Times.Once);
        otp.Verify(o => o.IssueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Password_reset_hides_the_cooldown_so_registered_phones_are_not_revealed()
    {
        users.Setup(u => u.PhoneExistsAsync(Phone, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        otp.Setup(o => o.IssueAsync(Phone, OtpPurposes.PasswordReset, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TooManyRequestsException(AppMessages.OtpCooldown, 42));
        var handler = new RequestPasswordResetCommandHandler(users.Object, otp.Object);

        var act = () => handler.Handle(new RequestPasswordResetCommand(Phone), CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Password_reset_for_an_unknown_phone_sends_nothing_and_succeeds()
    {
        var handler = new RequestPasswordResetCommandHandler(users.Object, otp.Object);

        await handler.Handle(new RequestPasswordResetCommand(Phone), CancellationToken.None);

        otp.Verify(o => o.IssueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Wrong_current_password_is_a_field_error_not_a_401()
    {
        var currentUser = new Mock<ICurrentUser>();
        currentUser.SetupGet(c => c.UserId).Returns(7);
        users.Setup(u => u.GetByIdAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(
            new AppUser(7, Phone, "hash", "A", RoleCodes.Vendor, null, AccountStatuses.Active, null));
        var hasher = new Mock<IPasswordHasher>();
        hasher.Setup(h => h.Verify("wrong", "hash")).Returns(false);
        var sessions = new Mock<ISessionRepository>();

        var handler = new ChangePasswordCommandHandler(currentUser.Object, users.Object, hasher.Object, sessions.Object);
        var act = () => handler.Handle(new ChangePasswordCommand("wrong", "N3w!Passw0rd"), CancellationToken.None);

        var error = await act.Should().ThrowAsync<ValidationAppException>();
        error.Which.Errors.Should().ContainKey(nameof(ChangePasswordCommand.CurrentPassword));
        users.Verify(u => u.UpdatePasswordHashAsync(It.IsAny<long>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("0905000001", true)]
    [InlineData("+84905000001", true)]
    [InlineData("905000001", false)]
    [InlineData("09050000012", false)]
    public void Phone_rule_matches_what_the_frontend_normalizes_to(string phone, bool valid)
        => AuthValidationRules.PhoneRegex().IsMatch(phone).Should().Be(valid);
}
