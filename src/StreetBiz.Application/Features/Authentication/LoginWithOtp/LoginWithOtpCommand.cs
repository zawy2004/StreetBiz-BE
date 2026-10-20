using FluentValidation;
using MediatR;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.Authentication;

namespace StreetBiz.Application.Features.Authentication.LoginWithOtp;

/// <summary>
/// AUTH-03 without a password: the account proves itself with the OTP sent to its
/// phone (FE-01, BR-03). Password sign-in stays available because UserAccounts
/// .password_hash is NOT NULL and AUTH-05/06/07 are password use cases.
/// </summary>
public sealed record LoginWithOtpCommand(
    string PhoneNumber, string Otp, string? DeviceInfo, string? IpAddress)
    : IRequest<AuthResultDto>;

public sealed class LoginWithOtpCommandValidator : AbstractValidator<LoginWithOtpCommand>
{
    public LoginWithOtpCommandValidator()
    {
        RuleFor(x => x.PhoneNumber)
            .Must(p => AuthValidationRules.PhoneRegex().IsMatch(p))
            .WithMessage(AppMessages.InvalidPhone);

        RuleFor(x => x.Otp)
            .Must(p => AuthValidationRules.OtpRegex().IsMatch(p))
            .WithMessage(AppMessages.OtpIncorrect);
    }
}

public sealed class LoginWithOtpCommandHandler(
    IUserAccountRepository userRepository,
    IOtpService otpService,
    IAuthTokenIssuer tokenIssuer) : IRequestHandler<LoginWithOtpCommand, AuthResultDto>
{
    public async Task<AuthResultDto> Handle(LoginWithOtpCommand request, CancellationToken cancellationToken)
    {
        // Consume first: a LOGIN code is only ever issued to a registered phone, so an
        // unknown number fails here with the same error as a wrong code and the response
        // never reveals whether the account exists (SEC-05).
        await otpService.ConsumeAsync(request.PhoneNumber, OtpPurposes.Login, request.Otp, cancellationToken);

        var user = await userRepository.GetByPhoneAsync(request.PhoneNumber, cancellationToken)
            ?? throw new AuthenticationException(AppMessages.InvalidCredentials);

        if (user.AccountStatus != AccountStatuses.Active)
        {
            throw new AuthenticationException(AppMessages.AccountSuspended);
        }

        return await tokenIssuer.IssueAsync(user, request.DeviceInfo, request.IpAddress, cancellationToken);
    }
}
