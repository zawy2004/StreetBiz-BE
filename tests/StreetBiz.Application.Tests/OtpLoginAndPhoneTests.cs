using FluentAssertions;
using MediatR;
using Moq;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.Authentication;
using StreetBiz.Application.Features.Authentication.LoginWithOtp;
using StreetBiz.Application.Features.Authentication.SendOtp;

namespace StreetBiz.Application.Tests;

/// <summary>AUTH-03 via OTP (FE-01, BR-03) and the CR-06 phone normalization it relies on.</summary>
public sealed class OtpLoginAndPhoneTests
{
    private const string Phone = "0905000001";

    private readonly Mock<IOtpService> otp = new();
    private readonly Mock<IUserAccountRepository> users = new();
    private readonly Mock<IAuthTokenIssuer> tokens = new();

    private static AppUser ActiveUser(string status = AccountStatuses.Active) =>
        new(1, Phone, "hash", "Người bán", RoleCodes.Vendor, null, status, DateTime.UtcNow);

    private LoginWithOtpCommandHandler Handler() =>
        new(users.Object, otp.Object, tokens.Object);

    [Fact]
    public async Task A_valid_login_code_opens_a_session()
    {
        users.Setup(u => u.GetByPhoneAsync(Phone, It.IsAny<CancellationToken>())).ReturnsAsync(ActiveUser());
        tokens.Setup(t => t.IssueAsync(It.IsAny<AppUser>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuthResultDto("access", DateTime.UtcNow.AddHours(1), "refresh",
                new UserDto(1, Phone, "Người bán", RoleCodes.Vendor, null, AccountStatuses.Active)));

        var result = await Handler().Handle(
            new LoginWithOtpCommand(Phone, "123456", "device", "127.0.0.1"), CancellationToken.None);

        result.AccessToken.Should().Be("access");
        otp.Verify(o => o.ConsumeAsync(Phone, OtpPurposes.Login, "123456", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task The_code_is_consumed_before_the_account_is_looked_up()
    {
        // An unknown phone never received a LOGIN code, so it fails on the code rather
        // than on "no such account" — the response cannot be used to enumerate accounts.
        otp.Setup(o => o.ConsumeAsync(Phone, OtpPurposes.Login, "000000", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AuthenticationException(AppMessages.OtpExpired));

        var act = () => Handler().Handle(
            new LoginWithOtpCommand(Phone, "000000", null, null), CancellationToken.None);

        await act.Should().ThrowAsync<AuthenticationException>().WithMessage(AppMessages.OtpExpired);
        users.Verify(u => u.GetByPhoneAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_suspended_account_cannot_sign_in_with_a_valid_code()
    {
        users.Setup(u => u.GetByPhoneAsync(Phone, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ActiveUser(AccountStatuses.Suspended));

        var act = () => Handler().Handle(
            new LoginWithOtpCommand(Phone, "123456", null, null), CancellationToken.None);

        await act.Should().ThrowAsync<AuthenticationException>().WithMessage(AppMessages.AccountSuspended);
        tokens.Verify(t => t.IssueAsync(It.IsAny<AppUser>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Requesting_a_login_code_for_an_unknown_phone_stays_silent()
    {
        users.Setup(u => u.PhoneExistsAsync(Phone, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var handler = new SendOtpCommandHandler(otp.Object, users.Object, Mock.Of<ISender>());

        await handler.Handle(new SendOtpCommand(Phone, OtpPurposes.Login), CancellationToken.None);

        otp.Verify(o => o.IssueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_login_code_cooldown_is_swallowed_so_it_cannot_reveal_a_registered_phone()
    {
        users.Setup(u => u.PhoneExistsAsync(Phone, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        otp.Setup(o => o.IssueAsync(Phone, OtpPurposes.Login, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TooManyRequestsException(AppMessages.OtpCooldown, 42));
        var handler = new SendOtpCommandHandler(otp.Object, users.Object, Mock.Of<ISender>());

        var act = () => handler.Handle(new SendOtpCommand(Phone, OtpPurposes.Login), CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Theory]
    [InlineData("+84905000001", "0905000001")]
    [InlineData("0905000001", "0905000001")]
    [InlineData("  0905000001  ", "0905000001")]
    [InlineData("+8490500000", "+8490500000")]   // too short to be the +84 form: left alone
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Phone_numbers_collapse_to_the_local_form(string? input, string expected) =>
        AuthValidationRules.NormalizePhone(input).Should().Be(expected);
}
