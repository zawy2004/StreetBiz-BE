using FluentValidation;
using MediatR;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.Authentication;

namespace StreetBiz.Application.Features.Authentication.Login;

/// <summary>AUTH-03: authenticate with phone + password and open a session.</summary>
public sealed record LoginCommand(
    string PhoneNumber, string Password, string? DeviceInfo, string? IpAddress)
    : IRequest<AuthResultDto>;

public sealed class LoginCommandValidator : AbstractValidator<LoginCommand>
{
    public LoginCommandValidator()
    {
        RuleFor(x => x.PhoneNumber).NotEmpty().WithMessage(AppMessages.InvalidPhone);
        RuleFor(x => x.Password).NotEmpty().WithMessage(AppMessages.PasswordRequired);
    }
}

public sealed class LoginCommandHandler(
    IUserAccountRepository userRepository,
    IPasswordHasher passwordHasher,
    IAuthTokenIssuer tokenIssuer,
    IDateTimeProvider clock,
    ISecurityEvents securityEvents) : IRequestHandler<LoginCommand, AuthResultDto>
{
    private const int MaxFailures = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    private static string Describe(LoginCommand request) =>
        $"IP {request.IpAddress ?? "?"} · {request.DeviceInfo ?? "thiết bị không rõ"}";

    public async Task<AuthResultDto> Handle(LoginCommand request, CancellationToken cancellationToken)
    {
        var user = await userRepository.GetByPhoneAsync(request.PhoneNumber, cancellationToken);

        if (user is null)
        {
            // Burn the same hashing time as a real check so response time does not reveal
            // which phone numbers have accounts, then fail uniformly (SEC-05).
            passwordHasher.Hash(request.Password);
            throw new AuthenticationException(AppMessages.InvalidCredentials);
        }

        var now = clock.UtcNow;
        if (user.LockoutUntil is { } lockedUntil && lockedUntil > now)
        {
            throw new TooManyRequestsException(
                AppMessages.AccountLocked, (int)Math.Ceiling((lockedUntil - now).TotalSeconds));
        }

        if (!passwordHasher.Verify(request.Password, user.PasswordHash))
        {
            var locked = await userRepository.RecordFailedLoginAsync(user.Id, MaxFailures, LockoutDuration, cancellationToken);
            await securityEvents.RecordAsync(
                user.Id,
                locked ? SecurityActions.AccountLocked : SecurityActions.LoginFailed,
                Describe(request),
                locked
                    ? ("Tài khoản tạm thời bị khoá", "Có nhiều lần đăng nhập sai. Tài khoản bị khoá 15 phút. Nếu không phải bạn, hãy đổi mật khẩu sau khi mở khoá.")
                    : null,
                cancellationToken);
            throw new AuthenticationException(AppMessages.InvalidCredentials);
        }

        if (user.AccountStatus != AccountStatuses.Active)
        {
            throw new AuthenticationException(AppMessages.AccountSuspended);
        }

        await userRepository.ClearFailedLoginsAsync(user.Id, cancellationToken);
        await securityEvents.RecordAsync(user.Id, SecurityActions.LoginSuccess, Describe(request), null, cancellationToken);
        return await tokenIssuer.IssueAsync(user, request.DeviceInfo, request.IpAddress, cancellationToken);
    }
}
