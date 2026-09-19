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
    IAuthTokenIssuer tokenIssuer) : IRequestHandler<LoginCommand, AuthResultDto>
{
    public async Task<AuthResultDto> Handle(LoginCommand request, CancellationToken cancellationToken)
    {
        var user = await userRepository.GetByPhoneAsync(request.PhoneNumber, cancellationToken);

        // Uniform failure for unknown phone or wrong password (SEC-05, avoids enumeration).
        if (user is null || !passwordHasher.Verify(request.Password, user.PasswordHash))
        {
            throw new AuthenticationException(AppMessages.InvalidCredentials);
        }

        if (user.AccountStatus != AccountStatuses.Active)
        {
            throw new AuthenticationException(AppMessages.AccountSuspended);
        }

        return await tokenIssuer.IssueAsync(user, request.DeviceInfo, request.IpAddress, cancellationToken);
    }
}
